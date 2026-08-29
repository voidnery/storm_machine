using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;

namespace StormMachine.Application.Runs;

/// <summary>
/// Единственное место, где собираются условия измерения.
/// </summary>
/// <remarks>
/// Принцип 12 (docs/01-analysis.md §8.2) требует, чтобы каждое измерение несло условия,
/// в которых сделано, — иначе два результата несопоставимы. Собирать эти условия
/// в нескольких местах значит обесценить сам принцип: копии расходятся.
/// <para>
/// Так и вышло. К И-19 сборщиков было пять — оркестратор, шапка консоли, экран окружения
/// и два экрана графического клиента, — и один из них уже отстал: шапка консоли не
/// заполняла профиль, который оркестратор заполнял. Оператор читал перед прогоном одни
/// условия, а в журнал ложились другие. Данные при этом не терялись, но именно так
/// расхождение и начинается.
/// </para>
/// <para>
/// Профиль передаётся, а не берётся отсюда: хранилище профилей необязательно, обращение
/// к нему асинхронно, и вытягивать его в сборщик значило бы сделать построение условий
/// операцией с вводом-выводом. Забрать имя профиля — дело вызывающего;
/// <see cref="ActiveProfileAsync"/> рядом, чтобы и это делалось одинаково.
/// </para>
/// </remarks>
public static class MeasurementConditions
{
    public static MeasurementContext Build(
        NetworkAdapter? adapter,
        IHighResolutionClock clock,
        Methodology methodology,
        string? profile = null,
        string? backend = null)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new MeasurementContext
        {
            InterfaceName = adapter?.Name ?? "неизвестен",
            AdapterKind = adapter?.Kind ?? AdapterKind.Unknown,
            InterfaceAddress = adapter?.IPv4Address,
            CalibrationBaselineMs = clock.CalibrationBaselineMs,
            ProductVersion = ProductInfo.Version,
            Methodology = methodology,
            Profile = profile,
            Backend = backend,
            StartedUtc = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Имя активного профиля, если профили есть и читаются.
    /// </summary>
    /// <remarks>
    /// Отсутствие профиля — не ошибка: продукт полностью работоспособен без них,
    /// и прогон из-за нечитаемого хранилища останавливать незачем. Условия просто
    /// останутся без этой строки.
    /// </remarks>
    public static async Task<string?> ActiveProfileAsync(
        IProfileStore? profiles,
        CancellationToken cancellationToken = default)
    {
        if (profiles is null)
        {
            return null;
        }

        try
        {
            return (await profiles.GetActiveAsync(cancellationToken).ConfigureAwait(false))?.Name;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
