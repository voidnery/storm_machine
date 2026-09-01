using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StormMachine.App.Views.Controls;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Цвет строки факта.
/// </summary>
/// <remarks>
/// Признак «проба на это указала» ставит сама проба, а не показ. Разбирать текст факта
/// в поисках тревожных слов значило бы завести второе, расходящееся с первым, мнение
/// о том, что считать находкой.
/// </remarks>
public sealed class FactBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true
            ? DesignTokens.Brush(DesignTokens.Warning)
            : DesignTokens.Brush(DesignTokens.Text);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Свойство только для показа.");
}

/// <summary>Готовые преобразователи для разметки.</summary>
public static class FactBrushes
{
    public static FactBrushConverter ByWarning { get; } = new();
}
