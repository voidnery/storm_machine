using System.Net.Sockets;
using StormMachine.Application.Abstractions;

namespace StormMachine.Platform;

/// <summary>
/// Что позволяет эта машина.
/// </summary>
/// <remarks>
/// Всё определяется проверкой, а не предположением. Права спрашиваются у системы,
/// драйвер ищется на диске и в службах, сырой сокет проверяется попыткой его открыть.
/// Продукт, который заявляет возможности по одному флагу прав, ошибается в обе стороны:
/// на части систем сырые сокеты доступны и без администратора, на части закрыты
/// политикой даже с ним.
/// </remarks>
public sealed class WindowsSystemCapabilities(INetworkEnvironment environment) : ISystemCapabilities
{
    /// <summary>Где Npcap ставит свою библиотеку.</summary>
    /// <remarks>
    /// Каталог, а не служба: служба может быть остановлена, а библиотека на месте —
    /// и наоборот. Наличие библиотеки — то, что определяет, сможет ли плагин
    /// вообще загрузиться.
    /// </remarks>
    private static readonly string[] DriverPaths =
    [
        Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll"),
        Path.Combine(Environment.SystemDirectory, "wpcap.dll"),
    ];

    private readonly INetworkEnvironment _environment = environment
        ?? throw new ArgumentNullException(nameof(environment));

    private bool? _rawSockets;
    private string? _driver;
    private bool _driverChecked;

    public bool IsElevated => _environment.IsElevated;

    public bool IsCaptureDriverInstalled => CaptureDriverDescription is not null;

    public string? CaptureDriverDescription
    {
        get
        {
            if (_driverChecked)
            {
                return _driver;
            }

            _driverChecked = true;

            foreach (var path in DriverPaths)
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);

                    _driver = string.IsNullOrWhiteSpace(info.FileVersion)
                        ? path
                        : $"{info.FileDescription ?? "wpcap"} {info.FileVersion}";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Файл есть, версию прочитать не дали. Для ответа «установлен ли»
                    // этого достаточно, и врать «не установлен» из-за прав нельзя.
                    _driver = path;
                }

                return _driver;
            }

            return _driver;
        }
    }

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
