using System.Globalization;
using StormMachine.Application.Scenarios;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Выполняющийся сценарий в панели операций.
/// </summary>
/// <remarks>
/// Сценарий по восьми целям идёт минутами — это самая длинная операция продукта,
/// и именно её не было видно в списке до И-14. Строка хода показывает шаг и цель,
/// а не проценты: доля выполненного у цепочки с обрывом при отказе всё равно
/// ничего не обещает.
/// </remarks>
public sealed class ActiveScenarioViewModel(string title, CancellationTokenSource cancellation)
    : ActiveOperationViewModel("сценарий", title, cancellation)
{
    private string? _targetLabel;

    /// <summary>Цель, которая проверяется сейчас, — в наборе целей их много.</summary>
    public void SetTarget(string? target)
    {
        _targetLabel = target;
        Detail = target ?? string.Empty;
    }

    public void Report(ScenarioProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var step = $"шаг {(progress.StepIndex + 1).ToString(CultureInfo.InvariantCulture)}"
                   + $" из {progress.StepCount.ToString(CultureInfo.InvariantCulture)}: {progress.StepName}";

        Detail = _targetLabel is null ? step : $"{_targetLabel} · {step}";
    }

    internal void Finish() => Complete();
}
