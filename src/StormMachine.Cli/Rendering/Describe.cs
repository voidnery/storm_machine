using StormMachine.Domain.Measurements;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Единый словарь понятий для показа.
/// </summary>
/// <remarks>
/// Заведено в И-3, когда те же самые тексты понадобились второму месту — журналу
/// прогонов. Расхождение уже началось: журнал печатал тип адаптера машинным именем
/// перечисления, а факты — без единиц измерения. Одна и та же величина не должна
/// выглядеть по-разному в зависимости от того, смотрят на неё сейчас или через неделю.
/// </remarks>
internal static class Describe
{
    public static string AdapterKind(AdapterKind kind) => kind switch
    {
        Domain.Measurements.AdapterKind.Physical => "физический",
        Domain.Measurements.AdapterKind.Wireless => "беспроводной",
        Domain.Measurements.AdapterKind.Virtual => "виртуальный коммутатор",
        Domain.Measurements.AdapterKind.Vpn => "VPN",
        Domain.Measurements.AdapterKind.Tunnel => "туннель",
        Domain.Measurements.AdapterKind.Loopback => "loopback",
        _ => "не определён",
    };

    public static string SampleStatus(SampleStatus status) => status switch
    {
        Domain.Measurements.SampleStatus.Success => "успех",
        Domain.Measurements.SampleStatus.Timeout => "таймаут",
        Domain.Measurements.SampleStatus.Unreachable => "недоступен",
        Domain.Measurements.SampleStatus.TtlExpired => "истёк TTL",
        Domain.Measurements.SampleStatus.Rejected => "отказ",
        _ => "ошибка",
    };

    public static string PhaseName(string? label) => label switch
    {
        "dns" => "DNS",
        "connect" => "TCP",
        "tls" => "TLS",
        "ttfb" => "первый байт",
        "download" => "скачивание",
        null => "—",
        _ => label,
    };

    /// <summary>
    /// Единица измерения рядом со значением факта.
    /// </summary>
    /// <remarks>
    /// Число без единицы бесполезно: «7.497» не даёт понять, много это или мало.
    /// Единица берётся из самого факта — проба знает, что именно она измерила.
    /// </remarks>
    public static string UnitSuffix(ProbeFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return UnitSuffix(fact.Unit);
    }

    public static string UnitSuffix(MeasurementUnit? unit) => unit switch
    {
        MeasurementUnit.Milliseconds => " мс",
        MeasurementUnit.MegabitsPerSecond => " Мбит/с",
        MeasurementUnit.Percent => " %",
        MeasurementUnit.Bytes => " байт",
        _ => string.Empty,
    };

    /// <summary>Показывает факты, сгруппированные по категориям.</summary>
    public static void WriteFacts(IReadOnlyList<ProbeFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        if (facts.Count == 0)
        {
            return;
        }

        foreach (var category in facts.Select(f => f.Category).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine();
            Console.WriteLine($"  [{category}]");

            foreach (var fact in facts.Where(f => string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase)))
            {
                var marker = fact.IsWarning ? "!" : " ";
                Console.WriteLine($"  {marker} {fact.Name,-24} {fact.Value}{UnitSuffix(fact)}");
            }
        }
    }
}
