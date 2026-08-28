using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Уровень вердикта в признак класса стиля.
/// </summary>
/// <remarks>
/// Три отдельных преобразователя вместо одного с параметром: параметр в разметке —
/// строка, и опечатка в ней не ловится ни компилятором, ни анализатором, а проявляется
/// молчаливо потерянной подсветкой отказа. Здесь опечатка не соберётся.
/// </remarks>
public sealed class VerdictLevelConverter(VerdictLevel level) : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is VerdictLevel actual && actual == level;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Свойство только для показа.");
}

/// <summary>
/// Уровень вердикта в цвет точки состояния.
/// </summary>
/// <remarks>
/// Цвет здесь несёт смысл, а не оформление: в списке мониторов его читают первым,
/// раньше имени. Поэтому и различаются все четыре состояния, включая «ещё не
/// проверялся» — серый, а не зелёный: не проверенное не есть исправное.
/// </remarks>
public sealed class VerdictBrushConverter : IValueConverter
{
    public static readonly VerdictBrushConverter Instance = new();

    private static readonly IBrush Pass = new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xA0));
    private static readonly IBrush Warn = new SolidColorBrush(Color.FromRgb(0xE0, 0xB1, 0x5C));
    private static readonly IBrush Fail = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));
    private static readonly IBrush Unknown = new SolidColorBrush(Color.FromRgb(0x5A, 0x63, 0x75));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            VerdictLevel.Pass => Pass,
            VerdictLevel.Warn => Warn,
            VerdictLevel.Fail => Fail,
            _ => Unknown,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Свойство только для показа.");
}

/// <summary>Готовые преобразователи для разметки.</summary>
public static class VerdictConverters
{
    public static VerdictLevelConverter IsPass { get; } = new(VerdictLevel.Pass);

    public static VerdictLevelConverter IsWarn { get; } = new(VerdictLevel.Warn);

    public static VerdictLevelConverter IsFail { get; } = new(VerdictLevel.Fail);
}
