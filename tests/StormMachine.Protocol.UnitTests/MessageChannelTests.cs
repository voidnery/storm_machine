using System.Buffers.Binary;
using StormMachine.Protocol;

namespace StormMachine.Protocol.UnitTests;

/// <summary>
/// Проверки кадрирования.
/// </summary>
/// <remarks>
/// Управляющий канал открыт для чужой машины, поэтому здесь проверяется не только
/// «сообщение доехало», но и что канал переживает повреждённый или враждебный поток:
/// заявленную длину в два гигабайта, обрыв посреди кадра, мусор вместо JSON.
/// Инструмент диагностики обязан на таком отказывать внятно, а не падать.
/// </remarks>
public sealed class MessageChannelTests
{
    private static ProtocolMessage Sample() => new()
    {
        Kind = MessageKind.Hello,
        ProtocolMajor = 1,
        ProtocolMinor = 0,
        Product = "storm/тест",
        MachineName = "СТЕНД-01",
        Thumbprint = new string('A', 64),
        Capabilities = [Capabilities.TcpThroughput, Capabilities.UdpQuality],
    };

    [Fact]
    public async Task RoundTrip_KeepsEveryField()
    {
        using var buffer = new MemoryStream();
        var sent = Sample();

        using (var writer = new MessageChannel(new NonClosingStream(buffer)))
        {
            await writer.SendAsync(sent);
        }

        buffer.Position = 0;

        using var reader = new MessageChannel(new NonClosingStream(buffer));
        var received = await reader.ReceiveAsync();

        Assert.NotNull(received);
        Assert.Equal(sent.Kind, received.Kind);
        Assert.Equal(sent.Product, received.Product);
        Assert.Equal(sent.MachineName, received.MachineName);
        Assert.Equal(sent.Thumbprint, received.Thumbprint);
        Assert.Equal(sent.Capabilities, received.Capabilities);
    }

    [Fact]
    public async Task RoundTrip_SurvivesCyrillicAndNewlines()
    {
        // Разбор «до перевода строки» сломался бы здесь. Длина пишется явно
        // именно поэтому, и тест закрепляет причину.
        using var buffer = new MemoryStream();

        var sent = new ProtocolMessage
        {
            Kind = MessageKind.Refused,
            Reason = RefusalReason.Version,
            Explanation = "Версии протокола несовместимы:\nу нас 1.0,\nу собеседника 2.0.",
        };

        using (var writer = new MessageChannel(new NonClosingStream(buffer)))
        {
            await writer.SendAsync(sent);
        }

        buffer.Position = 0;

        using var reader = new MessageChannel(new NonClosingStream(buffer));
        var received = await reader.ReceiveAsync();

        Assert.Equal(sent.Explanation, received?.Explanation);
        Assert.Equal(RefusalReason.Version, received?.Reason);
    }

    [Fact]
    public async Task ThreeMessages_ReadBackInOrder()
    {
        using var buffer = new MemoryStream();

        using (var writer = new MessageChannel(new NonClosingStream(buffer)))
        {
            await writer.SendAsync(new ProtocolMessage { Kind = MessageKind.Ping, Exchange = 1 });
            await writer.SendAsync(new ProtocolMessage { Kind = MessageKind.Pong, Exchange = 1 });
            await writer.SendAsync(new ProtocolMessage { Kind = MessageKind.Abort, Exchange = 2 });
        }

        buffer.Position = 0;

        using var reader = new MessageChannel(new NonClosingStream(buffer));

        Assert.Equal(MessageKind.Ping, (await reader.ReceiveAsync())?.Kind);
        Assert.Equal(MessageKind.Pong, (await reader.ReceiveAsync())?.Kind);
        Assert.Equal(MessageKind.Abort, (await reader.ReceiveAsync())?.Kind);
        Assert.Null(await reader.ReceiveAsync());
    }

    [Fact]
    public async Task ClosedStream_IsNotAnError()
    {
        // Штатное закрытие — это null, а не исключение: собеседник имеет право
        // положить трубку, и это не повод показывать оператору ошибку.
        using var reader = new MessageChannel(new MemoryStream());

        Assert.Null(await reader.ReceiveAsync());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(MessageChannel.MaxFrameBytes + 1)]
    public async Task ImpossibleLength_IsRefusedBeforeAllocating(int length)
    {
        var frame = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(frame, length);

        using var reader = new MessageChannel(new MemoryStream(frame));

        var error = await Assert.ThrowsAsync<ProtocolException>(() => reader.ReceiveAsync());

        Assert.Contains("длина кадра", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TruncatedFrame_SaysSoInsteadOfHanging()
    {
        var frame = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(frame, 100);

        using var reader = new MessageChannel(new MemoryStream(frame));

        var error = await Assert.ThrowsAsync<ProtocolException>(() => reader.ReceiveAsync());

        Assert.Contains("оборван", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GarbageInsteadOfJson_IsRefusedWithReason()
    {
        var payload = "это не JSON"u8.ToArray();
        var frame = new byte[4 + payload.Length];

        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame.AsSpan(4));

        using var reader = new MessageChannel(new MemoryStream(frame));

        var error = await Assert.ThrowsAsync<ProtocolException>(() => reader.ReceiveAsync());

        Assert.Contains("не разобран", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentSends_DoNotInterleaveBytes()
    {
        // Снимки измерения и ответы на ping идут из разных мест. Два кадра,
        // перемешавшиеся байтами, разобрать уже нельзя — отсюда замок на запись.
        using var buffer = new MemoryStream();
        using var writer = new MessageChannel(new NonClosingStream(buffer));

        var messages = Enumerable.Range(0, 50)
            .Select(i => new ProtocolMessage { Kind = MessageKind.Ping, Exchange = i })
            .ToList();

        await Task.WhenAll(messages.Select(m => writer.SendAsync(m)));

        buffer.Position = 0;

        using var reader = new MessageChannel(new NonClosingStream(buffer));
        var seen = new List<int>();

        while (await reader.ReceiveAsync() is { } message)
        {
            seen.Add(message.Exchange);
        }

        Assert.Equal(50, seen.Count);
        Assert.Equal(Enumerable.Range(0, 50).OrderBy(i => i), seen.OrderBy(i => i));
    }

    /// <summary>Поток, который не закрывается вместе с каналом — чтобы читать записанное.</summary>
    private sealed class NonClosingStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;

        public override bool CanSeek => inner.CanSeek;

        public override bool CanWrite => inner.CanWrite;

        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => inner.Position = value;
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

        public override void SetLength(long value) => inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    }
}
