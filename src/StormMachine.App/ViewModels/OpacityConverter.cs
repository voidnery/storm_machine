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

/// <summary>
/// Приглушает строку, которая не была наблюдением.
/// </summary>
/// <remarks>
/// Обратный к <see cref="OpacityConverter"/> по смыслу: там истина означает «жив
/// и отвечает», здесь — «проверки не было». Пропуск и обслуживание видны в истории
/// наравне с измерениями, но не должны читаться как измерения.
/// </remarks>
public sealed class GapOpacityConverter : IValueConverter
{
    public static readonly GapOpacityConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.45 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Свойство только для показа.");
}
