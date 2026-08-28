using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Discovery;
using StormMachine.Domain.Topology;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Правка карты и инвентаря оператором.
/// </summary>
/// <remarks>
/// Все команды здесь записывают <b>свидетельство</b>, а не результат. Инвентарь и карта
/// пересчитываются из свидетельств при каждом сканировании, и правка, записанная
/// в результат, была бы затёрта первым же пересчётом. Записанная свидетельством —
/// переживает любое их число, а отменяется удалением одной записи.
/// </remarks>
internal static class TopologyEditCommands
{
    public static void AddTo(Command topology, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(services);

        topology.Subcommands.Add(BuildLink(services));
        topology.Subcommands.Add(BuildUnlink(services));
        topology.Subcommands.Add(BuildHide(services));
        topology.Subcommands.Add(BuildEdits(services));
        topology.Subcommands.Add(BuildForget(services));
    }

    private static Command BuildLink(IServiceProvider services)
    {
        var fromArgument = new Argument<string>("от") { Description = "Тождество или адрес узла." };
        var toArgument = new Argument<string>("до") { Description = "Тождество или адрес второго узла." };

        var noteOption = new Option<string>("--почему")
        {
            Description = "Откуда известно про эту связь — попадёт в подпись на карте.",
            DefaultValueFactory = _ => string.Empty,
        };

        var command = new Command("link", "Указать связь, которой инструмент не увидел.")
        {
            fromArgument,
            toArgument,
            noteOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var note = parseResult.GetValue(noteOption);

            var edit = TopologyEdit.Link(
                parseResult.GetValue(fromArgument)!.Trim(),
                parseResult.GetValue(toArgument)!.Trim(),
                Environment.UserName,
                string.IsNullOrWhiteSpace(note) ? null : note.Trim());

            await store.SaveTopologyEditAsync(edit, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Записано: {edit.Describe()}.");
            Console.WriteLine("Связь переживёт пересканирование: это свидетельство, а не пометка на картинке.");

            return 0;
        });

        return command;
    }

    private static Command BuildUnlink(IServiceProvider services)
    {
        var fromArgument = new Argument<string>("от") { Description = "Тождество или адрес узла." };
        var toArgument = new Argument<string>("до") { Description = "Тождество или адрес второго узла." };

        var noteOption = new Option<string>("--почему")
        {
            Description = "Почему связи нет на самом деле.",
            DefaultValueFactory = _ => string.Empty,
        };

        var command = new Command("unlink", "Убрать связь, которую инструмент вывел ошибочно.")
        {
            fromArgument,
            toArgument,
            noteOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var note = parseResult.GetValue(noteOption);

            var edit = TopologyEdit.Unlink(
                parseResult.GetValue(fromArgument)!.Trim(),
                parseResult.GetValue(toArgument)!.Trim(),
                Environment.UserName,
                string.IsNullOrWhiteSpace(note) ? null : note.Trim());

            await store.SaveTopologyEditAsync(edit, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Записано: {edit.Describe()}.");

            return 0;
        });

        return command;
    }

    private static Command BuildHide(IServiceProvider services)
    {
        var nodeArgument = new Argument<string>("узел") { Description = "Тождество или адрес узла." };

        var command = new Command("hide", "Убрать узел с карты.") { nodeArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var edit = TopologyEdit.Hide(parseResult.GetValue(nodeArgument)!.Trim(), Environment.UserName);
            await store.SaveTopologyEditAsync(edit, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Записано: {edit.Describe()}.");
            Console.WriteLine("Узел остаётся в инвентаре — скрыт только на карте.");

            return 0;
        });

        return command;
    }

