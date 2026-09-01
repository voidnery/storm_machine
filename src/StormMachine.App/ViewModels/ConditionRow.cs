using System.Globalization;
using StormMachine.Domain.Measurements;

namespace StormMachine.App.ViewModels;

/// <summary>Условие измерения одним бейджем: что и какое.</summary>
public sealed record ConditionRow(string Label, string Value);

/// <summary>
/// Разбор условий измерения на бейджи — одинаково для всех страниц.
/// </summary>
/// <remarks>
/// Сами условия собирает <see cref="StormMachine.Application.Runs.MeasurementConditions"/>
/// и только он: копии расходятся. Здесь ровно перевод собранного в показываемое, и он тоже
/// один — иначе «Задержка» и «Анализ пути» начнут называть один и тот же порог разными
/// словами, а сравнивать условия между запусками оператору придётся через перевод в уме.
/// </remarks>
public static class ConditionRows
{
    public static IEnumerable<ConditionRow> From(MeasurementContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        yield return new ConditionRow("интерфейс", context.InterfaceName);

        // Порог часов — не техническая мелочь: ниже него измерять нечем, и число
        // рядом с медианой в 0.2 мс говорит, доверять ей или нет.
        yield return new ConditionRow(
            "порог часов",
            context.CalibrationBaselineMs > 0
                ? context.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture) + " мс"
                : "не измерен");

        yield return new ConditionRow("методика", context.Methodology.ToString());

        if (context.Profile is { Length: > 0 } profile)
        {
            yield return new ConditionRow("профиль", profile);
        }
    }
}
