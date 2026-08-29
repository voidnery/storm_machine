using System.Globalization;
using SharpPcap;
using SharpPcap.LibPcap;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;

namespace StormMachine.Capture.Npcap;

/// <summary>
/// Захват поверх уже установленного Npcap.
/// </summary>
/// <remarks>
/// <b>Без неразборчивого режима.</b> Адаптер открывается обычным, и это решение,
/// а не недосмотр: соседство по LLDP и CDP идёт на групповые адреса, которые наша
/// карта принимает и так, а ответы DHCP широковещательны. Неразборчивый режим добавил
/// бы к этому чужой одноадресный трафик — переписку соседей по сегменту, к диагностике
/// отношения не имеющую. Инструмент, который её собирает, называется иначе
/// (docs/01-analysis.md §1.4).
/// <para>
/// Драйвер продукт не распространяет: лицензия NPSL это запрещает. Отсюда всё
/// устройство класса — он обязан честно сказать «меня нет» и не упасть при этом.
/// Проверено спайком-09: типы поднимаются без драйвера, отказ приходит
/// опознаваемым <see cref="DllNotFoundException"/>.
/// </para>
/// </remarks>
public sealed class NpcapCaptureProvider : ICaptureProvider
{
    /// <summary>
    /// Что слушать.
    /// </summary>
    /// <remarks>
    /// Фильтр ставится на драйвер, а не в наш код: отсеивать в пользовательском
    /// пространстве значило бы протащить через границу весь трафик сегмента —
    /// и по нагрузке, и по смыслу это совсем другое действие.
    /// </remarks>
    private const string Filter =
        "(ether proto 0x88cc) or (ether dst 01:00:0c:cc:cc:cc) or (udp src port 67)";

    /// <summary>Сколько ждать кадр за один заход, мс.</summary>
    private const int ReadTimeout = 500;

    public CaptureRefusal Availability
    {
        get
        {
            try
            {
                var devices = CaptureDeviceList.Instance;

                return devices.Count == 0 ? CaptureRefusal.NoAdapters : CaptureRefusal.None;
            }
            catch (DllNotFoundException)
            {
                return CaptureRefusal.NoDriver;
            }
            catch (TypeInitializationException)
            {
                return CaptureRefusal.NoDriver;
            }
            catch (UnauthorizedAccessException)
            {
                // Npcap умеет ставиться с ограничением доступа администраторами.
                return CaptureRefusal.NeedsElevation;
            }
        }
    }

    public string? DriverDescription
    {
        get
        {
            try
            {
                var version = Pcap.Version;

                // Без драйвера библиотека отвечает строкой, а не исключением —
                // и по её содержанию видно, что спрашивать больше нечего.
                return version.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : version;
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
            {
                return null;
            }
        }
    }

    public IReadOnlyList<CaptureAdapter> Adapters()
    {
        try
        {
            var found = new List<CaptureAdapter>();

            foreach (var device in CaptureDeviceList.Instance)
            {
                if (device is not LibPcapLiveDevice live)
                {
                    continue;
                }

                var mac = live.MacAddress is { } address
                    ? string.Join('-', address.GetAddressBytes()
                        .Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))
                    : null;

                found.Add(new CaptureAdapter
                {
                    Id = live.Name,
                    Description = Blank(live.Description) ?? live.Name,
                    MacAddress = mac,
                    IsLoopback = live.Loopback,
                });
            }

            return found;
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            return [];
        }
    }

    public async Task<CaptureResult> ListenAsync(
        CaptureAdapter adapter,
        CaptureOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(options);

        var startedUtc = DateTimeOffset.UtcNow;
        var device = Find(adapter)
            ?? throw new InvalidOperationException($"Адаптер {adapter.DisplayName} не найден драйвером захвата.");

        var neighbors = new List<LinkNeighbor>();
        var sightings = new List<DhcpSighting>();
        var frames = 0;
        var unparsed = 0;

        await Task.Run(
            () =>
            {
                device.Open(DeviceModes.None, ReadTimeout);

                try
                {
                    device.Filter = Filter;

                    var deadline = DateTime.UtcNow + options.Duration;

                    while (DateTime.UtcNow < deadline
                           && frames < options.FrameLimit
                           && !cancellationToken.IsCancellationRequested)
                    {
                        if (device.GetNextPacket(out var capture) != GetPacketStatus.PacketRead)
                        {
                            continue;
                        }

                        frames++;

                        var finding = FrameParser.Parse(
                            capture.Data.ToArray(),
                            DateTimeOffset.UtcNow,
                            adapter.DisplayName);

                        if (finding is null)
                        {
                            unparsed++;

                            continue;
                        }

                        if (finding.Neighbor is { } neighbor && !Known(neighbors, neighbor))
                        {
                            neighbors.Add(neighbor);
                        }

                        if (finding.Dhcp is { } sighting)
                        {
                            sightings.Add(sighting);
                        }
                    }
                }
                finally
                {
                    device.Close();
                }
            },
            cancellationToken).ConfigureAwait(false);

        return new CaptureResult
        {
            Adapter = adapter,
            StartedUtc = startedUtc,
            Duration = DateTimeOffset.UtcNow - startedUtc,
            FramesSeen = frames,
            Unparsed = unparsed,
            Neighbors = neighbors,
            Dhcp = new DhcpFinding { Sightings = sightings },
        };
    }

    /// <summary>
    /// Один сосед объявляется каждые полминуты.
    /// </summary>
    /// <remarks>
    /// Складывать все его объявления подряд бессмысленно: за пять минут прослушивания
    /// один коммутатор дал бы десять одинаковых строк. Считается уже известным тот,
    /// у кого совпали имя и порт.
    /// </remarks>
    private static bool Known(List<LinkNeighbor> known, LinkNeighbor candidate) =>
        known.Exists(n => string.Equals(n.RemoteName, candidate.RemoteName, StringComparison.OrdinalIgnoreCase)
                          && string.Equals(n.RemotePort, candidate.RemotePort, StringComparison.OrdinalIgnoreCase)
                          && n.Protocol == candidate.Protocol);

    private static LibPcapLiveDevice? Find(CaptureAdapter adapter)
    {
        foreach (var device in CaptureDeviceList.Instance)
        {
            if (device is LibPcapLiveDevice live && string.Equals(live.Name, adapter.Id, StringComparison.Ordinal))
            {
                return live;
            }
        }

        return null;
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
