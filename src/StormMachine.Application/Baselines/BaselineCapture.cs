using StormMachine.Application.Abstractions;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Application.Baselines;

/// <summary>
/// Снятие эталона с выполненного прогона.
/// </summary>
/// <remarks>
/// Сборка эталона жила в команде консоли, и поэтому снять его можно было только
/// оттуда: экран отчётов предлагал сравнение с эталоном и тут же отправлял за самим
/// эталоном в другую программу. Правила отбора при этом всегда были доменными
/// (<see cref="Baseline.IsComparable" />), в консоли лежала только сборка — её сюда
/// и переносим, чтобы у окна и консоли был один эталон, а не два похожих.
/// </remarks>
public sealed class BaselineCapture(IBaselineStore store)
{
    private readonly IBaselineStore _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// Фиксирует эталон по прогону. Одноимённый перезаписывается.
    /// </summary>
    /// <remarks>
    /// Что годится в эталон, решает домен: счётчики проб — настройка замера,
    /// а не свойство сети, и «отправлено 8 против 10» ухудшением не является.
    /// </remarks>
    public async Task<Baseline> CaptureAsync(
        StoredRun run,
        string name,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var metrics = ProbeMetrics.FromStored(run.Series, run.Facts);

        var chosen = metrics
            .Where(m => Baseline.IsComparable(m.Key))
            .OrderBy(m => m.Key, StringComparer.OrdinalIgnoreCase)
            .Select(m => new BaselineMetric(m.Key, m.Value, Baseline.HigherIsBetterFor(m.Key, run.Unit)))
            .ToList();

        if (chosen.Count == 0)
        {
            throw new InvalidOperationException(
                "У прогона нет ни одной метрики, пригодной для сравнения, — фиксировать нечего.");
        }

        var trimmed = name.Trim();
        var existing = await _store.FindAsync(trimmed, cancellationToken).ConfigureAwait(false);

        var baseline = new Baseline
        {
            // Одноимённый эталон перезаписывается, а не заводится вторым: два эталона
            // с одним именем невозможно выбрать в отчёте осмысленно.
            Id = existing?.Id ?? Guid.NewGuid(),
            Name = trimmed,
            Description = description,
            Subject = run.Summary.ProbeName,
            Target = run.Target,
            Context = run.Context,
            Unit = run.Unit,
            Metrics = chosen,
            RunId = run.Summary.Id,
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        await _store.SaveAsync(baseline, cancellationToken).ConfigureAwait(false);

        return baseline;
    }
}
