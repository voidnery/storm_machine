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
            Description = "Кого опрашивать сверх шлюзов. Запоминается — назвать надо один раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        // Запоминание можно отключить: разовый опрос чужого коммутатора не должен
        // оставаться в настройках навсегда.
        var onceOption = new Option<bool>("--разово", "--once")
        {
            Description = "Не запоминать названные устройства: опросить только сейчас.",
        };

        // Прослушивание — тоже отдельным ключом: оно занимает десятки секунд,
        // и делать его при каждом взгляде на карту незачем.
        var captureOption = new Option<int>("--захват", "--capture")
        {
            Description = "Послушать эфир столько секунд и узнать, в чей порт воткнуты мы сами.",
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
            onceOption,
            captureOption,
            jsonOption,
        };

        // Правки оператора — подкоманды той же команды: они про ту же карту.
        TopologyEditCommands.AddTo(command, services);

        // Названные устройства — своя подкоманда, а не «forget»: то имя уже занято
        // отменой правки карты, и два разных «забыть» рядом читались бы одинаково.
        command.Subcommands.Add(BuildDevices(services));

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var topology = services.GetRequiredService<TopologyService>();

            var named = parseResult.GetValue(deviceOption) ?? [];

            // Названное запоминается сразу, до опроса: если оператор указал устройство,
            // он имел в виду его, а не «попробуй один раз». Отменить — ключ --разово.
            if (named.Length > 0 && !parseResult.GetValue(onceOption))
            {
                foreach (var address in named)
                {
                    await topology.RememberDeviceAsync(address, cancellationToken).ConfigureAwait(false);
                }

                Console.WriteLine($"Запомнил: {string.Join(", ", named)}. "
                                  + "Дальше их не надо называть — они опрашиваются сами.");
                Console.WriteLine();
            }

            var graph = await topology.BuildAsync(
                new TopologyOptions
                {
                    IncludeExternalPaths = !parseResult.GetValue(noPathsOption),
                    IncludeVirtualAdapters = !parseResult.GetValue(noVirtualOption),
                    CollapseThreshold = parseResult.GetValue(expandOption) ? int.MaxValue : 12,
                    UseSnmp = parseResult.GetValue(snmpOption),
                    SnmpTargets = named,
                    UseCapture = parseResult.GetValue(captureOption) > 0,
                    CaptureDuration = TimeSpan.FromSeconds(Math.Max(1, parseResult.GetValue(captureOption))),
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

    /// <summary>
    /// Устройства, которые опрашиваются сверх шлюзов.
    /// </summary>
    /// <remarks>
    /// Долг И-17: коммутатор без адреса управления в маршруте по умолчанию приходилось
    /// называть при каждом вызове. Обходить подсеть с учётными данными продукт
    /// не станет — это уже не диагностика, — но помнить названное однажды обязан.
    /// </remarks>
    private static Command BuildDevices(IServiceProvider services)
    {
        var forget = new Option<string?>("--забыть", "--forget")
        {
            Description = "Убрать устройство из списка.",
        };

        var command = new Command("devices", "Кого опрашивать по SNMP сверх шлюзов.") { forget };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var topology = services.GetRequiredService<TopologyService>();

            if (parseResult.GetValue(forget) is { Length: > 0 } address)
            {
                var removed = await topology.ForgetDeviceAsync(address, cancellationToken).ConfigureAwait(false);

                Console.WriteLine(removed
                    ? $"«{address}» больше не опрашивается."
                    : $"«{address}» и не был в списке.");

                return 0;
            }

            var remembered = await topology.RememberedDevicesAsync(cancellationToken).ConfigureAwait(false);

            if (remembered.Count == 0)
            {
                Console.WriteLine("Список пуст — опрашиваются только шлюзы из маршрута.");
                Console.WriteLine();
                Console.WriteLine("Коммутатор без адреса управления в маршруте туда не попадёт.");
                Console.WriteLine("Назвать его один раз: storm topology --snmp --устройство <адрес>");

                return 0;
            }

            Console.WriteLine("Опрашиваются сверх шлюзов:");
            Console.WriteLine();

            foreach (var device in remembered)
            {
                Console.WriteLine($"  {device}");
            }

            Console.WriteLine();
            Console.WriteLine("Убрать: storm topology devices --забыть <адрес>");

            return 0;
        });

        return command;
    }

}
