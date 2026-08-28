using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Outside;

namespace StormMachine.Probes;

/// <summary>
/// Собирает взгляд на сеть снаружи из четырёх независимых источников.
/// </summary>
/// <remarks>
/// Каждый источник может отказать по своей причине, и ни один отказ не отменяет
/// остальные: без базы ASN остаются адрес и имя, без ответа STUN остаётся IPv6.
/// Поэтому здесь нет ни одного места, где неудача одной части прекращает работу, —
/// вместо этого невыясненное попадает в <see cref="OutsideView.Notes"/> под своим именем.
/// </remarks>
public sealed class OutsideViewService(IHopAnnotator annotator) : IOutsideView
{
    /// <summary>Порт, на котором проверяется досягаемость по IPv6.</summary>
    private const int Ipv6ProbePort = 443;

    private readonly IHopAnnotator _annotator = annotator ?? throw new ArgumentNullException(nameof(annotator));

    public async Task<OutsideView> LookAsync(OutsideRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var servers = request.StunServers.Count > 0 ? request.StunServers : StunClient.DefaultServers;
        var notes = new List<string>();

        var probe = await StunClient.QueryAsync(servers, request.TimeoutMs, cancellationToken).ConfigureAwait(false);
        var replies = probe.Replies;
        var answered = replies.Where(r => r.Answered).ToList();

        var local = probe.Local;
        var external = answered.Count > 0 ? answered[0].Mapped : null;

        foreach (var failed in replies.Where(r => !r.Answered))
        {
            notes.Add(Sentence($"Сервер STUN {failed.Server}: {failed.Failure ?? "не ответил"}"));
        }

        // 0.0.0.0 — это «система не выбрала интерфейс», а не адрес. Показать его
        // локальным адресом значило бы выдать признак неудачи за результат.
        var localAddress = local is not null && !IPAddress.Any.Equals(local.Address)
            ? local.Address.ToString()
            : null;

        var view = new OutsideView
        {
            LocalAddress = localAddress,
            LocalPort = localAddress is null ? 0 : local!.Port,
            ExternalAddress = external?.Address.ToString(),
            ExternalPort = external?.Port ?? 0,
            Mapping = Classify(local, answered),
            Mappings =
            [
                .. replies.Select(r => new OutsideMapping(
                    r.Server,
                    r.Mapped?.Address.ToString(),
                    r.Mapped?.Port ?? 0,
                    r.Failure)),
            ],
        };

        if (external is not null)
        {
            view = await AnnotateAsync(view, external.Address.ToString(), notes, cancellationToken).ConfigureAwait(false);
        }

        if (request.CheckIpv6)
        {
            view = view with { Ipv6 = await CheckIpv6Async(request, cancellationToken).ConfigureAwait(false) };
        }

        return view with { Notes = notes };
    }

    /// <summary>
    /// Вывод о поведении NAT из того, что сказали серверы.
    /// </summary>
    /// <remarks>
    /// Сравниваются пары «адрес:порт» целиком. Одного адреса мало: при одном и том же
    /// внешнем адресе NAT вполне может выдавать разным адресатам разные порты — именно
    /// это и мешает прямому соединению, и именно это надо увидеть.
    /// </remarks>
    private static NatMapping Classify(IPEndPoint? local, List<StunReply> answered)
    {
        if (answered.Count == 0)
        {
            return NatMapping.Unknown;
        }

        var first = answered[0].Mapped!;

        if (local is not null && first.Address.Equals(local.Address) && first.Port == local.Port)
        {
            return NatMapping.None;
        }

        if (answered.Count == 1)
        {
            return NatMapping.Undetermined;
        }

        return answered.All(r => r.Mapped!.Equals(first))
            ? NatMapping.EndpointIndependent
            : NatMapping.AddressDependent;
    }

    private async Task<OutsideView> AnnotateAsync(
        OutsideView view,
        string address,
        List<string> notes,
        CancellationToken cancellationToken)
    {
        var annotations = await _annotator.AnnotateAsync([address], cancellationToken).ConfigureAwait(false);

        if (!_annotator.HasAsnData)
        {
            notes.Add("Принадлежность к автономной системе не определена: базы ASN нет. "
                      + $"Ожидается здесь: {_annotator.AsnDatabaseHint}");
        }

        if (!annotations.TryGetValue(address, out var annotation))
        {
            return view;
        }

        if (annotation.HostName is null)
        {
            notes.Add("Обратной записи (PTR) у внешнего адреса нет — у динамических адресов это обычное дело.");
        }

        return view with
        {
            HostName = annotation.HostName,
            AsNumber = annotation.AsNumber,
            AsOrganization = annotation.AsOrganization,
            Country = annotation.Country,
            Attribution = _annotator.Attribution,
        };
    }

    /// <summary>
    /// Три условия готовности к IPv6, проверяемые по отдельности.
    /// </summary>
    /// <remarks>
    /// Проверять их одной попыткой соединения нельзя: неудача не скажет, чего не хватило —
    /// адреса, записи AAAA или маршрута. А это три разные неисправности с тремя разными
    /// виновниками.
    /// </remarks>
    private static async Task<Ipv6Readiness> CheckIpv6Async(OutsideRequest request, CancellationToken cancellationToken)
    {
        var global = NetworkInterfaceIpv6();
        string? aaaa = null;
        string? failure = null;

        try
        {
            var addresses = await Dns
                .GetHostAddressesAsync(request.Ipv6Target, AddressFamily.InterNetworkV6, cancellationToken)
                .ConfigureAwait(false);

            aaaa = addresses.FirstOrDefault()?.ToString();
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            failure = ex.Message;
        }

        if (global is null || aaaa is null)
        {
            return new Ipv6Readiness
            {
                HasGlobalAddress = global is not null,
                GlobalAddress = global,
                ResolvesAaaa = aaaa is not null,
                AaaaAddress = aaaa,
                Reachable = false,
                Failure = failure,
            };
        }

        var reachable = false;

        try
        {
            using var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.TimeoutMs);

            await socket.ConnectAsync(IPAddress.Parse(aaaa), Ipv6ProbePort, timeout.Token).ConfigureAwait(false);
            reachable = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            failure = $"нет ответа за {request.TimeoutMs} мс на порт {Ipv6ProbePort}";
        }
        catch (SocketException ex)
        {
            failure = ex.SocketErrorCode.ToString();
        }

        return new Ipv6Readiness
        {
            HasGlobalAddress = true,
            GlobalAddress = global,
            ResolvesAaaa = true,
            AaaaAddress = aaaa,
            Reachable = reachable,
            Failure = reachable ? null : failure,
        };
    }

    /// <summary>Одна точка в конце, а не две: текст исключения её уже может содержать.</summary>
    private static string Sentence(string text) =>
        text.EndsWith('.') || text.EndsWith('!') ? text : text + ".";

    /// <summary>
    /// Глобальный адрес IPv6 машины, если он есть.
    /// </summary>
    /// <remarks>
    /// Петлевой интерфейс исключается вместе со своим <c>::1</c>: он есть всегда,
    /// и принять его за глобальный адрес значит объявить готовой к IPv6 любую машину.
    /// </remarks>
    private static string? NetworkInterfaceIpv6()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up
                || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                if (IpAddressScope.IsGloballyRoutableV6(unicast.Address))
                {
                    return unicast.Address.ToString();
                }
            }
        }

        return null;
    }
}
