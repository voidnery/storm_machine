using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

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
    private static readonly IBrush Warning = new SolidColorBrush(Color.FromRgb(0xD9, 0xA4, 0x41));
    private static readonly IBrush Normal = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xE6));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Warning : Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Свойство только для показа.");
}

/// <summary>Готовые преобразователи для разметки.</summary>
public static class FactBrushes
{
    public static FactBrushConverter ByWarning { get; } = new();
}
