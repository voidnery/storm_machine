using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using StormMachine.Application.Abstractions;

namespace StormMachine.Platform;

/// <summary>
/// Таймер измерений на основе <see cref="Stopwatch"/>.
/// </summary>
/// <remarks>
/// Замеры на стенде: разрешение 100 нс, стоимость чтения таймера — медиана 0 мкс,
/// p99 0.1 мкс. Этого хватает с большим запасом.
/// <para>
/// Существует потому, что значения задержки из системных API непригодны: они целочисленные
/// в миллисекундах и в локальной сети округляют почти всё в ноль
/// (docs/02-research.md, R-10).
/// </para>
/// </remarks>
public sealed class HighResolutionClock : IHighResolutionClock
{
    private const int CalibrationProbes = 60;
    private const int CalibrationWarmUp = 10;

    private static readonly double TicksToMilliseconds = 1000.0 / Stopwatch.Frequency;

    private double _calibrationBaselineMs;

    public double ResolutionNanoseconds => 1e9 / Stopwatch.Frequency;

    /// <summary>
    /// Пол разрешения измерительного стека, измеренный на loopback.
    /// </summary>
    /// <remarks>
    /// <b>Не вычитается из результатов.</b> Путь через loopback — это не чистые накладные
    /// расходы: там есть и собственная работа сетевого стека. Вычитание давало бы
    /// систематическую ошибку и на быстрых каналах уводило бы значения в минус.
    /// <para>
    /// Правильное прочтение: значения на уровне этого порога и ниже неотличимы от
    /// собственной работы измерительного стека. Величина попадает в отчёт как условие
    /// измерения, а не как поправка.
    /// </para>
    /// </remarks>
    public double CalibrationBaselineMs => _calibrationBaselineMs;

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public double ElapsedMilliseconds(long startTimestamp) =>
        (Stopwatch.GetTimestamp() - startTimestamp) * TicksToMilliseconds;

    public double ElapsedMilliseconds(long startTimestamp, long endTimestamp) =>
        (endTimestamp - startTimestamp) * TicksToMilliseconds;

    public Task CalibrateAsync(CancellationToken cancellationToken = default)
    {
        // Синхронный ICMP к loopback: асинхронный вариант добавил бы к замеру
        // стоимость планировщика задач, а мерим мы именно голый путь вызова.
        return Task.Run(() => Calibrate(cancellationToken), cancellationToken);
    }

    private void Calibrate(CancellationToken cancellationToken)
    {
        var measurements = new double[CalibrationProbes];
        var buffer = new byte[32];
        var taken = 0;

        try
        {
            using var ping = new Ping();

            for (var i = 0; i < CalibrationWarmUp + CalibrationProbes; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var start = Stopwatch.GetTimestamp();
                var reply = ping.Send(IPAddress.Loopback, 1000, buffer);
                var elapsed = ElapsedMilliseconds(start);

                if (i < CalibrationWarmUp || reply.Status != IPStatus.Success)
                {
                    continue;
                }

                measurements[taken++] = elapsed;

                if (taken == CalibrationProbes)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Калибровка — уточнение, а не условие работы. Если loopback недоступен
            // (бывает на машинах с жёсткими политиками), продолжаем с нулевым порогом
            // и честно показываем это в условиях измерения.
            _calibrationBaselineMs = 0;
            return;
        }

        if (taken == 0)
        {
            _calibrationBaselineMs = 0;
            return;
        }

        var sample = measurements.AsSpan(0, taken).ToArray();
        Array.Sort(sample);

        // Медиана, а не среднее: одиночный выброс от планировщика ОС не должен
        // сдвигать порог разрешения.
        _calibrationBaselineMs = sample[sample.Length / 2];
    }
}
