using System.Globalization;
using Avalonia.Data.Converters;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Приглушает строку недоступного устройства.
/// </summary>
/// <remarks>
/// В списке приходится различать «отвечает» и «известно, но молчит». Классы стилей
/// здесь не годятся: строки живут внутри ListBox, у которого свои состояния выбора
/// и наведения, и класс на элементе с ними конфликтует.
/// </remarks>
public sealed class OpacityConverter : IValueConverter
{
    public static readonly OpacityConverter Instance = new();

    private const double Dimmed = 0.45;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : Dimmed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Свойство только для показа.");
}
