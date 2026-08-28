using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;

namespace StormMachine.App.ViewModels;

/// <summary>Строка инвентаря.</summary>
public sealed record InventoryRow(
    string Identity,
    string Address,
    string? ExtraAddresses,
    string MacAddress,
    string HostName,
    string Vendor,
    string LastSeen,
    bool IsGateway,
    bool IsOnline,
    bool IsNamedByOperator)
{
    public static InventoryRow From(Device device)
    {
        ArgumentNullException.ThrowIfNull(device);

        var extra = device.ExtraAddresses;

        return new InventoryRow(
            device.Identity,
            device.Address,

            // Маршрутизаторы и гипервизоры занимают несколько адресов одним
            // интерфейсом. Без этой строки инвентарь молча терял бы часть найденного.
            extra.Count > 0 ? "ещё: " + string.Join(", ", extra) : null,
            device.MacAddress ?? "—",
            device.HostName ?? "—",

            // Не всегда вендор: у виртуального адреса VRRP реестр называет IANA,
            // у локального адреса производителя нет вовсе. Решает домен.
            device.VendorDisplay,
            device.LastSeenUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture),
            device.Role == "шлюз",
            device.IsOnline,
            device.Evidence.Any(e => e.Source == EvidenceSource.Manual && e.Kind == EvidenceKind.HostName));
    }
}

/// <summary>
/// Экран инвентаря: что известно о сети и что в ней изменилось.
/// </summary>
/// <remarks>
/// Список устройств отвечает на вопрос «что в сети», различия между сканированиями —
/// на вопрос «что изменилось», ради которого инвентарь и ведут: список сам по себе
/// про вчерашний день ничего не говорит.
/// <para>
/// Переименование здесь — не правка записи, а новое свидетельство с наивысшим весом.
/// Поэтому оно переживает пересканирование, а исходное наблюдение остаётся в снимке.
/// </para>
/// </remarks>
public sealed partial class DevicesPageViewModel(
    NavigationSection section,
    IDeviceStore store) : PageViewModel(section)
{
    private readonly IDeviceStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private IReadOnlyList<Device> _devices = [];

    public ObservableCollection<InventoryRow> Rows { get; } = [];

    public ObservableCollection<DiscoveryScan> Scans { get; } = [];

    public ObservableCollection<string> Differences { get; } = [];

    [ObservableProperty]
    private string _summary = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    /// <summary>Имя, которое оператор присваивает выбранному устройству.</summary>
    [ObservableProperty]
    private string _newName = string.Empty;

    [ObservableProperty]
    private InventoryRow? _selected;

    [ObservableProperty]
    private DiscoveryScan? _compareFrom;

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync().ConfigureAwait(true);

    /// <summary>
    /// Присваивает выбранному устройству имя.
    /// </summary>
    [RelayCommand]
    private async Task RenameAsync()
    {
        ErrorMessage = null;

        if (Selected is not { } row)
        {
            ErrorMessage = "Выберите устройство в списке.";
            return;
        }

        if (string.IsNullOrWhiteSpace(NewName))
        {
            ErrorMessage = "Укажите имя.";
            return;
        }

        try
        {
            await _store.PinAsync(
                row.Identity,
                Evidence.Of(EvidenceSource.Manual, EvidenceKind.HostName, NewName.Trim(), DateTimeOffset.UtcNow),
                CancellationToken.None).ConfigureAwait(true);

            StatusLine = $"Устройство {row.Address} названо «{NewName.Trim()}». "
                         + "Правка переживёт пересканирование: она сильнее любого наблюдения.";

            NewName = string.Empty;

            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не сохранено: {ex.Message}";
        }
    }

    /// <summary>Сравнивает выбранное сканирование с последним.</summary>
    [RelayCommand]
    private async Task CompareAsync()
    {
        ErrorMessage = null;
        Differences.Clear();

        if (CompareFrom is not { } from)
        {
            ErrorMessage = "Выберите сканирование для сравнения.";
            return;
        }

        try
        {
            var before = await _store.GetScanAsync(from.Id).ConfigureAwait(true);
            var latest = Scans.Count > 0 ? await _store.GetScanAsync(Scans[0].Id).ConfigureAwait(true) : null;

            if (before is null || latest is null)
            {
                ErrorMessage = "Сканирование не найдено.";
                return;
            }

            var diff = ScanDiff.Between(before.Devices, latest.Devices);

            if (diff.IsEmpty)
            {
                Differences.Add("Различий нет: сеть та же, что и была.");
                return;
            }

            foreach (var device in diff.Appeared)
            {
                Differences.Add($"+  {device.Address}  {device.DisplayName}");
            }

            foreach (var device in diff.Disappeared)
            {
                Differences.Add($"−  {device.Address}  {device.DisplayName}");
            }

            foreach (var (device, changes) in diff.Changed)
            {
                foreach (var change in changes)
                {
                    Differences.Add($"~  {device.Address}  {change.Field}: {change.Before ?? "—"} → {change.After ?? "—"}");
                }
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Сравнение не выполнено: {ex.Message}";
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(true);

            _devices = await _store.ListDevicesAsync(cancellationToken).ConfigureAwait(true);

            Rows.Clear();

            foreach (var device in _devices.OrderBy(d => IpAddressOrder.Of(d.Address)))
            {
                Rows.Add(InventoryRow.From(device));
            }

            var scans = await _store.ListScansAsync(20, cancellationToken).ConfigureAwait(true);

            Scans.Clear();

            foreach (var scan in scans)
            {
                Scans.Add(scan);
            }

            var addresses = _devices.Sum(d => Math.Max(1, d.Addresses.Count));

            Summary = Rows.Count == 0
                ? "Инвентарь пуст. Откройте «Обнаружение» и просканируйте свою сеть."
                : $"Устройств {Rows.Count} на {addresses} адресах, отвечали в последнем сканировании "
                  + $"{Rows.Count(r => r.IsOnline)}. Сканирований в истории: {Scans.Count}."
                  + (addresses > Rows.Count
                      ? " Узел с несколькими адресами — это один узел: опознаётся он по MAC."
                      : string.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Инвентарь недоступен: {ex.Message}";
        }
    }

}
