using System.Globalization;
using System.Runtime.CompilerServices;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Probes;

/// <summary>
/// Скорость до публичного сервера: NDT7 (M-Lab).
/// </summary>
/// <remarks>
/// Отвечает на вопрос «что нам продают» — в отличие от пробы до агента, которая отвечает
/// на вопрос «что между этими двумя точками». Цифра в договоре относится к каналу
/// до провайдера, и проверить её можно только измерением наружу.
/// <para>
/// Бэкенд показывается всегда — это требование, а не пожелание (E-20 анализа, R-08
/// исследования). Скорость до сервера в одном городе и до сервера в другом — разные
/// числа, и сравнивать их между запусками, не зная сервера, нельзя.
/// </para>
/// <para>
/// Меряется то, что дошло до прикладного уровня: это меньше скорости канала на величину
/// заголовков и повторных передач. Продукт говорит это прямо и не выдаёт одно за другое.
/// </para>
/// </remarks>
public sealed class SpeedtestProbe : IProbe, IDisposable
{
    /// <summary>Имя ряда приёма.</summary>
    private const string DownloadSeries = "приём";

    /// <summary>Имя ряда отдачи.</summary>
    private const string UploadSeries = "отдача";

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Throughput,
        Shape = ProbeResultShape.ComparedSeries,
        SeriesNoun = "Направление",

        // Приём и отдача — не взаимозаменяемые варианты, а две стороны одного канала.
        // «Быстрее всех» между ними — не вывод: у домашних каналов отдача уже приёма
        // по устройству тарифа, и объявлять это открытием незачем.
        SeriesAreAlternatives = false,
        Name = "speedtest",
        Title = "Скорость наружу",
        Description = "Приём и отдача до публичного сервера M-Lab по протоколу NDT7. Бэкенд показывается всегда.",
        Unit = MeasurementUnit.MegabitsPerSecond,
        Methodology = Methodology.Ndt7,
        RequiresElevation = false,

        // Сервер выбирает M-Lab: у него есть данные о загрузке узлов, которых у нас нет.
        RequiresTarget = false,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "duration", Label = "Длительность фазы, с", Type = ProbeParameterType.Integer,
                DefaultValue = 10, Minimum = 3, Maximum = 60,
                Description = "Сколько длится каждая из двух фаз. Десять секунд — значение самого NDT7.",
            },
            new ProbeParameter
            {
                Name = "direction", Label = "Что мерить", Type = ProbeParameterType.Choice,
                DefaultValue = Directions.Both,
                Choices = [Directions.Both, Directions.Download, Directions.Upload],
                Description = "Обе стороны — приём и отдача подряд, двумя фазами.",
            },
        ],
    };

    public IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ProbeValidation.Validate(Descriptor, request);
    }

    public async IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(observer);

        var seconds = request.GetParameter("duration", 10);
        var what = request.GetParameter("direction", Directions.Both);

        var server = await Ndt7Client.LocateAsync(_http, cancellationToken).ConfigureAwait(false);

        observer.OnResolved(server.Machine);

        // Бэкенд называется до измерения, а не после: если прогон оборвётся,
        // оператор всё равно будет знать, до чего мерили.
        observer.OnFact(ProbeFact.Text("speedtest", "Бэкенд", "M-Lab NDT7"));
        observer.OnFact(ProbeFact.Text("speedtest", "Сервер", server.Describe()));

        var sequence = 0;
        Ndt7Sample? download = null;
        Ndt7Sample? upload = null;

        if (Directions.IsBoth(what) || Directions.IsDownload(what))
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(seconds));

            await foreach (var sample in Guarded(
                               Ndt7Client.DownloadAsync(server, limit.Token), cancellationToken)
                               .ConfigureAwait(false))
            {
                download = sample;

                // Итоговый отсчёт — средняя за фазу, а не скорость за отрезок.
                // В ряд он не идёт: смешать в одном ряду мгновенные значения
                // и среднее значит испортить и минимум, и медиану.
                if (sample.IsFinal)
                {
                    continue;
                }

                yield return new Sample
                {
                    Sequence = sequence++,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Value = sample.Mbps,
                    Status = SampleStatus.Success,
                    Label = DownloadSeries,
                };
            }
        }

        if (Directions.IsBoth(what) || Directions.IsUpload(what))
        {
            await foreach (var sample in Guarded(
                               Ndt7Client.UploadAsync(server, TimeSpan.FromSeconds(seconds), cancellationToken),
                               cancellationToken).ConfigureAwait(false))
            {
                upload = sample;

                if (sample.IsFinal)
                {
                    continue;
                }

                yield return new Sample
                {
                    Sequence = sequence++,
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Value = sample.Mbps,
                    Status = SampleStatus.Success,
                    Label = UploadSeries,
                };
            }
        }

        Report(observer, download, upload);
    }

    /// <summary>
    /// Пропускает обрыв фазы, не роняя измерение целиком.
    /// </summary>
    /// <remarks>
    /// Отдача может не состояться там, где приём прошёл: провайдеры режут исходящий
    /// трафик чаще входящего. Ронять из-за этого весь прогон значило бы потерять
    /// уже измеренный приём.
    /// </remarks>
    private static async IAsyncEnumerable<Ndt7Sample> Guarded(
        IAsyncEnumerable<Ndt7Sample> source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = source.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            Ndt7Sample current;

            try
            {
                if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    yield break;
                }

                current = enumerator.Current;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Своё же ограничение по времени — штатный конец фазы.
                yield break;
            }

            yield return current;
        }
    }

    /// <summary>Средняя за фазу: накопленные байты, делённые на длительность фазы.</summary>
    private static double Average(Ndt7Sample sample) =>
        sample.ElapsedSeconds <= 0 ? 0 : sample.Bytes * 8 / sample.ElapsedSeconds / 1_000_000.0;

    private static void Report(IProbeObserver observer, Ndt7Sample? download, Ndt7Sample? upload)
    {
        if (download is { } received)
        {
            // Считается из накопленного, а не берётся у последнего отсчёта. Последний
            // мог оказаться четвертью секунды на обрыве фазы, и его скорость — про эту
            // четверть секунды, а не про измерение.
            observer.OnFact(ProbeFact.Number("speedtest", "Приём", Average(received),
                MeasurementUnit.MegabitsPerSecond));

            observer.OnFact(ProbeFact.Text("speedtest", "Принято",
                string.Create(CultureInfo.InvariantCulture, $"{received.Bytes / (double)(1L << 20):0.0} МБ "
                    + $"за {received.ElapsedSeconds:0.0} с")));
        }

        if (upload is { } sent)
        {
            observer.OnFact(ProbeFact.Number("speedtest", "Отдача", Average(sent),
                MeasurementUnit.MegabitsPerSecond));

            observer.OnFact(ProbeFact.Text("speedtest", "Отдано",
                string.Create(CultureInfo.InvariantCulture, $"{sent.Bytes / (double)(1L << 20):0.0} МБ "
                    + $"за {sent.ElapsedSeconds:0.0} с")));
        }

        if (download is null && upload is null)
        {
            observer.OnFact(ProbeFact.Warning("speedtest", "Итог", "Ни одна фаза не дала результата."));

            return;
        }

        observer.OnFact(ProbeFact.Text("speedtest", "О числах",
            "Измерено то, что дошло до прикладного уровня: это меньше скорости канала "
            + "на заголовки и повторные передачи. Сравнивать с цифрой в договоре можно "
            + "только с этой поправкой — и только с тем же сервером."));
    }

    public void Dispose() => _http.Dispose();
}
