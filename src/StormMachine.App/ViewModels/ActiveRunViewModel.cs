using System.Collections.Concurrent;
using System.Globalization;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Domain.Measurements;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Выполняющийся прогон пробы: то, что видно в Run Drawer, и источник сэмплов для графика.
/// </summary>
/// <remarks>
/// Очередь сэмплов заполняется фоновым потоком и разбирается вызовом
/// <see cref="Drain"/> из потока интерфейса. Такое разделение позволяет странице
/// обновлять график по таймеру, а не на каждый пришедший сэмпл.
/// </remarks>
public sealed class ActiveRunViewModel : ActiveOperationViewModel
{
    private readonly ConcurrentQueue<Sample> _queue;

    public ActiveRunViewModel(
        ProbeDescriptor descriptor,
        string title,
        ConcurrentQueue<Sample> queue,
        CancellationTokenSource cancellation)
        : base(descriptor?.Name ?? throw new ArgumentNullException(nameof(descriptor)), title, cancellation)
    {
        Descriptor = descriptor;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public ProbeDescriptor Descriptor { get; }

    public string ProbeName => Descriptor.Name;

    public int Received { get; private set; }

    /// <summary>Итог прогона. Заполняется после завершения.</summary>
    public RunOutcome? Outcome { get; private set; }

    /// <summary>
    /// Забирает накопленные сэмплы. Вызывается только из потока интерфейса.
    /// </summary>
    public List<Sample> Drain()
    {
        var drained = new List<Sample>();

        while (_queue.TryDequeue(out var sample))
        {
            drained.Add(sample);
        }

        if (drained.Count > 0)
        {
            Received += drained.Count;
            Detail = $"проб: {Received.ToString(CultureInfo.InvariantCulture)}";
        }

        return drained;
    }

    internal void Finish(RunOutcome outcome)
    {
        Outcome = outcome;
        Complete();
    }

    internal void Fail(string message) => Complete(message);
}
