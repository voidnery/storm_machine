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
/// Задержка под нагрузкой: bufferbloat.
/// </summary>
/// <remarks>
/// Отвечает на вопрос, которого не задаёт ни одна другая проба: что станет с разговором,
/// когда по тому же каналу кто-то начнёт качать. Канал может выдавать полную скорость
/// и быть непригодным для телефонии — пакет голоса встаёт в очередь за чужой загрузкой.
/// Скорость этого не покажет никогда, а холостой ping — тем более.
/// <para>
/// Меряется <b>прирост</b>, поэтому фазы две и вторая бессмысленна без первой. Холостая
/// задержка сама по себе — это расстояние до собеседника, а не свойство очередей.
/// </para>
/// <para>
/// Задержку меряет обычная проба ICMP, взятая из реестра, а не своя. Так число получается
/// тем же, что покажет <c>storm ping</c> до того же узла, и оператору не приходится
/// объяснять, почему две команды продукта расходятся.
/// </para>
/// </remarks>
/// <param name="registry">
/// Реестр проб — отложенно. Зависимость здесь настоящая и круговая: реестр собирается
/// из всех проб, а эта проба просит реестр. Разрывается она откладыванием, а не хитростью
/// с регистрацией: реестр нужен не при сборке, а при запуске измерения, и к этому моменту
/// он уже готов.
/// </param>
public sealed class BufferbloatProbe(AgentDirectory directory, Lazy<IProbeRegistry> registry) : IProbe
{
    /// <summary>Сколько ждать разгона нагрузки, прежде чем мерить задержку под ней.</summary>
    private static readonly TimeSpan LoadRampUp = TimeSpan.FromSeconds(2);

