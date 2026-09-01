using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;

namespace StormMachine.App.ViewModels;

/// <summary>Строка таблицы найденных устройств.</summary>
public sealed record DeviceRow(
    string Address,
    string MacAddress,
    string HostName,
    string Vendor,
    string Found,
    bool IsGateway,
    bool IsOnline)
{
    public static DeviceRow From(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var sources = device.Evidence
            .Where(e => e.Kind == EvidenceKind.Alive)
            .Select(e => Describe(e.Source))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var ports = device.OpenPorts.Count > 0
            ? " · порт " + string.Join(", ", device.OpenPorts)
            : string.Empty;

        return new DeviceRow(
            device.Address,
            device.MacAddress ?? "—",
            device.HostName ?? "—",

            // Не всегда вендор: у виртуального адреса VRRP реестр называет IANA,
            // у локального адреса производителя нет вовсе. Решает домен.
            device.VendorDisplay,
            sources.Count == 0 ? "не отвечает" : string.Join(", ", sources) + ports,
            device.Role == "шлюз",
            device.IsOnline);
    }

    private static string Describe(EvidenceSource source) => source switch
    {
        EvidenceSource.IcmpEcho => "ICMP",
        EvidenceSource.TcpConnect => "TCP",
        EvidenceSource.ArpTable => "ARP",
        EvidenceSource.ArpRequest => "ARP-запрос",
        EvidenceSource.Netbios => "NetBIOS",
        EvidenceSource.Mdns => "mDNS",
        EvidenceSource.Ssdp => "SSDP",
        _ => source.ToString(),
    };
}

/// <summary>
/// Экран обнаружения: сканирование подсети и его результат.
/// </summary>
/// <remarks>
/// Сканирование — активное действие по чужой сети, и экран устроен вокруг этого.
/// Объём показывается <b>до</b> запуска, темп ограничен, сделанное попадает в журнал
/// аудита. Требование раздела «Этика» в README.
/// </remarks>
public sealed partial class DiscoveryPageViewModel : PageViewModel, IDisposable
{
    private const double RefreshHz = 5;

    private readonly IDiscoveryService _discovery;
    private readonly IDeviceStore _store;
    private readonly INetworkEnvironment _environment;
    private readonly IOuiCatalog _oui;
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _cancellation;
    private DiscoveryProgress? _pending;
    private AddressRange? _range;

    public DiscoveryPageViewModel(
        NavigationSection section,
        IDiscoveryService discovery,
        IDeviceStore store,
        INetworkEnvironment environment,
        IOuiCatalog oui)
        : base(section)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _oui = oui ?? throw new ArgumentNullException(nameof(oui));

