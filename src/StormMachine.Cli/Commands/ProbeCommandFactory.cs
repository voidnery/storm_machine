using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Presets;
using StormMachine.Application.Probes;
using StormMachine.Application.Runs;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Строит команду командной строки по паспорту пробы.
/// </summary>
/// <remarks>
/// Реализация принципа 1 из docs/01-analysis.md §8.2: проба объявляет параметры, а
/// интерфейс строит форму по объявлению. Здесь «форма» — набор ключей командной строки.
/// <para>
/// Появилось в И-2. Шесть проб потребовали бы шести почти одинаковых файлов команд;
/// вместо этого команда одна и выводится из объявления. Проверка того же принципа
/// в графическом клиенте будет ровно такой же по устройству.
/// </para>
/// </remarks>
internal static class ProbeCommandFactory
{
    public static Command Create(IServiceProvider services, IProbe probe)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(probe);

        var descriptor = probe.Descriptor;

        // Проба без цели не просит её у оператора: спрашивать то, чем не воспользуешься,
        // значит заставить человека выдумать ответ.
        var targetArgument = new Argument<string>("цель")
        {
            Description = TargetHint(descriptor),
            Arity = descriptor.RequiresTarget ? ArgumentArity.ExactlyOne : ArgumentArity.ZeroOrOne,
        };

        var readers = new List<(string Name, Func<ParseResult, object?> Read)>();
        var command = new Command(descriptor.Name, descriptor.Description);

        if (descriptor.RequiresTarget)
        {
            command.Arguments.Add(targetArgument);
        }

        foreach (var parameter in descriptor.Parameters)
        {
            var (option, reader) = CreateOption(parameter);
            command.Options.Add(option);
            readers.Add((parameter.Name, reader));
        }

        var quietOption = new Option<bool>("--quiet", "-q")
        {
            Description = "Только итоговая сводка, без построчного вывода.",
        };

        var saveOption = new Option<bool>("--save")
        {
            Description = "Сохранить прогон в журнал (storm runs list).",
        };

        // Сквозной принцип «сохранить как пресет»: пресет рождается не из формы,
        // а из измерения, которое только что оказалось полезным.
        var savePresetOption = new Option<string>("--save-preset")
        {
            Description = "Сохранить эти параметры как пресет с указанным именем.",
            DefaultValueFactory = _ => string.Empty,
        };

