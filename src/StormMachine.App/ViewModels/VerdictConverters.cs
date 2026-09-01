using System.Globalization;
using Avalonia.Data.Converters;
using StormMachine.App.Views.Controls;
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

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        DesignTokens.Brush(value switch
        {
            VerdictLevel.Pass => DesignTokens.Success,
            VerdictLevel.Warn => DesignTokens.Warning,
            VerdictLevel.Fail => DesignTokens.Danger,
            _ => DesignTokens.TextMuted,
        });

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
