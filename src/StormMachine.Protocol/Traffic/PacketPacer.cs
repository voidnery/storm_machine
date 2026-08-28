using System.Diagnostics;

namespace StormMachine.Protocol.Traffic;

/// <summary>
/// Темповка пакетов: выдержать заданный интервал между отправками.
/// </summary>
/// <remarks>
/// <see cref="Thread.Sleep(int)"/> непригоден: спайк-01 измерил, что <c>Sleep(1)</c> спит
/// вдвое дольше запрошенного (p50 0.994 мс при p95 1.511 мс ошибки), а <c>timeBeginPeriod(1)</c>
/// этого не чинит — с Windows 10 2004 он влияет только на свой процесс. Гибрид «отдать квант,
/// пока далеко, дальше крутиться» дал ошибку такта p95 0.001 мс, и спайк-05 подтвердил это
/// уже со сквозной отправкой в сокет: при 0.1 мс между пакетами p99 остался нулевым.
/// <para>
/// Цена известна и названа заранее: <b>101 % одного ядра</b> на время теста (спайк-05).
/// Это не дефект, а условие точности, и генератор обязан идти в выделенном потоке,
/// а не в пуле — иначе он отберёт поток у всего остального.
/// </para>
/// </remarks>
public sealed class PacketPacer
{
    /// <summary>Порог перехода на кручение: ближе — не отдаём квант, чтобы не проспать.</summary>
    private static readonly long SpinThresholdTicks = Stopwatch.Frequency / 2000;

    private readonly long _intervalTicks;
    private long _next;

    public PacketPacer(double intervalMs)
    {
        if (intervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalMs), intervalMs, "Интервал должен быть больше нуля.");
        }

        _intervalTicks = Math.Max(1, (long)(Stopwatch.Frequency * intervalMs / 1000.0));
        _next = Stopwatch.GetTimestamp() + _intervalTicks;
    }

    /// <summary>Интервал, вычисленный по целевой скорости и размеру пакета.</summary>
    public static double IntervalFor(double targetMbps, int payloadBytes)
    {
        if (targetMbps <= 0 || payloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetMbps), "Скорость и размер должны быть больше нуля.");
        }

        var packetsPerSecond = targetMbps * 1_000_000.0 / (payloadBytes * 8.0);

        return 1000.0 / packetsPerSecond;
    }

    /// <summary>
    /// Ждёт следующего такта и возвращает ошибку в миллисекундах.
    /// </summary>
    /// <remarks>
    /// Следующий такт отсчитывается от намеченного, а не от фактического момента.
    /// Иначе ошибка накапливалась бы: опоздали на десятую долю миллисекунды тысячу раз —
    /// и тест длиннее задуманного на десятую долю секунды, а скорость посчитана не по той
    /// длительности.
    /// </remarks>
    public double WaitForNext()
    {
        var spin = new SpinWait();

        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var left = _next - now;

            if (left <= 0)
            {
                var error = -left * 1000.0 / Stopwatch.Frequency;
                _next += _intervalTicks;

                return error;
            }

            if (left > SpinThresholdTicks)
            {
                Thread.Sleep(0);
                continue;
            }

            spin.SpinOnce(-1);
        }
    }

    /// <summary>
    /// Пропускает пропущенные такты.
    /// </summary>
    /// <remarks>
    /// Нужно после долгой заминки: если поток простоял пятьдесят миллисекунд, честно
    /// догонять пятьдесят пропущенных тактов значит выдать очередь пакетов подряд —
    /// то есть измерить не то, что задумано. Пропущенное признаётся пропущенным.
    /// </remarks>
    public int SkipMissed()
    {
        var now = Stopwatch.GetTimestamp();
        var skipped = 0;

        while (_next < now)
        {
            _next += _intervalTicks;
            skipped++;
        }

        return skipped;
    }
}
