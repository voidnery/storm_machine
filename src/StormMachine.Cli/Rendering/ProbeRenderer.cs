using System.Diagnostics;
using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Cli.Commands;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ результатов пробы. Способ подачи выбирается по объявленной форме результата.
/// </summary>
/// <remarks>
/// Итерация И-2 показала, что шесть проб дают четыре несводимые формы данных. Ряд чисел,
/// водопад фаз, набор рядов для сравнения и матрица «хоп × попытка» требуют разного показа:
/// перцентили бессмысленны для водопада, а водопад бессмыслен для traceroute.
/// <para>
/// Форма берётся из объявления пробы, а не угадывается по содержимому сэмплов. Угадывание
/// работало бы до первой пробы, которая не вписалась в эвристику.
/// </para>
/// </remarks>
internal static class ProbeRenderer
{
    private const int WaterfallWidth = 40;

    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Собирает условия измерения для показа.
    /// </summary>
    /// <remarks>
    /// Живёт здесь, а не в фабрике команд, потому что нужен и обычному запуску пробы,
    /// и запуску из библиотеки пресетов. Условия измерения показываются одинаково
    /// независимо от того, откуда пришли параметры.
    /// </remarks>
    public static MeasurementContext BuildContext(
        NetworkAdapter? adapter,
        IHighResolutionClock clock,
        Methodology methodology)
    {
        ArgumentNullException.ThrowIfNull(clock);

        return new MeasurementContext
        {
            InterfaceName = adapter?.Name ?? "неизвестен",
            AdapterKind = adapter?.Kind ?? AdapterKind.Unknown,
            InterfaceAddress = adapter?.IPv4Address,
            CalibrationBaselineMs = clock.CalibrationBaselineMs,
            ProductVersion = Application.ProductInfo.Version,
            Methodology = methodology,
            StartedUtc = DateTimeOffset.UtcNow,
        };
    }

    public static void WriteHeader(
        ProbeDescriptor descriptor,
        Target target,
        MeasurementContext context,
        NetworkAdapter? adapter)
    {
        Console.WriteLine($"Проба     : {descriptor.Title}");
        Console.WriteLine($"Цель      : {target.DisplayName}");
        Console.WriteLine($"Интерфейс : {context.InterfaceName} ({Describe.AdapterKind(context.AdapterKind)})"
                          + (adapter?.IPv4Address is { } ip ? $", {ip}" : string.Empty));
        Console.WriteLine($"Методика  : {context.Methodology}");
        Console.WriteLine($"Порог     : {F(context.CalibrationBaselineMs)} мс — ниже него измерения недостоверны");

        if (context.TimingWarning is { } warning)
        {
            Console.WriteLine();
            Console.WriteLine($"ВНИМАНИЕ: {warning}");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// Готовит живой вывод под форму результата.
    /// </summary>
    /// <remarks>
    /// Появилось в И-7. Непрерывный MTR — первая проба, живой вывод которой требует памяти
    /// о предыдущих сэмплах: строка на каждую пробу дала бы тридцать строк в секунду
    /// на час наблюдения. Поэтому вместо статического метода — замыкание с состоянием
    /// на один прогон.
    /// </remarks>
    public static Action<Sample> CreateLiveWriter(ProbeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (descriptor.Shape != ProbeResultShape.PathTrace)
        {
            return new DenseLiveWriter(descriptor).Write;
        }

        var writer = new PathLiveWriter();

        return writer.Write;
    }

    public static void WriteLiveSample(ProbeDescriptor descriptor, Sample sample)
    {
        switch (descriptor.Shape)
        {
            case ProbeResultShape.PhasedTiming:
                // Фазы показываются целиком после завершения попытки: по одной строке
                // они не читаются, смысл именно в соотношении между ними.
                return;

            case ProbeResultShape.PathTrace:
                // Живой вывод трассировки требует памяти о предыдущих сэмплах —
                // им занимается PathLiveWriter из CreateLiveWriter.
                return;

            case ProbeResultShape.ComparedSeries:
                WriteComparedSample(sample, descriptor.Unit);
                return;

            default:
                WriteScalarSample(sample, descriptor.Unit);
                return;
        }
    }

    public static void WriteSummary(ProbeDescriptor descriptor, ProbeResult result, IHighResolutionClock clock)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {result.Target.DisplayName}"
                          + (result.ResolvedAddress is { } resolved && resolved != result.Target.DisplayName
                              ? $"  →  {resolved}"
                              : string.Empty)
                          + " ---");

        if (result.WasCancelled)
        {
            Console.WriteLine("Прогон прерван. Ниже — то, что успели измерить.");
        }

        switch (descriptor.Shape)
        {
            case ProbeResultShape.PhasedTiming:
                WriteWaterfall(result);
                break;

            case ProbeResultShape.ComparedSeries:
                WriteComparison(result, descriptor);
                break;

            case ProbeResultShape.PathTrace:
                WritePathSummary(result);
                break;

            default:
                WriteScalarSummary(result, clock);
                break;
        }

        Describe.WriteFacts(descriptor.Shape == ProbeResultShape.PathTrace
            ? RouteRenderer.RemainingFacts(result.Facts)
            : result.Facts);
    }

