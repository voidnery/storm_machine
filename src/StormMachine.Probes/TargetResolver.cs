using System.Net;
using System.Net.Sockets;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes;

/// <summary>Разрешение цели в конкретный адрес в момент выполнения.</summary>
/// <remarks>
/// Динамические цели («шлюз по умолчанию») хранят намерение, а не адрес: пресет
/// «пинговать шлюз» должен оставаться осмысленным в любой сети. Адрес подставляется здесь
/// и попадает в результат, чтобы потом было видно, что именно измеряли.
/// </remarks>
public sealed class TargetResolver(INetworkEnvironment environment)
{
    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    public async Task<IPAddress> ResolveAsync(Target target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);

        switch (target.Kind)
        {
            case TargetKind.IpAddress:
                return IPAddress.Parse(target.Value);

            case TargetKind.DefaultGateway:
            {
                var adapter = _environment.GetPrimaryAdapter()
                    ?? throw new InvalidOperationException(
                        "Не найден активный адаптер с маршрутом по умолчанию — шлюз определить не из чего.");

                if (adapter.Gateways.Count == 0)
                {
                    throw new InvalidOperationException($"У адаптера «{adapter.Name}» нет шлюза по умолчанию.");
                }

                return IPAddress.Parse(adapter.Gateways[0]);
            }

            case TargetKind.Hostname:
            case TargetKind.Url:
            {
                var host = target.Kind == TargetKind.Url
                    ? new Uri(target.Value).Host
                    : target.Value;

                var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

                return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Имя «{host}» не разрешается в адрес.");
            }

            case TargetKind.Subnet:
                throw new InvalidOperationException(
                    "Подсеть нельзя использовать как цель одиночной пробы — это цель для сканирования.");

            case TargetKind.ExternalIp:
                throw new NotSupportedException(
                    "Определение внешнего адреса появится в итерации И-11.");

            default:
                throw new NotSupportedException($"Неизвестный вид цели: {target.Kind}");
        }
    }
}
