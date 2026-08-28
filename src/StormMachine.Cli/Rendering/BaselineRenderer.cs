using System.Globalization;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;

namespace StormMachine.Cli.Rendering;

/// <summary>
/// Показ эталонов и сравнений.
/// </summary>
/// <remarks>
/// Расхождения условий печатаются <b>до</b> чисел, а не примечанием после. Если
/// сравнивать нельзя, сказать об этом надо раньше, чем покажут цифры: прочитанное
/// число уже не развидеть.
/// </remarks>
internal static class BaselineRenderer
{
    public static void WriteList(IReadOnlyList<Baseline> baselines)
    {
        ArgumentNullException.ThrowIfNull(baselines);

        if (baselines.Count == 0)
        {
            Console.WriteLine("Эталонов нет.");
            Console.WriteLine();
            Console.WriteLine("Зафиксировать норму по последнему измерению:");
            Console.WriteLine("  storm ping 192.168.1.1");
            Console.WriteLine("  storm baseline capture \"шлюз, норма\"");

            return;
        }

        Console.WriteLine($"  {"имя",-28} {"что",-10} {"цель",-22} {"метрик",6}  снят");

        foreach (var baseline in baselines)
        {
            Console.WriteLine(
                $"  {Cut(baseline.Name, 28),-28} {Cut(baseline.Subject, 10),-10} "
                + $"{Cut(baseline.Target.DisplayName, 22),-22} {baseline.Metrics.Count,6}  "
                + baseline.CapturedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture));
        }

        Console.WriteLine();
        Console.WriteLine("Сравнить: storm baseline compare <имя>");
    }

    public static void WriteDetails(Baseline baseline)
    {
        ArgumentNullException.ThrowIfNull(baseline);

        Console.WriteLine();
        Console.WriteLine($"Эталон    : {baseline.Name}");

        if (!string.IsNullOrWhiteSpace(baseline.Description))
        {
            Console.WriteLine($"Описание  : {baseline.Description}");
        }

        Console.WriteLine($"Измерение : {baseline.Subject} → {baseline.Target.DisplayName}");
        Console.WriteLine($"Снят      : {Local(baseline.CapturedUtc)}");

        if (baseline.RunId is { } run)
        {
            Console.WriteLine($"Из прогона: {run.ToString()[..8]}  —  storm runs show {run.ToString()[..8]}");
        }

        Console.WriteLine();
        WriteConditions(baseline.Context);

        Console.WriteLine();
        Console.WriteLine($"  {"метрика",-18} {"значение",12}  куда лучше");

        foreach (var metric in baseline.Metrics)
        {
            Console.WriteLine(
                $"  {metric.Name,-18} {metric.Value.ToString("0.###", CultureInfo.InvariantCulture),12}  "
                + (metric.HigherIsBetter ? "больше" : "меньше"));
        }

        Console.WriteLine();
    }

    public static void WriteComparison(BaselineComparison comparison, StoredRun current)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(current);

        Console.WriteLine();
        Console.WriteLine($"Эталон «{comparison.Baseline.Name}» — {comparison.Baseline.Describe()}");
        Console.WriteLine(
            $"Сейчас  : {current.Summary.ProbeName} → {current.Summary.TargetDisplay}, "
            + Local(current.Summary.StartedUtc));
        Console.WriteLine();

        // Расхождения условий — до чисел. Если сравнивать нельзя, читатель обязан
        // узнать это раньше, чем увидит проценты.
        if (comparison.Mismatches.Count > 0)
        {
            Console.WriteLine(comparison.HasSevereMismatch
                ? "! Условия изменились так, что числа напрямую несопоставимы:"
                : "~ Условия измерения отличаются от эталонных:");

            foreach (var mismatch in comparison.Mismatches)
            {
                Console.WriteLine($"    {mismatch.What}: было «{mismatch.Before}», стало «{mismatch.After}»");
            }

            if (comparison.HasSevereMismatch)
            {
                Console.WriteLine("    Сравнение показано полностью, но приписывать разницу сети");
                Console.WriteLine("    без проверки этих расхождений нельзя.");
            }

            Console.WriteLine();
        }

        if (comparison.Changes.Count == 0)
        {
            Console.WriteLine("Ни одна метрика эталона не найдена в текущем измерении — сравнивать нечего.");

            return;
        }

        Console.WriteLine($"  {"метрика",-16} {"эталон",12} {"сейчас",12} {"изменение",12}  оценка");

        foreach (var change in comparison.Changes)
        {
            var mark = change.Direction switch
            {
                ChangeDirection.Better => "+",
                ChangeDirection.Worse => "!",
                _ => " ",
            };

            var percent = change.Percent is { } value
                ? $"{(value >= 0 ? "+" : string.Empty)}{value.ToString("0.#", CultureInfo.InvariantCulture)} %"
                : "—";

            Console.WriteLine(
                $"{mark} {change.Name,-16} {change.Before.ToString("0.###", CultureInfo.InvariantCulture),12} "
                + $"{change.After.ToString("0.###", CultureInfo.InvariantCulture),12} {percent,12}  "
                + Verdict(change));
        }

        if (comparison.Missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("Не нашлись в текущем измерении: " + string.Join(", ", comparison.Missing));
            Console.WriteLine("Возможно, измеряли другой пробой.");
        }

        Console.WriteLine();
        Console.WriteLine($"Итог: {comparison.Verdict}.");
        Console.WriteLine(
            $"Изменением считается сдвиг больше {BaselineComparer.SignificantPercent.ToString("0", CultureInfo.InvariantCulture)} % "
            + "и больше порога достоверности.");
        Console.WriteLine("Меньшее — разброс, с которым сеть расходится сама с собой.");
        Console.WriteLine();
    }

    private static void WriteConditions(MeasurementContext context)
    {
        Console.WriteLine("  [условия, при которых снят эталон]");
        Console.WriteLine($"    интерфейс   : {context.InterfaceName} · {Describe(context.AdapterKind)}");
        Console.WriteLine(
            "    порог       : "
            + context.CalibrationBaselineMs.ToString("0.###", CultureInfo.InvariantCulture)
            + " мс");
        Console.WriteLine($"    методика    : {context.Methodology.Name}");

        if (context.Backend is { } backend)
        {
            Console.WriteLine($"    бэкенд      : {backend}");
        }

        Console.WriteLine($"    версия      : {context.ProductVersion}");

        if (!context.IsTimingTrustworthy)
        {
            Console.WriteLine("    ! адаптер вносит собственную задержку — эталон снят в таких условиях");
        }
    }

    private static string Verdict(MetricChange change) => change.Direction switch
    {
        ChangeDirection.Better => "лучше",
        ChangeDirection.Worse => "хуже",
        _ => change.Insignificance ?? "без изменений",
    };

    private static string Describe(AdapterKind kind) => kind switch
    {
        AdapterKind.Physical => "физический",
        AdapterKind.Wireless => "беспроводной",
        AdapterKind.Virtual => "виртуальный коммутатор",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Tunnel => "туннель",
        AdapterKind.Loopback => "loopback",
        _ => "тип не определён",
    };

    private static string Local(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    private static string Cut(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";
}
