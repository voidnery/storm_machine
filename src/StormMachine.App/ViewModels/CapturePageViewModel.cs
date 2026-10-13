using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Capture;
using StormMachine.Domain.Capture;
using StormMachine.Domain.Discovery;

namespace StormMachine.App.ViewModels;

/// <summary>Сосед, услышанный в эфире.</summary>
public sealed record NeighborRow(string Name, string Port, string Protocol, string About, string Seen);

/// <summary>Сервер DHCP, услышанный в сегменте.</summary>
public sealed record DhcpRow(
    string Address,
    string Mac,
    string Gateway,
    string Dns,
    string Sightings,
    string Seen,
    bool IsStranger);

/// <summary>
/// Экран прослушивания эфира: соседи по LLDP и CDP, серверы DHCP.
/// </summary>
/// <remarks>
/// До И-25 прослушивание жило только в консоли, и это было терпимо ровно потому,
/// что работало оно у немногих: Npcap продукт не распространяет. Со встроенным
/// в Windows захватом слушать может каждый — и возможность, доступная всем,
/// обязана быть в окне.
/// <para>
/// <b>Экран ничего не отправляет в сеть.</b> Та же граница, что у порта захвата:
/// соседство объявляется само, ответы DHCP широковещательны. Строка об этом стоит
/// рядом с кнопкой, а не в документации.
/// </para>
/// </remarks>
public sealed partial class CapturePageViewModel : PageViewModel, IDisposable
{
    /// <summary>За какой срок показывается история услышанного.</summary>
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(30);

    private readonly CaptureService _capture;
    private readonly IObservationStore? _observations;

    private CancellationTokenSource? _cancellation;

    public CapturePageViewModel(
        NavigationSection section,
        CaptureService capture,
        IObservationStore? observations = null)
        : base(section)
    {
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _observations = observations;
    }

    // ------------------------------------------------------------------ состояние

    [ObservableProperty]
    private bool _isAvailable;

    /// <summary>Почему нельзя и что с этим делать — словами порта захвата.</summary>
    [ObservableProperty]
    private string _availability = "…";

    /// <summary>Чем слушаем: встроенным в Windows или сторонним драйвером.</summary>
    [ObservableProperty]
    private string? _engine;

    /// <summary>Отказ чинится перезапуском с правами — тогда показываем кнопку.</summary>
    [ObservableProperty]
    private bool _canAskForRights;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    [ObservableProperty]
    private int _seconds = 60;

    [ObservableProperty]
    private string? _selectedAdapter;

    /// <summary>Итог последнего прослушивания — числом кадров, а не одними находками.</summary>
    [ObservableProperty]
    private string? _summary;

    /// <summary>Оговорка о пустом улове. Печатается всегда, когда слушали.</summary>
    [ObservableProperty]
    private string? _caveat;

    public ObservableCollection<string> Adapters { get; } = [];

    public ObservableCollection<NeighborRow> Neighbors { get; } = [];

    public ObservableCollection<DhcpRow> Servers { get; } = [];

    public ObservableCollection<NeighborRow> HeardNeighbors { get; } = [];

    public ObservableCollection<DhcpRow> HeardServers { get; } = [];

    public bool CanStart => IsAvailable && !IsRunning;

    public bool CanStop => IsRunning;

    public static string Promise =>
        "Продукт только слушает: ни одного кадра в сеть не отправляется. "
        + "Соседство устройства объявляют сами, ответы DHCP широковещательны.";

    public static string NeighborNote =>
        "Это соседи нашего адаптера, а не всего коммутатора: услышано то, что долетело "
        + "до нас. Полную картину по портам даёт опрос оборудования по SNMP.";

    public static string DhcpNote =>
        "Продукт не берётся называть сервер посторонним: два законных сервера в одном "
        + "домене бывают, а один подставной — тоже. Различает тот, кто знает свою сеть. "
        + "Отмечается только несовпадение с тем, что известно машине.";

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        Look();

