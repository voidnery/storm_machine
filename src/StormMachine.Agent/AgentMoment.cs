using StormMachine.Protocol;

namespace StormMachine.Agent;

/// <summary>Что именно произошло с агентом.</summary>
public enum AgentMomentKind
{
    /// <summary>Поднялся и слушает.</summary>
    Listening,

    /// <summary>Выдан код сопряжения.</summary>
    CodeIssued,

    /// <summary>Кто-то подключился.</summary>
    PeerArrived,

    /// <summary>Сопряжение состоялось.</summary>
    PeerPaired,

    /// <summary>Собеседник отключился.</summary>
    PeerLeft,

    /// <summary>Началось измерение.</summary>
    TestStarted,

    /// <summary>Промежуточный снимок измерения — из него и строится график.</summary>
    TestSample,

    /// <summary>Измерение закончилось.</summary>
    TestFinished,

    /// <summary>В чём-то отказано или что-то оборвалось.</summary>
    Trouble,
}

/// <summary>
/// Происходящее с агентом — в виде, пригодном не только для строчки в консоли.
/// </summary>
/// <remarks>
/// До И-26 агент умел рассказывать о себе одним способом: строкой текста
/// в <see cref="AgentSettings.Log" />. Для консоли этого довольно, для окна —
/// нет: из текста нельзя построить график, а человек на чужой площадке, у которого
/// запущен агент, видит ровно то же измерение, что и оператор, — и имеет право
/// видеть его так же.
/// <para>
/// Поэтому события отделены от их текста: текст остаётся для журнала, а окно
/// получает числа. Консоль о событиях не знает и работает как работала.
/// </para>
/// </remarks>
public sealed record AgentMoment
{
    public required AgentMomentKind Kind { get; init; }

    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>Кто на той стороне, если он известен.</summary>
    public string? Peer { get; init; }

    /// <summary>Что за измерение: пропускная способность, качество канала.</summary>
    public TestKind? Test { get; init; }

    /// <summary>Снимок измерения — только у <see cref="AgentMomentKind.TestSample" /> и итога.</summary>
    public TestSnapshot? Snapshot { get; init; }

    /// <summary>Та же мысль словами — ею живёт консоль и журнал в окне.</summary>
    public string? Text { get; init; }
}
