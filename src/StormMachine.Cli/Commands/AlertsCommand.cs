using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Rendering;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Алерты: <c>storm alerts</c>.
/// </summary>
/// <remarks>
/// Лента показывает и те события, о которых продукт промолчал из-за паузы между
/// оповещениями. Иначе история выглядела бы спокойной ровно в те минуты, когда
/// решено было не шуметь.
/// </remarks>
internal static class AlertsCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("alerts", "Лента событий, каналы оповещения и их настройка.");

        command.Subcommands.Add(BuildChannels(services));
        command.Subcommands.Add(BuildSet(services));
        command.Subcommands.Add(BuildTest(services));

        var monitor = new Option<string?>("--монитор", "--monitor") { Description = "Только по этому монитору." };

        var since = new Option<string?>("--за", "--since")
        {
            Description = "За какой срок: 24ч, 7д. По умолчанию за всё время.",
        };

        var notified = new Option<bool>("--только-оповещения", "--notified-only")
        {
            Description = "Только те события, о которых сообщали в каналы.",
        };

        var limit = new Option<int>("--сколько", "--limit")
        {
            Description = "Сколько последних событий показать.",
            DefaultValueFactory = _ => 50,
        };

        command.Options.Add(monitor);
        command.Options.Add(since);
        command.Options.Add(notified);
        command.Options.Add(limit);

        command.SetAction(async (parse, cancellationToken) =>
        {
            var store = services.GetRequiredService<IMonitorStore>();
            Guid? monitorId = null;

            if (parse.GetValue(monitor) is { } needle)
            {
                var found = await store.FindAsync(needle, cancellationToken).ConfigureAwait(false);

                if (found is null)
                {
                    Console.Error.WriteLine($"Монитор «{needle}» не найден.");

                    return 1;
                }

                monitorId = found.Id;
            }

            var query = new AlertQuery
            {
                MonitorId = monitorId,
                Since = Schedule.TryParseInterval(parse.GetValue(since), out var span)
                    ? DateTimeOffset.UtcNow - span
                    : null,
                NotifiedOnly = parse.GetValue(notified),
                Limit = parse.GetValue(limit),
            };

            MonitorRenderer.WriteAlerts(await store.ListAlertsAsync(query, cancellationToken).ConfigureAwait(false));

            return 0;
        });

        return command;
    }

    private static Command BuildChannels(IServiceProvider services)
    {
        var command = new Command("channels", "Каналы доставки и что им нужно для работы.");

        command.SetAction(async (_, cancellationToken) =>
        {
            var channels = services.GetRequiredService<IEnumerable<IAlertChannel>>().ToList();

            foreach (var channel in channels)
            {
                await channel.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }

            MonitorRenderer.WriteChannels(channels);

            Console.WriteLine("Настройки:");

            foreach (var (key, about, secret) in AlertSettings.All)
            {
                Console.WriteLine($"  {key,-32} {about}{(secret ? "  [хранится зашифрованным]" : string.Empty)}");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildSet(IServiceProvider services)
    {
        var key = new Argument<string>("ключ") { Description = "Ключ настройки, например alerts.webhook.url." };

        var value = new Argument<string?>("значение")
        {
            Description = "Значение. Без него настройка удаляется.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var command = new Command("set", "Задать настройку канала.") { key, value };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var settings = services.GetRequiredService<ISettingsStore>();
            var name = parse.GetValue(key)!;
            var known = AlertSettings.All.FirstOrDefault(k =>
                string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase));

            if (known.Key is null)
            {
                Console.Error.WriteLine($"Ключ «{name}» неизвестен. Список: storm alerts channels.");

                return 1;
            }

            var text = parse.GetValue(value);

            if (text is null)
            {
                await settings.RemoveAsync(known.Key, cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Настройка «{known.Key}» удалена.");

                return 0;
            }

            await settings.SetAsync(known.Key, text, known.IsSecret, cancellationToken).ConfigureAwait(false);

            Console.WriteLine($"Настройка «{known.Key}» задана.");

            if (known.IsSecret)
            {
                // Оговорка обязана прозвучать при вводе, а не выясниться после переезда
                // на другую машину: DPAPI привязывает значение к учётной записи.
                Console.WriteLine("  Значение зашифровано средствами Windows и привязано к этой учётной записи.");
                Console.WriteLine("  На другой машине или под другим пользователем его придётся задать заново.");
            }

            return 0;
        });

        return command;
    }

    private static Command BuildTest(IServiceProvider services)
    {
        var name = new Argument<string>("канал") { Description = "Имя канала: webhook, почта." };
        var command = new Command("test", "Отправить пробное сообщение в канал.") { name };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var needle = parse.GetValue(name)!;
            var channel = services.GetRequiredService<IEnumerable<IAlertChannel>>()
                .FirstOrDefault(c => string.Equals(c.Name, needle, StringComparison.OrdinalIgnoreCase));

            if (channel is null)
            {
                Console.Error.WriteLine($"Канал «{needle}» не зарегистрирован. Список: storm alerts channels.");

                return 1;
            }

            await channel.RefreshAsync(cancellationToken).ConfigureAwait(false);

            if (!channel.IsConfigured)
            {
                Console.Error.WriteLine($"Канал «{channel.Name}» не настроен: {channel.MissingConfiguration}.");

                return 1;
            }

            Console.WriteLine($"Отправляю пробное сообщение в «{channel.Name}»…");

            try
            {
                await channel.SendAsync(Sample(), cancellationToken).ConfigureAwait(false);

                Console.WriteLine("Отправлено. Проверь, дошло ли: продукт знает только то, "
                                  + "что приёмник принял запрос.");

                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Console.Error.WriteLine($"Не отправлено: {ex.Message}");

                return 1;
            }
        });

        return command;
    }

    /// <summary>
    /// Пробное оповещение.
    /// </summary>
    /// <remarks>
    /// Собрано из настоящих типов, а не из заглушки со строкой «test»: канал должен
    /// проверяться тем же путём, которым пойдёт настоящее сообщение, иначе проверка
    /// подтверждает работу проверки, а не канала.
    /// </remarks>
    private static AlertNotification Sample()
    {
        var monitor = new Monitor
        {
            Id = Guid.Empty,
            Name = "пробное сообщение",
            Subject = "ping",
            Target = Domain.Targets.Target.Host("example.test"),
            Schedule = Schedule.Every(TimeSpan.FromMinutes(5)),
            Thresholds = [Threshold.Parse("p95 < 100")],
        };

        var check = new MonitorCheck
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            StartedUtc = DateTimeOffset.UtcNow,
            Level = VerdictLevel.Fail,
            Summary = "Проверка канала оповещения. Настоящего измерения за этим сообщением нет.",
            Metric = "p95",
            Value = 142,
            Threshold = 100,
        };

        var alert = new AlertEvent
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            MonitorName = monitor.Name,
            AtUtc = DateTimeOffset.UtcNow,
            Action = AlertAction.Raised,
            Level = VerdictLevel.Fail,
            Reason = "Отправлено вручную командой «storm alerts test».",
            Summary = check.Summary,
            CheckId = check.Id,
            Notified = true,
        };

        return new AlertNotification(monitor, alert, check);
    }
}
