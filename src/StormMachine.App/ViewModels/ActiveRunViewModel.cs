using System.Collections.Concurrent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Domain.Measurements;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Выполняющийся прогон: то, что видно в Run Drawer, и источник сэмплов для графика.
/// </summary>
/// <remarks>
/// Очередь сэмплов заполняется фоновым потоком и разбирается вызовом
/// <see cref="Drain"/> из потока интерфейса. Такое разделение позволяет странице
/// обновлять график по таймеру, а не на каждый пришедший сэмпл.
/// </remarks>
public sealed partial class ActiveRunViewModel : ObservableObject
{
    private readonly ConcurrentQueue<Sample> _queue;
    private readonly CancellationTokenSource _cancellation;

    public ActiveRunViewModel(
        ProbeDescriptor descriptor,
        string title,
        ConcurrentQueue<Sample> queue,
        CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Descriptor = descriptor;
        Title = title;
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        StartedAt = DateTimeOffset.Now;
    }

    public ProbeDescriptor Descriptor { get; }

    public string Title { get; }

    public DateTimeOffset StartedAt { get; }

    public string ProbeName => Descriptor.Name;

    [ObservableProperty]
    private int _received;

    [ObservableProperty]
    private bool _isFinished;

    [ObservableProperty]
    private string? _error;

    /// <summary>Итог прогона. Заполняется после завершения.</summary>
    public RunOutcome? Outcome { get; private set; }

    public event EventHandler? Finished;

    public bool CanCancel => !IsFinished;

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
        }

        return drained;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (IsFinished)
        {
            return;
        }

        // Отмена не выбрасывает измеренное: оркестратор досчитывает итог
        // и — при сохранении — дописывает журнал.
        _cancellation.Cancel();
    }

    internal void Finish(RunOutcome outcome)
    {
        Outcome = outcome;
        IsFinished = true;
        OnPropertyChanged(nameof(CanCancel));
        Finished?.Invoke(this, EventArgs.Empty);
    }

    internal void Fail(string message)
    {
        Error = message;
        IsFinished = true;
        OnPropertyChanged(nameof(CanCancel));
        Finished?.Invoke(this, EventArgs.Empty);
    }
}
