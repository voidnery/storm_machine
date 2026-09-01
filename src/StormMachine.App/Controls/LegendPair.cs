using Avalonia;
using Avalonia.Controls.Primitives;

namespace StormMachine.App.Controls;

/// <summary>
/// Легенда парой: знак и то, что он означает.
/// </summary>
/// <remarks>
/// Легенды были написаны предложениями: «Приглушённая строка — устройство известно,
/// но в последнем сканировании не ответило. Пометка ✎ означает имя, присвоенное
/// оператором.» Чтобы понять один знак, приходилось прочитать про все. Пара ставит
/// знак слева, значение справа, и глаз находит нужную строку, а не разбирает абзац.
/// </remarks>
public class LegendPair : TemplatedControl
{
    /// <summary>Знак: символ, слово или образец начертания.</summary>
    public static readonly StyledProperty<string?> SignProperty =
        AvaloniaProperty.Register<LegendPair, string?>(nameof(Sign));

    public static readonly StyledProperty<string?> MeaningProperty =
        AvaloniaProperty.Register<LegendPair, string?>(nameof(Meaning));

    public string? Sign
    {
        get => GetValue(SignProperty);
        set => SetValue(SignProperty, value);
    }

    public string? Meaning
    {
        get => GetValue(MeaningProperty);
        set => SetValue(MeaningProperty, value);
    }
}
