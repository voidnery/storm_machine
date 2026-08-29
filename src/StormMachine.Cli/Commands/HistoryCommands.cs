using System.CommandLine;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Rendering;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Показ истории наблюдений за оборудованием.
/// </summary>
/// <remarks>
/// Появилось в И-21 вместе с самой историей. До неё продукт отвечал только на «что
/// сейчас», а спрашивают у него другое: «что было с портом ночью» и «когда появился
/// этот сервер DHCP». Второй вопрос особенно — посторонний сервер сам по себе
/// не доказательство, две законные пары в одном домене встречаются не реже подставного;
/// а вот сервер, появившийся вчера, это уже событие.
/// </remarks>
internal static class HistoryCommands
{
    /// <summary>Создаёт «snmp history» — ряд загрузки и ошибок по портам.</summary>
    public static Command CreatePortHistory(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var host = new Argument<string?>("устройство")
        {
            Description = "Адрес опрошенного устройства. Без него — все.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var portOption = new Option<int?>("--порт", "--port")
        {
            Description = "Номер порта (ifIndex). Без него — все порты.",
        };

        var hoursOption = new Option<int>("--часов", "--hours")
        {
            Description = "За сколько часов назад показывать.",
            DefaultValueFactory = _ => 24,
        };

        var command = new Command(
            "history",
            "Что было с портами раньше: загрузка и ошибки за прошедшее время.")
        {
            host,
            portOption,
            hoursOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IObservationStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var hours = Math.Clamp(parseResult.GetValue(hoursOption), 1, 24 * 400);
            var since = DateTimeOffset.UtcNow.AddHours(-hours);

            var points = await store
                .ListPortLoadAsync(parseResult.GetValue(host), parseResult.GetValue(portOption), since, cancellationToken)
                .ConfigureAwait(false);

            HistoryRenderer.WritePortLoad(points, hours);

            return 0;
        });

        return command;
    }

    /// <summary>Создаёт «capture history» — кого и когда слышали в эфире.</summary>
    public static Command CreateHeardHistory(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var daysOption = new Option<int>("--дней", "--days")
        {
            Description = "За сколько дней назад показывать.",
            DefaultValueFactory = _ => 30,
        };

        var command = new Command(
            "history",
            "Кого слышали раньше: соседи и серверы DHCP с датой первого появления.")
        {
            daysOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var store = services.GetRequiredService<IObservationStore>();
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

            var days = Math.Clamp(parseResult.GetValue(daysOption), 1, 400);
            var since = DateTimeOffset.UtcNow.AddDays(-days);

            var neighbors = await store.ListNeighborsAsync(since, cancellationToken).ConfigureAwait(false);
            var servers = await store.ListDhcpAsync(since, cancellationToken).ConfigureAwait(false);

            var gateways = services.GetRequiredService<Application.Capture.CaptureService>().KnownGateways();

            HistoryRenderer.WriteHeard(neighbors, servers, gateways, days);

            return 0;
        });

        return command;
    }

    /// <summary>Склонение часов и дней — для заголовков.</summary>
    public static string Hours(int count) =>
        Domain.Text.Plural.With(count, "час", "часа", "часов");

    public static string Days(int count) =>
        Domain.Text.Plural.With(count, "день", "дня", "дней");

    /// <summary>Момент в местном времени: историю читает человек, а не программа.</summary>
    public static string When(DateTimeOffset moment) =>
        moment.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
}