    // ------------------------------------------------------------ скалярный ряд

    private static void WriteScalarSample(Sample sample, MeasurementUnit unit)
    {
        if (sample.IsSuccess)
        {
            var ttl = sample.Ttl is { } t ? $"  TTL={t}" : string.Empty;
            Console.WriteLine($"  {sample.Sequence,5}  {F(sample.Value),9}{Describe.UnitSuffix(unit)}{ttl}");
            return;
        }

        Console.WriteLine($"  {sample.Sequence,5}  {Describe.SampleStatus(sample.Status),22}");
    }

    private static void WriteScalarSummary(ProbeResult result, IHighResolutionClock clock)
    {
        // «Отправлено, получено, потеряно» — язык проб задержки, где каждый сэмпл
        // это отдельный запрос. У пропускной способности сэмпл — это отсчёт скорости
        // за отрезок времени, и говорить о его потере бессмысленно.
        Console.WriteLine(result.Unit == MeasurementUnit.Milliseconds
            ? $"Отправлено {result.SentCount}, получено {result.SuccessCount}, "
              + $"потеряно {result.LostCount} ({result.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)}%)"
            : $"Отсчётов: {result.SuccessCount}");

        var stats = LatencyStatistics.Compute(result.Samples);

        if (stats.SampleCount == 0)
        {
            Console.WriteLine("Успешных ответов нет — статистику посчитать не по чему.");
            return;
        }

        // Скалярный ряд — не обязательно задержка. У пропускной способности значения
        // в мегабитах, и подписать их миллисекундами значило бы соврать о том, что
        // измерено. Джиттер и PDV к скорости неприменимы вовсе: это понятия о задержке,
        // и посчитать их по ряду скоростей можно, а истолковать нельзя.
        if (result.Unit != MeasurementUnit.Milliseconds)
        {
            WriteRateSummary(stats, result.Unit);
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  RTT        min {F(stats.MinMs)}   avg {F(stats.MeanMs)}   max {F(stats.MaxMs)} мс");
        Console.WriteLine($"  Перцентили p50 {F(stats.P50Ms)}   p95 {F(stats.P95Ms)}   p99 {F(stats.P99Ms)} мс");
        Console.WriteLine($"  Разброс    stddev {F(stats.StdDevMs)} мс");
        Console.WriteLine($"  Джиттер    {F(stats.JitterRfc3550Ms)} мс   (RFC 3550 §6.4.1)");
        Console.WriteLine($"  PDV        {F(stats.PdvMs)} мс   (p99 − p50)");

        if (stats.P50Ms <= clock.CalibrationBaselineMs && clock.CalibrationBaselineMs > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Замечание: медиана на уровне порога разрешения измерительного стека —");
            Console.WriteLine("  различить сеть и собственные накладные расходы на таких значениях нельзя.");
        }
    }

    /// <summary>
    /// Сводка ряда, измеренного не во времени.
    /// </summary>
    /// <remarks>
    /// Медиана, а не среднее, вынесена вперёд по той же причине, что и у задержки:
    /// одна просадка канала утягивает среднее и не трогает медиану, а вопрос
    /// «сколько обычно» — про медиану.
    /// </remarks>
    private static void WriteRateSummary(LatencyStatistics stats, MeasurementUnit unit)
    {
        var suffix = Describe.UnitSuffix(unit);

        Console.WriteLine();
        Console.WriteLine($"  Значения   min {F(stats.MinMs)}   медиана {F(stats.P50Ms)}   max {F(stats.MaxMs)}{suffix}");
        Console.WriteLine($"  Среднее    {F(stats.MeanMs)}{suffix}   разброс {F(stats.StdDevMs)}{suffix}");
        Console.WriteLine();
        Console.WriteLine("  Джиттер и PDV здесь не считаются: это понятия о задержке,");
        Console.WriteLine("  и к ряду скоростей они неприменимы.");
    }

    // ------------------------------------------------------------- водопад фаз

    private static void WriteWaterfall(ProbeResult result)
    {
        var attempts = result.Samples
            .GroupBy(s => s.Group ?? 0)
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var attempt in attempts)
        {
            var phases = attempt.Where(s => s.IsSuccess).ToList();

            if (phases.Count == 0)
            {
                var failed = attempt.First();
                Console.WriteLine($"Попытка {attempt.Key + 1}: {Describe.SampleStatus(failed.Status)} на фазе «{failed.Label}»");
                continue;
            }

            var total = phases.Sum(p => p.Value);

            if (attempts.Count > 1)
            {
                Console.WriteLine();
                Console.WriteLine($"Попытка {attempt.Key + 1}:");
            }

            Console.WriteLine();

            var offset = 0.0;
            foreach (var phase in phases)
            {
                var share = total > 0 ? phase.Value / total : 0;
                var startCell = (int)Math.Round(offset / Math.Max(total, 1e-9) * WaterfallWidth);
                var width = Math.Max(1, (int)Math.Round(share * WaterfallWidth));

                var bar = new string(' ', Math.Min(startCell, WaterfallWidth))
                          + new string('█', Math.Min(width, Math.Max(0, WaterfallWidth - startCell)));

                Console.WriteLine($"  {Describe.PhaseName(phase.Label),-14} {F(phase.Value),9} мс  {share,5:P0}  {bar}");
                offset += phase.Value;
            }

            Console.WriteLine($"  {"ИТОГО",-14} {F(total),9} мс");
        }

        // Какая фаза съедает время — единственный вывод, ради которого водопад и нужен.
        var slowest = result.Samples
            .Where(s => s.IsSuccess && s.Label is not null)
            .GroupBy(s => s.Label!)
            .Select(g => (Phase: g.Key, Total: g.Sum(s => s.Value)))
            .OrderByDescending(x => x.Total)
            .FirstOrDefault();

        if (slowest.Phase is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  Больше всего времени занимает фаза «{Describe.PhaseName(slowest.Phase)}».");
        }
    }

