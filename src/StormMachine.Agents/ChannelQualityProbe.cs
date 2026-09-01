using System.Globalization;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using StormMachine.Protocol;

namespace StormMachine.Agents;

/// <summary>
/// Качество канала до агента: потери, переупорядочивание, дрожание.
/// </summary>
/// <remarks>
/// Отвечает на вопрос, на который проба пропускной способности ответить не может.
/// TCP прячет потери повторной передачей: канал, теряющий два процента пакетов,
/// по TCP выглядит просто медленнее, и понять, что именно с ним не так, нельзя.
/// Телефония и видео идут по UDP, и им важно не «сколько мегабит», а «сколько потеряно
/// и насколько неровно приходит».
/// <para>
/// Скорость задаётся, а не измеряется. В этом весь смысл: канал нагружается ровно тем
/// потоком, который собираются по нему пустить, и проверяется, выдержит ли он его.
/// Гнать на предельной скорости значило бы измерять поведение перегруженного канала —
/// а вопрос был про рабочий.
/// </para>
/// </remarks>
public sealed class ChannelQualityProbe(AgentDirectory directory) : IProbe
{
    private readonly AgentDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Udp,
        Shape = ProbeResultShape.ScalarSeries,
        Name = "channel",
        Title = "Качество канала",
        Description = "Поток UDP заданной скорости до агента: потери, переупорядочивание, дрожание.",
        Unit = MeasurementUnit.MegabitsPerSecond,
        Methodology = Methodology.InterarrivalJitter,
        RequiresElevation = false,
        RequiresAgent = true,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "rate", Label = "Скорость, Мбит/с", Type = ProbeParameterType.Decimal,
                DefaultValue = 10.0, Minimum = 0.05, Maximum = 1000,
                Description = "Нагрузка, которую собираются пустить по каналу на самом деле.",
            },
            new ProbeParameter
            {
                Name = "duration", Label = "Длительность, с", Type = ProbeParameterType.Integer,
                DefaultValue = 10, Minimum = 1, Maximum = 600,
                Description = "Сколько длится измерение, не считая прогрева.",
            },
            new ProbeParameter
            {
                Name = "warmup", Label = "Прогрев, с", Type = ProbeParameterType.Integer,
                DefaultValue = 1, Minimum = 0, Maximum = 60,
                Description = "Отбрасываемое начало: первые пакеты идут по непрогретому пути.",
            },
            new ProbeParameter
            {
                Name = "size", Label = "Размер пакета, байт", Type = ProbeParameterType.Integer,
                DefaultValue = 172, Minimum = 32, Maximum = 1400,
                Description = "172 байта — типичный пакет G.711 с заголовками RTP.",
            },
            new ProbeParameter
            {
                Name = "direction", Label = "Направление", Type = ProbeParameterType.Choice,
                DefaultValue = Directions.Upload,
                Choices = [Directions.Upload, Directions.Download],
                Description = "upload — отдаём мы, download — отдаёт агент.",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ProbeValidationError>(ProbeValidation.Validate(Descriptor, request));

        if (request.Target.Kind is TargetKind.Subnet or TargetKind.DefaultGateway)
        {
            errors.Add(new ProbeValidationError(
                "target",
                "Цель — имя сопряжённого агента, а не адрес. Список: storm agents."));
        }

        return errors;
    }

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var agent = await _directory.FindAsync(request.Target.Value, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        $"Агент «{request.Target.Value}» не найден среди сопряжённых. Список: storm agents.");

        var test = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.UdpQuality,
            TargetMbps = request.GetParameter("rate", 10.0),
            DurationSeconds = request.GetParameter("duration", 10),
            WarmupSeconds = request.GetParameter("warmup", 1),
            PayloadBytes = request.GetParameter("size", 172),
            Direction = Directions.IsDownload(request.GetParameter("direction", Directions.Upload))
                ? TestDirection.Download
                : TestDirection.Upload,
        };

        observer.OnResolved(agent.Address ?? agent.DisplayName);
        observer.OnFact(ProbeFact.Text("agent", "Агент", $"{agent.DisplayName} ({agent.Product})"));
        observer.OnFact(ProbeFact.Text("agent", "Соединение", agent.DescribeDirection()));
        observer.OnFact(ProbeFact.Number("channel", "Заданная скорость", test.TargetMbps,
            MeasurementUnit.MegabitsPerSecond));
        observer.OnFact(ProbeFact.Number("channel", "Размер пакета", test.PayloadBytes, MeasurementUnit.Bytes));

        if (!agent.Capabilities.Contains(Protocol.Capabilities.PrecisePacing, StringComparer.OrdinalIgnoreCase))
        {
            // Без точной темповки поток пойдёт рывками, и дрожание измерит генератор,
            // а не канал. Промолчать об этом значило бы выдать шум за результат.
            observer.OnFact(ProbeFact.Warning(
                "channel",
                "Темповка",
                "Агент не заявил точную темповку. Дрожание в этом измерении может "
                + "принадлежать генератору, а не каналу."));
        }

        var snapshots = System.Threading.Channels.Channel.CreateUnbounded<TestSnapshot>();

        var running = Task.Run(async () =>
        {
            try
            {
                // При обратном направлении здесь ожидание звонка агента, а не отказ:
                // оператор попросил измерить, и заставлять его искать вторую команду
                // там, где нужна одна, незачем.
                var waiting = new PairingRelay(observer);

                using var session = await _directory
                    .OpenAsync(agent, waiting, cancellationToken)
                    .ConfigureAwait(false);

                var progress = new Progress<TestSnapshot>(s => snapshots.Writer.TryWrite(s));

                var result = await TestConductor
                    .RequestAsync(session, test, progress, cancellationToken)
                    .ConfigureAwait(false);

                snapshots.Writer.TryWrite(result with { IsFinal = true });
            }
            finally
            {
                snapshots.Writer.TryComplete();
            }
        }, cancellationToken);

        var sequence = 0;
        TestSnapshot? last = null;

        await foreach (var snapshot in snapshots.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            last = snapshot;

            if (snapshot.Mbps <= 0)
            {
                continue;
            }

            yield return new Sample
            {
                Sequence = sequence++,
                TimestampUtc = DateTimeOffset.UtcNow,
                Value = snapshot.Mbps,
                Status = SampleStatus.Success,
            };
        }

        await running.ConfigureAwait(false);

        Report(observer, last, test);
    }

    /// <summary>
    /// Что вышло — числами, которых ради этой пробы и добивались.
    /// </summary>
    /// <remarks>
    /// Потери, переупорядочивание и дрожание идут числовыми фактами, а не в ряд:
    /// ряд здесь один и он про скорость. Числовые факты при этом доступны порогам,
    /// и «loss &lt; 1» на шаге сценария работает так же, как у ping.
    /// </remarks>
    private static void Report(IProbeObserver observer, TestSnapshot? snapshot, TestRequest test)
    {
        if (snapshot is null)
        {
            observer.OnFact(ProbeFact.Warning("channel", "Итог", "Измерение не дало ни одного значения."));

            return;
        }

        var expected = snapshot.Packets + snapshot.Lost;
        var lossPercent = expected > 0 ? snapshot.Lost * 100.0 / expected : 0;

        observer.OnFact(ProbeFact.Number("channel", "Принято пакетов", snapshot.Packets, MeasurementUnit.Count));
        observer.OnFact(ProbeFact.Number("channel", "Потеряно пакетов", snapshot.Lost, MeasurementUnit.Count));

        observer.OnFact(new ProbeFact
        {
            Category = "channel",
            Name = "Потери",
            Value = string.Create(CultureInfo.InvariantCulture, $"{lossPercent:0.00}"),
            Numeric = lossPercent,
            Unit = MeasurementUnit.Percent,
            IsWarning = lossPercent >= 1,
        });

        observer.OnFact(new ProbeFact
        {
            Category = "channel",
            Name = "Пришло не в очередь",
            Value = snapshot.OutOfOrder.ToString(CultureInfo.InvariantCulture),
            Numeric = snapshot.OutOfOrder,
            Unit = MeasurementUnit.Count,
            IsWarning = snapshot.OutOfOrder > 0,
        });

        observer.OnFact(ProbeFact.Number("channel", "Дрожание", snapshot.JitterMs, MeasurementUnit.Milliseconds));

        // Достигнутая скорость рядом с заданной: если канал не выдержал, это видно
        // сразу и не требует деления чисел в уме.
        observer.OnFact(ProbeFact.Number("channel", "Достигнутая скорость", snapshot.Mbps,
            MeasurementUnit.MegabitsPerSecond));

        if (snapshot.Mbps < test.TargetMbps * 0.95)
        {
            observer.OnFact(ProbeFact.Warning(
                "channel",
                "Канал не выдержал",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Заказано {test.TargetMbps:0.##} Мбит/с, дошло {snapshot.Mbps:0.##}. Столько нагрузки канал не пропускает.")));
        }

        if (snapshot.Failure is { Length: > 0 } failure)
        {
            observer.OnFact(ProbeFact.Warning("channel", "Помешало", failure));
        }

        // Одностороннюю задержку проба не сообщает намеренно: часы двух машин
        // не синхронизированы, и её значение было бы сдвинуто на неизвестную величину.
        // Дрожание считается по разности соседних времён прохождения, и сдвиг из неё уходит.
        observer.OnFact(ProbeFact.Text("channel", "О задержке",
            "Односторонняя задержка не измеряется: часы машин не синхронизированы. "
            + "Дрожание от этого не страдает — оно считается по разности соседних пакетов."));
    }
}