    private readonly AgentDirectory _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    private readonly Lazy<IProbeRegistry> _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public ProbeDescriptor Descriptor { get; } = new()
    {
        Kind = ProbeKind.Throughput,
        Shape = ProbeResultShape.ComparedSeries,
        SeriesNoun = "Фаза",
        SeriesAreAlternatives = false,
        Name = "bufferbloat",
        Title = "Задержка под нагрузкой",
        Description = "Насколько вырастает задержка, когда канал загружен. Оценка A–F по приросту.",
        Unit = MeasurementUnit.Milliseconds,
        Methodology = Methodology.InterarrivalJitter,
        RequiresElevation = false,
        RequiresAgent = true,
        Parameters =
        [
            new ProbeParameter
            {
                Name = "idle", Label = "Холостая фаза, с", Type = ProbeParameterType.Integer,
                DefaultValue = 5, Minimum = 2, Maximum = 60,
                Description = "Сколько мерить задержку без нагрузки. Без этой фазы прирост не с чем считать.",
            },
            new ProbeParameter
            {
                Name = "loaded", Label = "Фаза под нагрузкой, с", Type = ProbeParameterType.Integer,
                DefaultValue = 10, Minimum = 3, Maximum = 120,
                Description = "Сколько мерить задержку при загруженном канале.",
            },
            new ProbeParameter
            {
                Name = "interval", Label = "Интервал ping, мс", Type = ProbeParameterType.Duration,
                DefaultValue = 50, Minimum = 20, Maximum = 1000,
                Description = "Чаще — больше выборка под всплесками, ради которых измерение и делается.",
            },
            new ProbeParameter
            {
                Name = "streams", Label = "Потоков нагрузки", Type = ProbeParameterType.Integer,
                DefaultValue = 4, Minimum = 1, Maximum = 64,
                Description = "Один поток может не наполнить канал, а незаполненный канал очередей не покажет.",
            },
            new ProbeParameter
            {
                Name = "direction", Label = "Чем грузить", Type = ProbeParameterType.Choice,
                DefaultValue = "upload",
                Choices = ["upload", "download"],
                Description = "Очереди в двух направлениях разные: у домашних каналов раздача обычно хуже.",
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

        if (!_registry.Value.TryGet("ping", out _))
        {
            errors.Add(new ProbeValidationError(
                "target",
                "Проба ping не зарегистрирована, а задержку меряет она."));
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

        if (!_registry.Value.TryGet("ping", out var ping))
        {
            throw new InvalidOperationException("Проба ping не зарегистрирована, а задержку меряет она.");
        }

        var idleSeconds = request.GetParameter("idle", 5);
        var loadedSeconds = request.GetParameter("loaded", 10);
        var intervalMs = request.GetParameter("interval", 50);
        var upload = !request.GetParameter("direction", "upload")
            .Equals("download", StringComparison.OrdinalIgnoreCase);

        var address = agent.Address ?? throw new InvalidOperationException(
            $"У агента «{agent.DisplayName}» не записан адрес — до него нечем мерить задержку.");

        observer.OnResolved(address);
        observer.OnFact(ProbeFact.Text("agent", "Агент", $"{agent.DisplayName} ({agent.Product})"));
        observer.OnFact(ProbeFact.Text("bufferbloat", "Чем грузим", upload ? "отдача" : "приём"));

        var sequence = 0;
        var idle = new List<Sample>();
        var loaded = new List<Sample>();

        // Холостая фаза. Без неё прирост не с чем считать: задержка под нагрузкой
        // сама по себе — это расстояние до собеседника, а не глубина очередей.
        await foreach (var sample in MeasureAsync(
                           ping, address, idleSeconds, intervalMs,
                           BufferbloatAssessment.IdleSeries, cancellationToken).ConfigureAwait(false))
        {
            var tagged = sample with { Sequence = sequence++, Label = BufferbloatAssessment.IdleSeries };
            idle.Add(tagged);

            yield return tagged;
        }

        var load = new TestRequest
        {
            Id = Guid.NewGuid(),
            Kind = TestKind.TcpThroughput,
            Streams = request.GetParameter("streams", 4),

            // Нагрузка живёт дольше измерения: разгон и остывание не должны попасть
            // в фазу, где мерится задержка.
            DurationSeconds = loadedSeconds + (int)LoadRampUp.TotalSeconds + 2,
            WarmupSeconds = 0,
            Direction = upload ? TestDirection.Upload : TestDirection.Download,
        };

        var achieved = 0.0;
        using var stopLoad = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var loading = Task.Run(async () =>
        {
            using var session = await _directory.OpenAsync(agent, null, stopLoad.Token).ConfigureAwait(false);

            var result = await TestConductor
                .RequestAsync(session, load, null, stopLoad.Token)
                .ConfigureAwait(false);

            achieved = result.Mbps;
        }, stopLoad.Token);

        try
        {
            // Разгон отбрасывается: первые секунды очереди ещё пустые, и задержка
            // в них показала бы канал лучше, чем он есть.
            await Task.Delay(LoadRampUp, cancellationToken).ConfigureAwait(false);

            await foreach (var sample in MeasureAsync(
                               ping, address, loadedSeconds, intervalMs,
                               BufferbloatAssessment.LoadedSeries, cancellationToken).ConfigureAwait(false))
            {
                var tagged = sample with { Sequence = sequence++, Label = BufferbloatAssessment.LoadedSeries };
                loaded.Add(tagged);

                yield return tagged;
            }
        }
        finally
        {
            await stopLoad.CancelAsync().ConfigureAwait(false);

            try
            {
                await loading.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or AgentException or InvalidOperationException)
            {
                // Нагрузка оборвалась — измеренное до обрыва остаётся, а честность
                // результата обеспечивает проверка ниже.
            }
        }

        Report(observer, idle, loaded, upload ? "отдача" : "приём", achieved);
    }

    /// <summary>Гоняет обычную пробу ICMP отведённое время.</summary>
    private static async IAsyncEnumerable<Sample> MeasureAsync(
        IProbe ping,
        string address,
        int seconds,
        int intervalMs,
        string phase,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var count = Math.Max(1, seconds * 1000 / Math.Max(1, intervalMs));

        var request = new ProbeRequest
        {
            Target = Target.Parse(address),
            Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = count,
                ["interval"] = intervalMs,
            },
        };

        await foreach (var sample in ping
                           .ExecuteAsync(request, NullProbeObserver.Instance, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return sample;
        }
    }

    /// <summary>
    /// Итог: прирост, буква и то, что она означает.
    /// </summary>
    /// <remarks>
    /// Буква идёт вместе с числом всегда. Шкалу A–F знает мало кто, а спорить
    /// с провайдером приходится числами — и число здесь первично.
    /// </remarks>
    private static void Report(
        IProbeObserver observer,
        List<Sample> idle,
        List<Sample> loaded,
        string direction,
        double achievedMbps)
    {
        var assessment = new BufferbloatAssessment
        {
            Idle = LatencyStatistics.Compute(idle),
            Loaded = LatencyStatistics.Compute(loaded),
            Direction = direction,
            LoadMbps = achievedMbps,
        };

        if (assessment.Grade == BufferbloatGrade.Unknown)
        {
            observer.OnFact(ProbeFact.Warning(
                "bufferbloat",
                "Оценка",
                BufferbloatAssessment.Explain(BufferbloatGrade.Unknown)));

            return;
        }

        var letter = BufferbloatAssessment.GradeLetter(assessment.Grade);

        observer.OnFact(new ProbeFact
        {
            Category = "bufferbloat",
            Name = "Оценка",
            Value = letter,
            IsWarning = assessment.Grade >= BufferbloatGrade.B,
        });

        observer.OnFact(new ProbeFact
        {
            Category = "bufferbloat",
            Name = "Прирост задержки",
            Value = assessment.IncreaseMs.ToString("0.0", CultureInfo.InvariantCulture),
            Numeric = assessment.IncreaseMs,
            Unit = MeasurementUnit.Milliseconds,
            IsWarning = assessment.Grade >= BufferbloatGrade.B,
        });

        observer.OnFact(ProbeFact.Number("bufferbloat", "p95 без нагрузки", assessment.Idle.P95Ms,
            MeasurementUnit.Milliseconds));

        observer.OnFact(ProbeFact.Number("bufferbloat", "p95 под нагрузкой", assessment.Loaded.P95Ms,
            MeasurementUnit.Milliseconds));

        if (achievedMbps > 0)
        {
            observer.OnFact(ProbeFact.Number("bufferbloat", "Скорость нагрузки", achievedMbps,
                MeasurementUnit.MegabitsPerSecond));
        }
        else
        {
            // Незаполненный канал очередей не показывает. Промолчать об этом значило бы
            // выдать «A+» за свойство канала, хотя это свойство неудавшейся нагрузки.
            observer.OnFact(ProbeFact.Warning(
                "bufferbloat",
                "Нагрузка",
                "Нагрузить канал не удалось. Оценка ниже относится к ненагруженному каналу "
                + "и о его очередях не говорит ничего."));
        }

        observer.OnFact(ProbeFact.Text("bufferbloat", "Что это значит",
            BufferbloatAssessment.Explain(assessment.Grade)));

        observer.OnFact(ProbeFact.Text("bufferbloat", "О шкале", BufferbloatAssessment.GradeSource));
    }
}
