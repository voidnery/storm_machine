namespace StormMachine.Application.Abstractions;

/// <summary>
/// Таймер измерений. Порт: реализация живёт в слое платформы.
/// </summary>
/// <remarks>
/// Существует потому, что <b>значения задержки из системных API использовать нельзя</b>.
/// Замер на стенде: <c>PingReply.RoundtripTime</c> вернул на 300 проб всего 6 различимых
/// значений (0…5 мс), тогда как собственный таймер — 285. В локальной сети, где RTT меньше
/// миллисекунды, системный API округляет почти всё в ноль, и джиттер, PDV и MOS,
/// посчитанные по нему, оказались бы мусором (docs/02-research.md, R-10).
/// <para>
/// Это принцип 8 из docs/01-analysis.md §8.2 и правило, нарушение которого роняет сборку.
/// </para>
/// </remarks>
public interface IHighResolutionClock
{
    /// <summary>Разрешение таймера в наносекундах. На стенде — 100 нс.</summary>
    double ResolutionNanoseconds { get; }

    /// <summary>
    /// Накладные расходы измерительного стека, измеренные на loopback при старте.
    /// Вычитаются из результата. На стенде — около 0.27 мс.
    /// </summary>
    double CalibrationBaselineMs { get; }

    long GetTimestamp();

    double ElapsedMilliseconds(long startTimestamp);

    double ElapsedMilliseconds(long startTimestamp, long endTimestamp);

    /// <summary>Измеряет базис накладных расходов. Вызывается один раз при запуске.</summary>
    Task CalibrateAsync(CancellationToken cancellationToken = default);
}
