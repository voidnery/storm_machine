using Avalonia;
using Avalonia.Controls.Primitives;

namespace StormMachine.App.Controls;

/// <summary>
/// Плитка-счётчик: подпись сверху, значение крупно под ней.
/// </summary>
/// <remarks>
/// Форма подсмотрена у показателей внизу «Задержки» — единственного места, где числа
/// читались с расстояния. Всюду ещё числа были вклеены в предложения («Журнал: 541
/// прогонов, 3908 сэмплов, 12.4 МБ»), и чтобы узнать одно, приходилось прочитать все.
/// </remarks>
public class StatTile : TemplatedControl
{
    public static readonly StyledProperty<string?> CaptionProperty =
        AvaloniaProperty.Register<StatTile, string?>(nameof(Caption));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<StatTile, string?>(nameof(Value));

    public string? Caption
    {
        get => GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
