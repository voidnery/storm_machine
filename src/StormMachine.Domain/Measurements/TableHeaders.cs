namespace StormMachine.Domain.Measurements;

/// <summary>
/// Шапки таблиц измеренного — одни на окно, консоль и отчёт.
/// </summary>
/// <remarks>
/// Таблиц с одними и теми же колонками в продукте шесть: прогонщик пробы, журнал,
/// анализ пути, инспекторы, консоль и PDF. Пока единица стояла подписью под таблицей,
/// они расходились незаметно; с единицей в шапке расхождение стало бы видно сразу —
/// поэтому шапка объявляется здесь, а не переписывается в каждой разметке.
///
/// Готовые свойства — для таблиц, у которых единица известна заранее: время оборота
/// всегда в миллисекундах, потери всегда в процентах. Там, где единицу приносит проба
/// (скорость — мегабиты), заголовок собирается на месте через <see cref="Units.Column" />.
/// </remarks>
public static class TableHeaders
{
    public static string Min { get; } = Units.Column("мин", MeasurementUnit.Milliseconds);

    public static string Median { get; } = Units.Column("медиана", MeasurementUnit.Milliseconds);

    public static string Max { get; } = Units.Column("макс", MeasurementUnit.Milliseconds);

    public static string Jitter { get; } = Units.Column("джиттер", MeasurementUnit.Milliseconds);

    public static string Loss { get; } = Units.Column("потери", MeasurementUnit.Percent);

    /// <summary>Число проб — счёт, а не величина: единицы у него нет.</summary>
    public static string Sent => "проб";
}
