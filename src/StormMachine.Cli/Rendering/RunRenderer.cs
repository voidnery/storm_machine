using System.Globalization;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Cli.Rendering;

/// <summary>Показ журнала прогонов.</summary>
internal static class RunRenderer
{
    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    public static void WriteList(IReadOnlyList<RunSummary> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        if (runs.Count == 0)
        {
            Console.WriteLine("Журнал пуст. Запусти пробу с ключом --save, чтобы результат сохранился.");
            return;
        }

        Console.WriteLine($"  {"id",-8} {"когда",-17} {"проба",-6} {"цель",-28} {"проб",5} {"потери",7} {"медиана",9}  состояние");

        foreach (var run in runs)
        {
            var shortId = run.Id.ToString()[..8];
            var when = run.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm:ss", CultureInfo.InvariantCulture);
            var median = run.MedianMs is { } value ? F(value) : "—";
            var loss = run.SentCount == 0 ? "—" : run.LossPercent.ToString("0", CultureInfo.InvariantCulture) + "%";

            Console.WriteLine(
                $"  {shortId,-8} {when,-17} {run.ProbeName,-6} {Shorten(run.TargetDisplay, 28),-28} "
                + $"{run.SentCount,5} {loss,7} {median,9}  {DescribeState(run)}");
        }

        Console.WriteLine();
        Console.WriteLine($"Показано прогонов: {runs.Count}. Подробности: storm runs show <id>");
    }

    public static void WriteDetails(StoredRun run, bool withSamples)
    {
        ArgumentNullException.ThrowIfNull(run);

        var summary = run.Summary;

        Console.WriteLine($"Прогон    : {summary.Id}");
        Console.WriteLine($"Проба     : {summary.ProbeName}");
        Console.WriteLine($"Цель      : {summary.TargetDisplay}"
                          + (summary.ResolvedAddress is { } resolved ? $"  →  {resolved}" : string.Empty));
        Console.WriteLine($"Начат     : {summary.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}");

        if (summary.Duration is { } duration)
        {
            Console.WriteLine($"Длился    : {duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} с");
        }

        Console.WriteLine($"Состояние : {DescribeState(summary)}");
        Console.WriteLine($"Интерфейс : {run.Context.InterfaceName} ({Describe.AdapterKind(run.Context.AdapterKind)})");
        Console.WriteLine($"Методика  : {run.Context.Methodology}");
        Console.WriteLine($"Порог     : {F(run.Context.CalibrationBaselineMs)} мс");

        // Профиль окружения — часть условий: измерения из разных мест несопоставимы,
        // и через полгода отличить замер у заказчика от замера в офисе будет нечем.
        if (run.Context.Profile is { } profile)
        {
            Console.WriteLine($"Профиль   : {profile}");
        }

        Console.WriteLine($"Версия    : {run.Context.ProductVersion}");

        if (run.Context.TimingWarning is { } warning)
        {
            Console.WriteLine();
            Console.WriteLine($"ВНИМАНИЕ: {warning}");
        }

        if (run.Parameters.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  [параметры]");
            foreach (var (key, value) in run.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"    {key,-16} {value ?? "—"}");
            }
        }

        if (run.Summary.Shape == ProbeResultShape.PathTrace)
        {
            RouteRenderer.Write(RouteRenderer.Analyse(run), run.Facts);
            Describe.WriteFacts(RouteRenderer.RemainingFacts(run.Facts));
        }
        else
        {
            WriteSeries(run);
            Describe.WriteFacts(run.Facts);
        }

        Console.WriteLine();
        Console.WriteLine($"Отправлено {summary.SentCount}, получено {summary.SuccessCount}, "
                          + $"потеряно {summary.LostCount} ({summary.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)}%)");

        if (!summary.HasRawSamples)
        {
            // Различие принципиальное: «подробности состарились» и «измерений не было»
            // выглядели бы одинаково, если об этом не сказать.
            Console.WriteLine();
            Console.WriteLine("  Сырые сэмплы удалены политикой хранения. Агрегаты выше сохранены полностью.");
            return;
        }

        if (withSamples)
        {
            WriteSamples(run);
        }
        else if (run.Samples.Count > 0)
        {
            Console.WriteLine($"Сырых сэмплов: {run.Samples.Count}. Показать: storm runs show {summary.Id.ToString()[..8]} --samples");
        }
    }

    private static void WriteSeries(StoredRun run)
    {
        if (run.Series.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"  {"ряд",-24} {"проб",5} {"потери",7} {"мин",9} {"медиана",9} {"макс",9} {"джиттер",9}");

        foreach (var series in run.Series)
        {
            var stats = series.Statistics;

            if (stats.SampleCount == 0)
            {
                Console.WriteLine($"  {Shorten(series.Label, 24),-24} {series.SentCount,5} {"100%",7} {"—",9} {"—",9} {"—",9} {"—",9}");
                continue;
            }

            var loss = series.LossPercent.ToString("0", CultureInfo.InvariantCulture) + "%";

            Console.WriteLine(
                $"  {Shorten(series.Label, 24),-24} {series.SentCount,5} {loss,7} "
                + $"{F(stats.MinMs),9} {F(stats.P50Ms),9} {F(stats.MaxMs),9} {F(stats.JitterRfc3550Ms),9}");
        }
    }

    private static void WriteSamples(StoredRun run)
    {
        Console.WriteLine();
        Console.WriteLine($"  {"№",5} {"время",12} {"значение",11} {"метка",-14} {"группа",6}  статус");

        foreach (var sample in run.Samples)
        {
            var value = sample.IsSuccess ? F(sample.Value) : "—";
            var time = sample.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var group = sample.Group?.ToString(CultureInfo.InvariantCulture) ?? "—";

            Console.WriteLine(
                $"  {sample.Sequence,5} {time,12} {value,11} {Shorten(sample.Label ?? "—", 14),-14} {group,6}  "
                + $"{Describe.SampleStatus(sample.Status)}");
        }
    }

    private static string DescribeState(RunSummary run) => run.State switch
    {
        RunState.Completed when run.LostCount == 0 => "завершён",
        RunState.Completed => "завершён, есть потери",
        RunState.Cancelled => "прерван оператором",
        RunState.Abandoned => "оборван сбоем",
        _ => "выполняется",
    };

    private static string Shorten(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";
}
