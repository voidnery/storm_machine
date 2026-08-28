using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.Application.Abstractions;
using StormMachine.Cli.Rendering;

namespace StormMachine.Cli.Commands;

/// <summary>
/// Взгляд снаружи: <c>storm outside</c>.
/// </summary>
/// <remarks>
/// Единственная команда продукта, которая обязательно обращается к чужим серверам.
/// Изнутри сети на вопрос «каким адресом нас видно и мешает ли NAT» ответить нельзя:
/// ответ есть только у собеседника за пределами трансляции. Поэтому команда отдельная
/// и вызывается вручную — фоном такое обращение не делается.
/// </remarks>
internal static class OutsideCommand
{
    public static Command Create(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var stunOption = new Option<string>("--stun")
        {
            Description = "Серверы STUN через запятую. Пусто — список по умолчанию.",
        };

        var ipv6Option = new Option<string>("--ipv6-цель", "--ipv6-target")
        {
            Description = "Имя, на котором проверяется готовность к IPv6.",
            DefaultValueFactory = _ => "example.com",
        };

        var noIpv6Option = new Option<bool>("--без-ipv6", "--no-ipv6")
        {
            Description = "Не проверять IPv6: это ещё одно обращение наружу.",
        };

        var timeoutOption = new Option<int>("--timeout")
        {
            Description = "Сколько ждать ответа, мс.",
            DefaultValueFactory = _ => 2000,
        };

        var command = new Command(
            "outside",
            "Как сеть видна снаружи: внешний адрес, NAT, принадлежность, IPv6. Обращается к чужим серверам.")
        {
            stunOption,
            ipv6Option,
            noIpv6Option,
            timeoutOption,
        };

        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var outside = services.GetRequiredService<IOutsideView>();

            var servers = (parseResult.GetValue(stunOption) ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            var request = new OutsideRequest
            {
                StunServers = servers,
                Ipv6Target = parseResult.GetValue(ipv6Option) ?? "example.com",
                CheckIpv6 = !parseResult.GetValue(noIpv6Option),
                TimeoutMs = parseResult.GetValue(timeoutOption),
            };

            var view = await outside.LookAsync(request, cancellationToken).ConfigureAwait(false);

            OutsideRenderer.Write(view);

            return view.ExternalAddress is null ? 1 : 0;
        });

        return command;
    }
}
