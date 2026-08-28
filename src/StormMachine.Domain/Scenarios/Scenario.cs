using System.Globalization;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.Scenarios;

/// <summary>Как сравнивать метрику с порогом.</summary>
public enum Comparison
{
    LessThan,

    AtMost,

    GreaterThan,

    AtLeast,
}

/// <summary>
/// Порог: какая метрика, с чем сравнивается и что означает нарушение.
/// </summary>
/// <remarks>
/// Пороги — конфигурация сценария, а не логика пробы (принцип 4 анализа §8.2). Один
/// и тот же прогон можно переоценить другими порогами, не измеряя заново, — и именно
/// поэтому вердикт отделён от измерения с самого начала.
/// </remarks>
public sealed record Threshold
{
    public required string Metric { get; init; }

    public required Comparison Comparison { get; init; }

    public required double Value { get; init; }

    /// <summary>Чем считать нарушение: предупреждением или отказом.</summary>
    public VerdictLevel Level { get; init; } = VerdictLevel.Fail;

    public bool IsSatisfiedBy(double actual) => Comparison switch
    {
        Comparison.LessThan => actual < Value,
        Comparison.AtMost => actual <= Value,
        Comparison.GreaterThan => actual > Value,
        _ => actual >= Value,
    };

    public string Describe() =>
        $"{Metric} {Sign()} {Value.ToString("0.###", CultureInfo.InvariantCulture)}";

    /// <summary>
    /// Тот же порог, но строже на заданный запас.
    /// </summary>
    /// <remarks>
    /// Так выражается гистерезис снятия алерта: подняли на «p95 &lt; 100», снимаем
    /// на «p95 &lt; 80». Запас всегда сужает допустимое — в какую сторону, знает
    /// сам порог по своему знаку, и спрашивать об этом вызывающего не нужно.
    /// </remarks>
    public Threshold Tighten(double margin)
    {
        var shift = Math.Abs(margin);

        return this with
        {
            Value = Comparison is Comparison.LessThan or Comparison.AtMost ? Value - shift : Value + shift,
        };
    }

    private string Sign() => Comparison switch
    {
        Comparison.LessThan => "<",
        Comparison.AtMost => "≤",
        Comparison.GreaterThan => ">",
        _ => "≥",
    };

    /// <summary>Разбирает запись вида <c>p95 &lt; 50</c> или <c>Осталось дней >= 14</c>.</summary>
    public static Threshold Parse(string text, VerdictLevel level = VerdictLevel.Fail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        // Знаки ищутся от длинных к коротким: иначе «<=» распалось бы на «<» и «=».
        (string Token, Comparison Comparison)[] signs =
        [
            ("<=", Comparison.AtMost),
            ("≤", Comparison.AtMost),
            (">=", Comparison.AtLeast),
            ("≥", Comparison.AtLeast),
            ("<", Comparison.LessThan),
            (">", Comparison.GreaterThan),
        ];

        foreach (var (token, comparison) in signs)
        {
            var at = text.IndexOf(token, StringComparison.Ordinal);

            if (at <= 0)
            {
                continue;
            }

            var metric = text[..at].Trim();
            var tail = text[(at + token.Length)..].Trim();

            if (metric.Length > 0
                && double.TryParse(tail, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return new Threshold
                {
                    Metric = metric,
                    Comparison = comparison,
                    Value = value,
                    Level = level,
                };
            }
        }

        throw new FormatException(
            $"Порог «{text}» не разобран. Ожидается вид «p95 < 50» или «Осталось дней >= 14».");
    }
}

/// <summary>Один шаг сценария.</summary>
public sealed record ScenarioStep
{
    /// <summary>Человекочитаемое имя шага: «Разрешение имени», «Соединение», «TLS».</summary>
    public required string Name { get; init; }

    public required string ProbeName { get; init; }

    public required Target Target { get; init; }

    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<Threshold> Thresholds { get; init; } = [];

    /// <summary>
    /// Метрика, представляющая длительность фазы в разбивке.
    /// </summary>
    /// <remarks>
    /// Не время шага. Шаг длится столько, сколько задано числом проб и паузой между
    /// ними: пять запросов с паузой 200 мс займут секунду независимо от того, отвечает
    /// сервер за 3 мс или за 300. Ставить такое число в разбивку по фазам значит
    /// отвечать на вопрос «где медленно» настройками замера, а не измерением.
    /// <para>
    /// По умолчанию медиана: она устойчива к одиночному выбросу, а фаза измеряется
    /// несколько раз именно для того, чтобы выброс не был принят за норму.
    /// </para>
    /// </remarks>
    public string PhaseMetric { get; init; } = "p50";

