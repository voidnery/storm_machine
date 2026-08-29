using StormMachine.Application.Abstractions;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Application.Scenarios;

/// <summary>Сценарий в списке: зашитый шаблон или собранный оператором.</summary>
/// <param name="Key">Чем его называют в командах: ключ шаблона или имя сценария.</param>
/// <param name="Title">Как он называется человеку.</param>
/// <param name="About">Из чего состоит.</param>
/// <param name="IsTemplate">Шаблон это или своё.</param>
/// <param name="Custom">Сам сценарий, если он свой.</param>
public sealed record ScenarioEntry(
    string Key,
    string Title,
    string About,
    bool IsTemplate,
    Scenario? Custom = null);

/// <summary>
/// Шаблоны и собранные оператором сценарии в одном месте.
/// </summary>
/// <remarks>
/// Появилась в И-22 вместе с возможностью собирать свои цепочки — до неё сценарии
/// существовали только зашитыми, и собрать свой можно было лишь правкой кода (долг И-11).
/// <para>
/// Шаблоны остались зашитыми намеренно. Они начало разговора, а не ограничение: половина
/// собранных сценариев родится из шаблона, который поправили, и держать их в базе значило
/// бы дать оператору испортить то, к чему всегда можно вернуться. По той же причине
/// «собрать из шаблона» — отдельная операция: она делает копию, а не открывает оригинал.
/// </para>
/// <para>
/// Клиенты спрашивают сценарии только здесь. Иначе консоль знала бы про шаблоны,
/// а окно — про свои, и один и тот же ключ означал бы разное.
/// </para>
/// </remarks>
public sealed class ScenarioLibrary(IScenarioStore store)
{
    private readonly IScenarioStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Всё, что можно запустить: сначала шаблоны, потом своё.
    /// </summary>
    /// <remarks>
    /// Порядок не косметика: шаблоны проверены и объяснены, а своё оператор собрал сам
    /// и знает про него всё. Начинающему нужны первые.
    /// </remarks>
    public async Task<IReadOnlyList<ScenarioEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        var entries = ScenarioTemplates.All
            .Select(t => new ScenarioEntry(t.Key, t.Title, t.About, IsTemplate: true))
            .ToList();

        foreach (var scenario in await _store.ListAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new ScenarioEntry(
                scenario.Name,
                scenario.Name,
                Describe(scenario),
                IsTemplate: false,
                scenario));
        }

        return entries;
    }

    /// <summary>
    /// Готовит сценарий к запуску по цели.
    /// </summary>
    /// <remarks>
    /// Своё имя ищется раньше ключа шаблона: сценарий, названный «web», должен
    /// запускаться, а не молча подменяться шаблоном. Совпадение имён — дело оператора,
    /// и решать за него, что он имел в виду, продукт не должен; побеждает то,
    /// что он завёл сам.
    /// </remarks>
    public async Task<Scenario> CreateAsync(
        string keyOrName,
        string host,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyOrName);

        var custom = await _store.FindAsync(keyOrName, cancellationToken).ConfigureAwait(false);

        if (custom is not null)
        {
            return Retarget(custom, host);
        }

        return ScenarioTemplates.Create(keyOrName, host);
    }

    /// <summary>
    /// Подставляет цель во все шаги.
    /// </summary>
    /// <remarks>
    /// Собранный сценарий хранится с целью, которую задали при сборке, но запускается
    /// по той, что назвали при запуске: цепочка проверок описывает <b>как</b> проверять,
    /// а не <b>что</b>. Иначе сценарий пришлось бы заводить заново для каждого узла.
    /// <para>
    /// Шаг, у которого цель задана явно и не совпадает с целью сценария, не трогается:
    /// в сравнении резолверов каждый шаг спрашивает свой сервер, и подмена превратила бы
    /// пять разных резолверов в пять одинаковых.
    /// </para>
    /// </remarks>
    private static Scenario Retarget(Scenario scenario, string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return scenario;
        }

        var target = Target.Parse(host);

        // Цель, общая для большинства шагов, и есть цель сценария: её и подменяем.
        var common = scenario.Steps
            .GroupBy(s => s.Target.Value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        return scenario with
        {
            Steps =
            [
                .. scenario.Steps.Select(step =>
                    string.Equals(step.Target.Value, common, StringComparison.OrdinalIgnoreCase)
                        ? step with { Target = target }
                        : step),
            ],
        };
    }

    /// <summary>Из чего состоит сценарий, одной строкой.</summary>
    public static string Describe(Scenario scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        if (scenario.Steps.Count == 0)
        {
            return "шагов нет";
        }

        var probes = scenario.Steps.Select(s => s.ProbeName).Distinct(StringComparer.OrdinalIgnoreCase);

        return $"{Domain.Text.Plural.With(scenario.Steps.Count, "шаг", "шага", "шагов")}: "
               + string.Join(" → ", probes);
    }
}
