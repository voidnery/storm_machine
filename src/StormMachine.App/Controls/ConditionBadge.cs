using Avalonia;
using Avalonia.Controls.Primitives;

namespace StormMachine.App.Controls;

/// <summary>
/// Бейдж условия измерения: подпись и значение одним чипом.
/// </summary>
/// <remarks>
/// Интерфейс, собственный порог часов, методика — это не описание страницы, а условия,
/// в которых получены числа под ней. Их сравнивают между запусками, и предложением
/// («Ethernet · порог 0.221 мс · ICMP RFC 792») сравнивать неудобно: глаз ищет границы
/// значений там, где стоят точки. Чипы дают границы сами.
/// </remarks>
public class ConditionBadge : TemplatedControl
{
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<ConditionBadge, string?>(nameof(Label));

    public static readonly StyledProperty<string?> ValueProperty =
        AvaloniaProperty.Register<ConditionBadge, string?>(nameof(Value));

    public string? Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string? Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }
}
