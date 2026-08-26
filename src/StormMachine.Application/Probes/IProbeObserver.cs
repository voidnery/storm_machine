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
}

/// <summary>Собирает факты одного прогона.</summary>
public sealed class ProbeCollector : IProbeObserver
{
    private readonly List<ProbeFact> _facts = [];

    public string? ResolvedAddress { get; private set; }

    public IReadOnlyList<ProbeFact> Facts => _facts;

    public void OnResolved(string address) => ResolvedAddress = address;

    public void OnFact(ProbeFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        _facts.Add(fact);
    }

    /// <summary>Факты одной категории — для показа сгруппированными.</summary>
    public IEnumerable<ProbeFact> ByCategory(string category) =>
        _facts.Where(f => string.Equals(f.Category, category, StringComparison.OrdinalIgnoreCase));
}
