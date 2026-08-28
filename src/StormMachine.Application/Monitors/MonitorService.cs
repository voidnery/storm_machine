using System.Diagnostics;
using Microsoft.Extensions.Logging;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.Monitors;

/// <summary>
/// Одна проверка монитора: запуск, вердикт, запись, оповещение.
/// </summary>
/// <remarks>
/// Отделено от планировщика намеренно. Планировщик решает <b>когда</b>, служба —
/// <b>что происходит</b>. Благодаря этому «проверить сейчас» из интерфейса и проверка
/// по расписанию — один и тот же код, а не две похожие ветки, которые со временем
/// разойдутся.
/// </remarks>
public sealed class MonitorService(
    IMonitorStore store,
    IProbeRegistry registry,
    RunOrchestrator orchestrator,
    ScenarioRunner scenarios,
    IEnumerable<IAlertChannel> channels,
    TimeProvider time,
    ILogger<MonitorService> logger)
{
    private readonly IMonitorStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IProbeRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly RunOrchestrator _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
    private readonly ScenarioRunner _scenarios = scenarios ?? throw new ArgumentNullException(nameof(scenarios));
    private readonly IReadOnlyList<IAlertChannel> _channels = [.. channels ?? []];
    private readonly TimeProvider _time = time ?? throw new ArgumentNullException(nameof(time));
    private readonly ILogger<MonitorService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Каналы, известные продукту, — для показа списка и проверки настройки.</summary>
    public IReadOnlyList<IAlertChannel> Channels => _channels;

    /// <summary>Проверка выполнена и записана.</summary>
    public event EventHandler<MonitorCheck>? Checked;

    /// <summary>Состояние алерта сменилось.</summary>
    public event EventHandler<AlertEvent>? Alerted;

    /// <summary>
    /// Выполняет проверку и записывает всё, что из неё следует.
    /// </summary>
    /// <param name="monitor">Монитор.</param>
    /// <param name="scheduled">
    /// Момент, на который проверка была назначена. Пусто — запуск руками.
    /// </param>
    public async Task<MonitorCheck> CheckAsync(
        Monitor monitor,
        DateTimeOffset? scheduled = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var now = _time.GetUtcNow();
        var startedUtc = scheduled ?? now;

        // Обслуживание проверяется по фактическому моменту, а не по назначенному:
        // окно могли завести уже после того, как срок был посчитан.
        if (monitor.Schedule.MaintenanceAt(now) is { } window)
        {
            return await RecordAsync(
                monitor,
                new MonitorCheck
                {
                    Id = Guid.NewGuid(),
                    MonitorId = monitor.Id,
                    StartedUtc = now,
                    Kind = CheckKind.Maintenance,
                    Level = VerdictLevel.Unknown,
                    Summary = $"Обслуживание: {window.Describe()}",
                },
                null,
                cancellationToken).ConfigureAwait(false);
        }

        var watch = Stopwatch.StartNew();

        var check = monitor.Kind == MonitorKind.Scenario
            ? await RunScenarioAsync(monitor, startedUtc, cancellationToken).ConfigureAwait(false)
            : await RunProbeAsync(monitor, startedUtc, cancellationToken).ConfigureAwait(false);

        watch.Stop();

        return await RecordAsync(
            monitor,
            check with { Duration = watch.Elapsed },
            TriggerFor(monitor, check),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Записывает пропуск: сроки, прошедшие, пока продукт не работал.
    /// </summary>
    /// <remarks>
    /// Одной записью на весь провал, а не сотней пустых: важно не число, а то,
    /// что в это время сеть никто не наблюдал.
    /// </remarks>
    public Task<MonitorCheck> RecordMissedAsync(
        Monitor monitor,
        DateTimeOffset since,
        int count,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        return RecordAsync(
            monitor,
            new MonitorCheck
            {
                Id = Guid.NewGuid(),
                MonitorId = monitor.Id,
                StartedUtc = since,
                Kind = CheckKind.Missed,
                Level = VerdictLevel.Unknown,
                MissedCount = count,
                Summary = $"Пропущено проверок: {count}. Продукт не работал — о сети в это время данных нет.",
            },
            null,
            cancellationToken);
    }

    private async Task<MonitorCheck> RunProbeAsync(
        Monitor monitor,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        var blank = new MonitorCheck
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            StartedUtc = startedUtc,
            Summary = string.Empty,
        };

        if (!_registry.TryGet(monitor.Subject, out var probe))
        {
            return blank with
            {
                Level = VerdictLevel.Fail,
                Summary = $"Проба «{monitor.Subject}» не зарегистрирована.",
                Error = "Неизвестная проба.",
            };
        }

        var request = new ProbeRequest
        {
            Target = monitor.Target,
            Parameters = monitor.Parameters.ToDictionary(p => p.Key, p => (object?)p.Value, StringComparer.OrdinalIgnoreCase),
        };

        var errors = probe.Validate(request);

        if (errors.Count > 0)
        {
            return blank with
            {
                Level = VerdictLevel.Fail,
                Summary = "Параметры монитора не приняты пробой.",
                Error = string.Join("; ", errors.Select(e => $"{e.ParameterName}: {e.Message}")),
            };
        }

        try
        {
            var outcome = await _orchestrator
                .RunAsync(probe, request, new RunOptions { Save = true, PresetId = monitor.PresetId }, cancellationToken)
                .ConfigureAwait(false);

            var shape = probe.Descriptor.Shape;
            var verdict = ThresholdEvaluator.Evaluate(outcome.Result, monitor.Thresholds, shape);

            return blank with
            {
                Level = verdict.Level,
                Summary = verdict.Summary,
                RunId = outcome.RunId,
                Metric = verdict.MetricName,
                Value = verdict.MetricValue,
                Threshold = verdict.Threshold,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Отказ измерения — это отказ проверяемого, а не сбой продукта: цель,
            // до которой не удалось добраться, недоступна по определению. Оговорка
            // в тексте оставлена, чтобы отличать это от нарушения порога.
            _logger.LogWarning(ex, "Монитор {Monitor}: проверка не выполнена.", monitor.Name);

            return blank with
            {
                Level = VerdictLevel.Fail,
                Summary = $"Проверка не выполнена: {ex.Message}",
                Error = ex.Message,
            };
        }
    }

    private async Task<MonitorCheck> RunScenarioAsync(
        Monitor monitor,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        var blank = new MonitorCheck
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            StartedUtc = startedUtc,
            Summary = string.Empty,
        };

        try
        {
            var scenario = ScenarioTemplates.Create(monitor.Subject, monitor.Target.Value);
            var run = await _scenarios
                .RunAsync(scenario, save: true, onProgress: null, cancellationToken)
                .ConfigureAwait(false);

            var failure = run.FirstFailure;

            return blank with
            {
                Level = run.Level,
                Summary = failure is null
                    ? $"{scenario.Name}: все шаги пройдены."
                    : $"{scenario.Name}: шаг «{failure.Name}» — {failure.Verdict.Summary}",
                RunId = failure?.RunId ?? run.Steps.FirstOrDefault(s => s.RunId is not null)?.RunId,
                Metric = failure?.Verdict.MetricName,
                Value = failure?.Verdict.MetricValue,
                Threshold = failure?.Verdict.Threshold,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Монитор {Monitor}: сценарий не выполнен.", monitor.Name);

            return blank with
            {
                Level = VerdictLevel.Fail,
                Summary = $"Сценарий не выполнен: {ex.Message}",
                Error = ex.Message,
            };
        }
    }

    /// <summary>Порог, по которому поставлен вердикт, — он же задаёт сторону запаса на снятие.</summary>
    private static Threshold? TriggerFor(Monitor monitor, MonitorCheck check) =>
        check.Metric is null
            ? null
            : monitor.Thresholds.FirstOrDefault(t =>
                string.Equals(t.Metric, check.Metric, StringComparison.OrdinalIgnoreCase));

    private async Task<MonitorCheck> RecordAsync(
        Monitor monitor,
        MonitorCheck check,
        Threshold? trigger,
        CancellationToken cancellationToken)
    {
        // Отмена не должна отменять запись: проверка состоялась, и её итог обязан
        // остаться в истории доступности, иначе окно молча превратится в пробел.
        await _store.AppendCheckAsync(check, CancellationToken.None).ConfigureAwait(false);

        var status = await _store.GetStatusAsync(monitor.Id, CancellationToken.None).ConfigureAwait(false);
        var next = status with
        {
            Level = check.Kind == CheckKind.Measured ? check.Level : status.Level,
            LastRunUtc = check.Kind == CheckKind.Measured ? check.StartedUtc : status.LastRunUtc,
            LastSummary = check.Summary,
        };

        // Итог проверки объявляется до того, что из него следует. Иначе в выводе
        // алерт появлялся бы раньше проверки, его вызвавшей, и читался бы как
        // сработавший сам по себе.
        Checked?.Invoke(this, check);

        if (monitor.Alert is { } rule)
        {
            var decision = AlertEvaluator.Apply(status.Alert, check, trigger, rule, _time.GetUtcNow());

            next = next with { Alert = decision.State };

            if (decision.Action != AlertAction.None)
            {
                await RaiseAsync(monitor, check, decision, rule, cancellationToken).ConfigureAwait(false);
            }
        }

        await _store.SaveStatusAsync(monitor.Id, next, CancellationToken.None).ConfigureAwait(false);

        return check;
    }

    private async Task RaiseAsync(
        Monitor monitor,
        MonitorCheck check,
        AlertDecision decision,
        AlertRule rule,
        CancellationToken cancellationToken)
    {
        var alert = new AlertEvent
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            MonitorName = monitor.Name,
            AtUtc = _time.GetUtcNow(),
            Action = decision.Action,
            Level = check.Level,
            Reason = decision.Reason,
            Summary = check.Summary,
            CheckId = check.Id,
            Notified = decision.Notify,
        };

        if (decision.Notify)
        {
            var (delivered, failures) = await DeliverAsync(
                new AlertNotification(monitor, alert, check),
                rule,
                cancellationToken).ConfigureAwait(false);

            alert = alert with { Channels = delivered, DeliveryErrors = failures };
        }

        await _store.AppendAlertAsync(alert, CancellationToken.None).ConfigureAwait(false);

        Alerted?.Invoke(this, alert);
    }

    private async Task<(IReadOnlyList<string> Delivered, IReadOnlyList<string> Failures)> DeliverAsync(
        AlertNotification notification,
        AlertRule rule,
        CancellationToken cancellationToken)
    {
        var delivered = new List<string>();
        var failures = new List<string>();

        foreach (var name in rule.Channels)
        {
            var channel = _channels.FirstOrDefault(c =>
                string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                failures.Add($"{name}: канал не зарегистрирован.");

                continue;
            }

            await channel.RefreshAsync(cancellationToken).ConfigureAwait(false);

            if (!channel.IsConfigured)
            {
                failures.Add($"{name}: {channel.MissingConfiguration ?? "не настроен"}.");

                continue;
            }

            try
            {
                await channel.SendAsync(notification, cancellationToken).ConfigureAwait(false);
                delivered.Add(name);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Один упавший канал не отменяет остальные: почта могла лечь,
                // а звук — прозвучать, и молчать об этом нельзя.
                _logger.LogWarning(ex, "Канал {Channel} не доставил алерт.", name);
                failures.Add($"{name}: {ex.Message}");
            }
        }

        return (delivered, failures);
    }
}
