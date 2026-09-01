using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Длительная операция, видимая в панели операций.
/// </summary>
/// <remarks>
/// Появилась в И-14 и закрыла долг И-11: список выполняющихся операций знал только
/// про пробы, а сценарий — самая длинная операция в продукте — шёл мимо него.
/// Оператор, запустивший проверку восьми целей и ушедший на другой экран, не имел
/// способа узнать, идёт ли она ещё, и не мог её остановить.
/// <para>
/// Тип общий, потому что оболочке всё равно, что именно выполняется: ей нужны
/// значок вида, название, строка хода и кнопка отмены. Что стоит за ними —
/// одна проба, цепочка шагов или проверка монитора — дело того, кто операцию завёл.
/// </para>
/// </remarks>
public abstract partial class ActiveOperationViewModel : ObservableObject
{
    private readonly CancellationTokenSource _cancellation;

    protected ActiveOperationViewModel(string kind, string title, CancellationTokenSource cancellation)
    {
        Kind = kind;
        Title = title;
        _cancellation = cancellation ?? throw new ArgumentNullException(nameof(cancellation));
        StartedAt = DateTimeOffset.Now;
    }

    /// <summary>Короткая пометка вида: <c>ping</c>, <c>сценарий</c>, <c>монитор</c>.</summary>
    public string Kind { get; }

    public string Title { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>Строка хода: сколько проб пришло, какой шаг идёт.</summary>
    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    private bool _isFinished;

    [ObservableProperty]
    private string? _error;

    public bool CanCancel => !IsFinished;

    public event EventHandler? Finished;

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

    protected void Complete(string? error = null)
    {
        Error = error;
        IsFinished = true;
        OnPropertyChanged(nameof(CanCancel));
        Finished?.Invoke(this, EventArgs.Empty);
    }
}
