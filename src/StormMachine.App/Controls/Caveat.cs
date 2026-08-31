using Avalonia;
using Avalonia.Controls.Primitives;

namespace StormMachine.App.Controls;

/// <summary>
/// Оговорка честности: то, чего измерение <b>не</b> доказывает.
/// </summary>
/// <remarks>
/// «Не равно недоступности», «покрытие 0 % — доверять нельзя», «сырые сэмплы удалены
/// политикой» — строки, ради которых продукт и считается честным. Набранные как
/// остальные подписи, они читаются как оформление; здесь у них знак и цвет
/// предупреждения — тот же язык, которым уже говорит полоса об окружении.
/// <para>
/// Видимость управляется самой оговоркой: пустой текст — нет строки. Поэтому
/// <see cref="Avalonia.Visual.IsVisible"/> у этого элемента не привязывают —
/// привязка была бы перебита присвоением отсюда.
/// </para>
/// </remarks>
public class Caveat : TemplatedControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Caveat, string?>(nameof(Text));

    public Caveat() => IsVisible = false;

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            IsVisible = !string.IsNullOrWhiteSpace(Text);
        }
    }
}
