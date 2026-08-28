using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StormMachine.Protocol;

/// <summary>
/// Кадрирование сообщений в потоке.
/// </summary>
/// <remarks>
/// TCP — поток байт, а не сообщений: два вызова записи могут прийти одним чтением,
/// а один — двумя. Поэтому длина пишется явно, а не выводится из «читаем до конца
/// строки»: JSON с переводом строки внутри строкового значения сломал бы такой разбор.
/// <para>
/// Предел размера кадра — не перестраховка. Управляющий канал открыт для чужой машины,
/// и заявленная длина в четыре гигабайта не должна превращаться в четыре гигабайта
/// выделенной памяти раньше, чем в отказ.
/// </para>
/// </remarks>
public sealed class MessageChannel(Stream stream) : IDisposable
{
    /// <summary>Предел кадра. Управляющие сообщения на порядки меньше — это защита, а не запас.</summary>
    public const int MaxFrameBytes = 256 * 1024;

    /// <summary>
    /// Настройки сериализации.
    /// </summary>
    /// <remarks>
    /// Пропуск пустых полей задаётся и здесь, и в атрибуте контекста: явно переданный
    /// экземпляр настроек перекрывает объявленное атрибутом. Без этой строки широкий
    /// тип сообщения гнал бы в провод десяток пустых полей в каждом кадре — ровно то,
    /// чего его устройство обещало избежать.
    /// </remarks>
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ProtocolJsonContext Context = new(Options);

    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly byte[] _lengthBuffer = new byte[4];

    public async Task SendAsync(ProtocolMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.SerializeToUtf8Bytes(message, Context.ProtocolMessage);

        if (payload.Length > MaxFrameBytes)
        {
            throw new ProtocolException(
                $"Сообщение {message.Kind} длиной {payload.Length} байт не помещается в кадр "
                + $"({MaxFrameBytes} байт).");
        }

        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        // Запись под замком: снимки измерения и ответы на ping идут из разных мест,
        // и два кадра, перемешавшиеся байтами, разобрать уже нельзя.
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Читает сообщение. <c>null</c> — собеседник закрыл соединение штатно.</summary>
    public async Task<ProtocolMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        if (!await ReadExactlyAsync(_lengthBuffer, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = BinaryPrimitives.ReadInt32BigEndian(_lengthBuffer);

        if (length is <= 0 or > MaxFrameBytes)
        {
            throw new ProtocolException(
                $"Заявлена длина кадра {length} байт — вне допустимого (1…{MaxFrameBytes}). "
                + "Это либо не наш протокол, либо повреждённый поток.");
        }

        var payload = new byte[length];

        if (!await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            throw new ProtocolException($"Кадр оборван: обещано {length} байт, поток кончился раньше.");
        }

        try
        {
            return JsonSerializer.Deserialize(payload, Context.ProtocolMessage)
                   ?? throw new ProtocolException("Кадр разобран в пустое сообщение.");
        }
        catch (JsonException ex)
        {
            throw new ProtocolException($"Кадр не разобран: {ex.Message}", ex);
        }
    }

    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;

        while (read < buffer.Length)
        {
            var got = await _stream
                .ReadAsync(buffer.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);

            if (got == 0)
            {
                // Ноль байт на первом же чтении — штатное закрытие; в середине кадра —
                // обрыв, и это разные события для того, кто читает.
                return false;
            }

            read += got;
        }

        return true;
    }

    public void Dispose()
    {
        _writeGate.Dispose();
        _stream.Dispose();
    }
}

/// <summary>Нарушение протокола: не наш собеседник, повреждённый поток или отказ.</summary>
public sealed class ProtocolException : Exception
{
    public ProtocolException()
    {
    }

    public ProtocolException(string message)
        : base(message)
    {
    }

    public ProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProtocolException(string message, RefusalReason reason)
        : base(message) => Reason = reason;

    /// <summary>Причина отказа, если собеседник её назвал.</summary>
    public RefusalReason? Reason { get; }
}
