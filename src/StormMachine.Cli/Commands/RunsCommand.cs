using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Results;

namespace StormMachine.Cli.Commands;

/// <summary>
/// <c>storm runs</c> — журнал прогонов.
/// </summary>
internal static class RunsCommand
{
    public static Command Create(IServiceProvider services)
    {
        var command = new Command("runs", "Журнал прогонов: список, подробности, уборка.")
        {
            CreateList(services),
            CreateShow(services),
            CreateReport(services),
            CreateExport(services),
            CreateDelete(services),
            CreatePurge(services),
            CreateUsage(services),
        };

        return command;
    }

    private static Command CreateList(IServiceProvider services)
    {
        var limitOption = new Option<int>("--limit", "-n") { Description = "Сколько строк показать.", DefaultValueFactory = _ => 20 };
        var probeOption = new Option<string>("--probe") { Description = "Только прогоны указанной пробы.", DefaultValueFactory = _ => string.Empty };
        var failedOption = new Option<bool>("--failed") { Description = "Только прогоны с потерями или сбоями." };

        var command = new Command("list", "Последние прогоны.") { limitOption, probeOption, failedOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var probeName = parseResult.GetValue(probeOption);

            var runs = await store.ListAsync(
                new RunQuery
                {
                    Limit = parseResult.GetValue(limitOption),
                    ProbeName = string.IsNullOrWhiteSpace(probeName) ? null : probeName,
                    OnlyFailed = parseResult.GetValue(failedOption),
                },
                cancellationToken).ConfigureAwait(false);

            RunRenderer.WriteList(runs);
            return 0;
        });

