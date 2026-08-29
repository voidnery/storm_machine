using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Presets;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Presets;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Сценарии: <c>storm scenario</c>.
/// </summary>
/// <remarks>
/// Сценарий отвечает на вопрос, на который одиночная проба ответить не может:
/// «работает ли это целиком, и если нет — где именно сломалось». Одно число
/// «страница открылась за 460 мс» не говорит, медленно в разрешении имени,
/// в соединении, в рукопожатии TLS или на сервере.
/// </remarks>
internal static class ScenarioCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("scenario", "Цепочки проб с порогами: проверить работу целиком.");

        command.Subcommands.Add(BuildTemplates());
        command.Subcommands.Add(BuildSets(services));
        command.Subcommands.Add(BuildRun(services));

        // Сборка своих цепочек (И-22). До неё сценарии существовали только зашитыми.
        foreach (var editor in ScenarioEditCommands.Create(services))
        {
            command.Subcommands.Add(editor);
        }

        command.SetAction(async (_, cancellationToken) =>
        {
            var library = services.GetRequiredService<ScenarioLibrary>();

            ScenarioRenderer.WriteLibrary(
                await library.ListAsync(cancellationToken).ConfigureAwait(false));

            return 0;
        });

        return command;
    }

    private static Command BuildTemplates()
    {
        var command = new Command("templates", "Готовые сценарии.");

        command.SetAction((_, _) =>
        {
            ScenarioRenderer.WriteTemplates();
            return Task.FromResult(0);
        });

        return command;
    }

    private static Command BuildSets(IServiceProvider services)
    {
        var command = new Command("sets", "Готовые наборы целей.");

        command.SetAction((_, _) =>
        {
            ScenarioRenderer.WriteTargetSets(services.GetRequiredService<INetworkEnvironment>());
            return Task.FromResult(0);
        });

        return command;
    }

    private static Command BuildRun(IServiceProvider services)
    {
        var templateArgument = new Argument<string>("шаблон")
        {
            Description = "Ключ шаблона или имя своего сценария. Список: storm scenario.",
        };

        var targetArgument = new Argument<string>("цель")
        {
            Description = "Имя узла, список через запятую, имя набора (storm scenario sets) или @файл.",
        };

        var noSaveOption = new Option<bool>("--no-save")
        {
            Description = "Не сохранять шаги в журнал прогонов.",
        };

        var savePresetOption = new Option<string>("--сохранить-пресет", "--save-preset")
        {
            Description = "Записать этот сценарий в библиотеку под указанным именем.",
            DefaultValueFactory = _ => string.Empty,
        };

        var command = new Command("run", "Выполнить сценарий: шаблон или свой.")
        {
            templateArgument,
            targetArgument,
            noSaveOption,
            savePresetOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var runner = services.GetRequiredService<ScenarioRunner>();
            var library = services.GetRequiredService<ScenarioLibrary>();
            var clock = services.GetRequiredService<IHighResolutionClock>();
            var environment = services.GetRequiredService<INetworkEnvironment>();
            var store = services.GetRequiredService<IRunStore>();

            var save = !parseResult.GetValue(noSaveOption);
            var template = parseResult.GetValue(templateArgument)!;

            TargetSet set;

            try
            {
                set = ReadTargets(parseResult.GetValue(targetArgument)!, environment);
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

            // Калибровка нужна каждому измерению: без неё порог достоверности
            // неизвестен, и мелкие значения нечем отличить от собственного шума.
            // Одна на весь набор: часы за время прогона не меняются.
            await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            void OnCancelKey(object? sender, ConsoleCancelEventArgs args)
            {
                args.Cancel = true;
                linked.Cancel();
            }

            Console.CancelKeyPress += OnCancelKey;

            var runs = new List<(string Target, Domain.Scenarios.ScenarioRun Run)>(set.Targets.Count);

            try
            {
                foreach (var target in set.Targets)
                {
                    var scenario = await library
                        .CreateAsync(template, target, linked.Token)
                        .ConfigureAwait(false);

                    ScenarioRenderer.WriteHeader(scenario, environment.GetPrimaryAdapter(), clock, set);

                    var run = await runner
                        .RunAsync(scenario, save, ScenarioRenderer.CreateProgressWriter(), linked.Token)
                        .ConfigureAwait(false);

                    ScenarioRenderer.WriteRun(run, clock.CalibrationBaselineMs);
                    runs.Add((target, run));
                }
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine();
                Console.WriteLine("Сценарий прерван.");
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= OnCancelKey;
            }

            // Сводка по набору — то, ради чего целей несколько: одна упавшая цель
            // из пяти и пять упавших целей из пяти означают разные неисправности.
            if (runs.Count > 1)
            {
                ScenarioRenderer.WriteSetSummary(set, runs);
            }

            if (parseResult.GetValue(savePresetOption) is { Length: > 0 } presetName)
            {
                await SavePresetAsync(services, presetName, template, set, cancellationToken).ConfigureAwait(false);
            }

            return runs.Any(r => r.Run.Level == Domain.Results.VerdictLevel.Fail) ? 1 : 0;
        });

        return command;
    }

    /// <summary>
    /// Кладёт выполненный сценарий в библиотеку.
    /// </summary>
    /// <remarks>
    /// Пресет рождается не из формы, а из проверки, которая только что оказалась
    /// полезной, — то же правило, что у проб. Цель сохраняется исходной строкой:
    /// если это было имя набора, повторный запуск возьмёт набор заново, и пресет
    /// «проверить все наши сайты» переживёт появление девятого сайта.
    /// </remarks>
    private static async Task SavePresetAsync(
        IServiceProvider services,
        string name,
        string template,
        TargetSet set,
        CancellationToken cancellationToken)
    {
        var presets = services.GetRequiredService<PresetService>();
        var now = DateTimeOffset.UtcNow;

        var preset = new Preset
        {
            Id = Guid.NewGuid(),
            Name = name,
            Kind = PresetKind.Scenario,
            Subject = template,
            Target = Domain.Targets.Target.Host(set.Key, set.Title),
            Version = 1,
            CreatedUtc = now,
            UpdatedUtc = now,
        };

        var existing = await presets.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            preset = preset with { Id = existing.Id, CreatedUtc = existing.CreatedUtc };
        }

        try
        {
            var saved = await presets.SaveAsync(preset, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine($"Пресет «{saved.Name}» сохранён (редакция {saved.Version}).");
            Console.WriteLine($"  storm presets run \"{saved.Name}\"");
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Пресет не сохранён: {ex.Message}");
        }
    }

    /// <summary>
    /// Разбирает поле цели, включая чтение файла со списком.
    /// </summary>
    /// <remarks>
    /// Файл читается здесь, а не в слое сценариев: диск — забота внешнего слоя,
    /// и тащить его в разбор наборов значило бы дать слою приложения то, что ему
    /// по устройству проекта не положено.
    /// </remarks>
    private static TargetSet ReadTargets(string specification, INetworkEnvironment environment)
    {
        var text = specification.Trim();

        if (text.Length == 0 || text[0] != TargetSets.FilePrefix)
        {
            return TargetSets.Resolve(text, environment);
        }

        var path = text[1..];

        if (!File.Exists(path))
        {
            throw new ArgumentException($"Файл со списком целей не найден: {path}");
        }

        return TargetSets.FromLines(
            Path.GetFileNameWithoutExtension(path),
            $"файл {path}",
            File.ReadAllLines(path));
    }
}
