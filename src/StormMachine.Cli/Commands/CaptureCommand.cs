using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Capture;
using StormMachine.Cli.Rendering;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Пассивное прослушивание: <c>storm capture</c>.
/// </summary>
/// <remarks>
/// Уровень 2. Продукт <b>только слушает</b>: ни одного кадра в сеть не отправляется,
/// неразборчивый режим не включается. Соседство по LLDP и CDP идёт на групповые адреса,
/// которые карта принимает и так, а ответы DHCP широковещательны — этого достаточно,
/// и чужая переписка по сегменту продукту не нужна.
/// <para>
/// Драйвер захвата в поставку не входит ни при каких условиях: лицензия NPSL это
/// запрещает. Без него команда честно говорит, чего не хватает и откуда это взять.
/// </para>
/// </remarks>
internal static class CaptureCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var command = new Command("capture", "Слушать эфир: соседи по LLDP и CDP, посторонний DHCP.");

        command.Subcommands.Add(BuildDevices(services));
        command.Subcommands.Add(BuildListen(services));

        command.SetAction((_, _) =>
        {
            var capture = services.GetRequiredService<CaptureService>();

            CaptureRenderer.WriteAvailability(capture);

            return Task.FromResult(0);
        });

        return command;
    }

    private static Command BuildDevices(IServiceProvider services)
    {
        var command = new Command("devices", "На каких адаптерах можно слушать.");

        command.SetAction((_, _) =>
        {
            var capture = services.GetRequiredService<CaptureService>();

            if (!capture.IsAvailable)
            {
                CaptureRenderer.WriteAvailability(capture);

                return Task.FromResult(1);
            }

            CaptureRenderer.WriteAdapters(capture.Adapters(), capture.Primary());

            return Task.FromResult(0);
        });

        return command;
    }

    private static Command BuildListen(IServiceProvider services)
    {
        var seconds = new Option<int>("--секунд", "--seconds")
        {
            Description = "Сколько слушать. Меньше минуты — сосед может не успеть объявиться.",
            DefaultValueFactory = _ => 60,
        };

        var adapterOption = new Option<string?>("--адаптер", "--adapter")
        {
            Description = "На каком адаптере слушать. Без ключа — тот, через который идёт маршрут по умолчанию.",
        };

        var neighborsOnly = new Option<bool>("--только-соседи", "--neighbors-only")
        {
            Description = "Не слушать DHCP.",
        };

        var dhcpOnly = new Option<bool>("--только-dhcp", "--dhcp-only")
        {
            Description = "Не слушать соседей.",
        };

        var command = new Command("listen", "Послушать эфир и показать, что услышано.")
        {
            seconds, adapterOption, neighborsOnly, dhcpOnly,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var capture = services.GetRequiredService<CaptureService>();

            if (!capture.IsAvailable)
            {
                CaptureRenderer.WriteAvailability(capture);

                return 1;
            }

            var adapters = capture.Adapters();
            var named = parse.GetValue(adapterOption);

            var adapter = named is null
                ? capture.Primary()
                : adapters.FirstOrDefault(a =>
                    a.DisplayName.Contains(named, StringComparison.OrdinalIgnoreCase)
                    || a.Id.Contains(named, StringComparison.OrdinalIgnoreCase));

            if (adapter is null)
            {
                Console.Error.WriteLine(named is null
                    ? "Подходящего адаптера не нашлось. Список: storm capture devices."
                    : $"Адаптер «{named}» не найден. Список: storm capture devices.");

                return 1;
            }

            var duration = TimeSpan.FromSeconds(Math.Max(1, parse.GetValue(seconds)));

            var options = new CaptureOptions
            {
                Duration = duration,
                Neighbors = !parse.GetValue(dhcpOnly),
                Dhcp = !parse.GetValue(neighborsOnly),
            };

            Console.WriteLine($"Слушаю {adapter.DisplayName} — "
                              + $"{duration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)} с. "
                              + "Ничего в сеть не отправляется.");
            Console.WriteLine();

            var result = await capture.ListenAsync(adapter, options, cancellationToken).ConfigureAwait(false);

            CaptureRenderer.WriteResult(result, capture.KnownGateways());

            return 0;
        });

        return command;
    }
}
