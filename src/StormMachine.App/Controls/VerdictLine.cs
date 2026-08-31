using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using StormMachine.Domain.Results;

namespace StormMachine.App.Controls;

/// <summary>
/// Вердикт: знак уровня и слово, которым продукт этот уровень называет.
/// </summary>
/// <remarks>
/// Итог измерения набирался как обычный текст и проигрывал в заметности синей кнопке
/// рядом. Знак берётся из <see cref="VerdictWording"/>, а не пишется в разметке:
/// словарь вердиктов один на консоль и окно, и разойтись им незачем — экраны сказали бы
/// об одном измерении разными знаками, и оператор решил бы, что видит разные вещи.
/// Цвет уровня — не единственный носитель смысла: знак читается и без цвета.
/// </remarks>
public class VerdictLine : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<VerdictLine, string?>(nameof(Text));

    public static readonly StyledProperty<VerdictLevel> LevelProperty =
        AvaloniaProperty.Register<VerdictLine, VerdictLevel>(nameof(Level));

    public static readonly DirectProperty<VerdictLine, string> MarkProperty =
        AvaloniaProperty.RegisterDirect<VerdictLine, string>(nameof(Mark), o => o.Mark);

    private string _mark = VerdictWording.Mark(VerdictLevel.Unknown);

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public VerdictLevel Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    /// <summary>Знак уровня из словаря вердиктов.</summary>
    public string Mark
    {
        get => _mark;
        private set => SetAndRaise(MarkProperty, ref _mark, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        base.OnPropertyChanged(change);

        if (change.Property != LevelProperty)
        {
            return;
        }

        Mark = VerdictWording.Mark(Level);

        PseudoClasses.Set(":pass", Level == VerdictLevel.Pass);
        PseudoClasses.Set(":warn", Level == VerdictLevel.Warn);
        PseudoClasses.Set(":fail", Level == VerdictLevel.Fail);
    }
}