    // ------------------------------------------------- сравнение нескольких рядов

    private static void WriteComparedSample(Sample sample, MeasurementUnit unit)
    {
        var label = sample.Label ?? "—";

        if (sample.IsSuccess)
        {
            Console.WriteLine($"  {label,-16} {F(sample.Value),9}{Describe.UnitSuffix(unit)}");
            return;
        }

        var detail = sample.RespondedBy is { } code ? code : Describe.SampleStatus(sample.Status);
        Console.WriteLine($"  {label,-16} {detail,12}");
    }

    private static void WriteComparison(ProbeResult result, ProbeDescriptor descriptor)
    {
        var groups = result.Samples
            .Where(s => s.Label is not null)
            .GroupBy(s => s.Label!)
            .ToList();

        if (groups.Count == 0)
        {
            Console.WriteLine("Данных для сравнения нет.");
            return;
        }

        var noun = descriptor.SeriesNoun ?? "ряд";

        // Единица в заголовке, а не в каждой ячейке: колонки читаются как одно целое,
        // а без единицы «77.828» не говорит, много это или мало.
        var unit = Describe.UnitSuffix(descriptor.Unit).Trim();
        var header = unit.Length == 0 ? noun : $"{noun}, {unit}";

        Console.WriteLine();
        Console.WriteLine($"  {header,-18} {"мин",9} {"медиана",9} {"макс",9}   {"ответов",8}");

        foreach (var group in groups)
        {
            var stats = LatencyStatistics.Compute([.. group]);

            if (stats.SampleCount == 0)
            {
                Console.WriteLine($"  {group.Key,-18} {"нет ответа",-30}");
                continue;
            }

            Console.WriteLine($"  {group.Key,-18} {F(stats.MinMs),9} {F(stats.P50Ms),9} {F(stats.MaxMs),9}   "
                              + $"{stats.SampleCount,3} из {group.Count(),-3}");
        }

        // «Быстрее всех» — вывод только там, где ряды взаимозаменяемы. Для фаз одного
        // измерения это не вывод, а его предмет: то, что под нагрузкой медленнее,
        // и есть то, ради чего измерение делалось.
        if (!descriptor.SeriesAreAlternatives)
        {
            return;
        }

        var fastest = groups
            .Select(g => (Series: g.Key, Stats: LatencyStatistics.Compute([.. g])))
            .Where(x => x.Stats.SampleCount > 0)
            .OrderBy(x => x.Stats.P50Ms)
            .FirstOrDefault();

        if (fastest.Series is not null)
        {
            Console.WriteLine();
            Console.WriteLine($"  Быстрее всех отвечает {fastest.Series} — медиана {F(fastest.Stats.P50Ms)} мс.");
        }
    }

