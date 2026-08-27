using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Discovery;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Инвентарь: <c>storm devices</c>, история сканирований и различия между ними.
/// </summary>
/// <remarks>
/// Список устройств отвечает на вопрос «что в сети». Различия между сканированиями —
/// на вопрос «что изменилось», ради которого инвентарь и ведут: сам по себе список
/// вчерашнего дня ничего не говорит.
/// </remarks>
internal static class DevicesCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("devices", "Инвентарь: устройства, сканирования, различия.");

        command.Subcommands.Add(BuildList(services));
        command.Subcommands.Add(BuildScans(services));
        command.Subcommands.Add(BuildDiff(services));
        command.Subcommands.Add(BuildName(services));
        command.Subcommands.Add(BuildAudit(services));

        // Без подкоманды показывается список: самый частый вопрос не должен
        // требовать лишнего слова.
        command.SetAction(async (_, cancellationToken) =>
            await ListAsync(services, cancellationToken).ConfigureAwait(false));

        return command;
    }

    private static Command BuildList(IServiceProvider services)
    {
        var command = new Command("list", "Все известные устройства.");

        command.SetAction(async (_, cancellationToken) =>
            await ListAsync(services, cancellationToken).ConfigureAwait(false));

        return command;
    }

    private static async Task<int> ListAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var store = services.GetRequiredService<IDeviceStore>();
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var devices = await store.ListDevicesAsync(cancellationToken).ConfigureAwait(false);
        DeviceRenderer.WriteInventory(devices);

        return devices.Count > 0 ? 0 : 1;
    }

    private static Command BuildScans(IServiceProvider services)
    {
        var limitOption = new Option<int>("--limit")
        {
            Description = "Сколько сканирований показать.",
            DefaultValueFactory = _ => 20,
        };

        var command = new Command("scans", "История сканирований.") { limitOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var scans = await store
                .ListScansAsync(parseResult.GetValue(limitOption), cancellationToken)
                .ConfigureAwait(false);

            if (scans.Count == 0)
            {
                Console.WriteLine("Сканирований не было. Запустите: storm discover");
                return 1;
            }

            Console.WriteLine($"  {"id",-8} {"когда",-17} {"диапазон",-20} {"опрошено",8} {"найдено",8}");

            foreach (var scan in scans)
            {
                Console.WriteLine($"  {scan.Id.ToString()[..8]} {scan.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm} "
                                  + $"{scan.Range,-20} {scan.Probed,8} {scan.Devices.Count,8}"
                                  + (scan.WasCancelled ? "  (прервано)" : string.Empty));
            }

            Console.WriteLine();
            Console.WriteLine("Различия между двумя: storm devices diff <id> <id>");

            return 0;
        });

        return command;
    }

    private static Command BuildDiff(IServiceProvider services)
    {
        var beforeArgument = new Argument<string>("было") { Description = "Идентификатор раннего сканирования." };
        var afterArgument = new Argument<string>("стало")
        {
            Description = "Идентификатор позднего сканирования. По умолчанию — последнее.",
            DefaultValueFactory = _ => string.Empty,
        };

        var command = new Command("diff", "Что изменилось между сканированиями.")
        {
            beforeArgument,
            afterArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var before = await FindAsync(store, parseResult.GetValue(beforeArgument)!, cancellationToken).ConfigureAwait(false);

            if (before is null)
            {
                return 1;
            }

            var afterText = parseResult.GetValue(afterArgument)!;
            DiscoveryScan? after;

            if (string.IsNullOrWhiteSpace(afterText))
            {
                var scans = await store.ListScansAsync(1, cancellationToken).ConfigureAwait(false);
                after = scans.Count > 0
                    ? await store.GetScanAsync(scans[0].Id, cancellationToken).ConfigureAwait(false)
                    : null;
            }
            else
            {
                after = await FindAsync(store, afterText, cancellationToken).ConfigureAwait(false);
            }

            if (after is null)
            {
                Console.Error.WriteLine("Второе сканирование не найдено.");
                return 1;
            }

            Console.WriteLine($"Было : {before.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm}  {before.Range}  "
                              + $"({before.Devices.Count} устройств)");
            Console.WriteLine($"Стало: {after.StartedUtc.ToLocalTime():dd.MM.yyyy HH:mm}  {after.Range}  "
                              + $"({after.Devices.Count} устройств)");

            DeviceRenderer.WriteDiff(ScanDiff.Between(before.Devices, after.Devices));

            return 0;
        });

        return command;
    }

    /// <summary>
    /// Присвоить устройству имя вручную.
    /// </summary>
    /// <remarks>
    /// Правка оператора — тоже свидетельство, только с наивысшим весом. Поэтому она
    /// переживает пересканирование: инвентарь вычисляется из свидетельств,
    /// а не переписывается последним наблюдением.
    /// </remarks>
    private static Command BuildName(IServiceProvider services)
    {
        var identityArgument = new Argument<string>("устройство")
        {
            Description = "MAC-адрес устройства или его IP, если MAC неизвестен.",
        };

        var nameArgument = new Argument<string>("имя") { Description = "Как называть это устройство." };

        var command = new Command("name", "Назвать устройство вручную — правка переживает пересканирование.")
        {
            identityArgument,
            nameArgument,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var identity = parseResult.GetValue(identityArgument)!.Trim();
            var name = parseResult.GetValue(nameArgument)!.Trim();

            await store.PinAsync(
                identity,
                Evidence.Of(EvidenceSource.Manual, EvidenceKind.HostName, name, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Устройство {identity} названо «{name}».");
            Console.WriteLine("Правка переживёт пересканирование: она сильнее любого наблюдения.");

            return 0;
        });

        return command;
    }

    private static Command BuildAudit(IServiceProvider services)
    {
        var limitOption = new Option<int>("--limit")
        {
            Description = "Сколько записей показать.",
            DefaultValueFactory = _ => 50,
        };

        var command = new Command("audit", "Журнал активных действий по сети.") { limitOption };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IDeviceStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var entries = await store
                .ListAuditAsync(parseResult.GetValue(limitOption), cancellationToken)
                .ConfigureAwait(false);

            DeviceRenderer.WriteAudit(entries);

            return 0;
        });

        return command;
    }

    private static async Task<DiscoveryScan?> FindAsync(
        IDeviceStore store,
        string text,
        CancellationToken cancellationToken)
    {
        if (Guid.TryParse(text, out var exact))
        {
            return await store.GetScanAsync(exact, cancellationToken).ConfigureAwait(false);
        }

        var scans = await store.ListScansAsync(100, cancellationToken).ConfigureAwait(false);
        var match = scans.FirstOrDefault(s => s.Id.ToString().StartsWith(text, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            Console.Error.WriteLine($"Сканирование, начинающееся с «{text}», не найдено.");
            return null;
        }

        return await store.GetScanAsync(match.Id, cancellationToken).ConfigureAwait(false);
    }
}