        return command;
    }

    private static Command CreateShow(IServiceProvider services)
    {
        var idArgument = new Argument<string>("id") { Description = "Идентификатор прогона; достаточно первых символов." };
        var samplesOption = new Option<bool>("--samples") { Description = "Показать сырые сэмплы." };

        var command = new Command("show", "Подробности прогона.") { idArgument, samplesOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var id = await ResolveIdAsync(store, parseResult.GetValue(idArgument)!, cancellationToken).ConfigureAwait(false);

            if (id is null)
            {
                return 1;
            }

            var run = await store.GetAsync(id.Value, cancellationToken).ConfigureAwait(false);

            if (run is null)
            {
                Console.Error.WriteLine($"Прогон {id} не найден.");
                return 1;
            }

            RunRenderer.WriteDetails(run, parseResult.GetValue(samplesOption));
            return 0;
        });

        return command;
    }

    private static Command CreateReport(IServiceProvider services)
    {
        var idArgument = new Argument<string>("id") { Description = "Идентификатор прогона; достаточно первых символов." };
        var outOption = new Option<string>("--out", "-o")
        {
            Description = "Куда сохранить файл. Без него — рядом, с именем по умолчанию.",
            DefaultValueFactory = _ => string.Empty,
        };
        var noChartOption = new Option<bool>("--no-chart") { Description = "Не рисовать график." };

        var command = new Command("report", "Сформировать отчёт PDF по прогону.")
        {
            idArgument, outOption, noChartOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var id = await ResolveIdAsync(store, parseResult.GetValue(idArgument)!, cancellationToken).ConfigureAwait(false);

            if (id is null)
            {
                return 1;
            }

            var run = await store.GetAsync(id.Value, cancellationToken).ConfigureAwait(false);

            if (run is null)
            {
                Console.Error.WriteLine($"Прогон {id} не найден.");
                return 1;
            }

            var renderer = services.GetRequiredService<IReportRenderer>();

            var report = await renderer.RenderAsync(
                ReportRequest.ForRun(
                    run,
                    author: Environment.UserName,
                    includeChart: !parseResult.GetValue(noChartOption)),
                cancellationToken).ConfigureAwait(false);

            var path = parseResult.GetValue(outOption);
            if (string.IsNullOrWhiteSpace(path))
            {
                path = report.SuggestedFileName;
            }

            await File.WriteAllBytesAsync(path, report.Content, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Отчёт {renderer.Format} сохранён: {Path.GetFullPath(path)}");
            Console.WriteLine($"  {report.Content.Length / 1024.0:0.0} КБ");

            return 0;
        });

        return command;
    }

    /// <summary>
    /// Выгрузка прогона в CSV, JSON или PNG.
    /// </summary>
    /// <remarks>
    /// Отдельно от отчёта: отчёт объясняет, выгрузка отдаёт. В CSV и JSON всегда
    /// попадают условия измерения — ряд чисел без интерфейса, методики и порога
    /// достоверности нельзя ни повторить, ни сопоставить.
    /// </remarks>
    private static Command CreateExport(IServiceProvider services)
    {
        var idArgument = new Argument<string>("id")
        {
            Description = "Идентификатор прогона; достаточно первых символов.",
        };

        var formatOption = new Option<string>("--формат", "--format")
        {
            Description = "csv, json или png.",
            DefaultValueFactory = _ => "csv",
        };

        var outOption = new Option<string>("--файл", "--out")
        {
            Description = "Куда сохранить. Без него — рядом, с именем по умолчанию.",
            DefaultValueFactory = _ => string.Empty,
        };

        var command = new Command("export", "Выгрузить прогон: CSV, JSON или PNG.")
        {
            idArgument, formatOption, outOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var raw = parseResult.GetValue(formatOption)!.Trim().ToLowerInvariant();

            var format = raw switch
            {
                "csv" => ExportFormat.Csv,
                "json" => ExportFormat.Json,
                "png" => ExportFormat.Png,
                _ => (ExportFormat?)null,
            };

            if (format is null)
            {
                Console.Error.WriteLine($"Формат «{raw}» неизвестен. Доступны: csv, json, png.");

                return 2;
            }

            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var id = await ResolveIdAsync(store, parseResult.GetValue(idArgument)!, cancellationToken)
                .ConfigureAwait(false);

            if (id is null)
            {
                return 1;
            }

            var run = await store.GetAsync(id.Value, cancellationToken).ConfigureAwait(false);

            if (run is null)
            {
                Console.Error.WriteLine($"Прогон {id} не найден.");

                return 1;
            }

            var exporter = services.GetRequiredService<IRunExporter>();

            try
            {
                var file = await exporter.ExportAsync(run, format.Value, cancellationToken).ConfigureAwait(false);

                var path = parseResult.GetValue(outOption) is { Length: > 0 } chosen
                    ? chosen
                    : file.SuggestedFileName;

                await File.WriteAllBytesAsync(path, file.Content, cancellationToken).ConfigureAwait(false);

                Console.WriteLine($"Выгружено: {Path.GetFullPath(path)} ({file.Content.Length / 1024.0:0.0} КБ)");

                if (format == ExportFormat.Csv && !run.Summary.HasRawSamples)
                {
                    Console.WriteLine("Сырые измерения удалены политикой хранения — выгружены агрегаты.");
                }

                return 0;
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }
        });

        return command;
    }

    private static Command CreateDelete(IServiceProvider services)
    {
        var idArgument = new Argument<string>("id") { Description = "Идентификатор прогона; достаточно первых символов." };
        var command = new Command("delete", "Удалить прогон.") { idArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var id = await ResolveIdAsync(store, parseResult.GetValue(idArgument)!, cancellationToken).ConfigureAwait(false);

            if (id is null)
            {
                return 1;
            }

            var deleted = await store.DeleteAsync(id.Value, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(deleted ? $"Прогон {id} удалён." : $"Прогон {id} не найден.");
            return deleted ? 0 : 1;
        });

        return command;
    }

    private static Command CreatePurge(IServiceProvider services)
    {
        var dryRunOption = new Option<bool>("--dry-run") { Description = "Показать, что будет удалено, не удаляя." };
        var rawDaysOption = new Option<int>("--raw-days") { Description = "Сколько дней хранить сырые сэмплы.", DefaultValueFactory = _ => 90 };
        var runDaysOption = new Option<int>("--run-days") { Description = "Сколько дней хранить прогоны.", DefaultValueFactory = _ => 365 };

        var command = new Command("purge", "Применить политику хранения.") { dryRunOption, rawDaysOption, runDaysOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var policy = new RetentionPolicy
            {
                RawSampleHorizon = TimeSpan.FromDays(parseResult.GetValue(rawDaysOption)),
                RunHorizon = TimeSpan.FromDays(parseResult.GetValue(runDaysOption)),
            };

            var dryRun = parseResult.GetValue(dryRunOption);
            var report = await store.ApplyRetentionAsync(policy, dryRun, cancellationToken).ConfigureAwait(false);

            Console.WriteLine(dryRun ? "Пробный прогон уборки — ничего не удалено." : "Уборка выполнена.");
            Console.WriteLine($"  Прогонов к удалению целиком : {report.RunsDeleted}");
            Console.WriteLine($"  Прогонов свернуть до агрегатов: {report.RunsDownsampled}");
            Console.WriteLine($"  Сэмплов к удалению          : {report.SamplesDeleted}");

            if (report.RunsDownsampled > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Свёрнутые прогоны остаются в журнале: агрегаты по рядам сохраняются,");
                Console.WriteLine("  удаляются только сырые сэмплы.");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateUsage(IServiceProvider services)
    {
        var command = new Command("usage", "Сколько места занимает журнал.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var (size, runs, samples) = await store.GetUsageAsync(cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Файл базы : {store.Location}");
            Console.WriteLine($"Размер    : {size / 1024.0 / 1024.0:0.00} МБ");
            Console.WriteLine($"Прогонов  : {runs}");
            Console.WriteLine($"Сэмплов   : {samples.ToString("N0", CultureInfo.InvariantCulture)}");

            return 0;
        });

        return command;
    }

    /// <summary>
    /// Находит прогон по полному идентификатору или по его началу.
    /// </summary>
    /// <remarks>
    /// Набирать целиком 36 символов из вывода предыдущей команды — занятие, которое
    /// никто не станет делать дважды. Достаточно первых нескольких символов, пока они
    /// однозначны; неоднозначность сообщается явно, а не разрешается наугад.
    /// </remarks>
    private static async Task<Guid?> ResolveIdAsync(IRunStore store, string raw, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(raw, out var exact))
        {
            return exact;
        }

        if (raw.Length < 4)
        {
            Console.Error.WriteLine("Укажи хотя бы четыре первых символа идентификатора.");
            return null;
        }

        var candidates = await store
            .ListAsync(new RunQuery { Limit = 10_000 }, cancellationToken)
            .ConfigureAwait(false);

        var matches = candidates
            .Where(r => r.Id.ToString().StartsWith(raw, StringComparison.OrdinalIgnoreCase))
            .ToList();

        switch (matches.Count)
        {
            case 1:
                return matches[0].Id;

            case 0:
                Console.Error.WriteLine($"Прогон, начинающийся с «{raw}», не найден.");
                return null;

            default:
                Console.Error.WriteLine($"Начало «{raw}» подходит {matches.Count} прогонам — уточни:");
                foreach (var match in matches.Take(10))
                {
                    Console.Error.WriteLine($"  {match.Id}  {match.ProbeName,-6} {match.TargetDisplay}");
                }

                return null;
        }
    }
}
