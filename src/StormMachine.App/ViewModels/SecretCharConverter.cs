using System.Globalization;
using Avalonia.Data.Converters;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Признак секрета — в символ маскировки поля.
/// </summary>
/// <remarks>
/// Форма настроек каналов строится по объявлению, и какие поля секретны, объявляет
/// само объявление. Заводить ради этого два разных шаблона поля значило бы держать
/// две копии одной разметки и однажды забыть замаскировать пароль в одной из них.
/// </remarks>
public sealed class SecretCharConverter : IValueConverter
{
    public static SecretCharConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? '•' : '\0';

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Маскировка поля читается только в одну сторону.");
}
