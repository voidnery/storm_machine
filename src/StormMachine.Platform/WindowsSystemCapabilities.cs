using System.Net.Sockets;
using StormMachine.Application.Abstractions;

namespace StormMachine.Platform;

/// <summary>
/// Что позволяет эта машина.
/// </summary>
/// <remarks>
/// Всё определяется проверкой, а не предположением. Права спрашиваются у системы,
/// сырой сокет проверяется попыткой его открыть. Продукт, который заявляет
/// возможности по одному флагу прав, ошибается в обе стороны: на части систем
/// сырые сокеты доступны и без администратора, на части закрыты политикой даже с ним.
/// <para>
/// Поиска драйвера захвата здесь больше нет — он уехал туда, где на этот вопрос
/// умеют отвечать полно: сам источник захвата различает «драйвера нет», «есть,
/// но не пускает» и «встроенный на месте, нужны права», а поиск файла на диске
/// отвечал только «да» и «нет», и то про один конкретный драйвер.
/// </para>
/// </remarks>
public sealed class WindowsSystemCapabilities(INetworkEnvironment environment) : ISystemCapabilities
{
    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    private bool? _rawSockets;

    public bool IsElevated => _environment.IsElevated;

    public bool CanOpenRawSockets
    {
        get
        {
            if (_rawSockets is { } known)
            {
                return known;
            }

            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);

                _rawSockets = true;
            }
            catch (SocketException)
            {
                _rawSockets = false;
            }
            catch (UnauthorizedAccessException)
            {
                _rawSockets = false;
            }

            return _rawSockets.Value;
        }
    }
}