    private static Command BuildEdits(IServiceProvider services)
    {
        var command = new Command("edits", "Все правки карты, сделанные оператором.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var edits = await store.ListTopologyEditsAsync(cancellationToken).ConfigureAwait(false);
            var aliases = await store.ListAliasesAsync(cancellationToken).ConfigureAwait(false);

            if (edits.Count == 0 && aliases.Count == 0)
            {
                Console.WriteLine("Правок нет: карта показывает ровно то, что увидел инструмент.");
                return 1;
            }

            if (edits.Count > 0)
            {
                Console.WriteLine($"  {"id",-8} {"когда",-17} {"кто",-14} что");

                foreach (var edit in edits)
                {
                    Console.WriteLine($"  {edit.Id.ToString()[..8]} {edit.AtUtc.ToLocalTime():dd.MM.yyyy HH:mm} "
                                      + $"{Shorten(edit.Operator, 14),-14} {edit.Describe()}");

                    if (edit.Note is { Length: > 0 } note)
                    {
                        Console.WriteLine($"           {note}");
                    }
                }

                Console.WriteLine();
                Console.WriteLine("Отменить: storm topology forget <id>");
            }

            if (aliases.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Объединённые устройства:");

                foreach (var alias in aliases)
                {
                    Console.WriteLine($"  {alias.Alias} → {alias.Primary}   "
                                      + $"{alias.AtUtc.ToLocalTime():dd.MM.yyyy HH:mm}, {alias.Operator}");
                }

                Console.WriteLine();
                Console.WriteLine("Разъединить: storm devices unmerge <тождество>");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildForget(IServiceProvider services)
    {
        var idArgument = new Argument<string>("id") { Description = "Идентификатор правки из storm topology edits." };

        var command = new Command("forget", "Отменить правку — наблюдения при этом не трогаются.") { idArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var text = parseResult.GetValue(idArgument)!.Trim();
            var edits = await store.ListTopologyEditsAsync(cancellationToken).ConfigureAwait(false);

            var match = edits.FirstOrDefault(e =>
                e.Id.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                Console.Error.WriteLine($"Правка, начинающаяся с «{text}», не найдена.");
                return 1;
            }

            await store.RemoveTopologyEditAsync(match.Id, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Отменено: {match.Describe()}.");
            Console.WriteLine("Карта вернулась к тому, что видит инструмент.");

            return 0;
        });

        return command;
    }

    /// <summary>Команды объединения дублей — они живут в инвентаре, а не на карте.</summary>
    public static void AddMergeTo(Command devices, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(services);

        var primaryArgument = new Argument<string>("основное") { Description = "Тождество, которое останется." };
        var duplicateArgument = new Argument<string>("дубль") { Description = "Тождество, которое к нему присоединится." };

        var merge = new Command(
            "merge",
            "Объединить два дубля в одно устройство — например, провод и Wi-Fi одного ноутбука.")
        {
            primaryArgument,
            duplicateArgument,
        };

        merge.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var primary = parseResult.GetValue(primaryArgument)!.Trim();
            var duplicate = parseResult.GetValue(duplicateArgument)!.Trim();

            try
            {
                await store.MergeAsync(primary, duplicate, Environment.UserName, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            Console.WriteLine($"Устройства объединены: {duplicate} присоединено к {primary}.");
            Console.WriteLine("Объединение живёт в инвентаре, поэтому действует и в списке, и в различиях,");
            Console.WriteLine("и на карте — объединять то же самое ещё раз где-то ещё не придётся.");

            return 0;
        });

        var unmergeArgument = new Argument<string>("тождество") { Description = "Что отсоединить." };
        var unmerge = new Command("unmerge", "Разъединить ранее объединённые записи.") { unmergeArgument };

        unmerge.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            await store.UnmergeAsync(parseResult.GetValue(unmergeArgument)!.Trim(), cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine("Записи разъединены.");

            return 0;
        });

        devices.Subcommands.Add(merge);
        devices.Subcommands.Add(unmerge);
    }

    /// <summary>Присвоение роли устройству — тоже свидетельство с наивысшим весом.</summary>
    public static Command BuildRole(IServiceProvider services)
    {
        var identityArgument = new Argument<string>("устройство") { Description = "MAC или адрес." };
        var roleArgument = new Argument<string>("роль")
        {
            Description = "Например: шлюз, коммутатор, принтер, точка доступа, сервер.",
        };

        var command = new Command("role", "Указать, что это за устройство.") { identityArgument, roleArgument };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var identity = parseResult.GetValue(identityArgument)!.Trim();
            var role = parseResult.GetValue(roleArgument)!.Trim();

            await store.PinAsync(
                identity,
                Evidence.Of(EvidenceSource.Manual, EvidenceKind.Role, role, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Устройству {identity} присвоена роль «{role}».");

            return 0;
        });

        return command;
    }

    private static string Shorten(string value, int width) =>
        value.Length <= width ? value : value[..(width - 1)] + "…";
}
