using System.Net;
using System.Net.Sockets;
using StormMachine.Protocol.Traffic;

namespace StormMachine.Protocol;

/// <summary>
/// Ведение измерения поверх управляющего канала.
/// </summary>
/// <remarks>
/// Обе роли написаны здесь, рядом. Клиент и агент меняются местами в зависимости
/// от направления теста: при отдаче поток гонит инициатор, при приёме — собеседник.
/// Разнести эти половины по разным проектам значило бы завести две реализации одного
/// разговора, и первая же правка развела бы их — а расхождение в измерительном
/// инструменте означает, что две стороны меряют разное.
/// <para>
/// Порт данных всегда открывает <b>принимающая</b> сторона и называет его сообщением
/// <see cref="MessageKind.TestReady"/>. Так у отправителя не остаётся выбора, куда слать,
/// и не возникает случая, когда обе стороны ждут друг друга.
/// </para>
/// </remarks>
public static class TestConductor
{
    /// <summary>
    /// Сторона, начавшая измерение: просит, ждёт готовности, гонит или принимает поток.
    /// </summary>
    public static async Task<TestSnapshot> RequestAsync(
        SecureSession session,
        TestRequest request,
        IProgress<TestSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        // Принимает тот, кто не отправляет. При отдаче принимает собеседник,
        // при приёме — мы, и порт открываем тоже мы.
        var weReceive = request.Direction == TestDirection.Download;

        TcpListener? tcp = null;
        Socket? udp = null;
        var dataPort = 0;

        try
        {
            if (weReceive)
            {
                (tcp, udp, dataPort) = OpenReceiver(request.Kind);
            }

            await session.Channel.SendAsync(
                new ProtocolMessage
                {
                    Kind = MessageKind.StartTest,
                    Request = request,
                    DataPort = dataPort,
                },
                cancellationToken).ConfigureAwait(false);

            var answer = await session.Channel.ReceiveAsync(cancellationToken).ConfigureAwait(false)
                         ?? throw new ProtocolException("Собеседник закрыл соединение, не ответив на просьбу измерить.");

            if (answer.Kind == MessageKind.Refused)
            {
                throw new ProtocolException(
                    answer.Explanation ?? "Собеседник отказался измерять.",
                    answer.Reason ?? RefusalReason.Unsupported);
            }

            if (answer.Kind != MessageKind.TestReady)
            {
                throw new ProtocolException($"Ожидалось TestReady, пришло {answer.Kind}.");
            }

            return weReceive
                ? await ReceiveAndTellAsync(session, tcp!, udp!, request, progress, cancellationToken)
                    .ConfigureAwait(false)
                : await SendAndAskAsync(session, answer.DataPort, request, progress, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            tcp?.Stop();
            udp?.Dispose();
        }
    }

    /// <summary>
    /// Сторона, принявшая просьбу: открывает порт, если принимает, и делает свою половину.
    /// </summary>
    /// <param name="allow">
    /// Согласие на измерение. Агент, занятый другим тестом или запущенный только
    /// для сопряжения, обязан отказать — и объяснить, почему.
    /// </param>
    public static async Task<TestSnapshot?> ServeAsync(
        SecureSession session,
        ProtocolMessage message,
        Func<TestRequest, string?> allow,
        IProgress<TestSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(allow);

        if (message.Kind != MessageKind.StartTest || message.Request is not { } request)
        {
            await RefuseAsync(session, RefusalReason.Unsupported,
                $"Ожидалось StartTest с параметрами, пришло {message.Kind}.", cancellationToken)
                .ConfigureAwait(false);

            return null;
        }

        if (allow(request) is { Length: > 0 } refusal)
        {
            await RefuseAsync(session, RefusalReason.Busy, refusal, cancellationToken).ConfigureAwait(false);

            return null;
        }

        // Зеркально просьбе: инициатор отдаёт — значит принимаем мы.
        var weReceive = request.Direction == TestDirection.Upload;

        TcpListener? tcp = null;
        Socket? udp = null;
        var dataPort = 0;

        try
        {
            if (weReceive)
            {
                (tcp, udp, dataPort) = OpenReceiver(request.Kind);
            }

            await session.Channel.SendAsync(
                new ProtocolMessage
                {
                    Kind = MessageKind.TestReady,
                    Exchange = message.Exchange,
                    Request = request,
                    DataPort = dataPort,
                },
                cancellationToken).ConfigureAwait(false);

            return weReceive
                ? await ReceiveAndTellAsync(session, tcp!, udp!, request, progress, cancellationToken)
                    .ConfigureAwait(false)
                : await SendAndAskAsync(session, message.DataPort, request, progress, cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            tcp?.Stop();
            udp?.Dispose();
        }
    }

    /// <summary>
    /// Принимает поток и сообщает итог собеседнику.
    /// </summary>
    /// <remarks>
    /// Итог считает принимающая сторона — это не деталь реализации, а единственный
    /// способ получить верное число. Отправитель знает, сколько он отдал в сокет;
    /// потери и переупорядочивание существуют только на приёме, и результат
    /// отправителя завышен ровно на то, что не дошло.
    /// <para>
    /// Промежуточные снимки идут туда же и по тому же управляющему каналу: без них
    /// у отправителя нечего показывать на живом графике, а десять секунд молчания
    /// с последующим одним числом выглядят как зависание.
    /// </para>
    /// </remarks>
    private static async Task<TestSnapshot> ReceiveAndTellAsync(
        SecureSession session,
        TcpListener? tcp,
        Socket? udp,
        TestRequest request,
        IProgress<TestSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        var relay = new Progress<TestSnapshot>(snapshot =>
        {
            progress?.Report(snapshot);

            _ = session.Channel.SendAsync(
                new ProtocolMessage { Kind = MessageKind.TestProgress, Snapshot = snapshot with { Id = request.Id } },
                cancellationToken);
        });

        var result = await ReceiveAsync(tcp, udp, request, relay, cancellationToken).ConfigureAwait(false)
                     with { Id = request.Id };

        await session.Channel.SendAsync(
            new ProtocolMessage { Kind = MessageKind.TestResult, Snapshot = result },
            cancellationToken).ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// Гонит поток и берёт итог у того, кто его принял.
    /// </summary>
    /// <remarks>
    /// Свой счётчик остаётся запасным ответом: если собеседник не прислал итог,
    /// показать нечего, а «сколько мы предложили каналу» — это хоть что-то, и оно
    /// названо своим именем в пояснении к результату.
    /// </remarks>
    private static async Task<TestSnapshot> SendAndAskAsync(
        SecureSession session,
        int dataPort,
        TestRequest request,
        IProgress<TestSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        using var finished = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var listening = ListenAsync(session, progress, finished.Token);
        var offered = await SendAsync(session.Peer, dataPort, request, cancellationToken).ConfigureAwait(false);

        // Ждём итога от принимающей стороны, но не бесконечно: оборвавшийся собеседник
        // не должен подвесить измерение навсегда.
        finished.CancelAfter(TimeSpan.FromSeconds(10));

        var reported = await listening.ConfigureAwait(false);

        return reported ?? offered with
        {
            Failure = offered.Failure
                      ?? "Принимающая сторона не прислала итог. Показано то, что отдано в сокет, — "
                      + "это не то же самое, что дошло.",
        };
    }

    /// <summary>Читает управляющий канал, пока не придёт итог измерения.</summary>
    private static async Task<TestSnapshot?> ListenAsync(
        SecureSession session,
        IProgress<TestSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var message = await session.Channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);

                switch (message?.Kind)
                {
                    case MessageKind.TestProgress when message.Snapshot is { } snapshot:
                        progress?.Report(snapshot);
                        break;

                    case MessageKind.TestResult when message.Snapshot is { } result:
                        return result;

                    case null:
                        return null;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ProtocolException)
        {
            // Собеседник замолчал. Итог возьмём свой — и скажем, что он свой.
        }

        return null;
    }

    /// <summary>
    /// Открывает порт под приём.
    /// </summary>
    /// <remarks>
    /// Порт выбирает система, а не продукт. Фиксированный номер рано или поздно
    /// оказывается занят чужой программой, и оператор получил бы отказ там, где
    /// свободных портов десятки тысяч.
    /// </remarks>
    private static (TcpListener? Tcp, Socket? Udp, int Port) OpenReceiver(TestKind kind)
    {
        if (kind == TestKind.TcpThroughput)
        {
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();

            return (listener, null, ((IPEndPoint)listener.LocalEndpoint).Port);
        }

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            // Приём на скорости в сотни мегабит переполнит стандартную очередь ядра
            // раньше, чем поток дойдёт до нашего кода, и потери оказались бы нашими,
            // а не канала.
            ReceiveBufferSize = 8 * 1024 * 1024,
        };

        socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        return (null, socket, ((IPEndPoint)socket.LocalEndPoint!).Port);
    }

    private static Task<TestSnapshot> ReceiveAsync(
        TcpListener? tcp,
        Socket? udp,
        TestRequest request,
        IProgress<TestSnapshot>? progress,
        CancellationToken cancellationToken) =>
        request.Kind == TestKind.TcpThroughput
            ? TcpThroughput.ReceiveAsync(tcp!, request, progress, cancellationToken)
            : UdpQuality.ReceiveAsync(udp!, request, progress, cancellationToken);

    private static Task<TestSnapshot> SendAsync(
        PeerInfo peer,
        int dataPort,
        TestRequest request,
        CancellationToken cancellationToken)
    {
        if (dataPort <= 0)
        {
            throw new ProtocolException("Собеседник не назвал порт для потока данных.");
        }

        var host = peer.Address ?? throw new ProtocolException(
            "Неизвестно, куда слать поток: адрес собеседника не определён.");

        return request.Kind == TestKind.TcpThroughput
            ? TcpThroughput.SendAsync(host, dataPort, request, null, cancellationToken)
            : UdpQuality.SendAsync(host, dataPort, request, cancellationToken);
    }

    private static async Task RefuseAsync(
        SecureSession session,
        RefusalReason reason,
        string explanation,
        CancellationToken cancellationToken)
    {
        try
        {
            await session.Channel.SendAsync(
                new ProtocolMessage
                {
                    Kind = MessageKind.Refused,
                    Reason = reason,
                    Explanation = explanation,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ProtocolException or OperationCanceledException)
        {
            // Объяснить не удалось — соединение уже разорвано.
        }
    }
}
