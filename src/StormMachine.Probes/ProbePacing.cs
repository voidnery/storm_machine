using StormMachine.Application.Abstractions;

namespace StormMachine.Probes;

/// <summary>
/// Выдерживание интервала между пробами.
/// </summary>
/// <remarks>
/// Вынесено из <see cref="IcmpProbe"/> в И-2: тот же расчёт понадобился TCP- и UDP-пробам.
/// <para>
/// Гибридное ожидание: основную часть спим, последние доли миллисекунды выбираем активным
/// ожиданием. Причина — замеры этапа исследования: <c>Thread.Sleep(1)</c> ошибается примерно
/// на миллисекунду, и <c>timeBeginPeriod</c> этого не исправляет (docs/02-research.md, R-10).
/// </para>
/// </remarks>
internal static class ProbePacing
{
    private const double SpinTailMs = 2.0;

    /// <summary>
    /// Ждёт до момента следующей отправки, считая от <paramref name="previousSendTimestamp"/>.
    /// </summary>
    /// <remarks>
    /// Интервал отсчитывается от предыдущей <b>отправки</b>, а не от получения ответа.
    /// Иначе темп «плывёт» вслед за задержкой сети: чем хуже канал, тем реже пробы,
    /// и тем оптимистичнее выглядит статистика.
    /// </remarks>
    public static async Task WaitUntilNextAsync(
        IHighResolutionClock clock,
        long previousSendTimestamp,
        int intervalMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clock);

        var remaining = intervalMs - clock.ElapsedMilliseconds(previousSendTimestamp);

        if (remaining <= 0)
        {
            return;
        }

        if (remaining > SpinTailMs)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(remaining - SpinTailMs), cancellationToken).ConfigureAwait(false);
        }

        while (clock.ElapsedMilliseconds(previousSendTimestamp) < intervalMs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.SpinWait(50);
        }
    }
}
