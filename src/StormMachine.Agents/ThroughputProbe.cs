using System.Globalization;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Agents;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using StormMachine.Protocol;

namespace StormMachine.Agents;

/// <summary>
/// Пропускная способность до сопряжённого агента.
/// </summary>
/// <remarks>
/// Сделана обычной пробой, а не отдельной командой. Из этого следует всё остальное:
/// прогон попадает в журнал, открывается в отчёте, ставится пресетом и годится шагом
/// сценария — ровно как ping. Заводить для неё отдельный путь значило бы построить
/// второй продукт рядом с первым.
/// <para>
/// Цель — имя сопряжённого агента, а не адрес. Адрес агента меняется (DHCP, переезд),
/// а сопряжение нет: личностью агента является его отпечаток. Пресет, сохранённый
/// сегодня, обязан работать и после смены адреса.
/// </para>
/// </remarks>
public sealed class ThroughputProbe(AgentDirectory directory) : IProbe
{
    private readonly AgentDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Throughput,
        Shape = ProbeResultShape.ScalarSeries,
        Name = "throughput",
        Title = "Пропускная способность",
        Description = "Скорость до сопряжённого агента: N потоков TCP, прогрев, отбрасывание разгона (RFC 6349).",
        Unit = MeasurementUnit.MegabitsPerSecond,
        Methodology = Methodology.TcpThroughput,
        RequiresElevation = false,
        RequiresAgent = true,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "streams", Label = "Потоков", Type = ProbeParameterType.Integer,
                DefaultValue = 4, Minimum = 1, Maximum = 64,
                Description = "Один поток не наполняет канал: он упирается в окно, делённое на RTT.",
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
                DefaultValue = 2, Minimum = 0, Maximum = 60,
                Description = "Отбрасываемый разгон TCP. Включить его в среднее значит занизить результат.",
            },
            new ProbeParameter
            {
                Name = "direction", Label = "Направление", Type = ProbeParameterType.Choice,
                DefaultValue = "upload",
                Choices = ["upload", "download"],
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
            Kind = TestKind.TcpThroughput,
            Streams = request.GetParameter("streams", 4),
            DurationSeconds = request.GetParameter("duration", 10),
            WarmupSeconds = request.GetParameter("warmup", 2),
            Direction = request.GetParameter("direction", "upload")
                .Equals("download", StringComparison.OrdinalIgnoreCase)
                ? TestDirection.Download
                : TestDirection.Upload,
        };

        observer.OnResolved(agent.Address ?? agent.DisplayName);
        observer.OnFact(ProbeFact.Text("agent", "Агент", $"{agent.DisplayName} ({agent.Product})"));
        observer.OnFact(ProbeFact.Text("agent", "Отпечаток", PeerIdentity.Group(agent.Thumbprint)));
        observer.OnFact(ProbeFact.Text("agent", "Соединение", agent.DescribeDirection()));
        observer.OnFact(ProbeFact.Number("agent", "Потоков", test.Streams, MeasurementUnit.Count));

        // Отброшенное на разгоне названо до результата, а не после: измерение,
        // которое молчит о том, что выбросило, проверить нельзя.
        observer.OnFact(ProbeFact.Number("agent", "Отброшено на разгоне, с", test.WarmupSeconds,
            MeasurementUnit.Count));

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

        Report(observer, last);
    }

    private static void Report(IProbeObserver observer, TestSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            observer.OnFact(ProbeFact.Warning("agent", "Итог", "Измерение не дало ни одного значения."));

            return;
        }

        observer.OnFact(ProbeFact.Number("agent", "Средняя скорость", snapshot.Mbps,
            MeasurementUnit.MegabitsPerSecond));

        // Два факта об одном объёме: человеку — крупные единицы, порогу — байты.
        // Один факт «Передано, МБ» с единицей «байт» противоречил сам себе.
        observer.OnFact(ProbeFact.Text("agent", "Передано", Volume(snapshot.Bytes)));

        observer.OnFact(ProbeFact.Number("agent", "Передано байт", snapshot.Bytes, MeasurementUnit.Bytes));

        observer.OnFact(ProbeFact.Text("agent", "Измерялось", string.Create(
            CultureInfo.InvariantCulture,
            $"{snapshot.ElapsedSeconds:0.0} с после прогрева")));

        if (snapshot.Failure is { Length: > 0 } failure)
        {
            observer.OnFact(ProbeFact.Warning("agent", "Помешало", failure));
        }

        // Порог достоверности часов к пропускной способности отношения не имеет,
        // и упоминать его здесь было бы враньём о том, что ограничивает точность.
        // Ограничивает её другое, и это названо прямо.
        observer.OnFact(ProbeFact.Text("agent", "О точности",
            "Верхняя граница — не канал, а машины по краям: сетевая карта, процессор "
            + "и очереди ядра. Результат ниже линии тарифа сам по себе не доказывает, "
            + "что виноват провайдер."));
    }

    /// <summary>Объём в единицах, которые читаются глазами.</summary>
    private static string Volume(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.00} ГБ"),
        >= 1L << 20 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} МБ"),
        >= 1L << 10 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 10):0} КБ"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} байт"),
    };
}
