using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;

namespace StormMachine.App.Controls;

/// <summary>Состояние длящейся операции — то, чем управляется знак у строки статуса.</summary>
public enum OperationState
{
    /// <summary>Ничего не происходило: строка немая.</summary>
    None,

    /// <summary>Операция идёт прямо сейчас.</summary>
    Running,

    /// <summary>Закончилась и сделала, что обещала.</summary>
    Done,

    /// <summary>Закончилась отказом.</summary>
    Failed,
}

/// <summary>
/// Строка статуса операции: знак состояния и текст на своём месте.
/// </summary>
/// <remarks>
/// «Прогон остановлен. Измеренное сохранено.» жило справа от поля с именем пресета
/// и терялось: место у строки было случайное, а знака состояния не было вовсе, поэтому
/// «идёт» и «кончилось отказом» выглядели одинаково. Здесь состояние несут знак и цвет,
/// а место у строки постоянное — под панелью запуска, где её и ищут.
/// <para>
/// Как и у <see cref="Caveat"/>, видимостью распоряжается сам элемент: пустой текст —
/// нет строки, и привязывать <see cref="Avalonia.Visual.IsVisible"/> снаружи не нужно.
/// </para>
/// </remarks>
public class StatusLine : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StatusLine, string?>(nameof(Text));

    public static readonly StyledProperty<OperationState> StateProperty =
        AvaloniaProperty.Register<StatusLine, OperationState>(nameof(State));

    public StatusLine() => IsVisible = false;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public OperationState State
    {
        get => GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            IsVisible = !string.IsNullOrWhiteSpace(Text);
        }
        else if (change.Property == StateProperty)
        {
            PseudoClasses.Set(":running", State == OperationState.Running);
            PseudoClasses.Set(":done", State == OperationState.Done);
            PseudoClasses.Set(":failed", State == OperationState.Failed);
        }
    }
}
