using StormMachine.Domain.Measurements;

namespace StormMachine.Application.Probes;

/// <summary>
/// Побочный канал пробы: то, что не является сэмплом.
/// </summary>
/// <remarks>
/// Пробы регистрируются как singleton, поэтому состояние конкретного прогона не может
/// жить на самой пробе. Наблюдатель создаётся вызывающей стороной на каждый запуск
/// и передаётся внутрь — так проба остаётся без состояния, а факты и разрешённый адрес
/// доходят до результата.
/// </remarks>
public interface IProbeObserver
{
    /// <summary>Цель разрешилась в конкретный адрес.</summary>
    void OnResolved(string address);

    /// <summary>Проба установила структурный факт.</summary>
    void OnFact(ProbeFact fact);

    /// <summary>
    /// Проба сообщает, что происходит прямо сейчас.
    /// </summary>
    /// <remarks>
    /// Третий канал понадобился в И-19. Ход подготовки — не сэмпл и не факт: он ничего
    /// не измеряет и в журнале ему делать нечего, но показать его надо <b>пока идёт
    /// ожидание</b>, а не в итоге. Факт для этого не годится по времени: факты видны
    /// после прогона, а сообщение «жду звонка агента, на его машине набрать вот это»
    /// после прогона бесполезно — ждать уже нечего.
    /// <para>
    /// До этого пробы агента писали такие сообщения прямо в <c>Console</c>. В консоли
    /// это работало, а графический клиент собран как <c>WinExe</c> и консоли не имеет:
    /// сообщение с указанием, что набрать на второй машине, пропадало, и прогон
    /// молча стоял до истечения срока ожидания.
    /// </para>
    /// </remarks>
    void OnProgress(string message);
}

/// <summary>Наблюдатель, который ничего не запоминает. Для вызовов, где факты не нужны.</summary>
public sealed class NullProbeObserver : IProbeObserver
{
    public static readonly NullProbeObserver Instance = new();

    private NullProbeObserver()
    {
    }

    public void OnResolved(string address)
    {
    }

    public void OnFact(ProbeFact fact)
    {
    }

    public void OnProgress(string message)
    {
    }
}

/// <summary>Собирает факты одного прогона.</summary>
/// <remarks>
/// Ход подготовки не копится, а отдаётся сразу: он нужен во время ожидания.
/// Обработчик вызывается из того потока, в котором работает проба, — переносить
/// вызов в поток интерфейса обязан тот, кто обработчик передал.
/// </remarks>
public sealed class ProbeCollector(Action<string>? onProgress = null) : IProbeObserver
{
    private readonly List<ProbeFact> _facts = [];
    private readonly Action<string>? _onProgress = onProgress;

    public string? ResolvedAddress { get; private set; }

    public IReadOnlyList<ProbeFact> Facts => _facts;

    public void OnResolved(string address) => ResolvedAddress = address;

    public void OnFact(ProbeFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        _facts.Add(fact);
    }

    public void OnProgress(string message) => _onProgress?.Invoke(message);

    /// <summary>Факты одной категории — для показа сгруппированными.</summary>
    public IEnumerable<ProbeFact> ByCategory(string category) =>
        _facts.Where(f => string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase));
}
