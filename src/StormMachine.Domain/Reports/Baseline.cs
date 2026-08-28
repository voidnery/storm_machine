using System.Globalization;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.Reports;

/// <summary>Одна метрика эталона.</summary>
/// <param name="Name">Имя метрики: <c>p50</c>, <c>p95</c>, <c>loss</c>.</param>
/// <param name="Value">Значение на момент фиксации.</param>
/// <param name="HigherIsBetter">
/// Куда «лучше». Задаётся при фиксации, а не выводится при сравнении: направление
/// зависит от того, что мерили, и через год восстановить это по одному имени метрики
/// будет уже нечем.
/// </param>
public sealed record BaselineMetric(string Name, double Value, bool HigherIsBetter);

/// <summary>
/// Эталон: снимок измерения, с которым сравнивают то, что происходит сейчас.
/// </summary>
/// <remarks>
/// Смысл эталона — ответить на вопрос «стало лучше или хуже», а не «сколько сейчас».
/// Поэтому вместе с числами он хранит <see cref="Context"/> — условия, при которых
/// снят. Сравнение измерения через кабель с эталоном, снятым по Wi-Fi, даёт красивую
/// цифру улучшения, которой не было: изменился не канал, а способ смотреть на него.
/// <para>
/// Продукт такое сравнение не запрещает — бывает, что оно и нужно, — но обязан
/// назвать расхождение условий рядом с числами.
/// </para>
/// </remarks>
public sealed record Baseline
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Что измеряли: имя пробы или ключ сценария.</summary>
    public required string Subject { get; init; }

    public required Target Target { get; init; }

    /// <summary>Условия, в которых снят эталон. Без них сравнение недостоверно.</summary>
    public required MeasurementContext Context { get; init; }

    public required MeasurementUnit Unit { get; init; }

    public required IReadOnlyList<BaselineMetric> Metrics { get; init; }

    /// <summary>Прогон, с которого снят эталон, — чтобы можно было открыть исходное измерение.</summary>
    public Guid? RunId { get; init; }

    public required DateTimeOffset CapturedUtc { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} — {Description}";

    /// <summary>
    /// Годится ли метрика для эталона.
    /// </summary>
    /// <remarks>
    /// Счётчики отправленного и полученного — не свойство сети, а настройка замера:
    /// «отправлено 8» против «отправлено 10» означает, что оператор попросил разное
    /// число проб, и объявлять это ухудшением было бы прямым враньём. Всё, что они
    /// сообщают о сети, уже есть в потерях.
    /// <para>
    /// Разбивка по рядам тоже не идёт: <c>p95@ttfb</c> осмысленно ровно для той пробы,
    /// что его дала, а эталон сравнивают и с соседними измерениями.
    /// </para>
    /// </remarks>
    public static bool IsComparable(string metric)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        if (metric.Contains(SeriesSeparator, StringComparison.Ordinal))
        {
            return false;
        }

        return !metric.Equals("sent", StringComparison.OrdinalIgnoreCase)
               && !metric.Equals("received", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Разделитель ряда в имени метрики — тот же, что у метрик проб.</summary>
    private const char SeriesSeparator = '@';

    /// <summary>
    /// Куда «лучше» для метрики с таким именем и такой единицей.
    /// </summary>
    /// <remarks>
    /// Имя решает раньше единицы: потери и доля измеряются в процентах одинаково,
    /// но у потерь меньше — лучше, а у доступности наоборот. Спрашивать об этом
    /// оператора при каждой фиксации значило бы задавать вопрос, ответ на который
    /// продукт знает сам.
    /// </remarks>
    public static bool HigherIsBetterFor(string metric, MeasurementUnit unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metric);

        var name = ProbeMetricBase(metric);

        if (name.Equals("loss", StringComparison.OrdinalIgnoreCase)
            || name.Equals("jitter", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pdv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (name.Equals("mos", StringComparison.OrdinalIgnoreCase)
            || name.Equals("uptime", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return unit switch
        {
            MeasurementUnit.MegabitsPerSecond => true,
            MeasurementUnit.Milliseconds => false,
            MeasurementUnit.Percent => false,
            _ => false,
        };
    }

    /// <summary>Отбрасывает уточнение ряда: <c>p95@ttfb</c> — это всё ещё <c>p95</c>.</summary>
    private static string ProbeMetricBase(string metric)
    {
        var at = metric.IndexOf('@', StringComparison.Ordinal);

        return at < 0 ? metric : metric[..at];
    }

    public string Describe() =>
        $"{Subject} → {Target.DisplayName}, снят {CapturedUtc.LocalDateTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}";
}

/// <summary>Фильтр для списка эталонов.</summary>
public sealed record BaselineQuery
{
    public string? Subject { get; init; }

    public string? Search { get; init; }

    public int Limit { get; init; } = 200;
}
