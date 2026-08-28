using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Topology;
using StormMachine.Cli.Rendering;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Карта сети: <c>storm topology</c>.
/// </summary>
/// <remarks>
/// В консоли карта показывается деревом, а не картинкой, — и это не заглушка.
/// Дерево отвечает на тот же вопрос «что с чем связано и насколько мы в этом уверены»,
/// проверяется глазами быстрее графа и переносится в переписку копированием.
/// </remarks>
internal static class TopologyCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var expandOption = new Option<bool>("--expand")
        {
            Description = "Показать все конечные узлы поимённо, не сворачивая в счётчик.",
        };

        var noPathsOption = new Option<bool>("--no-paths")
        {
            Description = "Не добавлять внешние узлы из сохранённых трассировок.",
        };

        var noVirtualOption = new Option<bool>("--no-virtual")
        {
            Description = "Пропустить виртуальные коммутаторы и VPN.",
        };

        // Опрос оборудования — отдельным ключом, а не по умолчанию: он идёт по чужой
        // сети и занимает секунды на устройство. Делать это молча при каждом взгляде
        // на карту значило бы слать трафик к оборудованию заказчика без спроса.
        var snmpOption = new Option<bool>("--snmp")
        {
            Description = "Опросить оборудование по SNMP: порты коммутаторов и соседей.",
        };

        var deviceOption = new Option<string[]>("--устройство", "--device")
        {
            Description = "Кого опрашивать. Без ключа — шлюзы. Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var jsonOption = new Option<string>("--json")
        {
            Description = "Записать карту в файл JSON.",
            DefaultValueFactory = _ => string.Empty,
        };

        var command = new Command("topology", "Карта сети: что с чем связано и насколько это достоверно.")
        {
            expandOption,
            noPathsOption,
            noVirtualOption,
            snmpOption,
            deviceOption,
            jsonOption,
        };

        // Правки оператора — подкоманды той же команды: они про ту же карту.
        TopologyEditCommands.AddTo(command, services);

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var topology = services.GetRequiredService<TopologyService>();

            var graph = await topology.BuildAsync(
                new TopologyOptions
                {
                    IncludeExternalPaths = !parseResult.GetValue(noPathsOption),
                    IncludeVirtualAdapters = !parseResult.GetValue(noVirtualOption),
                    CollapseThreshold = parseResult.GetValue(expandOption) ? int.MaxValue : 12,
                    UseSnmp = parseResult.GetValue(snmpOption),
                    SnmpTargets = parseResult.GetValue(deviceOption) ?? [],
                },
                Console.WriteLine,
                cancellationToken).ConfigureAwait(false);

            TopologyRenderer.Write(graph);

            var path = parseResult.GetValue(jsonOption);

            if (!string.IsNullOrWhiteSpace(path))
            {
                await File.WriteAllTextAsync(path, TopologyDocumentJson.Serialize(graph), cancellationToken)
                    .ConfigureAwait(false);

                Console.WriteLine();
                Console.WriteLine($"Карта записана: {Path.GetFullPath(path)}");
            }

            return graph.IsEmpty ? 1 : 0;
        });

        return command;
    }
}
