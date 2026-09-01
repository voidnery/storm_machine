namespace StormMachine.Domain.Measurements;

/// <summary>
/// Как продукт называет единицы измерения.
/// </summary>
/// <remarks>
/// Словарь один на консоль и окно — по той же причине, что и
/// <see cref="StormMachine.Domain.Results.VerdictWording"/>: расхождение стоит доверия.
/// К И-24 оно уже было. Консоль печатала «Средняя скорость 94.3 Мбит/с», окно —
/// «Средняя скорость 94.3», а таблица рядов в окне подписывалась «Времена
/// в миллисекундах» независимо от того, что проба меряла. Для пробы скорости это
/// не отсутствие подписи, а неверная подпись: мегабиты в секунду объявлялись
/// миллисекундами.
/// </remarks>
public static class Units
{
    /// <summary>Единица с ведущим пробелом — приписать к числу.</summary>
    public static string Suffix(MeasurementUnit? unit) => unit switch
    {
        MeasurementUnit.Milliseconds => " мс",
        MeasurementUnit.MegabitsPerSecond => " Мбит/с",
        MeasurementUnit.Percent => " %",
        MeasurementUnit.Bytes => " байт",
        _ => string.Empty,
    };

    /// <summary>Единица сама по себе — для заголовка колонки или подписи поля.</summary>
    public static string Short(MeasurementUnit unit) => Suffix(unit).TrimStart();

    /// <summary>
    /// Как называется сама измеряемая величина: «время», «скорость».
    /// </summary>
    /// <remarks>
    /// Нужно там, где подпись объясняет целую таблицу: «Времена в миллисекундах»
    /// у пробы скорости было прямой неправдой.
    /// </remarks>
    public static string Quantity(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Milliseconds => "Времена",
        MeasurementUnit.MegabitsPerSecond => "Скорости",
        MeasurementUnit.Percent => "Доли",
        MeasurementUnit.Bytes => "Размеры",
        _ => "Значения",
    };

    /// <summary>Подпись к таблице значений: «Времена в миллисекундах».</summary>
    public static string TableCaption(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Milliseconds => "Времена в миллисекундах.",
        MeasurementUnit.MegabitsPerSecond => "Скорости в мегабитах в секунду.",
        MeasurementUnit.Percent => "Значения в процентах.",
        MeasurementUnit.Bytes => "Размеры в байтах.",
        _ => string.Empty,
    };

    /// <summary>Число с единицей: «94.3 Мбит/с».</summary>
    public static string Format(double value, MeasurementUnit? unit, string format = "0.###") =>
        value.ToString(format, System.Globalization.CultureInfo.InvariantCulture) + Suffix(unit);

    /// <summary>
    /// Измеренное значение с единицей и разумной точностью.
    /// </summary>
    /// <remarks>
    /// Единственное место, где решается, как показать измеренное число. До И-24+ таких
    /// мест было шесть, и одна и та же медиана показывалась тремя способами: «12.345 мс»,
    /// «12.345» и «12 мс».
    /// <para>
    /// Знаки после запятой уменьшаются с ростом значения: «244.16 мс» сообщает точность,
    /// которой у сетевого измерения нет. Ниже миллисекунды знаки, наоборот, нужны —
    /// там живёт собственный порог часов, и «0.3 мс» вместо «0.317 мс» стёрло бы
    /// разницу между измерением и шумом измерителя.
    /// </para>
    /// </remarks>
    public static string Measured(double value, MeasurementUnit unit) =>
        Number(value, unit) + Suffix(unit);

    /// <summary>
    /// То же число без единицы — для колонки, где единица названа подписью таблицы.
    /// </summary>
    /// <remarks>
    /// Единица у каждого из сорока чисел превращает таблицу в частокол букв;
    /// в подписи она читается один раз и относится ко всем.
    /// </remarks>
    public static string Number(double value, MeasurementUnit unit)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;

        return unit switch
        {
            MeasurementUnit.Milliseconds => value.ToString(TimeFormat(value), culture),
            MeasurementUnit.MegabitsPerSecond => value.ToString("0.#", culture),
            MeasurementUnit.Percent => value.ToString("0.#", culture),
            MeasurementUnit.Bytes => value.ToString("0", culture),
            _ => value.ToString("0.###", culture),
        };
    }

    /// <summary>Время с единицей: «0.317 мс», «12.3 мс», «244 мс».</summary>
    public static string Milliseconds(double value) =>
        Measured(value, MeasurementUnit.Milliseconds);

    private static string TimeFormat(double value) => Math.Abs(value) switch
    {
        < 1 => "0.000",
        < 100 => "0.0",
        _ => "0",
    };
}
