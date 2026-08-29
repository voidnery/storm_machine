using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Scenarios;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Сборка своих сценариев.
/// </summary>
/// <remarks>
/// Закрывает долг И-11: на экране выбирался готовый шаблон, а собрать свою цепочку
/// из произвольных проб можно было только правкой кода.
/// <para>
/// Шаг добавляется отдельной командой, а не одной строкой со всей цепочкой. Причина
/// в том, что у шага четыре части — проба, цель, параметры и пороги, — и строка,
/// вмещающая их все для пяти шагов, не набирается и не читается. Собирать цепочку
/// по шагу медленнее ровно один раз, а исправлять один шаг из пяти — каждый раз.
/// </para>
/// </remarks>
internal static class ScenarioEditCommands
{
    public static IEnumerable<Command> Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        yield return BuildNew(services);
        yield return BuildStep(services);
        yield return BuildDrop(services);
        yield return BuildMove(services);
        yield return BuildShow(services);
        yield return BuildRemove(services);
        yield return BuildFrom(services);
    }

    // -------------------------------------------------------------------- завести

    private static Command BuildNew(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Как сценарий будет называться." };

        var about = new Option<string?>("--описание", "--about")
        {
            Description = "Что этот сценарий проверяет.",
        };

        var command = new Command("new", "Завести пустой сценарий и добавлять в него шаги.")
        {
            name,
            about,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IScenarioStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var title = parse.GetValue(name)!.Trim();

            if (await store.FindAsync(title, cancellationToken).ConfigureAwait(false) is not null)
            {
                Console.Error.WriteLine($"Сценарий «{title}» уже есть. Посмотреть: storm scenario show {title}");

                return 1;
            }

            await store.SaveAsync(
                new Scenario
                {
                    Id = Guid.NewGuid(),
                    Name = title,
                    Description = parse.GetValue(about),
                    Steps = [],
                },
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Сценарий «{title}» заведён. Шагов пока нет.");
            Console.WriteLine();
            Console.WriteLine($"  storm scenario step {title} --проба ping --цель 192.168.1.1");
            Console.WriteLine($"  storm scenario show {title}");

            return 0;
        });

        return command;
    }

    // ---------------------------------------------------------------- добавить шаг

    private static Command BuildStep(IServiceProvider services)
    {
        var name = new Argument<string>("сценарий") { Description = "Имя сценария." };

        var probe = new Option<string>("--проба", "--probe")
        {
            Description = "Какая проба выполняет шаг: ping, dns, http, tls…",
            Required = true,
        };

        var target = new Option<string?>("--цель", "--target")
        {
            Description = "Цель шага. Без неё берётся цель, названная при запуске.",
        };

        var title = new Option<string?>("--название", "--title")
        {
            Description = "Как шаг называется в отчёте. По умолчанию — название пробы.",
        };

        var parameters = new Option<string[]>("--параметр", "--param")
        {
            Description = "Параметр пробы вида имя=значение. Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var thresholds = new Option<string[]>("--порог", "--threshold")
        {
            Description = "Порог вида «p95 < 100». Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var command = new Command("step", "Добавить шаг в конец сценария.")
        {
            name,
            probe,
            target,
            title,
            parameters,
            thresholds,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IScenarioStore>();
            var registry = services.GetRequiredService<IProbeRegistry>();

            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var scenario = await store.FindAsync(parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (scenario is null)
            {
                Console.Error.WriteLine($"Сценарий «{parse.GetValue(name)}» не найден.");

                return 1;
            }

            var probeName = parse.GetValue(probe)!;

            if (!registry.TryGet(probeName, out var found))
            {
                Console.Error.WriteLine($"Проба «{probeName}» не зарегистрирована. Список: storm probes.");

                return 1;
            }

            // Цель шага может отличаться от цели сценария — так устроено сравнение
            // резолверов, где каждый шаг спрашивает свой сервер. Без неё шаг возьмёт
            // ту, что назовут при запуске.
            var stepTarget = parse.GetValue(target) is { Length: > 0 } text
                ? Target.Parse(text)
                : scenario.Steps.Count > 0
                    ? scenario.Steps[^1].Target
                    : Target.Parse("127.0.0.1");

            List<Threshold> limits;

            try
            {
                limits = [.. (parse.GetValue(thresholds) ?? []).Select(t => Threshold.Parse(t))];
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }

            var step = new ScenarioStep
            {
                Name = parse.GetValue(title) ?? found.Descriptor.Title,
                ProbeName = probeName,
                Target = stepTarget,
                Parameters = ParseParameters(parse.GetValue(parameters) ?? []),
                Thresholds = limits,
            };

            var errors = found.Validate(new ProbeRequest { Target = step.Target, Parameters = step.Parameters });

            if (errors.Count > 0)
            {
                // Проверять надо здесь, а не при запуске: сценарий, собранный
                // с непригодными параметрами, упал бы посреди прогона — и оператор
                // узнал бы об этом, потратив время на предыдущие шаги.
                foreach (var error in errors)
                {
                    Console.Error.WriteLine($"Параметр {error.ParameterName}: {error.Message}");
                }

                return 2;
            }

            await Save(store, scenario with { Steps = [.. scenario.Steps, step] }, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"Шаг {scenario.Steps.Count + 1} добавлен: {step.Name} ({probeName}) → {step.Target.DisplayName}");

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------- изменить

    private static Command BuildDrop(IServiceProvider services)
    {
        var name = new Argument<string>("сценарий") { Description = "Имя сценария." };
        var number = new Argument<int>("номер") { Description = "Номер шага, считая с единицы." };

        var command = new Command("drop", "Убрать шаг из сценария.") { name, number };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IScenarioStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var scenario = await store.FindAsync(parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (scenario is null)
            {
                Console.Error.WriteLine($"Сценарий «{parse.GetValue(name)}» не найден.");

                return 1;
            }

            var index = parse.GetValue(number) - 1;

            if (index < 0 || index >= scenario.Steps.Count)
            {
                Console.Error.WriteLine(
                    $"У сценария {scenario.Steps.Count} шагов — номер {parse.GetValue(number)} вне их.");

                return 2;
            }

            var dropped = scenario.Steps[index];
            var steps = scenario.Steps.ToList();
            steps.RemoveAt(index);

            await Save(store, scenario with { Steps = steps }, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Шаг {parse.GetValue(number)} «{dropped.Name}» убран.");

            return 0;
        });

        return command;
    }

    private static Command BuildMove(IServiceProvider services)
    {
        var name = new Argument<string>("сценарий") { Description = "Имя сценария." };
        var from = new Argument<int>("откуда") { Description = "Номер шага сейчас." };
        var to = new Argument<int>("куда") { Description = "Каким по счёту он должен стать." };

        var command = new Command("move", "Переставить шаг: порядок в цепочке важен.")
        {
            name,
            from,
            to,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IScenarioStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var scenario = await store.FindAsync(parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (scenario is null)
            {
                Console.Error.WriteLine($"Сценарий «{parse.GetValue(name)}» не найден.");

                return 1;
            }

            var source = parse.GetValue(from) - 1;
            var destination = Math.Clamp(parse.GetValue(to) - 1, 0, Math.Max(0, scenario.Steps.Count - 1));

            if (source < 0 || source >= scenario.Steps.Count)
            {
                Console.Error.WriteLine($"У сценария {scenario.Steps.Count} шагов — номер {parse.GetValue(from)} вне их.");

                return 2;
            }

            var steps = scenario.Steps.ToList();
            var moved = steps[source];

            steps.RemoveAt(source);
            steps.Insert(destination, moved);

            await Save(store, scenario with { Steps = steps }, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"«{moved.Name}» теперь шаг {destination + 1}.");

            return 0;
        });

        return command;
    }

    // -------------------------------------------------------------------- смотреть

    private static Command BuildShow(IServiceProvider services)
    {
        var name = new Argument<string>("сценарий") { Description = "Имя сценария или ключ шаблона." };

        var command = new Command("show", "Показать сценарий по шагам.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IScenarioStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var text = parse.GetValue(name)!;
            var scenario = await store.FindAsync(text, cancellationToken).ConfigureAwait(false);

            if (scenario is null)
            {
                try
                {
                    // Шаблон тоже показывается: он и есть образец, с которого
                    // собирают своё, и посмотреть на него до сборки полезнее,
                    // чем после первой неудачи.
                    scenario = ScenarioTemplates.Create(text, "пример.рф");
                }
                catch (ArgumentException ex)
                {
                    Console.Error.WriteLine(ex.Message);

                    return 1;
                }
            }

            ScenarioRenderer.WriteDefinition(scenario);

            return 0;
        });

        return command;
    }

    private static Command BuildRemove(IServiceProvider services)
    {
        var name = new Argument<string>("сценарий") { Description = "Имя сценария." };

        var command = new Command("rm", "Удалить свой сценарий. Шаблоны удалить нельзя.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IScenarioStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var scenario = await store.FindAsync(parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (scenario is null)
            {
                Console.Error.WriteLine(
                    $"Свой сценарий «{parse.GetValue(name)}» не найден. Шаблоны удалить нельзя — "
                    + "они часть продукта.");

                return 1;
            }

            await store.DeleteAsync(scenario.Id, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Сценарий «{scenario.Name}» удалён.");

            return 0;
        });

        return command;
    }

    /// <summary>Копия шаблона под своим именем — с неё удобнее начинать, чем с пустого.</summary>
    private static Command BuildFrom(IServiceProvider services)
    {
        var template = new Argument<string>("шаблон") { Description = "Ключ шаблона: web, dns, voice." };
        var name = new Argument<string>("имя") { Description = "Как назвать копию." };

        var command = new Command("from", "Собрать свой сценарий из шаблона и дальше править его.")
        {
            template,
            name,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IScenarioStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var title = parse.GetValue(name)!.Trim();

            if (await store.FindAsync(title, cancellationToken).ConfigureAwait(false) is not null)
            {
                Console.Error.WriteLine($"Сценарий «{title}» уже есть.");

                return 1;
            }

            Scenario source;

            try
            {
                source = ScenarioTemplates.Create(parse.GetValue(template)!, "пример.рф");
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }

            // Копия, а не ссылка: править шаблон нельзя, и в этом его ценность —
            // к нему всегда можно вернуться.
            await store.SaveAsync(
                new Scenario
                {
                    Id = Guid.NewGuid(),
                    Name = title,
                    Description = $"Собран из шаблона «{parse.GetValue(template)}».",
                    Steps = source.Steps,
                },
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Сценарий «{title}» собран из шаблона: "
                              + $"{ScenarioLibrary.Describe(source)}.");
            Console.WriteLine($"  storm scenario show {title}");

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ помощники

    /// <summary>Сохраняет с поднятой редакцией: прогоны разных редакций несравнимы.</summary>
    private static Task Save(IScenarioStore store, Scenario scenario, CancellationToken cancellationToken) =>
        store.SaveAsync(
            scenario with { Version = scenario.Version + 1, UpdatedUtc = DateTimeOffset.UtcNow },
            cancellationToken);

    private static Dictionary<string, object?> ParseParameters(string[] values)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var text in values)
        {
            var split = text.IndexOf('=', StringComparison.Ordinal);

            if (split <= 0)
            {
                continue;
            }

            var name = text[..split].Trim();
            var value = text[(split + 1)..].Trim();

            // Числа разбираются числами: проба объявляет типы своих параметров,
            // и строка «4» там, где ждут целое, до неё не доедет.
            parameters[name] = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? number
                : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)
                    ? real
                    : value;
        }

        return parameters;
    }
}
