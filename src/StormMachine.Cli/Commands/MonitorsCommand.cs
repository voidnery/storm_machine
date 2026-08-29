using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Application.Probes;
using StormMachine.Application.Scenarios;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Мониторы: <c>storm monitors</c>.
/// </summary>
/// <remarks>
/// Монитор — это проба или сценарий, повторяющиеся по расписанию, с порогами и оценкой
/// доступности. Своих измерений он не делает: запускает те же пробы через тот же
/// оркестратор, поэтому каждая проверка ложится в журнал обычным прогоном.
/// </remarks>
internal static class MonitorsCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("monitors", "Постоянные проверки: расписание, доступность, SLA.");

        command.Subcommands.Add(BuildAdd(services));
        command.Subcommands.Add(BuildShow(services));
        command.Subcommands.Add(BuildRun(services));
        command.Subcommands.Add(BuildEnable(services, on: true));
        command.Subcommands.Add(BuildEnable(services, on: false));
        command.Subcommands.Add(BuildRemove(services));
        command.Subcommands.Add(BuildChecks(services));
        command.Subcommands.Add(BuildSla(services));
        command.Subcommands.Add(BuildWatch(services));
        command.Subcommands.Add(MonitorServiceCommands.Create(services));

        command.SetAction(async (_, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var monitors = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            var rows = new List<(Monitor Monitor, MonitorStatus Status)>();

            foreach (var monitor in monitors)
            {
                rows.Add((monitor, await store.GetStatusAsync(monitor.Id, cancellationToken).ConfigureAwait(false)));
            }

            MonitorRenderer.WriteList(rows);

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ завести

    private static Command BuildAdd(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Как монитор будет называться." };

        var probe = new Option<string?>("--проба", "--probe") { Description = "Имя пробы: ping, dns, http…" };
        var scenario = new Option<string?>("--сценарий", "--scenario") { Description = "Ключ шаблона сценария: web, dns, voice." };

        // Два вида наблюдения за самим оборудованием (И-21). Они смотрят не пакетами
        // со своей машины, а по накопленной истории опроса и прослушивания.
        var portOf = new Option<string?>("--порт-устройства", "--port-of")
        {
            Description = "Следить за портом оборудования: адрес устройства. Нужен --номер-порта.",
        };

        var portIndex = new Option<int?>("--номер-порта", "--port-index")
        {
            Description = "Номер порта (ifIndex) на устройстве.",
        };

        var dhcp = new Option<bool>("--dhcp")
        {
            Description = "Следить за появлением серверов DHCP в сегменте.",
        };
        var target = new Option<string?>("--цель", "--target") { Description = "Адрес, имя узла или URL." };

        var every = new Option<string?>("--каждые", "--every")
        {
            Description = "Интервал: 30с, 5м, 2ч, 1д. Не чаще 30 с.",
        };

        var cron = new Option<string?>("--расписание", "--cron")
        {
            Description = "Выражение cron из пяти полей: «0 3 * * *» — каждый день в три ночи.",
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

        var warnings = new Option<string[]>("--предупреждение", "--warn")
        {
            Description = "То же, но нарушение считается предупреждением, а не отказом.",
            AllowMultipleArgumentsPerToken = true,
        };

        var maintenance = new Option<string[]>("--обслуживание", "--maintenance")
        {
            Description = "Окно обслуживания: «пн-пт 02:00-04:00 обновления». Можно несколько раз.",
            AllowMultipleArgumentsPerToken = true,
        };

        var catchUp = new Option<bool>("--догонять", "--catch-up")
        {
            Description = "После простоя выполнить один раз. По умолчанию пропущенное не наверстывается.",
        };

        var alert = new Option<bool>("--алерт", "--alert") { Description = "Включить оповещение." };

        var channels = new Option<string[]>("--канал", "--channel")
        {
            Description = "Канал оповещения: webhook, почта, консоль, звук, уведомление.",
            AllowMultipleArgumentsPerToken = true,
        };

        var raiseAfter = new Option<int>("--поднять-после", "--raise-after")
        {
            Description = "Сколько нарушений подряд поднимают алерт.",
            DefaultValueFactory = _ => 2,
        };

        var clearAfter = new Option<int>("--снять-после", "--clear-after")
        {
            Description = "Сколько нормальных проверок подряд его снимают.",
            DefaultValueFactory = _ => 2,
        };

        var margin = new Option<double?>("--запас", "--margin")
        {
            Description = "Запас по метрике для снятия: подняли на 100, снимем на 100 минус запас.",
        };

        var cooldown = new Option<string?>("--пауза", "--cooldown")
        {
            Description = "Не оповещать чаще, чем раз в этот срок. По умолчанию 15м.",
        };

        var repeat = new Option<string?>("--повтор", "--repeat")
        {
            Description = "Напоминать, пока алерт держится. По умолчанию не напоминать.",
        };

        var sla = new Option<double?>("--цель-sla", "--slo")
        {
            Description = "Требуемая доступность в процентах: 99.5.",
        };

        var slaWindow = new Option<string?>("--окно-sla", "--slo-window")
        {
            Description = "За какой срок считать цель. По умолчанию 30д.",
        };

        var description = new Option<string?>("--описание", "--description") { Description = "Пояснение к монитору." };

        var command = new Command("add", "Завести монитор.")
        {
            name, probe, scenario, portOf, portIndex, dhcp, target, every, cron, parameters,
            thresholds, warnings, maintenance, catchUp, alert, channels, raiseAfter, clearAfter,
            margin, cooldown, repeat, sla, slaWindow, description,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var registry = services.GetRequiredService<IProbeRegistry>();

            var probeName = parse.GetValue(probe);
            var scenarioKey = parse.GetValue(scenario);
            var device = parse.GetValue(portOf);
            var watchDhcp = parse.GetValue(dhcp);

            var chosen = new[] { probeName is not null, scenarioKey is not null, device is not null, watchDhcp }
                .Count(x => x);

            if (chosen != 1)
            {
                Console.Error.WriteLine(
                    "Нужно указать ровно одно: --проба, --сценарий, --порт-устройства или --dhcp.");

                return 1;
            }

            if (device is not null && parse.GetValue(portIndex) is null)
            {
                Console.Error.WriteLine(
                    "Для наблюдения за портом нужен его номер: «--номер-порта <ifIndex>». "
                    + "Узнать номера: storm snmp interfaces <устройство>.");

                return 1;
            }

            if (probeName is not null && !registry.TryGet(probeName, out _))
            {
                Console.Error.WriteLine($"Проба «{probeName}» не зарегистрирована. Список: storm probes.");

                return 1;
            }

            if (scenarioKey is not null
                && !ScenarioTemplates.All.Any(t => string.Equals(t.Key, scenarioKey, StringComparison.OrdinalIgnoreCase)))
            {
                Console.Error.WriteLine(
                    $"Шаблон «{scenarioKey}» неизвестен. Есть: "
                    + string.Join(", ", ScenarioTemplates.All.Select(t => t.Key)) + ".");

                return 1;
            }

            // У наблюдения за оборудованием цель — само устройство, а у наблюдения
            // за DHCP её нет вовсе: слушают сегмент, а не узел. Требовать её здесь
            // значило бы заставить оператора выдумать ответ.
            var targetText = parse.GetValue(target)
                             ?? device
                             ?? (watchDhcp ? "<сегмент>" : null);

            if (string.IsNullOrWhiteSpace(targetText))
            {
                Console.Error.WriteLine("Не задана цель — «--цель <адрес>».");

                return 1;
            }

            var schedule = BuildSchedule(parse.GetValue(every), parse.GetValue(cron), parse.GetValue(catchUp));

            if (schedule is null)
            {
                Console.Error.WriteLine("Нужно указать ровно одно: --каждые или --расписание.");

                return 1;
            }

            foreach (var text in parse.GetValue(maintenance) ?? [])
            {
                if (!MaintenanceWindow.TryParse(text, out var window) || window is null)
                {
                    Console.Error.WriteLine(
                        $"Окно обслуживания «{text}» не разобрано. Ожидается «пн-пт 02:00-04:00 причина».");

                    return 1;
                }

                schedule = schedule with { Maintenance = [.. schedule.Maintenance, window] };
            }

            List<Threshold> limits;

            try
            {
                limits =
                [
                    .. (parse.GetValue(thresholds) ?? []).Select(t => Threshold.Parse(t)),
                    .. (parse.GetValue(warnings) ?? []).Select(t => Threshold.Parse(t, VerdictLevel.Warn)),
                ];
            }
            catch (FormatException ex)
            {
                Console.Error.WriteLine(ex.Message);

                return 1;
            }

            var monitor = new Monitor
            {
                Id = Guid.NewGuid(),
                Name = parse.GetValue(name)!,
                Description = parse.GetValue(description),
                Kind = (probeName, scenarioKey, device, watchDhcp) switch
                {
                    (not null, _, _, _) => MonitorKind.Probe,
                    (_, not null, _, _) => MonitorKind.Scenario,
                    (_, _, not null, _) => MonitorKind.PortLoad,
                    _ => MonitorKind.Dhcp,
                },
                Subject = probeName ?? scenarioKey ?? device ?? "dhcp",
                Target = watchDhcp
                    ? Domain.Targets.Target.Parse("0.0.0.0")
                    : Domain.Targets.Target.Parse(targetText),
                Parameters = WithPort(ParseParameters(parse.GetValue(parameters) ?? []), parse.GetValue(portIndex)),
                Thresholds = limits,
                Schedule = schedule,
                Alert = BuildAlert(parse, alert, channels, raiseAfter, clearAfter, margin, cooldown, repeat),
                Objective = BuildObjective(parse.GetValue(sla), parse.GetValue(slaWindow)),
            };

            var errors = monitor.Validate();

            if (errors.Count > 0)
            {
                foreach (var error in errors)
                {
                    Console.Error.WriteLine(error);
                }

                return 1;
            }

            // Первый срок назначается сразу: монитор, заведённый и не запланированный,
            // выглядел бы работающим и молчал бы до перезапуска продукта.
            monitor = monitor with { NextDueUtc = monitor.Schedule.NextAfter(DateTimeOffset.UtcNow) };

            await store.SaveAsync(monitor, cancellationToken).ConfigureAwait(false);

            WarnAboutChannels(services, monitor);

            Console.WriteLine($"Монитор «{monitor.Name}» заведён.");
            Console.WriteLine($"  {monitor.Schedule.Describe()}, следующая проверка "
                              + $"{monitor.NextDueUtc?.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)}");
            Console.WriteLine();
            // Служба называется первой не для рекламы: монитор — обещание непрерывности,
            // и способ, при котором наблюдение прекращается с закрытием окна, стоит
            // назвать вторым, а не единственным.
            Console.WriteLine("Чтобы проверки шли постоянно — «storm monitors service install»:");
            Console.WriteLine("служба наблюдает, пока включена машина, и переживает закрытие клиента.");
            Console.WriteLine();
            Console.WriteLine("Без неё проверки идут только при работающем планировщике:");
            Console.WriteLine("«storm monitors watch» или запущенный графический клиент.");

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ показать

    /// <summary>Добавляет номер порта к параметрам монитора, если он задан.</summary>
    private static IReadOnlyDictionary<string, string?> WithPort(
        IReadOnlyDictionary<string, string?> parameters,
        int? port)
    {
        if (port is not { } index)
        {
            return parameters;
        }

        var copy = new Dictionary<string, string?>(parameters, StringComparer.OrdinalIgnoreCase)
        {
            [Application.Monitors.EquipmentWatch.PortParameter] =
                index.ToString(CultureInfo.InvariantCulture),
        };

        return copy;
    }

    private static Command BuildShow(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя монитора или его начало." };
        var command = new Command("show", "Показать монитор целиком.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var monitor = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (monitor is null)
            {
                return 1;
            }

            MonitorRenderer.WriteDetails(
                monitor,
                await store.GetStatusAsync(monitor.Id, cancellationToken).ConfigureAwait(false));

            return 0;
        });

        return command;
    }

    private static Command BuildRun(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя монитора или его начало." };

        var command = new Command("run", "Проверить сейчас, не дожидаясь срока. Срок не сдвигается.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var scheduler = services.GetRequiredService<MonitorScheduler>();
            var monitor = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (monitor is null)
            {
                return 1;
            }

            Console.WriteLine($"Проверяю «{monitor.Name}»…");
            Console.WriteLine();

            // Итог печатается по событию, а не после ожидания: тогда он выходит
            // раньше алерта, который им вызван, — как оно и происходит на самом деле.
            scheduler.Checked += OnChecked;

            try
            {
                var check = await scheduler.RunNowAsync(monitor, cancellationToken).ConfigureAwait(false);

                return check.Level == VerdictLevel.Fail ? 2 : 0;
            }
            finally
            {
                scheduler.Checked -= OnChecked;
            }

            static void OnChecked(object? sender, Domain.Monitors.MonitorCheck check) =>
                MonitorRenderer.WriteCheck(check);
        });

        return command;
    }

    private static Command BuildEnable(IServiceProvider services, bool on)
    {
        var name = new Argument<string>("имя") { Description = "Имя монитора или его начало." };

        var command = new Command(
            on ? "on" : "off",
            on ? "Включить монитор." : "Выключить монитор, не удаляя историю.")
        {
            name,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var monitor = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (monitor is null)
            {
                return 1;
            }

            // При включении срок назначается заново от текущего момента: старый,
            // оставшийся с выключения, означал бы залп пропущенных проверок.
            await store.SaveAsync(
                monitor with
                {
                    IsEnabled = on,
                    UpdatedUtc = DateTimeOffset.UtcNow,
                    NextDueUtc = on ? monitor.Schedule.NextAfter(DateTimeOffset.UtcNow) : null,
                },
                cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Монитор «{monitor.Name}» {(on ? "включён" : "выключен")}.");

            return 0;
        });

        return command;
    }

    private static Command BuildRemove(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя монитора или его начало." };
        var command = new Command("rm", "Удалить монитор вместе с историей его проверок.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var monitor = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (monitor is null)
            {
                return 1;
            }

            await store.DeleteAsync(monitor.Id, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Монитор «{monitor.Name}» удалён. Проверки удалены вместе с ним.");
            Console.WriteLine("События в ленте алертов остались: они остаются фактом и без монитора.");

            return 0;
        });

        return command;
    }

    private static Command BuildChecks(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя монитора или его начало." };

        var limit = new Option<int>("--сколько", "--limit")
        {
            Description = "Сколько последних проверок показать.",
            DefaultValueFactory = _ => 30,
        };

        var command = new Command("checks", "История проверок монитора.") { name, limit };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var monitor = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (monitor is null)
            {
                return 1;
            }

            var checks = await store
                .ListChecksAsync(
                    new CheckQuery { MonitorId = monitor.Id, Limit = parse.GetValue(limit) },
                    cancellationToken)
                .ConfigureAwait(false);

            if (checks.Count == 0)
            {
                Console.WriteLine("Проверок ещё не было.");

                return 0;
            }

            foreach (var check in checks)
            {
                MonitorRenderer.WriteCheck(check);
            }

            return 0;
        });

        return command;
    }

    private static Command BuildSla(IServiceProvider services)
    {
        var name = new Argument<string>("имя") { Description = "Имя монитора или его начало." };

        var window = new Option<string?>("--за", "--window")
        {
            Description = "За какой срок считать: 24ч, 7д, 30д. По умолчанию — окно цели или 7д.",
        };

        var compareOption = new Option<bool>("--сравнить", "--compare")
        {
            Description = "Сравнить с предыдущим таким же периодом: этот месяц против прошлого.",
        };

        var command = new Command("sla", "Доступность, инциденты и бюджет ошибок.")
        {
            name,
            window,
            compareOption,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            var monitor = await Find(store, parse.GetValue(name)!, cancellationToken).ConfigureAwait(false);

            if (monitor is null)
            {
                return 1;
            }

            var span = Schedule.TryParseInterval(parse.GetValue(window), out var parsed)
                ? parsed
                : monitor.Objective?.Window ?? TimeSpan.FromDays(7);

            var now = DateTimeOffset.UtcNow;
            var from = now - span;

            var checks = await store
                .ListChecksAsync(
                    new CheckQuery { MonitorId = monitor.Id, Since = from, Limit = 100_000 },
                    cancellationToken)
                .ConfigureAwait(false);

            var current = AvailabilityCalculator.Compute(checks, from, now, monitor.Objective);

            MonitorRenderer.WriteAvailability(monitor, current);

            if (!parse.GetValue(compareOption))
            {
                return 0;
            }

            // Предыдущий период берётся ровно такой же длины и вплотную к текущему:
            // сравнивать месяц с неделей бессмысленно, а разрыв между периодами
            // спрятал бы то, что в нём случилось.
            var previousFrom = from - span;

            var before = await store
                .ListChecksAsync(
                    new CheckQuery { MonitorId = monitor.Id, Since = previousFrom, Limit = 100_000 },
                    cancellationToken)
                .ConfigureAwait(false);

            MonitorRenderer.WriteComparison(new AvailabilityComparison
            {
                Before = AvailabilityCalculator.Compute(
                    [.. before.Where(c => c.StartedUtc < from)],
                    previousFrom,
                    from,
                    monitor.Objective),
                After = current,
            });

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ работать

    private static Command BuildWatch(IServiceProvider services)
    {
        var command = new Command(
            "watch",
            "Запустить планировщик и выполнять проверки, пока команда не остановлена.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var scheduler = services.GetRequiredService<MonitorScheduler>();
            var store = services.GetRequiredService<IMonitorStore>();
            var monitors = await store.ListAsync(cancellationToken).ConfigureAwait(false);
            var enabled = monitors.Count(m => m.IsEnabled);

            if (enabled == 0)
            {
                Console.WriteLine("Включённых мониторов нет — сторожить нечего.");

                return 0;
            }

            MonitorRenderer.WriteMisfires(await scheduler.PlanAsync(cancellationToken).ConfigureAwait(false));

            scheduler.Checked += (_, check) => MonitorRenderer.WriteCheck(check);

            Console.WriteLine($"Планировщик запущен, мониторов: {enabled}. Остановить — Ctrl+C.");
            Console.WriteLine();

            await scheduler.StartAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Ctrl+C — обычный способ остановить сторожа, а не ошибка.
            }
            finally
            {
                await scheduler.StopAsync().ConfigureAwait(false);
                Console.WriteLine();
                Console.WriteLine("Планировщик остановлен. Назначенные сроки сохранены в базе "
                                  + "и переживут перезапуск.");
            }

            return 0;
        });

        return command;
    }

    // ------------------------------------------------------------------ помощники

    /// <summary>
    /// Предупреждает про каналы, которых нет в этом клиенте.
    /// </summary>
    /// <remarks>
    /// Это не ошибка: «звук» и «уведомление» живут в графическом клиенте, и монитор
    /// с ними осмыслен. Но проверки, идущие из консоли, ими оповещать не будут,
    /// и узнать об этом лучше сейчас, чем в ленте после аварии.
    /// </remarks>
    private static void WarnAboutChannels(IServiceProvider services, Monitor monitor)
    {
        if (monitor.Alert is not { Channels.Count: > 0 } rule)
        {
            return;
        }

        var known = services.GetRequiredService<IEnumerable<IAlertChannel>>()
            .Select(c => c.Name)
            .ToList();

        var missing = rule.Channels
            .Where(c => !known.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (missing.Count == 0)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"Внимание: {string.Join(", ", missing)} — в консоли таких каналов нет.");
        Console.WriteLine($"  Здесь доступны: {string.Join(", ", known)}.");
        Console.WriteLine("  «звук» и «уведомление» работают только в графическом клиенте.");
    }

    private static async Task<Monitor?> Find(
        IMonitorStore store,
        string needle,
        CancellationToken cancellationToken)
    {
        try
        {
            var monitor = await store.FindAsync(needle, cancellationToken).ConfigureAwait(false);

            if (monitor is null)
            {
                Console.Error.WriteLine($"Монитор «{needle}» не найден. Список: storm monitors.");
            }

            return monitor;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);

            return null;
        }
    }

    private static Schedule? BuildSchedule(string? every, string? cron, bool catchUp)
    {
        var misfire = catchUp ? MisfirePolicy.RunOnce : MisfirePolicy.Skip;

        if (every is not null && cron is not null)
        {
            return null;
        }

        if (every is not null)
        {
            return Schedule.TryParseInterval(every, out var interval)
                ? Schedule.Every(interval, misfire)
                : null;
        }

        return cron is not null ? Schedule.ByCron(cron, misfire) : null;
    }

    private static AlertRule? BuildAlert(
        ParseResult parse,
        Option<bool> alert,
        Option<string[]> channels,
        Option<int> raiseAfter,
        Option<int> clearAfter,
        Option<double?> margin,
        Option<string?> cooldown,
        Option<string?> repeat)
    {
        var wanted = parse.GetValue(alert);
        var named = parse.GetValue(channels) ?? [];

        // Указанный канал сам по себе означает намерение оповещать: требовать
        // ещё и флаг значило бы молча проглотить «--канал webhook».
        if (!wanted && named.Length == 0)
        {
            return null;
        }

        return new AlertRule
        {
            RaiseAfter = parse.GetValue(raiseAfter),
            ClearAfter = parse.GetValue(clearAfter),
            ClearMargin = parse.GetValue(margin),
            Cooldown = Schedule.TryParseInterval(parse.GetValue(cooldown), out var pause)
                ? pause
                : TimeSpan.FromMinutes(15),
            RepeatEvery = Schedule.TryParseInterval(parse.GetValue(repeat), out var again) ? again : null,
            Channels = named,
        };
    }

    private static ServiceLevelObjective? BuildObjective(double? target, string? window) =>
        target is not { } percent
            ? null
            : new ServiceLevelObjective
            {
                TargetPercent = percent,
                Window = Schedule.TryParseInterval(window, out var span) ? span : TimeSpan.FromDays(30),
            };

    private static Dictionary<string, string?> ParseParameters(string[] pairs)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);

            if (at > 0)
            {
                parameters[pair[..at].Trim()] = pair[(at + 1)..].Trim();
            }
        }

        return parameters;
    }
}
