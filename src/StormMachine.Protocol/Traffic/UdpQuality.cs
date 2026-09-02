using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace StormMachine.Protocol.Traffic;

/// <summary>
/// Качество канала по UDP: потери, переупорядочивание, дрожание.
/// </summary>
/// <remarks>
/// UDP отвечает на вопрос, на который TCP ответить не может. TCP прячет потери
/// повторной передачей: канал, теряющий два процента пакетов, по TCP выглядит просто
/// медленнее, и понять, что именно с ним не так, нельзя. Телефония же и видео идут
/// по UDP, и им важно не «сколько мегабит», а «сколько потеряно и насколько неровно
/// приходит».
/// <para>
/// Считает принимающая сторона. Отправитель знает, сколько отдал в сокет; потери
/// и переупорядочивание существуют только на приёме.
/// </para>
/// </remarks>
public static class UdpQuality
{
    /// <summary>Минимальный пакет: идентификатор теста, номер и отметка отправки.</summary>
    public const int HeaderBytes = 32;

    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Сколько ждать опоздавших после конца отправки.</summary>
    private static readonly TimeSpan DrainWindow = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Отправляет поток с заданной скоростью.
    /// </summary>
    /// <remarks>
    /// Генератор идёт в выделенном потоке с повышенным приоритетом: точная темповка
    /// занимает ядро целиком (спайк-05, 101 % одного ядра), и в пуле он отобрал бы
    /// поток у всего остального.
    /// </remarks>
    public static Task<TestSnapshot> SendAsync(
        string host,
        int port,
        TestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(request);

        var payloadBytes = Math.Max(HeaderBytes, request.PayloadBytes);
        var interval = PacketPacer.IntervalFor(request.TargetMbps, payloadBytes);
        var duration = TimeSpan.FromSeconds(Math.Max(1, request.DurationSeconds + request.WarmupSeconds));

        var completion = new TaskCompletionSource<TestSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(Pump(host, port, request, payloadBytes, interval, duration, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                completion.SetCanceled(cancellationToken);
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "storm-udp-pacer",
        };

        thread.Start();

        return completion.Task;
    }

    private static TestSnapshot Pump(
        string host,
        int port,
        TestRequest request,
        int payloadBytes,
        double intervalMs,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        var addresses = Dns.GetHostAddresses(host, AddressFamily.InterNetwork);

        if (addresses.Length == 0)
        {
            throw new ProtocolException($"Имя {host} не разрешилось в адрес IPv4.");
        }

        var target = new IPEndPoint(addresses[0], port);
        var payload = new byte[payloadBytes];
        Random.Shared.NextBytes(payload);

        request.Id.TryWriteBytes(payload);

        var pacer = new PacketPacer(intervalMs);
        var watch = Stopwatch.StartNew();

        long sequence = 0;
        long skipped = 0;

        while (watch.Elapsed < duration && !cancellationToken.IsCancellationRequested)
        {
            pacer.WaitForNext();

            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(16), sequence);
            BinaryPrimitives.WriteInt64BigEndian(payload.AsSpan(24), Stopwatch.GetTimestamp());

            try
            {
                socket.SendTo(payload, SocketFlags.None, target);
                sequence++;
            }
            catch (SocketException)
            {
                // Переполненная очередь отправки — это уже перегруз канала, а не ошибка
                // измерения. Пакет считается непосланным, и такт пропускается.
                skipped += pacer.SkipMissed() + 1;
            }
        }

        return new TestSnapshot
        {
            Id = request.Id,
            ElapsedSeconds = watch.Elapsed.TotalSeconds,
            Bytes = sequence * payloadBytes,
            Packets = sequence,
            Lost = skipped,
            Mbps = watch.Elapsed.TotalSeconds <= 0
                ? 0
                : sequence * payloadBytes * 8 / watch.Elapsed.TotalSeconds / 1_000_000.0,
            IsFinal = true,
            Failure = skipped > 0 ? $"Очередь отправки переполнялась: не послано пакетов {skipped}." : null,
        };
    }

