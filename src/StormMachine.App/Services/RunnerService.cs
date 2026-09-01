using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using Avalonia.Threading;
using StormMachine.App.ViewModels;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Domain.Measurements;

namespace StormMachine.App.Services;

/// <summary>
/// Выполняющиеся прогоны приложения.
/// </summary>
/// <remarks>
/// Существует ради панели операций: тесты длинные, и оператор не обязан сидеть на экране,
/// с которого запустил измерение. Список активных операций доступен отовсюду
/// (UX-каркас, docs/01-analysis.md §9.3).
/// <para>
/// Сэмплы приходят из фонового потока. В интерфейс они попадают <b>не по одному</b>:
/// монитор с интервалом 100 мс дал бы десять обращений к диспетчеру в секунду на каждый
/// прогон, а при нескольких прогонах — заметное дёрганье. Вместо этого сырые измерения копятся
/// в очереди, а страница разбирает её по таймеру (см. <see cref="ActiveRunViewModel"/>).
/// </para>
/// </remarks>
public sealed class RunnerService(RunOrchestrator orchestrator)
{
    private readonly RunOrchestrator _orchestrator = orchestrator
        ?? throw new ArgumentNullException(nameof(orchestrator));

    /// <summary>
    /// Активные операции. Меняется только в потоке интерфейса.
    /// </summary>
    /// <remarks>
    /// Не только пробы: сценарий — самая длинная операция продукта, и до И-14
    /// он шёл мимо этого списка. Оператор, ушедший с экрана, не знал, идёт ли
    /// проверка, и не мог её остановить.
    /// </remarks>
    public ObservableCollection<ActiveOperationViewModel> Active { get; } = [];

    public bool HasActive => Active.Count > 0;

    public event EventHandler? ActiveChanged;

    /// <summary>
    /// Запускает пробу и возвращает объект, за которым можно следить.
    /// </summary>
    public ActiveRunViewModel Start(
        IProbe probe,
        ProbeRequest request,
        bool save,
        string title,
        Guid? presetId = null,
        int? presetVersion = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(request);

        var queue = new ConcurrentQueue<Sample>();
        var cts = new CancellationTokenSource();
        var run = new ActiveRunViewModel(probe.Descriptor, title, queue, cts);

        Active.Add(run);
        ActiveChanged?.Invoke(this, EventArgs.Empty);

        _ = ExecuteAsync(run, probe, request, save, queue, cts, presetId, presetVersion);

        return run;
    }

    /// <summary>
    /// Заводит операцию сценария и возвращает объект, за которым можно следить.
    /// </summary>
    /// <remarks>
    /// В отличие от пробы, сценарий служба не выполняет: его гоняет страница,
    /// потому что после каждой цели ей надо разложить результат по шагам.
    /// Служба здесь отвечает только за то, чтобы операция была видна и отменяема
    /// с любого экрана.
    /// </remarks>
    public ActiveScenarioViewModel StartScenario(string title, CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);

        var operation = new ActiveScenarioViewModel(title, cancellation);

        Active.Add(operation);
        ActiveChanged?.Invoke(this, EventArgs.Empty);

        return operation;
    }

    /// <summary>Убирает операцию из списка. Вызывается из потока интерфейса.</summary>
    public void Remove(ActiveOperationViewModel operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        Active.Remove(operation);
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteAsync(
        ActiveRunViewModel run,
        IProbe probe,
        ProbeRequest request,
        bool save,
        ConcurrentQueue<Sample> queue,
        CancellationTokenSource cts,
        Guid? presetId,
        int? presetVersion)
    {
        try
        {
            var outcome = await _orchestrator.RunAsync(
                probe,
                request,
                new RunOptions
                {
                    Save = save,
                    OnSample = queue.Enqueue,

                    // В отличие от сырых измерений, ход подготовки не копится в очереди:
                    // сообщений тут единицы за прогон, и каждое надо показать сразу.
                    // Именно оно объясняет оператору, почему прогон стоит и что
                    // сделать на второй машине, чтобы он пошёл.
                    OnProgress = message => Dispatcher.UIThread.Post(() => run.Report(message)),
                    PresetId = presetId,
                    PresetVersion = presetVersion,
                },
                cts.Token).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() => run.Finish(outcome));
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() => run.Fail(ex.Message));
        }
        finally
        {
            cts.Dispose();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Active.Remove(run);
                ActiveChanged?.Invoke(this, EventArgs.Empty);
            });
        }
    }
}