    /// <summary>
    /// Живой вывод, который не тонет в частых отсчётах.
    /// </summary>
    /// <remarks>
    /// Проба задержки под нагрузкой опрашивает цель двадцать раз в секунду — это её
    /// устройство, а не настройка: всплески, ради которых измерение и делается, на редких
    /// отсчётах не видны. Но триста строк в терминале не читает никто, и полезное в них
    /// теряется вернее, чем если бы их не было вовсе.
    /// <para>
    /// Поэтому вывод переключается сам: пока отсчёты редкие — строка на отсчёт, как
    /// у ping; как только они пошли чаще, дальше идёт сводка за полсекунды. Переключение
    /// объявляется вслух — молча сменить смысл строк значило бы заставить читателя
    /// гадать, что он видит. Ничего при этом не теряется: итоговая таблица считается
    /// по всем отсчётам, а не по показанным.
    /// </para>
    /// </remarks>
    private sealed class DenseLiveWriter(ProbeDescriptor descriptor)
    {
        /// <summary>Окно сводки. Полсекунды — предел, на котором глаз ещё считает строки живыми.</summary>
        private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(500);

        /// <summary>Сколько отсчётов в окне ещё читаются поштучно.</summary>
        private const int DenseThreshold = 4;

        private readonly Stopwatch _watch = Stopwatch.StartNew();
        private readonly Dictionary<string, List<double>> _window = new(StringComparer.Ordinal);

        private TimeSpan _windowStart = TimeSpan.Zero;
        private int _inWindow;
        private bool _dense;

        public void Write(Sample sample)
        {
            var now = _watch.Elapsed;

            if (now - _windowStart >= Window)
            {
                Flush();
                _windowStart = now;
                _inWindow = 0;
            }

            _inWindow++;

            if (!_dense && _inWindow > DenseThreshold)
            {
                _dense = true;

                Console.WriteLine($"  … отсчёты идут чаще {DenseThreshold} за полсекунды — "
                                  + "дальше сводка, а не строка на отсчёт");
            }

            if (!_dense)
            {
                WriteLiveSample(descriptor, sample);

                return;
            }

            if (!sample.IsSuccess)
            {
                return;
            }

            var key = sample.Label ?? string.Empty;

            if (!_window.TryGetValue(key, out var values))
            {
                values = [];
                _window[key] = values;
            }

            values.Add(sample.Value);
        }

