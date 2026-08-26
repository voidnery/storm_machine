namespace StormMachine.Domain.Measurements;

/// <summary>
/// Структурный факт, установленный пробой: то, что не является числом во временном ряду.
/// </summary>
/// <remarks>
/// Появился в итерации И-2, когда выяснилось, что скалярной серии хватает только трём
/// пробам из шести. Записи DNS, цепочка сертификатов TLS, код ответа и заголовки HTTP —
/// это факты, а не измерения: у них нет порядкового номера, они не образуют ряд
/// и по ним не считают перцентили.
/// <para>
/// Разделение на два канала — сэмплы и факты — не украшение, а условие того, чтобы
/// <see cref="Sample"/> остался лёгкой структурой для горячего пути. Попытка втащить
/// словарь в каждый сэмпл убила бы требование по аллокациям на сериях в десятки тысяч проб.
/// </para>
/// </remarks>
public sealed record ProbeFact
{
    /// <summary>Группа фактов: <c>dns</c>, <c>tls</c>, <c>http</c>, <c>path</c>.</summary>
    public required string Category { get; init; }

    public required string Name { get; init; }

    /// <summary>Значение в виде текста — годится и для показа, и для сериализации.</summary>
    public required string Value { get; init; }

    /// <summary>Числовое значение, если факт измерим (срок до истечения сертификата, размер тела).</summary>
    public double? Numeric { get; init; }

    public MeasurementUnit? Unit { get; init; }

    /// <summary>Факт указывает на проблему: истекающий сертификат, устаревший протокол.</summary>
    public bool IsWarning { get; init; }

    public static ProbeFact Text(string category, string name, string value) => new()
    {
        Category = category,
        Name = name,
        Value = value,
    };

    public static ProbeFact Number(string category, string name, double value, MeasurementUnit unit) => new()
    {
        Category = category,
        Name = name,
        Value = value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
        Numeric = value,
        Unit = unit,
    };

    public static ProbeFact Warning(string category, string name, string value) => new()
    {
        Category = category,
        Name = name,
        Value = value,
        IsWarning = true,
    };
}