    /// <summary>
    /// Принимает поток и считает то, что с ним случилось по дороге.
    /// </summary>
    /// <remarks>
    /// Приёмник получает сокет снаружи: порт объявляется собеседнику до начала измерения,
    /// и открыть его должна та же сторона, что его назвала.
    /// </remarks>
    public static async Task<TestSnapshot> ReceiveAsync(
        Socket socket,
        TestRequest request,
        IProgress<TestSnapshot>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(request);

        var payloadBytes = Math.Max(HeaderBytes, request.PayloadBytes);
        var buffer = new byte[Math.Max(2048, payloadBytes)];
        var counters = new ArrivalCounters();

        var warmup = TimeSpan.FromSeconds(Math.Max(0, request.WarmupSeconds));
        var measuring = TimeSpan.FromSeconds(Math.Max(1, request.DurationSeconds));
        var duration = warmup + measuring + DrainWindow;

        var watch = new Stopwatch();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var reporter = ReportAsync(request.Id, counters, watch, warmup, progress, stop.Token);
        var any = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!stop.IsCancellationRequested)
            {
                SocketReceiveFromResult received;

                try
                {
                    received = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, any, stop.Token)
                        .ConfigureAwait(false);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
                {
                    // На Windows UDP-сокет получает ICMP «порт недоступен» как ошибку
                    // чтения. К нашему потоку это не относится — читаем дальше.
                    continue;
                }

                if (received.ReceivedBytes < HeaderBytes
                    || new Guid(buffer.AsSpan(0, 16)) != request.Id)
                {
                    continue;
                }

                if (!watch.IsRunning)
                {
                    watch.Start();

                    // Срок отсчитывается от первого пакета и ставится на сам сокет.
                    // Проверять время перед чтением бесполезно: когда отправитель
                    // замолчал, чтение просто не вернётся, и измерение зависнет
                    // ровно в тот момент, когда оно уже закончилось.
                    stop.CancelAfter(duration);
                }

                var sequence = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(16));
                var sentAt = BinaryPrimitives.ReadInt64BigEndian(buffer.AsSpan(24));