    /// <summary>
    /// Продолжать ли сценарий, если шаг не прошёл.
    /// </summary>
    /// <remarks>
    /// По умолчанию нет. В синтетической транзакции шаги зависят друг от друга:
    /// не разрешилось имя — соединяться не с чем, и проверять TLS бессмысленно.
    /// Прогонять оставшиеся шаги значило бы получить россыпь отказов вместо
    /// одного внятного «сломалось здесь».
    /// </remarks>
    public bool ContinueOnFailure { get; init; }
}

/// <summary>
/// Сценарий: цепочка проб с порогами.
/// </summary>
/// <remarks>
/// Появился в И-11 и закрыл разрыв, который был виден с И-0: <see cref="Verdict"/>
/// существовал десять итераций и ни разу не заполнялся. Отдельная проба измеряет,
/// но не судит — судить не по чему, пороги задаёт человек. Сценарий и есть то место,
/// где человек их задаёт.
/// </remarks>
public sealed record Scenario
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public required IReadOnlyList<ScenarioStep> Steps { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public int Version { get; init; } = 1;
}

/// <summary>Итог одного шага.</summary>
public sealed record ScenarioStepResult
{
    public required string Name { get; init; }

    public required string ProbeName { get; init; }

    public required Verdict Verdict { get; init; }

    /// <summary>Идентификатор прогона в журнале, если шаг сохранялся.</summary>
    public Guid? RunId { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Значения метрик, по которым ставился вердикт.</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; } =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Измеренная длительность фазы. Пусто, если шаг не дал ни одного ответа.</summary>
    public double? PhaseMs { get; init; }

    /// <summary>Чем измерена <see cref="PhaseMs"/> — чтобы подпись не врала о происхождении числа.</summary>
    public string? PhaseMetric { get; init; }

    /// <summary>Форма результата пробы — от неё зависит, чем являются <see cref="Series"/>.</summary>
    public ProbeResultShape Shape { get; init; } = ProbeResultShape.ScalarSeries;

    /// <summary>
    /// Разложение шага на ряды, если проба его даёт.
    /// </summary>
    /// <remarks>
    /// Что это за ряды, говорит <see cref="Shape"/>, и разница принципиальная. Фазы
    /// водопада HTTP идут подряд и складываются в целое — под ними осмысленна доля.
    /// Ряды сравнения (пять резолверов) идут параллельно и не складываются ни во что —
    /// под ними осмысленно место в порядке, от быстрого к медленному. Свести их
    /// к одной таблице значило бы приписать сумму тому, у чего её нет.
    /// </remarks>
    public IReadOnlyList<SeriesStatistics> Series { get; init; } = [];

    /// <summary>
    /// Факты, на которые проба указала как на проблему.
    /// </summary>
    /// <remarks>
    /// Расхождение ответов резолверов, истекающий сертификат, код 5xx — вердикт по
    /// порогам их не видит, потому что пороги ставят на числа. Прятать их за отдельной
    /// командой значило бы показать «всё в норме» там, где проба прямо сказала обратное.
    /// </remarks>
    public IReadOnlyList<ProbeFact> Warnings { get; init; } = [];

    /// <summary>Шаг не выполнялся, потому что оборвался предыдущий.</summary>
    public bool WasSkipped { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// Итог сценария.
/// </summary>
/// <remarks>
/// Разбивка по фазам — то, ради чего сценарий и собирают: «имя разрешилось за 12 мс,
/// соединение встало за 30, рукопожатие TLS заняло 180, первый байт пришёл через 240».
/// Такая строка говорит, где именно медленно, а одно число «страница открылась
/// за 460 мс» — не говорит.
/// </remarks>
public sealed record ScenarioRun
{
    public required Guid Id { get; init; }

    public required string ScenarioName { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public required IReadOnlyList<ScenarioStepResult> Steps { get; init; }

    /// <summary>
    /// Сколько шла проверка.
    /// </summary>
    /// <remarks>
    /// Именно проверка, а не измеренное ею событие: сюда входят паузы между пробами.
    /// Складывать длительности фаз ради «времени открытия страницы» нельзя — шаги
    /// измеряют пересекающиеся отрезки, и сумма дала бы двойной счёт.
    /// </remarks>
    public TimeSpan Duration => Steps.Aggregate(TimeSpan.Zero, (total, step) => total + step.Duration);

    /// <summary>
    /// Итог сценария — худший из вердиктов шагов.
    /// </summary>
    /// <remarks>
    /// Именно худший, а не средний и не последний: сценарий проверяет цепочку,
    /// и одно сломанное звено делает непригодной всю.
    /// </remarks>
    public VerdictLevel Level => Steps.Count == 0
        ? VerdictLevel.Unknown
        : Steps.Max(s => s.Verdict.Level);

    public ScenarioStepResult? FirstFailure =>
        Steps.FirstOrDefault(s => s.Verdict.Level == VerdictLevel.Fail);
}
