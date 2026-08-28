using System.Globalization;
using StormMachine.Domain.Results;

namespace StormMachine.Domain.Measurements;

/// <summary>Оценка bufferbloat по приросту задержки под нагрузкой.</summary>
public enum BufferbloatGrade
{
    /// <summary>Нечего оценивать: не хватило измерений.</summary>
    Unknown = 0,

    APlus = 1,
    A = 2,
    B = 3,
    C = 4,
    D = 5,
    F = 6,
}

/// <summary>
/// Что стало с задержкой, когда канал загрузили.
/// </summary>
/// <remarks>
/// Bufferbloat — это не медленный канал, а слишком глубокие очереди в нём. Канал может
/// выдавать полную скорость и при этом быть непригодным для разговора: пакет голоса
/// встаёт в очередь за мегабайтом чужой загрузки и приходит на полсекунды позже. Именно
/// поэтому измеряется не скорость, а <b>прирост</b> задержки: сама по себе задержка
/// под нагрузкой ничего не говорит, пока не с чем сравнить.
/// <para>
/// Прирост считается по p95, а не по среднему. Bufferbloat проявляется всплесками:
/// очередь наполняется и опустошается, и среднее размазывает как раз то, ради чего
/// измерение делалось. Разговор рвётся на всплесках, а не на среднем.
/// </para>
/// <para>
/// Шкала A+…F — соглашение сообщества (Waveform, DSLReports), а не стандарт. Названо
/// прямо: буква удобна для разговора с провайдером, но ссылаться на неё как на норму
/// нельзя, и продукт всегда показывает рядом само число прироста.
/// </para>
/// </remarks>
public sealed record BufferbloatAssessment
{
    /// <summary>Ключ ряда без нагрузки — им помечаются сэмплы холостой фазы.</summary>
    public const string IdleSeries = "без нагрузки";

    /// <summary>Ключ ряда под нагрузкой.</summary>
    public const string LoadedSeries = "под нагрузкой";

    /// <summary>Источник шкалы. Показывается вместе с буквой: это соглашение, а не норма.</summary>
    public const string GradeSource = "шкала Waveform/DSLReports — соглашение сообщества, не стандарт";

    public required LatencyStatistics Idle { get; init; }

    public required LatencyStatistics Loaded { get; init; }

    /// <summary>Направление, в котором грузили канал.</summary>
    public required string Direction { get; init; }

    /// <summary>Достигнутая при этом скорость. Ноль — нагрузки не было.</summary>
    public double LoadMbps { get; init; }

    /// <summary>Прирост p95 задержки под нагрузкой, миллисекунды.</summary>
    public double IncreaseMs => Idle.SampleCount == 0 || Loaded.SampleCount == 0
        ? double.NaN
        : Loaded.P95Ms - Idle.P95Ms;

    public BufferbloatGrade Grade => GradeFor(IncreaseMs);

    /// <summary>
    /// Буква по приросту.
    /// </summary>
    /// <remarks>
    /// Отрицательный прирост округляется до нуля, а не отбрасывается как ошибка:
    /// под нагрузкой задержка иногда оказывается ниже холостой, потому что канал
    /// прогрелся, а маршрут перестроился. Это не повод объявлять измерение неудачным.
    /// </remarks>
    public static BufferbloatGrade GradeFor(double increaseMs)
    {
        if (double.IsNaN(increaseMs))
        {
            return BufferbloatGrade.Unknown;
        }

        return Math.Max(0, increaseMs) switch
        {
            < 5 => BufferbloatGrade.APlus,
            < 30 => BufferbloatGrade.A,
            < 60 => BufferbloatGrade.B,
            < 200 => BufferbloatGrade.C,
            < 400 => BufferbloatGrade.D,
            _ => BufferbloatGrade.F,
        };
    }

    public static string GradeLetter(BufferbloatGrade grade) => grade switch
    {
        BufferbloatGrade.APlus => "A+",
        BufferbloatGrade.A => "A",
        BufferbloatGrade.B => "B",
        BufferbloatGrade.C => "C",
        BufferbloatGrade.D => "D",
        BufferbloatGrade.F => "F",
        _ => "—",
    };

    /// <summary>
    /// Что буква означает на практике.
    /// </summary>
    /// <remarks>
    /// Названо через то, что человек заметит: разговор, игру, видеозвонок. Буква сама
    /// по себе не говорит ничего тому, кто не знает шкалы, а знает её мало кто.
    /// </remarks>
    public static string Explain(BufferbloatGrade grade) => grade switch
    {
        BufferbloatGrade.APlus =>
            "Очереди почти не растут. Разговор и игра не заметят фоновой загрузки.",
        BufferbloatGrade.A =>
            "Очереди растут немного. Разговор под загрузкой останется разборчивым.",
        BufferbloatGrade.B =>
            "Очереди заметны. Видеозвонок под загрузкой начнёт подтормаживать.",
        BufferbloatGrade.C =>
            "Очереди большие. Разговор под загрузкой будет рваться, игра станет непригодной.",
        BufferbloatGrade.D =>
            "Очереди очень большие. Любая загрузка делает канал непригодным для разговора.",
        BufferbloatGrade.F =>
            "Очереди огромные. Скачивание файла обрывает разговор целиком.",
        _ => "Оценить не по чему: не хватило измерений в одной из фаз.",
    };

    /// <summary>
    /// Вердикт для сценария.
    /// </summary>
    /// <remarks>
    /// Отказ ставится с C, а не с D: именно на C разговор начинает рваться, и именно
    /// это оператор придёт показывать провайдеру. Предупреждение с B — там ещё работает,
    /// но уже видно.
    /// </remarks>
    public Verdict ToVerdict()
    {
        var letter = GradeLetter(Grade);

        if (Grade == BufferbloatGrade.Unknown)
        {
            return Verdict.NotEvaluated(Explain(Grade));
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Оценка {letter}: под нагрузкой ({Direction}) задержка выросла на {IncreaseMs:0.0} мс "
            + $"— с {Idle.P95Ms:0.0} до {Loaded.P95Ms:0.0} мс по p95.");

        var level = Grade switch
        {
            BufferbloatGrade.APlus or BufferbloatGrade.A => VerdictLevel.Pass,
            BufferbloatGrade.B => VerdictLevel.Warn,
            _ => VerdictLevel.Fail,
        };

        return new Verdict
        {
            Level = level,
            Summary = summary,
            Explanation = Explain(Grade) + " " + GradeSource + ".",
            MetricName = "bufferbloat",
            MetricValue = IncreaseMs,
        };
    }
}