                counters.Accept(sequence, sentAt, Stopwatch.GetTimestamp(), received.ReceivedBytes,
                    measured: watch.Elapsed >= warmup);
            }
        }
        catch (OperationCanceledException)
        {
            // Прервано оператором — то, что успели посчитать, остаётся в силе.
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);

            try
            {
                await reporter.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        // Окно измерения — заказанное, а не время работы приёмника: ожидание опоздавших
        // в него не входит. Короче заказанного оно бывает только при отмене оператором.
        var elapsed = watch.Elapsed - warmup;

        return counters.Finish(request.Id, elapsed < measuring ? elapsed : measuring);
    }

    private static async Task ReportAsync(
        Guid id,
        ArrivalCounters counters,
        Stopwatch watch,
        TimeSpan warmup,
        IProgress<TestSnapshot>? progress,
        CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            return;
        }

        // Как и у TCP: снимок несёт скорость за отрезок, а не среднюю с начала —
        // иначе просадка канала на графике не видна.
        var previousBytes = 0L;
        var previousElapsed = TimeSpan.Zero;

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(SnapshotInterval, cancellationToken).ConfigureAwait(false);

            var elapsed = watch.Elapsed - warmup;

            // Отрезок короче половины периода не годится: сразу после прогрева
            // до первого такта проходят микросекунды, а байты за них уже посчитаны,
            // и деление даёт число в тысячи раз выше возможного. Один такой отсчёт
            // портит и медиану, и разброс — то есть весь ответ.
            if (elapsed - previousElapsed < SnapshotInterval / 2)
            {
                continue;
            }

            var snapshot = counters.Snapshot(id, elapsed);
            var seconds = (elapsed - previousElapsed).TotalSeconds;

            progress.Report(snapshot with
            {
                Mbps = seconds <= 0 ? 0 : (snapshot.Bytes - previousBytes) * 8 / seconds / 1_000_000.0,
            });

            previousBytes = snapshot.Bytes;
            previousElapsed = elapsed;
        }
    }

    /// <summary>
    /// Что случилось с потоком по дороге.
    /// </summary>
    /// <remarks>
    /// Дрожание считается по RFC 3550 §6.4.1 — рекуррентно, по разнице времён прохождения
    /// соседних пакетов. Формула повторяет доменную <c>LatencyStatistics</c> не по небрежности:
    /// протокол не зависит от доменной модели намеренно (агент собирается отдельным
    /// маленьким бинарём), а входные данные здесь другие — не ряд RTT, а поток времён
    /// прохождения в одну сторону.
    /// <para>
    /// Часы двух машин не синхронизированы, и время прохождения в одну сторону само
    /// по себе бессмысленно. Дрожание же считается по <b>разности</b> соседних времён,
    /// и постоянный сдвиг часов из неё уходит. Поэтому дрожание честно, а односторонняя
    /// задержка отдельно не сообщается — её нечем проверить.
    /// </para>
    /// </remarks>
    private sealed class ArrivalCounters
    {
        private readonly Lock _gate = new();

        private long _packets;
        private long _bytes;
        private long _outOfOrder;

        private long _firstSequence = -1;
        private long _highestSequence = -1;

        private double _jitterTicks;
        private long _previousTransit;
        private bool _hasPrevious;

        public void Accept(long sequence, long sentAt, long receivedAt, int bytes, bool measured)
        {
            lock (_gate)
            {
                // Прогревочные пакеты не идут ни в счётчики, ни в границы окна.
                // Считать их номера в потерях — ошибка, которая была здесь и стоила
                // двадцати процентов мнимых потерь на петле: номер последнего пакета
                // включал прогрев, а число принятых — нет.
                if (!measured)
                {
                    return;
                }

                if (_firstSequence < 0)
                {
                    _firstSequence = sequence;
                }

                if (sequence < _highestSequence)
                {
                    _outOfOrder++;
                }
                else
                {
                    _highestSequence = sequence;
                }

                _packets++;
                _bytes += bytes;

                var transit = receivedAt - sentAt;

                if (_hasPrevious)
                {
                    var difference = Math.Abs(transit - _previousTransit);
                    _jitterTicks += (difference - _jitterTicks) / 16.0;
                }

                _previousTransit = transit;
                _hasPrevious = true;
            }
        }

        /// <summary>
        /// Снимок за окно измерения.
        /// </summary>
        /// <remarks>
        /// Знаменатель приходит снаружи — это заказанное окно, а не время между первым
        /// и последним прочитанным пакетом. Времена чтения для этого не годятся: когда
        /// приёмник не успевает читать, пакеты лежат в буфере сокета и вычитываются
        /// пачкой. Окно по ним выходит во столько же раз короче, во сколько приёмник
        /// опоздал, а скорость — во столько же раз выше. На общем раннере продукт
        /// объявил 96 Мбит/с у потока в восемь: байты пришли все, но прочитаны были
        /// за одну шестую секунды вместо двух.
        /// <para>
        /// Времена отправителя для этого тоже не годятся, и по той же причине, по какой
        /// не сообщается односторонняя задержка: часы двух машин не синхронизированы.
        /// </para>
        /// </remarks>
        public TestSnapshot Snapshot(Guid id, TimeSpan window)
        {
            lock (_gate)
            {
                var seconds = Math.Max(0, window.TotalSeconds);

                var expected = _firstSequence < 0 ? 0 : _highestSequence - _firstSequence + 1;

                return new TestSnapshot
                {
                    Id = id,
                    ElapsedSeconds = seconds,
                    Bytes = _bytes,
                    Packets = _packets,
                    Mbps = seconds <= 0 ? 0 : _bytes * 8 / seconds / 1_000_000.0,
                    Lost = Math.Max(0, expected - _packets),
                    OutOfOrder = _outOfOrder,
                    JitterMs = _jitterTicks * 1000.0 / Stopwatch.Frequency,
                };
            }
        }

        public TestSnapshot Finish(Guid id, TimeSpan elapsed) =>
            Snapshot(id, elapsed) with
            {
                IsFinal = true,
                Failure = _packets == 0
                    ? "Ни один пакет не дошёл. Проверь, что UDP на этот порт разрешён брандмауэром."
                    : null,
            };
    }
}
