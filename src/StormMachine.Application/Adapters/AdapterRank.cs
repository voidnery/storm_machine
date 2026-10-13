using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;

namespace StormMachine.Application.Adapters;

/// <summary>
/// Насколько адаптер «настоящий» — тот ли это, про который думает оператор.
/// </summary>
/// <remarks>
/// Список адаптеров Windows шире, чем список карт: в нём живут служебные фильтры
/// NDIS, туннели и виртуальные коммутаторы. Часть из них носит MAC настоящей карты
/// и появляется только временами — например, пока загружен драйвер захвата.
/// Поймано боевым прогоном: продукт показал оператору «Ethernet 4-WFP Native MAC
/// Layer LightWeight Filter-0000» вместо имени его карты.
/// <para>
/// Порядок простой и проверяемый: сначала то, у чего есть адрес и маршрут, —
/// через это и ходит трафик; потом то, у чего есть хотя бы адрес; потом физическое.
/// Служебному фильтру без адреса достаётся последнее место, и выбирается он только
/// тогда, когда больше нечего выбрать.
/// </para>
/// </remarks>
public static class AdapterRank
{
    /// <summary>Чем меньше число, тем вероятнее это та карта, которую имеет в виду человек.</summary>
    public static int Of(NetworkAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);

        var real = adapter.Kind is AdapterKind.Physical or AdapterKind.Wireless;
        var addressed = !string.IsNullOrWhiteSpace(adapter.IPv4Address);
        var routed = addressed && adapter.Gateways.Count > 0;

        return (routed, addressed, real) switch
        {
            (true, _, true) => 0,
            (true, _, false) => 1,
            (_, true, true) => 2,
            (_, true, false) => 3,
            (_, _, true) => 4,
            _ => 5,
        };
    }

    /// <summary>Те же адаптеры, начиная с самых настоящих. Порядок внутри ступени сохраняется.</summary>
    public static IEnumerable<NetworkAdapter> MostRealFirst(IEnumerable<NetworkAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        return adapters.OrderBy(Of);
    }
}
