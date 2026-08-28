using System.Diagnostics;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Application.Scenarios;

/// <summary>Ход сценария — для показа, пока он идёт.</summary>
public sealed record ScenarioProgress(int StepIndex, int StepCount, string StepName, ScenarioStepResult? Finished);

/// <summary>
/// Выполнение сценария.
/// </summary>
/// <remarks>
/// Шаги идут по очереди и обрываются при отказе, если шаг не помечен иначе. Причина
/// в устройстве самой проверки: в синтетической транзакции шаги зависят друг от друга,
/// и прогонять оставшиеся после падения первого значило бы получить россыпь отказов
/// вместо одного внятного «сломалось здесь».
/// <para>
/// Своих измерений не делает — вызывает те же пробы через тот же оркестратор, что
/// и одиночный запуск. Поэтому каждый шаг попадает в журнал обычным прогоном
/// и открывается в отчёте как любой другой.
/// </para>
/// </remarks>
public sealed class ScenarioRunner(IProbeRegistry registry, RunOrchestrator orchestrator)
{
    private readonly IProbeRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly RunOrchestrator _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));

    public async Task<ScenarioRun> RunAsync(
        Scenario scenario,
        bool save = true,
        Action<ScenarioProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scenario);

        var startedUtc = DateTimeOffset.UtcNow;
        var results = new List<ScenarioStepResult>(scenario.Steps.Count);
        var broken = false;

        for (var i = 0; i < scenario.Steps.Count; i++)
        {
            var step = scenario.Steps[i];

            onProgress?.Invoke(new ScenarioProgress(i, scenario.Steps.Count, step.Name, null));

            var result = broken
                ? Skipped(step)
                : await RunStepAsync(step, save, cancellationToken).ConfigureAwait(false);

            results.Add(result);
            onProgress?.Invoke(new ScenarioProgress(i, scenario.Steps.Count, step.Name, result));

            if (result.Verdict.Level == VerdictLevel.Fail && !step.ContinueOnFailure)
            {
                broken = true;
            }
        }

        return new ScenarioRun
        {
            Id = Guid.NewGuid(),
            ScenarioName = scenario.Name,
            StartedUtc = startedUtc,
            Steps = results,
        };
    }

    private async Task<ScenarioStepResult> RunStepAsync(
        ScenarioStep step,
        bool save,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();

        if (!_registry.TryGet(step.ProbeName, out var probe))
        {
            watch.Stop();

            return new ScenarioStepResult
            {
                Name = step.Name,
                ProbeName = step.ProbeName,
                Verdict = Verdict.Fail($"Проба «{step.ProbeName}» не зарегистрирована."),
                Duration = watch.Elapsed,
                Error = "Неизвестная проба.",
            };
        }

        var request = new ProbeRequest
        {
            Target = step.Target,
            Parameters = new Dictionary<string, object?>(step.Parameters, StringComparer.OrdinalIgnoreCase),
        };

        var errors = probe.Validate(request);

        if (errors.Count > 0)
        {
            watch.Stop();

            return new ScenarioStepResult
            {
                Name = step.Name,
                ProbeName = step.ProbeName,
                Verdict = Verdict.Fail("Параметры шага не приняты пробой."),
                Duration = watch.Elapsed,
                Error = string.Join("; ", errors.Select(e => $"{e.ParameterName}: {e.Message}")),
            };
        }

        try
        {
            var outcome = await _orchestrator
                .RunAsync(probe, request, new RunOptions { Save = save }, cancellationToken)
                .ConfigureAwait(false);

            watch.Stop();

            var shape = probe.Descriptor.Shape;
            var metrics = ProbeMetrics.Read(outcome.Result, shape);

            return new ScenarioStepResult
            {
                Name = step.Name,
                ProbeName = step.ProbeName,
                Verdict = ThresholdEvaluator.Evaluate(outcome.Result, step.Thresholds, shape),
                RunId = outcome.RunId,
                Duration = watch.Elapsed,
                Metrics = metrics,

                // Длительность фазы берётся из измерения, а не из часов вокруг шага:
                // шаг длится столько, сколько задано числом проб и паузой между ними.
                PhaseMs = metrics.TryGetValue(step.PhaseMetric, out var phase) ? phase : null,
                PhaseMetric = step.PhaseMetric,

                // Ряды показываются только там, где проба их действительно даёт:
                // у ICMP составляющих нет, и рисовать одну полоску под самой собой
                // значило бы выдать оформление за разбор.
                Shape = shape,
                Series = shape is ProbeResultShape.PhasedTiming or ProbeResultShape.ComparedSeries
                    ? SeriesBreakdown.Compute(shape, outcome.Result.Samples)
                    : [],
                Warnings = [.. outcome.Result.Facts.Where(f => f.IsWarning)],
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            watch.Stop();

            return new ScenarioStepResult
            {
                Name = step.Name,
                ProbeName = step.ProbeName,
                Verdict = Verdict.Fail($"Шаг не выполнен: {ex.Message}"),
                Duration = watch.Elapsed,
                Error = ex.Message,
            };
        }
    }

    /// <summary>
    /// Шаг, до которого не дошло.
    /// </summary>
    /// <remarks>
    /// Пропущенный шаг — не отказ и не успех. Показать его отказом значило бы обвинить
    /// исправную часть цепочки в поломке предыдущей.
    /// </remarks>
    private static ScenarioStepResult Skipped(ScenarioStep step) => new()
    {
        Name = step.Name,
        ProbeName = step.ProbeName,
        Verdict = Verdict.NotEvaluated("Пропущен: предыдущий шаг не прошёл."),
        Duration = TimeSpan.Zero,
        WasSkipped = true,
    };
}