        command.Options.Add(quietOption);
        command.Options.Add(saveOption);
        command.Options.Add(savePresetOption);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var (name, read) in readers)
            {
                parameters[name] = read(parseResult);
            }

            var request = new ProbeRequest
            {
                Target = descriptor.RequiresTarget
                    ? ParseTarget(parseResult.GetValue(targetArgument)!)
                    : Target.Parse(descriptor.Title),
                Parameters = parameters,
            };

            return await RunAsync(
                services,
                probe,
                request,
                parseResult.GetValue(quietOption),
                parseResult.GetValue(saveOption),
                parseResult.GetValue(savePresetOption),
                cancellationToken).ConfigureAwait(false);
        });

        return command;
    }

    private static async Task<int> RunAsync(
        IServiceProvider services,
        IProbe probe,
        ProbeRequest request,
        bool quiet,
        bool save,
        string? savePresetName,
        CancellationToken cancellationToken)
    {
        var environment = services.GetRequiredService<INetworkEnvironment>();
        var clock = services.GetRequiredService<IHighResolutionClock>();
        var orchestrator = services.GetRequiredService<RunOrchestrator>();
        var descriptor = probe.Descriptor;

        var errors = probe.Validate(request);
        if (errors.Count > 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine($"Параметр --{error.ParameterName}: {error.Message}");
            }

            return 2;
        }

        if (save)
        {
            var store = services.GetRequiredService<IRunStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        await clock.CalibrateAsync(cancellationToken).ConfigureAwait(false);

        var adapter = environment.GetPrimaryAdapter();

        // Профиль забирается тем же способом, что и в оркестраторе, и по той же причине:
        // шапка обязана показывать те условия, которые лягут в журнал. Разойтись им
        // нельзя — иначе оператор читает одно, а сравнивать потом будет другое.
        var profile = await MeasurementConditions
            .ActiveProfileAsync(services.GetService<IProfileStore>(), cancellationToken)
            .ConfigureAwait(false);

        ProbeRenderer.WriteHeader(
            descriptor,
            request.Target,
            MeasurementConditions.Build(adapter, clock, descriptor.Methodology, profile),
            adapter);

        // Ctrl+C отменяет измерение, но не прерывает подведение итога: уже измеренное
        // должно и показаться, и — при --save — доехать до журнала.
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
                    OnSample = quiet ? null : ProbeRenderer.CreateLiveWriter(descriptor),

                    // Ход подготовки идёт и в тихом режиме: в нём подавлены измерения,
                    // а «жду звонка агента, набери на его машине вот это» — просьба
                    // к оператору, без которой прогон просто не состоится.
                    OnProgress = ProbeRenderer.WriteProgress,
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

        ProbeRenderer.WriteSummary(descriptor, outcome.Result, clock);

        if (outcome.RunId is { } runId)
        {
            Console.WriteLine();
            Console.WriteLine($"Сохранено в журнал: {runId}");
            Console.WriteLine($"  storm runs show {runId}");
        }

        if (!string.IsNullOrWhiteSpace(savePresetName))
        {
            await SavePresetAsync(services, descriptor.Name, request, savePresetName, cancellationToken)
                .ConfigureAwait(false);
        }

        return outcome.Result.SuccessCount > 0 ? 0 : 1;
    }

    private static async Task SavePresetAsync(
        IServiceProvider services,
        string probeName,
        ProbeRequest request,
        string name,
        CancellationToken cancellationToken)
    {
        var presets = services.GetRequiredService<PresetService>();
        var preset = PresetService.FromRequest(name, probeName, request);

        var existing = await presets.FindByNameAsync(name, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            // Совпадение по имени — это тот же тест, а не второй такой же.
            // Библиотека из десяти «Шлюз (1)…(10)» бесполезна.
            preset = preset with { Id = existing.Id, CreatedUtc = existing.CreatedUtc };
        }

        try
        {
            var saved = await presets.SaveAsync(preset, cancellationToken).ConfigureAwait(false);

            Console.WriteLine();
            Console.WriteLine(existing is null
                ? $"Сохранено как пресет «{saved.Name}» (редакция {saved.Version})."
                : $"Пресет «{saved.Name}» обновлён (редакция {saved.Version}).");
            Console.WriteLine($"  storm presets run \"{saved.Name}\"");
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine($"Пресет не сохранён: {ex.Message}");
        }
    }

    private static (Option Option, Func<ParseResult, object?> Read) CreateOption(ProbeParameter parameter)
    {
        var name = "--" + parameter.Name;
        var description = parameter.Description ?? parameter.Label;

        switch (parameter.Type)
        {
            case ProbeParameterType.Boolean:
            {
                var option = new Option<bool>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => parameter.DefaultValue is bool b && b,
                };

                return (option, parse => parse.GetValue(option));
            }

            case ProbeParameterType.Text:
            case ProbeParameterType.Choice:
            {
                var fallback = parameter.DefaultValue as string ?? string.Empty;
                var option = new Option<string>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => fallback,
                };

                return (option, parse => parse.GetValue(option));
            }

            case ProbeParameterType.Decimal:
            {
                var fallback = Convert.ToDouble(parameter.DefaultValue ?? 0d, System.Globalization.CultureInfo.InvariantCulture);
                var option = new Option<double>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => fallback,
                };

                return (option, parse => parse.GetValue(option));
            }

            default:
            {
                var fallback = Convert.ToInt32(parameter.DefaultValue ?? 0, System.Globalization.CultureInfo.InvariantCulture);
                var option = new Option<int>(name)
                {
                    Description = description,
                    DefaultValueFactory = _ => fallback,
                };

                return (option, parse => parse.GetValue(option));
            }
        }
    }

    private static string TargetHint(ProbeDescriptor descriptor) => descriptor.Kind switch
    {
        ProbeKind.Dns => "Имя для разрешения, например example.com.",
        ProbeKind.Http => "Адрес: example.com или https://example.com/path.",
        ProbeKind.Tls => "Имя узла, например example.com.",
        _ => "IP-адрес, имя узла или слово gateway для шлюза по умолчанию.",
    };

    private static Target ParseTarget(string raw) =>
        raw.Equals("gateway", StringComparison.OrdinalIgnoreCase) || raw.Equals("шлюз", StringComparison.OrdinalIgnoreCase)
            ? Target.Gateway("шлюз по умолчанию")
            : Target.Parse(raw);

}