        // Ход сканирования приходит из десятков потоков сразу; в интерфейс он попадает
        // по таймеру. Иначе диспетчер захлебнётся на двухстах пятидесяти адресах.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.0 / RefreshHz) };
        _timer.Tick += (_, _) => PumpProgress();
    }

    // ------------------------------------------------------------------ параметры

    [ObservableProperty]
    private string _rangeText = string.Empty;

    [ObservableProperty]
    private int _parallelism = 64;

    [ObservableProperty]
    private int _timeoutMs = 700;

    [ObservableProperty]
    private bool _probeCommonPorts = true;

    [ObservableProperty]
    private bool _resolveNames = true;

    // ------------------------------------------------------------------ состояние

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusLine = "Укажите диапазон и запустите сканирование.";

    [ObservableProperty]
    private string _rangeSummary = string.Empty;

    [ObservableProperty]
    private string _interfaceInfo = string.Empty;

    [ObservableProperty]
    private string _catalogInfo = string.Empty;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string _progressText = string.Empty;

    // Итог скана: числа отдельно, объяснения отдельно.

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _foundText = "—";

    [ObservableProperty]
    private string _respondedText = "—";

    [ObservableProperty]
    private string _withMacText = "—";

    /// <summary>Устройства, найденные только по ARP: обычный ping-sweep их не видит.</summary>
    [ObservableProperty]
    private string? _arpNote;

    /// <summary>Виртуальные адреса среди найденного — оговорка о том, что это не сеть.</summary>
    [ObservableProperty]
    private string? _virtualNote;

    public ObservableCollection<DeviceRow> Devices { get; } = [];

    public bool CanStart => !IsRunning;

    public bool CanStop => IsRunning;

    public override Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(RangeText))
        {
            RangeText = DefaultRange();
        }

        UpdateRange();

        var adapter = _environment.GetPrimaryAdapter();
        InterfaceInfo = $"{adapter?.Name ?? "интерфейс не определён"}"
                        + (adapter?.IPv4Address is { } ip ? $", {ip}" : string.Empty);

        CatalogInfo = _oui.Count > 0
            ? $"вендоры: {_oui.Count.ToString(CultureInfo.InvariantCulture)} записей реестра IEEE"
            : "вендоры: база не загрузилась";

        return Task.CompletedTask;
    }

    public override void Deactivate() => _timer.Stop();

    partial void OnRangeTextChanged(string value)
    {
        _ = value;
        UpdateRange();
    }

    // ------------------------------------------------------------------ команды

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning)
        {
            return;
        }

        ErrorMessage = null;
        UpdateRange();

        if (_range is not { } range)
        {
            return;
        }

        Devices.Clear();
        HasResult = false;
        ArpNote = null;
        VirtualNote = null;
        ProgressPercent = 0;
        ProgressText = string.Empty;

        var request = new DiscoveryRequest
        {
            Range = range,
            Parallelism = Math.Max(1, Parallelism),
            TimeoutMs = Math.Max(50, TimeoutMs),
            ProbeCommonPorts = ProbeCommonPorts,
            ResolveNames = ResolveNames,
        };

        IsRunning = true;
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));

        StatusLine = $"Опрашиваю {range.Count.ToString(CultureInfo.InvariantCulture)} адресов…";
        _timer.Start();

        _cancellation = new CancellationTokenSource();

        try
        {
            await _store.InitializeAsync(_cancellation.Token).ConfigureAwait(true);

            // Запись в журнал аудита делается ДО сканирования: активное действие должно
            // остаться в журнале, даже если оно прервётся или упадёт.
            await _store.RecordAsync(
                new AuditEntry
                {
                    Id = Guid.NewGuid(),
                    AtUtc = DateTimeOffset.UtcNow,
                    Action = "discovery",
                    Target = range.Text,
                    Operator = Environment.UserName,
                    Details = $"интерфейс {InterfaceInfo}, адресов {range.Count.ToString(CultureInfo.InvariantCulture)}, "
                              + $"одновременно {request.Parallelism.ToString(CultureInfo.InvariantCulture)}",
                },
                _cancellation.Token).ConfigureAwait(true);

            var scan = await Task.Run(
                () => _discovery.ScanAsync(request, p => _pending = p, _cancellation.Token),
                _cancellation.Token).ConfigureAwait(true);

            Show(scan);

            await _store.SaveScanAsync(scan, CancellationToken.None).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusLine = "Сканирование остановлено.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Сканирование не выполнено: {ex.Message}";
            StatusLine = "Сканирование завершилось ошибкой.";
        }
        finally
        {
            _timer.Stop();
            _cancellation?.Dispose();
            _cancellation = null;

            IsRunning = false;
            OnPropertyChanged(nameof(CanStart));
            OnPropertyChanged(nameof(CanStop));
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _cancellation?.Cancel();
        StatusLine = "Останавливаю — найденное будет сохранено.";
    }

    [RelayCommand]
    private void UseOwnSubnet() => RangeText = DefaultRange();

    /// <summary>
    /// Останавливает сканирование при закрытии приложения.
    /// </summary>
    /// <remarks>
    /// Страница живёт дольше одного сканирования и владеет его отменой, поэтому обязана
    /// её освободить. Само сканирование при этом прерывается корректно: найденное
    /// уже записано, а незавершённый проход просто не сохранится.
    /// </remarks>
    public void Dispose()
    {
        _timer.Stop();
        _cancellation?.Cancel();
        _cancellation?.Dispose();
        _cancellation = null;
    }

    // ------------------------------------------------------------------ внутреннее

    private void Show(DiscoveryScan scan)
    {
        Devices.Clear();

        foreach (var device in scan.Devices)
        {
            Devices.Add(DeviceRow.From(device));
        }

        ProgressPercent = 100;
        ProgressText = $"опрошено {scan.Probed} из {scan.Probed}";

        StatusLine = scan.WasCancelled
            ? "Сканирование прервано. Ниже — то, что успели найти."
            : $"Готово за {scan.Duration?.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) ?? "?"} с.";

        var arpOnly = scan.Devices.Count(d => d.Evidence.Any(e =>
            e.Kind == EvidenceKind.Alive
            && e.Source is EvidenceSource.ArpTable or EvidenceSource.ArpRequest));

        var virtualAddresses = scan.Devices.Any(d => MacAddresses.DescribeVirtual(d.MacAddress) is not null);

        // Числа плитками, объяснения — своими формами: в одной строке, чтобы узнать
        // одно число, приходилось прочитать всё, а объяснение про ARP терялось в хвосте.
        FoundText = scan.Devices.Count.ToString(CultureInfo.InvariantCulture);
        RespondedText = scan.Responded.ToString(CultureInfo.InvariantCulture);
        WithMacText = scan.WithMac.ToString(CultureInfo.InvariantCulture);
        HasResult = true;

        ArpNote = arpOnly > 0
            ? $"Из найденных {arpOnly} отозвались только на ARP: они молчат на ICMP "
              + "и на проверяемые порты, но на втором уровне отвечают. Обычный "
              + "ping-sweep их не находит."
            : null;

        VirtualNote = virtualAddresses ? MacAddresses.VirtualExplanation : null;
    }

    private void PumpProgress()
    {
        if (_pending is not { } progress)
        {
            return;
        }

        _pending = null;

        ProgressPercent = progress.Percent;
        ProgressText = $"опрошено {progress.Probed} из {progress.Total}, найдено {progress.Found}";
    }

    private void UpdateRange()
    {
        if (string.IsNullOrWhiteSpace(RangeText))
        {
            _range = null;
            RangeSummary = string.Empty;
            return;
        }

        try
        {
            _range = AddressRange.Parse(RangeText.Trim());

            // Объём показывается до запуска, а не после: оператор должен понимать,
            // что именно он собирается сделать с чужой сетью.
            RangeSummary = $"{_range.Count.ToString(CultureInfo.InvariantCulture)} адресов: "
                           + $"{_range.First} … {_range.Last}";

            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            _range = null;
            RangeSummary = string.Empty;
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>
    /// Подсеть текущего интерфейса.
    /// </summary>
    /// <remarks>
    /// «Своя сеть» — единственное разумное значение по умолчанию: инструмент открывают,
    /// чтобы увидеть сеть, в которой стоит компьютер, а не чтобы вводить маску.
    /// </remarks>
    private string DefaultRange()
    {
        var adapter = _environment.GetPrimaryAdapter();

        if (adapter?.IPv4Address is not { } address || adapter.PrefixLength <= 0)
        {
            return string.Empty;
        }

        try
        {
            return AddressRange.FromInterface(IPAddress.Parse(address), adapter.PrefixLength).Text;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            return string.Empty;
        }
    }
}
