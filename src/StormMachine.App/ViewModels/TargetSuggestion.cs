using Avalonia.Controls;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Подсказка цели из инвентаря.
/// </summary>
/// <remarks>
/// Появилась в И-24 по требованию оператора: сеть уже просканирована, устройства
/// известны — набирать адреса руками там, где их можно подставить, неправильно.
/// <para>
/// <see cref="ToString" /> возвращает голый адрес намеренно: это то, что попадает
/// в поле цели при выборе, и оно обязано разбираться <c>Target.Parse</c>. Полная
/// строка с именем и ролью — только в выпадающем списке.
/// </para>
/// </remarks>
public sealed record TargetSuggestion(string Address, string Description)
{
    public override string ToString() => Address;
}

/// <summary>Загрузка и фильтрация подсказок — одна на все страницы с полем цели.</summary>
public static class TargetSuggestions
{
    /// <summary>
    /// Фильтр выпадающего списка: совпадение ищется и по адресу, и по имени с ролью.
    /// </summary>
    /// <remarks>
    /// Штатный фильтр сравнивает только текст элемента (адрес); оператор же помнит
    /// устройство по имени. «nas» обязан найти 192.168.200.53 — NAS_BBTENNIS.
    /// </remarks>
    public static AutoCompleteFilterPredicate<object?> Filter { get; } = (search, item) =>
        string.IsNullOrEmpty(search)
        || (item is TargetSuggestion suggestion
            && suggestion.Description.Contains(search, StringComparison.OrdinalIgnoreCase));

    /// <summary>Собирает подсказки из инвентаря. Отказ инвентаря — не отказ страницы.</summary>
    public static async Task<IReadOnlyList<TargetSuggestion>> LoadAsync(
        IDeviceStore store,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(true);

            var devices = await store.ListDevicesAsync(cancellationToken).ConfigureAwait(true);

            return
            [
                .. devices
                    .OrderBy(d => IpAddressOrder.Of(d.Address))
                    .Select(d => new TargetSuggestion(
                        d.Address,
                        d.Address
                        + (d.HostName is { Length: > 0 } name ? $" · {name}" : string.Empty)
                        + (d.RoleDisplay is { Length: > 0 } role ? $" · {role}" : string.Empty))),
            ];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // Подсказки — удобство подстановки; без них поле цели работает как раньше.
            return [];
        }
    }
}
