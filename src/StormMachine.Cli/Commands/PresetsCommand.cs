using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Presets;
using StormMachine.Application.Runs;
using StormMachine.Application.Scenarios;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Presets;

namespace StormMachine.Cli.Commands;

/// <summary>
/// <c>storm presets</c> — библиотека тестов.
/// </summary>
/// <remarks>
/// Смысл пресета не в экономии набора текста, а в повторяемости: измерение, которое
/// нельзя повторить теми же параметрами, не с чем сравнивать.
/// </remarks>
internal static class PresetsCommand
{
    public static Command Create(IServiceProvider services)
    {
        return new Command("presets", "Библиотека тестов: список, запуск, обмен.")
        {
            CreateList(services),
            CreateShow(services),
            CreateRun(services),
            CreateDelete(services),
            CreateExport(services),
            CreateImport(services),
        };
    }

    private static Command CreateList(IServiceProvider services)
    {
        var probeOption = new Option<string>("--probe") { Description = "Только пресеты указанной пробы.", DefaultValueFactory = _ => string.Empty };
        var tagOption = new Option<string>("--tag") { Description = "Только пресеты с этим тегом.", DefaultValueFactory = _ => string.Empty };
        var searchOption = new Option<string>("--search") { Description = "Поиск по имени и описанию.", DefaultValueFactory = _ => string.Empty };

        var command = new Command("list", "Что есть в библиотеке.") { probeOption, tagOption, searchOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var presets = services.GetRequiredService<PresetService>();

            var found = await presets.ListAsync(
                new PresetQuery
                {
                    Subject = Nullify(parseResult.GetValue(probeOption)),
                    Tag = Nullify(parseResult.GetValue(tagOption)),
                    Search = Nullify(parseResult.GetValue(searchOption)),
                },
                cancellationToken).ConfigureAwait(false);

            PresetRenderer.WriteList(found);

            var tags = await presets.GetTagsAsync(cancellationToken).ConfigureAwait(false);
            if (tags.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"Теги в библиотеке: {string.Join(", ", tags)}");
            }

            return 0;
        });

        return command;
    }

    private static Command CreateShow(IServiceProvider services)
    {
        var nameArgument = new Argument<string>("имя") { Description = "Имя пресета или его идентификатор." };
        var command = new Command("show", "Подробности пресета.") { nameArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var presets = services.GetRequiredService<PresetService>();
            var preset = await ResolveAsync(presets, parseResult.GetValue(nameArgument)!, cancellationToken).ConfigureAwait(false);

            if (preset is null)
            {
                return 1;
            }

            PresetRenderer.WriteDetails(preset);

            // Проверка показывается и здесь: пресет мог быть создан, когда параметры
            // пробы были другими, и узнать об этом лучше до запуска.
            var errors = presets.Validate(preset);
            if (errors.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("ВНИМАНИЕ: пресет не пройдёт проверку при запуске:");
                foreach (var error in errors)
                {
                    Console.WriteLine($"  {error.Field}: {error.Message}");
                }
            }

            return 0;
        });

        return command;
    }

    private static Command CreateRun(IServiceProvider services)
    {
        var nameArgument = new Argument<string>("имя") { Description = "Имя пресета или его идентификатор." };
        var saveOption = new Option<bool>("--save") { Description = "Сохранить прогон в журнал.", DefaultValueFactory = _ => true };
        var quietOption = new Option<bool>("--quiet", "-q") { Description = "Только итоговая сводка." };

        var command = new Command("run", "Запустить пресет.") { nameArgument, saveOption, quietOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var presets = services.GetRequiredService<PresetService>();
            var orchestrator = services.GetRequiredService<RunOrchestrator>();
            var clock = services.GetRequiredService<IHighResolutionClock>();
            var environment = services.GetRequiredService<INetworkEnvironment>();

            var preset = await ResolveAsync(presets, parseResult.GetValue(nameArgument)!, cancellationToken).ConfigureAwait(false);

            if (preset is null)
            {
                return 1;
            }

            var errors = presets.Validate(preset);
            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine($"{error.Field}: {error.Message}");
                }

                return 2;
            }

            // Пресет сценария идёт другим путём: у него нет пробы и нет параметров,
            // зато есть цепочка шагов с порогами. Запуск при этом остаётся тем же —
            // тот же ScenarioRunner, что и у «storm scenario run».
            if (preset.Kind == PresetKind.Scenario)
            {
                return await RunScenarioPresetAsync(services, preset, parseResult, saveOption, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!presets.TryGetProbe(preset, out var probe))
            {
                Console.Error.WriteLine($"Проба «{preset.Subject}» не зарегистрирована.");
                return 1;
            }

            var quiet = parseResult.GetValue(quietOption);
            var save = parseResult.GetValue(saveOption);
            var request = PresetService.ToRequest(preset);

            Console.WriteLine($"Пресет    : {preset.Name} (редакция {preset.Version})");

            await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);
            var adapter = environment.GetPrimaryAdapter();

            var profile = await MeasurementConditions
                .ActiveProfileAsync(services.GetService<IProfileStore>(), cancellationToken)
                .ConfigureAwait(false);

            ProbeRenderer.WriteHeader(
                probe.Descriptor,
                preset.Target,
                MeasurementConditions.Build(adapter, clock, probe.Descriptor.Methodology, profile),
                adapter);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            void OnCancelKey(object? sender, ConsoleCancelEventArgs args)
            {
                args.Cancel = true;
                linked.Cancel();
            }

            Console.CancelKeyPress += OnCancelKey;

            RunOutcome outcome;
            try
            {
                outcome = await orchestrator.RunAsync(
                    probe,
                    request,
                    new RunOptions
                    {
                        Save = save,
                        OnSample = quiet ? null : ProbeRenderer.CreateLiveWriter(probe.Descriptor),
                        PresetId = preset.Id,
                        PresetVersion = preset.Version,
                    },
                    linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Ошибка выполнения: {ex.Message}");
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKey;
            }

            ProbeRenderer.WriteSummary(probe.Descriptor, outcome.Result, clock);

            await presets.RecordRunAsync(preset.Id, CancellationToken.None).ConfigureAwait(false);

            if (outcome.RunId is { } runId)
            {
                Console.WriteLine();
                Console.WriteLine($"Сохранено в журнал: {runId}");
            }

            return outcome.Result.SuccessCount > 0 ? 0 : 1;
        });

        return command;
    }

    private static Command CreateDelete(IServiceProvider services)
    {
        var nameArgument = new Argument<string>("имя") { Description = "Имя пресета или его идентификатор." };
        var command = new Command("delete", "Удалить пресет.") { nameArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var presets = services.GetRequiredService<PresetService>();
            var preset = await ResolveAsync(presets, parseResult.GetValue(nameArgument)!, cancellationToken).ConfigureAwait(false);

            if (preset is null)
            {
                return 1;
            }

            await presets.DeleteAsync(preset.Id, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Пресет «{preset.Name}» удалён. Прогоны, сделанные им, остаются в журнале.");

            return 0;
        });

        return command;
    }

    private static Command CreateExport(IServiceProvider services)
    {
        var outOption = new Option<string>("--out", "-o") { Description = "Файл для записи. Без него — вывод на экран.", DefaultValueFactory = _ => string.Empty };
        var tagOption = new Option<string>("--tag") { Description = "Выгрузить только пресеты с этим тегом.", DefaultValueFactory = _ => string.Empty };

        var command = new Command("export", "Выгрузить библиотеку в файл JSON.") { outOption, tagOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var presets = services.GetRequiredService<PresetService>();

            var found = await presets.ListAsync(
                new PresetQuery { Tag = Nullify(parseResult.GetValue(tagOption)), Limit = 10_000 },
                cancellationToken).ConfigureAwait(false);

            if (found.Count == 0)
            {
                Console.Error.WriteLine("Выгружать нечего: подходящих пресетов нет.");
                return 1;
            }

            var json = PresetBundleJson.Write(PresetService.ToBundle(found, Environment.UserName));
            var path = parseResult.GetValue(outOption);

            if (string.IsNullOrWhiteSpace(path))
            {
                Console.WriteLine(json);
                return 0;
            }

            await File.WriteAllTextAsync(path, json, cancellationToken).ConfigureAwait(false);
            Console.WriteLine($"Выгружено пресетов: {found.Count} → {Path.GetFullPath(path)}");

            return 0;
        });

        return command;
    }

    private static Command CreateImport(IServiceProvider services)
    {
        var fileArgument = new Argument<string>("файл") { Description = "Файл JSON с пресетами." };
        var keepOption = new Option<bool>("--keep-existing") { Description = "Не трогать пресеты с совпадающими именами." };

        var command = new Command("import", "Загрузить пресеты из файла JSON.") { fileArgument, keepOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = parseResult.GetValue(fileArgument)!;

            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"Файл не найден: {Path.GetFullPath(path)}");
                return 1;
            }

            var presets = services.GetRequiredService<PresetService>();

            PresetImportReport report;
            try
            {
                var bundle = PresetBundleJson.Read(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));

                report = await presets
                    .ImportAsync(bundle, overwrite: !parseResult.GetValue(keepOption), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Console.Error.WriteLine($"Файл не прочитан: {ex.Message}");
                return 1;
            }

            Console.WriteLine($"Добавлено {report.Added}, обновлено {report.Updated}, пропущено {report.Skipped}.");

            if (report.Problems.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Пропущены:");
                foreach (var problem in report.Problems)
                {
                    Console.WriteLine($"  {problem}");
                }
            }

            return 0;
        });

        return command;
    }

    /// <summary>Находит пресет по имени или идентификатору.</summary>
    /// <summary>
    /// Запуск пресета сценария.
    /// </summary>
    /// <remarks>
    /// Цель хранится исходной строкой, а не разрешённым списком: если это было имя
    /// набора, повторный запуск возьмёт набор заново. Пресет «проверить все наши
    /// сайты» обязан переживать появление девятого сайта — иначе он превращается
    /// в снимок прошлого года, выдающий себя за проверку.
    /// </remarks>
    private static async Task<int> RunScenarioPresetAsync(
        IServiceProvider services,
        Preset preset,
        ParseResult parseResult,
        Option<bool> saveOption,
        CancellationToken cancellationToken)
    {
        var runner = services.GetRequiredService<ScenarioRunner>();
        var clock = services.GetRequiredService<IHighResolutionClock>();
        var environment = services.GetRequiredService<INetworkEnvironment>();
        var store = services.GetRequiredService<IRunStore>();
        var presets = services.GetRequiredService<PresetService>();

        var save = parseResult.GetValue(saveOption);

        TargetSet set;

        try
        {
            set = TargetSets.Resolve(preset.Target.Value, environment);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);

            return 2;
        }

        if (set.Targets.Count == 0)
        {
            Console.Error.WriteLine($"Набор «{set.Key}» пуст: {set.Origin}.");

            return 2;
        }

        if (save)
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);

        Console.WriteLine($"Пресет    : {preset.Name} (редакция {preset.Version})");

        var runs = new List<(string Target, Domain.Scenarios.ScenarioRun Run)>(set.Targets.Count);

        foreach (var target in set.Targets)
        {
            var scenario = ScenarioTemplates.Create(preset.Subject, target);

            ScenarioRenderer.WriteHeader(scenario, environment.GetPrimaryAdapter(), clock, set);

            var run = await runner
                .RunAsync(scenario, save, ScenarioRenderer.CreateProgressWriter(), cancellationToken)
                .ConfigureAwait(false);

            ScenarioRenderer.WriteRun(run, clock.CalibrationBaselineMs);
            runs.Add((target, run));
        }

        if (runs.Count > 1)
        {
            ScenarioRenderer.WriteSetSummary(set, runs);
        }

        await presets.RecordRunAsync(preset.Id, cancellationToken).ConfigureAwait(false);

        return runs.Any(r => r.Run.Level == Domain.Results.VerdictLevel.Fail) ? 1 : 0;
    }

    private static async Task<Preset?> ResolveAsync(PresetService presets, string raw, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(raw, out var id))
        {
            var byId = await presets.GetAsync(id, cancellationToken).ConfigureAwait(false);

            if (byId is not null)
            {
                return byId;
            }
        }

        var byName = await presets.FindByNameAsync(raw, cancellationToken).ConfigureAwait(false);

        if (byName is not null)
        {
            return byName;
        }

        Console.Error.WriteLine($"Пресет «{raw}» не найден. Список: storm presets list");
        return null;
    }

    private static string? Nullify(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>Показ библиотеки пресетов.</summary>
internal static class PresetRenderer
{
    public static void WriteList(IReadOnlyList<Preset> presets)
    {
        ArgumentNullException.ThrowIfNull(presets);

        if (presets.Count == 0)
        {
            Console.WriteLine("Библиотека пуста. Сохрани измерение как пресет: storm ping gateway --save-preset \"Шлюз\"");
            return;
        }

        Console.WriteLine($"  {"имя",-28} {"что",-10} {"цель",-24} {"ред.",5} {"запусков",9}  теги");

        foreach (var preset in presets)
        {
            var tags = preset.Tags.Count == 0 ? string.Empty : string.Join(", ", preset.Tags);

            Console.WriteLine(
                $"  {Shorten(preset.Name, 28),-28} {Subject(preset),-10} {Shorten(preset.Target.DisplayName, 24),-24} "
                + $"{preset.Version,5} {preset.RunCount,9}  {tags}");
        }

        Console.WriteLine();
        Console.WriteLine($"Всего пресетов: {presets.Count}. Запуск: storm presets run <имя>");
    }

    public static void WriteDetails(Preset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);

        Console.WriteLine($"Имя       : {preset.Name}");

        if (!string.IsNullOrWhiteSpace(preset.Description))
        {
            Console.WriteLine($"Описание  : {preset.Description}");
        }

        Console.WriteLine(preset.Kind == PresetKind.Scenario
            ? $"Сценарий  : {preset.Subject}"
            : $"Проба     : {preset.Subject}");
        Console.WriteLine($"Цель      : {preset.Target.DisplayName}");
        Console.WriteLine($"Редакция  : {preset.Version}");
        Console.WriteLine($"Создан    : {preset.CreatedUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
        Console.WriteLine($"Изменён   : {preset.UpdatedUtc.ToLocalTime():dd.MM.yyyy HH:mm}");
        Console.WriteLine($"Запусков  : {preset.RunCount}"
                          + (preset.LastRunUtc is { } last ? $", последний {last.ToLocalTime():dd.MM.yyyy HH:mm}" : string.Empty));

        if (preset.Tags.Count > 0)
        {
            Console.WriteLine($"Теги      : {string.Join(", ", preset.Tags)}");
        }

        if (preset.Parameters.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  [параметры]");
            foreach (var (key, value) in preset.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"    {key,-16} {value ?? "—"}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"Идентификатор: {preset.Id.ToString()[..8]}");
    }

    /// <summary>
    /// Что запускает пресет, одной колонкой.
    /// </summary>
    /// <remarks>
    /// Сценарий помечен явно: «web» в колонке проб читалось бы как проба с таким
    /// именем, а её не существует, и оператор искал бы её в «storm probes».
    /// </remarks>
    private static string Subject(Preset preset) =>
        preset.Kind == PresetKind.Scenario ? $"сцен. {preset.Subject}" : preset.Subject;

    private static string Shorten(string value, int limit) =>
        value.Length <= limit ? value : value[..(limit - 1)] + "…";

    public static string FormatCount(int value) => value.ToString(CultureInfo.InvariantCulture);
}