        /// <summary>
        /// Сводка за окно.
        /// </summary>
        /// <remarks>
        /// Медиана и максимум, а не среднее: измерение частыми отсчётами делается ради
        /// всплесков, и среднее — единственное, что их прячет.
        /// </remarks>
        private void Flush()
        {
            foreach (var (label, values) in _window)
            {
                if (values.Count == 0)
                {
                    continue;
                }

                values.Sort();

                var median = values[values.Count / 2];
                var name = label.Length == 0 ? string.Empty : $"{label,-16} ";
                var unit = Describe.UnitSuffix(descriptor.Unit);

                Console.WriteLine($"  {name}{values.Count,4} отсч.  медиана {F(median)}{unit}"
                                  + $"   макс {F(values[^1])}{unit}");
            }

            _window.Clear();
        }
    }

    // ------------------------------------------------------ матрица «хоп × попытка»

    /// <summary>
    /// Живой вывод трассировки: подробно на разведке, по строке на цикл дальше.
    /// </summary>
    /// <remarks>
    /// Разведка идёт по одному хопу и читается построчно — так же, как <c>tracert</c>.
    /// Циклы непрерывного наблюдения идут раз в секунду по всем хопам сразу, и подробный
    /// вывод превратился бы в поток, в котором ничего не видно. Начало нового цикла
    /// распознаётся по возврату TTL назад: цикл всегда начинается с первого хопа.
    /// </remarks>
    private sealed class PathLiveWriter
    {
        private int _lastTtl;
        private int _round;
        private int _roundProbes;
        private int _roundSilent;
        private double _lastRtt = double.NaN;
        private string? _lastResponder;

        public void Write(Sample sample)
        {
            if (sample.Group is not { } ttl)
            {
                return;
            }

            // Разведка идёт по нарастающей и повторяет один TTL несколько раз, цикл
            // наблюдения проходит все хопы ровно по разу. Поэтому признак нового цикла
            // разный: на разведке — только возврат TTL назад, дальше — ещё и повтор.
            var isNewRound = _round == 0 ? ttl < _lastTtl : ttl <= _lastTtl;

            if (isNewRound)
            {
                FlushRound();
                _round++;
            }

            _lastTtl = ttl;

            if (_round == 0)
            {
                WriteDiscovery(sample, ttl);
                return;
            }

            _roundProbes++;

            if (sample.IsSuccess)
            {
                _lastRtt = sample.Value;
                _lastResponder = sample.RespondedBy;
            }
            else
            {
                _roundSilent++;
            }
        }

        private static void WriteDiscovery(Sample sample, int ttl)
        {
            Console.WriteLine(sample.IsSuccess
                ? $"  {ttl,3}  {sample.RespondedBy ?? "?",-24} {F(sample.Value),9} мс"
                : $"  {ttl,3}  {"*",-24} {Describe.SampleStatus(sample.Status),12}");
        }

        private void FlushRound()
        {
            if (_round == 0)
            {
                Console.WriteLine();
                return;
            }

            var rtt = double.IsNaN(_lastRtt) ? "—" : F(_lastRtt) + " мс";

            Console.WriteLine($"  цикл {_round,-5} хопов {_roundProbes,3}   без ответа {_roundSilent,3}   "
                              + $"последний ответ: {_lastResponder ?? "нет"} {rtt}");

            _roundProbes = 0;
            _roundSilent = 0;
            _lastRtt = double.NaN;
            _lastResponder = null;
        }
    }

    private static void WritePathSummary(ProbeResult result)
    {
        RouteRenderer.Write(PathAnalysis.Compute(result.Samples, result.ResolvedAddress), result.Facts);
    }
}
