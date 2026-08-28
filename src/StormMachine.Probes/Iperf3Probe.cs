using System.Globalization;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes;

/// <summary>
/// Скорость через существующий <c>iperf3 -s</c>.
/// </summary>
/// <remarks>
/// Две причины, по которым эта проба есть, хотя свой агент делает то же самое.
/// <para>
/// Первая — мост в чужие сети: там, где агента поставить не дадут, а iperf3 уже стоит.
/// У провайдера, на чужом стенде, на сетевом оборудовании.
/// </para>
/// <para>
/// Вторая — проверка себя. Два инструмента, меряющие один канал разными реализациями,
/// обязаны сходиться. Расхождение означает ошибку в одном из них, и узнать об этом лучше
/// на стенде, чем в споре с провайдером. Именно это и есть приёмка И-13.
/// </para>
/// </remarks>
public sealed class Iperf3Probe : IProbe
{
    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Throughput,
        Shape = ProbeResultShape.ScalarSeries,
        Name = "iperf3",
        Title = "Скорость через iperf3",
        Description = "Измерение к существующему «iperf3 -s». Мост туда, где своего агента поставить нельзя.",
        Unit = MeasurementUnit.MegabitsPerSecond,
        Methodology = Methodology.TcpThroughput,
        RequiresElevation = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "port", Label = "Порт", Type = ProbeParameterType.Integer,
                DefaultValue = Iperf3Client.DefaultPort, Minimum = 1, Maximum = 65535,
                Description = "Порт, на котором слушает iperf3 -s.",
            },
            new ProbeParameter
            {
                Name = "duration", Label = "Длительность, с", Type = ProbeParameterType.Integer,
                DefaultValue = 10, Minimum = 1, Maximum = 600,
                Description = "Сколько длится измерение.",
            },
            new ProbeParameter
            {
                Name = "streams", Label = "Потоков", Type = ProbeParameterType.Integer,
                DefaultValue = 4, Minimum = 1, Maximum = 128,
                Description = "Один поток не наполняет канал: он упирается в окно, делённое на RTT.",
            },
            new ProbeParameter
            {
                Name = "omit", Label = "Отбрасываемый разгон, с", Type = ProbeParameterType.Integer,
                DefaultValue = 2, Minimum = 0, Maximum = 60,
                Description = "То же, что «iperf3 -O». По умолчанию 2 — как у пробы throughput: "
                              + "сравнивать имеет смысл одинаково настроенные измерения.",
            },
            new ProbeParameter
            {
                Name = "reverse", Label = "Обратное направление", Type = ProbeParameterType.Boolean,
                DefaultValue = false,
                Description = "Отдаёт сервер, принимаем мы. То же, что «iperf3 -R».",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<ProbeValidationError>(ProbeValidation.Validate(Descriptor, request));

        if (request.Target.Kind is TargetKind.Subnet)
        {
            errors.Add(new ProbeValidationError("target", "Цель — адрес или имя машины с iperf3 -s."));
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

        var host = request.Target.Kind == TargetKind.Url
            ? new Uri(request.Target.Value).Host
            : request.Target.Value;

        var port = request.GetParameter("port", Iperf3Client.DefaultPort);
        var seconds = request.GetParameter("duration", 10);
        var streams = request.GetParameter("streams", 4);
        var omit = request.GetParameter("omit", 2);
        var reverse = request.GetParameter("reverse", false);

        observer.OnResolved($"{host}:{port}");
        observer.OnFact(ProbeFact.Text("iperf3", "Направление",
            reverse ? "отдаёт сервер, принимаем мы" : "отдаём мы, принимает сервер"));
        observer.OnFact(ProbeFact.Number("iperf3", "Потоков", streams, MeasurementUnit.Count));
        observer.OnFact(ProbeFact.Number("iperf3", "Отброшено на разгоне, с", omit, MeasurementUnit.Count));

        var rates = System.Threading.Channels.Channel.CreateUnbounded<double>();

        var running = Task.Run(async () =>
        {
            try
            {
                return await Iperf3Client
                    .RunAsync(host, port, seconds, streams, omit, reverse, m => rates.Writer.TryWrite(m), cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                rates.Writer.TryComplete();
            }
        }, cancellationToken);

        var sequence = 0;

        await foreach (var mbps in rates.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            if (mbps <= 0)
            {
                continue;
            }

            yield return new Sample
            {
                Sequence = sequence++,
                TimestampUtc = DateTimeOffset.UtcNow,
                Value = mbps,
                Status = SampleStatus.Success,
            };
        }

        var result = await running.ConfigureAwait(false);

        yield return new Sample
        {
            Sequence = sequence,
            TimestampUtc = DateTimeOffset.UtcNow,
            Value = result.Mbps,
            Status = SampleStatus.Success,
        };

        Report(observer, result);
    }

    private static void Report(IProbeObserver observer, Iperf3Result result)
    {
        observer.OnFact(ProbeFact.Number("iperf3", "Средняя скорость", result.Mbps,
            MeasurementUnit.MegabitsPerSecond));

        // Чьим счётом получено число, названо прямо: разница между отданным
        // и дошедшим — это и есть потери, и подменять одно другим нельзя.
        observer.OnFact(ProbeFact.Text("iperf3", "Считано", result.CountedBy));

        observer.OnFact(ProbeFact.Text("iperf3", "Отдано",
            string.Create(CultureInfo.InvariantCulture, $"{result.BytesSent / (double)(1L << 20):0.0} МБ")));

        if (result.BytesReceived > 0)
        {
            observer.OnFact(ProbeFact.Text("iperf3", "Принято",
                string.Create(CultureInfo.InvariantCulture, $"{result.BytesReceived / (double)(1L << 20):0.0} МБ")));

            var lost = result.BytesSent - result.BytesReceived;

            if (lost > 0 && result.BytesSent > 0)
            {
                var percent = lost * 100.0 / result.BytesSent;

                observer.OnFact(new ProbeFact
                {
                    Category = "iperf3",
                    Name = "Не дошло",
                    Value = string.Create(CultureInfo.InvariantCulture, $"{percent:0.00}"),
                    Numeric = percent,
                    Unit = MeasurementUnit.Percent,
                    IsWarning = percent >= 1,
                });
            }
        }
        else
        {
            observer.OnFact(ProbeFact.Warning("iperf3", "Счёт сервера",
                "Сервер не прислал свой счёт байт. Показано отданное в сокет — "
                + "это не то же самое, что дошло."));
        }

        observer.OnFact(ProbeFact.Text("iperf3", "Зачем это",
            "Число рядом с собственным измерением до агента: две разные реализации "
            + "на одном канале обязаны сходиться, и расхождение — повод разбираться, "
            + "а не выбирать удобное."));
    }
}