        await LoadHistoryAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Чем слушаем и можем ли вообще.
    /// </summary>
    /// <remarks>
    /// Считается при каждом открытии экрана, а не один раз на запуск: драйвер могли
    /// поставить, а продукт — перезапустить с правами, не закрывая окна.
    /// </remarks>
    private void Look()
    {
        IsAvailable = _capture.IsAvailable;
        Availability = _capture.Explain();
        Engine = IsAvailable ? _capture.DriverDescription : null;
        CanAskForRights = !IsAvailable && _capture.Availability == CaptureRefusal.NeedsElevation;

        Adapters.Clear();

        foreach (var adapter in _capture.Adapters())
        {
            Adapters.Add(adapter.DisplayName);
        }

        SelectedAdapter ??= _capture.Primary()?.DisplayName ?? Adapters.FirstOrDefault();

        StatusLine = IsAvailable
            ? $"Готов слушать. Адаптеров: {Adapters.Count.ToString(CultureInfo.InvariantCulture)}."
            : string.Empty;

        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
    }

    [RelayCommand]
    private void AskForRights() => ErrorMessage = Services.Elevation.Restart();

    [RelayCommand]
    private async Task ListenAsync()
    {
        if (IsRunning || !IsAvailable)
        {
            return;
        }

        var adapter = _capture.Adapters()
                          .FirstOrDefault(a => string.Equals(
                              a.DisplayName,
                              SelectedAdapter,
                              StringComparison.Ordinal))
                      ?? _capture.Primary();

        if (adapter is null)
        {
            ErrorMessage = "Не на чем слушать: подходящих адаптеров не видно.";

            return;
        }

        ErrorMessage = null;
        Summary = null;
        Caveat = null;
        Neighbors.Clear();
        Servers.Clear();

        IsRunning = true;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));

        var duration = TimeSpan.FromSeconds(Math.Clamp(Seconds, 5, 3600));
        StatusLine = $"Слушаю {adapter.DisplayName} — {CaptureResult.Describe(duration)}.";

        _cancellation = new CancellationTokenSource();

        try
        {
            var result = await _capture
                .ListenAsync(adapter, new CaptureOptions { Duration = duration }, _cancellation.Token)
                .ConfigureAwait(true);

            Show(result);

            await LoadHistoryAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Прослушивание остановлено.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Молчать здесь нельзя: прослушивание идёт минуту, и беда, съеденная
            // без следа, выглядит как «ничего не услышали».
            ErrorMessage = ex.Message;
            StatusLine = string.Empty;
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;

            IsRunning = false;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    [RelayCommand]
    private void Stop() => _cancellation?.Cancel();

    /// <summary>
    /// Уход со страницы прослушивание не прерывает.
    /// </summary>
    /// <remarks>
    /// Минута ожидания — повод сходить на другой экран, а не причина потерять
    /// услышанное. Отмена остаётся за кнопкой, и за ней же — осознанное решение
    /// оператора.
    /// </remarks>
    public void Dispose()
    {
        _cancellation?.Dispose();
        _cancellation = null;
    }

    /// <summary>Услышанное за один заход.</summary>
    private void Show(CaptureResult result)
    {
        var known = _capture.KnownGateways();

        foreach (var neighbor in result.Neighbors)
        {
            Neighbors.Add(new NeighborRow(
                neighbor.RemoteName ?? neighbor.RemoteChassisId ?? "без имени",
                neighbor.RemotePort ?? "—",
                neighbor.Protocol == NeighborProtocol.Lldp ? "LLDP" : "CDP",
                neighbor.RemoteDescription ?? "—",
                Moment(neighbor.ObservedUtc)));
        }

        foreach (var server in result.Dhcp.Sightings
                     .GroupBy(s => (s.ServerAddress, s.OfferedGateway), ServerKey.Instance))
        {
            var first = server.First();

            Servers.Add(new DhcpRow(
                first.ServerAddress,
                first.ServerMac ?? "—",
                first.OfferedGateway ?? "—",
                first.OfferedDns.Count > 0 ? string.Join(", ", first.OfferedDns) : "—",
                server.Count().ToString(CultureInfo.InvariantCulture),
                Moment(first.ObservedUtc),
                Stranger(first.OfferedGateway, known)));
        }

        Summary =
            $"Слушали {CaptureResult.Describe(result.Duration)} на {result.Adapter.DisplayName}. "
            + $"Кадров через фильтр: {result.FramesSeen.ToString(CultureInfo.InvariantCulture)}.";

        // Оговорка печатается всегда, а не только на пустом улове: «ничего
        // не услышали» и «ничего нет» — разные утверждения, и второе продукт
        // делать не вправе.
        Caveat =
            $"Устройство, объявляющееся раз в минуту, могло не попасть в это окно "
            + $"({CaptureResult.Describe(result.Duration)}).";

        StatusLine = string.Empty;
    }

    private async Task LoadHistoryAsync(CancellationToken cancellationToken)
    {
        if (_observations is null)
        {
            return;
        }

        try
        {
            var since = DateTimeOffset.UtcNow - HistoryWindow;
            var known = _capture.KnownGateways();

            var neighbors = await _observations.ListNeighborsAsync(since, cancellationToken)
                .ConfigureAwait(true);

            var servers = await _observations.ListDhcpAsync(since, cancellationToken).ConfigureAwait(true);

            HeardNeighbors.Clear();

            foreach (var neighbor in neighbors)
            {
                HeardNeighbors.Add(new NeighborRow(
                    neighbor.SystemName ?? neighbor.ChassisId,
                    neighbor.PortName ?? neighbor.PortId,
                    neighbor.Protocol == NeighborProtocol.Lldp ? "LLDP" : "CDP",
                    neighbor.LocalInterface,
                    $"впервые {Moment(neighbor.FirstSeenUtc)}, последний раз {Moment(neighbor.LastSeenUtc)}"));
            }

            HeardServers.Clear();

            foreach (var server in servers)
            {
                HeardServers.Add(new DhcpRow(
                    server.ServerAddress,
                    server.ServerMac ?? "—",
                    server.OfferedGateway,
                    server.OfferedDns.Count > 0 ? string.Join(", ", server.OfferedDns) : "—",
                    server.Sightings.ToString(CultureInfo.InvariantCulture),
                    $"впервые {Moment(server.FirstSeenUtc)}, последний раз {Moment(server.LastSeenUtc)}",
                    Stranger(server.OfferedGateway, known)));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // История — дополнение к услышанному сейчас. Её недоступность не повод
            // отказать в прослушивании.
            ErrorMessage = $"История услышанного не прочиталась: {ex.Message}";
        }
    }

    /// <summary>
    /// Шлюз, которого машина не знает.
    /// </summary>
    /// <remarks>
    /// Единственное утверждение о постороннем DHCP, которое продукт берётся делать
    /// сам. Всё прочее — факты без вердикта.
    /// </remarks>
    private static bool Stranger(string? gateway, IReadOnlyList<string> known) =>
        gateway is { Length: > 0 }
        && known.Count > 0
        && !known.Contains(gateway, StringComparer.OrdinalIgnoreCase);

    private static string Moment(DateTimeOffset utc) =>
        utc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Сервер и объявляемый им шлюз — вместе: смена шлюза это событие.</summary>
    private sealed class ServerKey : IEqualityComparer<(string Address, string? Gateway)>
    {
        internal static readonly ServerKey Instance = new();

        public bool Equals((string Address, string? Gateway) x, (string Address, string? Gateway) y) =>
            string.Equals(x.Address, y.Address, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Gateway, y.Gateway, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Address, string? Gateway) obj) =>
            HashCode.Combine(
                obj.Address.ToUpperInvariant(),
                obj.Gateway?.ToUpperInvariant());
    }
}
