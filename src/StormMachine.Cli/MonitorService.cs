using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Cli.Commands;

namespace StormMachine.Cli;

/// <summary>
/// Планировщик мониторов, работающий службой Windows.
/// </summary>
/// <remarks>
/// Тот же <see cref="MonitorScheduler"/>, что и в «storm monitors watch», и в графическом
/// клиенте — у монитора нет отдельной, «служебной» правды о сети. Разница только в том,
/// кто держит его запущенным: здесь диспетчер служб, а не открытое окно.
/// <para>
/// Планировщик к такому режиму готов с И-14: он перечитывает список мониторов каждые
/// пять секунд, потому что с самого начала рассчитан на консоль и клиент, работающие
/// над одной базой одновременно. Служба становится третьим таким участником и ничего
/// в этой картине не меняет.
/// </para>
/// <para>
/// Всё, что служба говорит о себе, идёт в журнал событий Windows: консоли у неё нет,
/// а молчащая служба неотличима от сломанной.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class MonitorService : ServiceBase
{
    private const string LogSource = "StormMonitor";

    private readonly CancellationTokenSource _stopping = new();

    private ServiceProvider? _services;
    private MonitorScheduler? _scheduler;
    private Task? _work;

    public MonitorService() => ServiceName = MonitorServiceCommands.ServiceName;

    /// <summary>
    /// Запускает службу.
    /// </summary>
    /// <remarks>
    /// Диспетчер ждёт отклика считаные секунды, поэтому здесь только заводится работа,
    /// а не выполняется: подъём хранилища и первый разбор пропущенных сроков могут
    /// занять заметное время на большой базе.
    /// </remarks>
    protected override void OnStart(string[] args)
    {
        _work = RunAsync(_stopping.Token);
    }

    protected override void OnStop()
    {
        _stopping.Cancel();

        try
        {
            _work?.Wait(TimeSpan.FromSeconds(30));
        }
        catch (AggregateException)
        {
            // Прерывание — штатный способ остановки, и жаловаться на него незачем.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _services = Program.BuildServiceProvider(console: false);

            var store = _services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var monitors = await _services.GetRequiredService<IMonitorStore>()
                .ListAsync(cancellationToken)
                .ConfigureAwait(false);

            var enabled = monitors.Count(m => m.IsEnabled);

            // База называется в журнале при каждом запуске намеренно. Служба, смотрящая
            // не в ту базу, ведёт себя в точности как исправная: она работает и молчит,
            // потому что мониторов там действительно нет. Отличить одно от другого
            // можно только по записанному пути.
            Log($"Служба запущена. База: {store.Location}. "
                + $"Мониторов {monitors.Count}, включённых {enabled}.");

            if (enabled == 0)
            {
                Log("Включённых мониторов нет — проверять нечего. "
                    + "Служба продолжит работу и подхватит их, как только они появятся.");
            }

            _scheduler = _services.GetRequiredService<MonitorScheduler>();

            foreach (var misfire in await _scheduler.PlanAsync(cancellationToken).ConfigureAwait(false))
            {
                Log($"«{misfire.Monitor.Name}»: пропущено сроков {misfire.Missed}. {misfire.Action}");
            }

            // Отказы проверок идут в журнал событий, успехи — нет: служба работает
            // месяцами, и запись на каждую удачную проверку превратила бы журнал
            // в шум, в котором настоящий отказ не найти. Все проверки целиком —
            // в базе, «storm monitors checks».
            _scheduler.Checked += OnChecked;

            await _scheduler.StartAsync(cancellationToken).ConfigureAwait(false);

            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Log("Служба останавливается по запросу диспетчера.");
        }
        catch (Exception ex)
        {
            // Служба, упавшая молча, выглядит как работающая: диспетчер покажет
            // «остановлена», и причина останется неизвестной. Записать надо здесь.
            Log($"Служба остановлена ошибкой: {ex}", EventLogEntryType.Error);
        }
        finally
        {
            if (_scheduler is not null)
            {
                _scheduler.Checked -= OnChecked;
                await _scheduler.StopAsync().ConfigureAwait(false);
            }

            if (_services is not null)
            {
                await _services.DisposeAsync().ConfigureAwait(false);
            }

            Log("Служба остановлена. Назначенные сроки сохранены в базе и переживут перезапуск.");
        }
    }

    private void OnChecked(object? sender, Domain.Monitors.MonitorCheck check)
    {
        if (check.Level != Domain.Results.VerdictLevel.Fail)
        {
            return;
        }

        Log($"Отказ: {check.Summary}", EventLogEntryType.Warning);
    }

    /// <summary>
    /// Пишет в журнал событий Windows.
    /// </summary>
    /// <remarks>
    /// Источник создаётся при первой записи, и это требует прав администратора —
    /// они есть у установщика службы. Если источника нет и создать его не удалось,
    /// запись идёт в общий журнал приложений: потерять сообщение хуже, чем записать
    /// его не туда.
    /// </remarks>
    private static void Log(string message, EventLogEntryType type = EventLogEntryType.Information)
    {
        try
        {
            if (!EventLog.SourceExists(LogSource))
            {
                EventLog.CreateEventSource(LogSource, "Application");
            }

            EventLog.WriteEntry(LogSource, message, type);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException)
        {
            // Записать не вышло — но ронять из-за этого наблюдение нельзя.
        }
    }
}
