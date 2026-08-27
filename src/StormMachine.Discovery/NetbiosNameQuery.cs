using System.Net;
using System.Net.Sockets;
using System.Text;

namespace StormMachine.Discovery;

/// <summary>
/// Запрос имени узла по NetBIOS (NBSTAT, RFC 1002).
/// </summary>
/// <remarks>
/// Нужен там, где обратной зоны DNS нет вовсе, — то есть в большинстве офисных сетей.
/// Рабочая станция Windows не имеет записи PTR, но охотно называет себя по NetBIOS,
/// и без этого запроса инвентарь показывал бы столбец имён пустым.
/// <para>
/// Пакет собирается вручную — как и разбор DNS в <c>DnsWire</c>, и по той же причине:
/// формат маленький, полностью описан, а библиотека тянула бы чужую лицензию
/// и чужие обновления ради полусотни строк.
/// </para>
/// </remarks>
internal static class NetbiosNameQuery
{
    private const int Port = 137;

    /// <summary>Ответ приходит за миллисекунды или не приходит вовсе.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(300);

    /// <summary>Заголовок запроса NBSTAT: он одинаков для всех узлов, кроме идентификатора.</summary>
    private static ReadOnlySpan<byte> Request =>
    [
        0x00, 0x00,             // идентификатор — заполняется перед отправкой
        0x00, 0x00,             // флаги: обычный запрос
        0x00, 0x01,             // один вопрос
        0x00, 0x00,             // ответов нет
        0x00, 0x00,             // записей полномочий нет
        0x00, 0x00,             // дополнительных записей нет

        // Имя «*», закодированное первым уровнем кодирования NetBIOS: каждый байт
        // разбивается на две половины, к каждой прибавляется 'A'.
        0x20,
        0x43, 0x4B, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
        0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
        0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
        0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41, 0x41,
        0x00,

        0x00, 0x21,             // тип NBSTAT
        0x00, 0x01,             // класс IN
    ];

    /// <summary>Длина имени в записи ответа: 15 знаков плюс байт типа.</summary>
    private const int NameLength = 16;

    /// <summary>Смещение до счётчика имён в ответе.</summary>
    private const int NamesCountOffset = 56;

    /// <summary>Флаг «имя принадлежит группе», а не самому узлу.</summary>
    private const byte GroupFlag = 0x80;

    /// <summary>Спрашивает имя узла. <c>null</c> — узел не ответил или NetBIOS выключен.</summary>
    public static async Task<string?> AskAsync(IPAddress address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var request = Request.ToArray();

        // Идентификатор нужен, чтобы не принять чужой ответ за свой. Берётся из хеша
        // адреса, а не из случайного числа: одинаковый запрос к одному узлу должен
        // выглядеть одинаково — так его проще узнать в захвате трафика при разборе.
        request[0] = (byte)(address.GetHashCode() >> 8);
        request[1] = (byte)address.GetHashCode();

        var buffer = new byte[512];

        try
        {
            await socket.SendToAsync(request, new IPEndPoint(address, Port), cancellationToken).ConfigureAwait(false);

            var result = await socket
                .ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                .AsTask()
                .WaitAsync(Timeout, cancellationToken)
                .ConfigureAwait(false);

            return Parse(buffer.AsSpan(0, result.ReceivedBytes), request[0], request[1]);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or ObjectDisposedException
                                   || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// Достаёт из ответа имя самого узла.
    /// </summary>
    /// <remarks>
    /// В ответе перечислены все имена, зарегистрированные узлом: собственное,
    /// имя рабочей группы, служебные. Нужно первое уникальное — групповые описывают
    /// не узел, а домен, и подставлять их в инвентарь значило бы назвать все машины
    /// офиса одинаково.
    /// </remarks>
    internal static string? Parse(ReadOnlySpan<byte> response, byte idHigh, byte idLow)
    {
        if (response.Length <= NamesCountOffset || response[0] != idHigh || response[1] != idLow)
        {
            return null;
        }

        var count = response[NamesCountOffset];
        var cursor = NamesCountOffset + 1;

        for (var i = 0; i < count; i++)
        {
            // Запись: 15 знаков имени, байт типа, два байта флагов.
            if (cursor + NameLength + 2 > response.Length)
            {
                return null;
            }

            var flags = response[cursor + NameLength];
            var name = Encoding.ASCII.GetString(response.Slice(cursor, NameLength - 1)).TrimEnd();

            cursor += NameLength + 2;

            if ((flags & GroupFlag) == 0 && name.Length > 0 && !name.StartsWith("__", StringComparison.Ordinal))
            {
                return name;
            }
        }

        return null;
    }
}
