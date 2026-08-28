using System.Text;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;
using StormMachine.Domain.Topology;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Reporting.UnitTests;

/// <summary>
/// Четыре шаблона отчёта.
/// </summary>
/// <remarks>
/// Проверяется не оформление — его читает человек, — а то, что документ вообще
/// собирается из положенных ему частей и что шаблоны не сваливаются в один.
/// Отдельно закреплено главное решение: развёрнутые измерения бывают только
/// в техническом отчёте. Акт со ста восемью развёрнутыми прогонами занимает
/// сто двадцать девять страниц, и такой документ не подписывают.
/// </remarks>
public sealed class ReportTemplateTests
{
    /// <summary>Раскладка, укладывающая узлы в строку: настоящая здесь не нужна.</summary>
    private sealed class SimpleLayout : ITopologyLayout
    {
        public PlacedGraph Arrange(TopologyGraph graph)
        {
            var nodes = new List<PlacedNode>();
            var x = 100.0;

            foreach (var node in graph.Nodes)
            {
                var (width, height) = PlacedGraph.SizeOf(node.Kind);

                nodes.Add(new PlacedNode(node, x, 60, width, height));
                x += width + 40;
            }

            return new PlacedGraph
            {
                Nodes = nodes,
                Links =
                [
                    .. graph.Links
                        .Select(l => new PlacedLink(l, 100, 60, x - 140, 60)),
                ],
                Width = x,
                Height = 140,
            };
        }
    }

    private static readonly DateTimeOffset Noon = new(2026, 3, 10, 12, 0, 0, TimeSpan.Zero);

    private static PdfReportRenderer Renderer() => new(new SimpleLayout());

    private static MeasurementContext Context(AdapterKind adapter = AdapterKind.Physical) => new()
    {
        InterfaceName = "Ethernet",
        AdapterKind = adapter,
        InterfaceAddress = "192.168.1.10",
        CalibrationBaselineMs = 0.2,
        ProductVersion = "0.1.0",
        Methodology = Methodology.IcmpEcho,
        StartedUtc = Noon,
    };

    private static StoredRun Run(int index = 0, AdapterKind adapter = AdapterKind.Physical)
    {
        var samples = Enumerable.Range(0, 20)
            .Select(i => new Sample
            {
                Sequence = i,
                TimestampUtc = Noon.AddSeconds(i),
                Value = 10 + (i % 5),
                Status = SampleStatus.Success,
            })
            .ToList();

        return new StoredRun
        {
            Summary = new RunSummary
            {
                Id = Guid.NewGuid(),
                Kind = ProbeKind.Icmp,
                ProbeName = "ping",
                Shape = ProbeResultShape.ScalarSeries,
                TargetDisplay = $"192.168.1.{index + 1}",
                StartedUtc = Noon.AddMinutes(index),
                CompletedUtc = Noon.AddMinutes(index).AddSeconds(20),
                State = RunState.Completed,
                SentCount = 20,
                SuccessCount = 20,
                MedianMs = 12,
                HasRawSamples = true,
            },
            Target = Target.Ip($"192.168.1.{index + 1}"),
            Context = Context(adapter),
            Unit = MeasurementUnit.Milliseconds,
            Series = [SeriesBreakdown.WholeRun(samples)],
            Facts = [ProbeFact.Text("icmp", "Замечание", "проверка шаблона")],
            Samples = samples,
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase),
        };
    }

    private static TopologyGraph Topology() => new()
    {
        Nodes =
        [
            new TopologyNode { Id = "me", Kind = TopologyNodeKind.ThisMachine, Label = "эта машина" },
            new TopologyNode { Id = "gw", Kind = TopologyNodeKind.Router, Label = "шлюз", Address = "192.168.1.1" },
        ],
        Links = [new TopologyLink("me", "gw", LinkKind.Layer2, LinkConfidence.Confirmed, "подтверждено ARP")],
    };

    private static ServiceLevelSection Level()
    {
        var monitor = new Monitor
        {
            Id = Guid.NewGuid(),
            Name = "доступность шлюза",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Schedule = Schedule.Every(TimeSpan.FromMinutes(1)),
            Objective = new ServiceLevelObjective { TargetPercent = 99.5, Window = TimeSpan.FromHours(1) },
        };

        var checks = Enumerable.Range(0, 60)
            .Select(i => new MonitorCheck
            {
                Id = Guid.NewGuid(),
                MonitorId = monitor.Id,
                StartedUtc = Noon.AddMinutes(i),
                Kind = i is 30 or 31 ? CheckKind.Missed : CheckKind.Measured,
                Level = i is 20 or 21 ? VerdictLevel.Fail : VerdictLevel.Pass,
                Summary = i is 20 or 21 ? "цель не отвечает" : "норма",
            })
            .ToList();

        return new ServiceLevelSection(
            monitor,
            AvailabilityCalculator.Compute(checks, Noon, Noon.AddHours(1), monitor.Objective),
            checks);
    }

    private static BaselineComparison Comparison()
    {
        var baseline = new Baseline
        {
            Id = Guid.NewGuid(),
            Name = "норма",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Unit = MeasurementUnit.Milliseconds,
            Context = Context(AdapterKind.Wireless),
            Metrics =
            [
                new BaselineMetric("p95", 100, HigherIsBetter: false),
                new BaselineMetric("loss", 0, HigherIsBetter: false),
            ],
            CapturedUtc = Noon.AddDays(-30),
        };

        return BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 40, ["loss"] = 0 },
            Context());
    }

    private static int PageCount(byte[] pdf) =>
        Encoding.ASCII.GetString(pdf).Split("/Type /Page").Length - 1;

    // ------------------------------------------------------------------ общее

    [Theory(DisplayName = "Каждый шаблон даёт годный PDF")]
    [InlineData(ReportTemplate.Technical)]
    [InlineData(ReportTemplate.Executive)]
    [InlineData(ReportTemplate.Acceptance)]
    public async Task EveryTemplateRenders(ReportTemplate template)
    {
        var report = await Renderer().RenderAsync(new ReportRequest
        {
            Template = template,
            Runs = [Run()],
            Author = "тест",
        });

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(report.Content, 0, 5));
        Assert.True(report.Content.Length > 2000, "Документ подозрительно мал.");
        Assert.True(PageCount(report.Content) >= 1);
    }

    [Fact(DisplayName = "Имя файла называет шаблон")]
    public async Task FileNameNamesTheTemplate()
    {
        var act = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Acceptance,
            Runs = [Run()],
        });

        Assert.StartsWith("storm-акт-", act.SuggestedFileName, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", act.SuggestedFileName, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Отчёт без содержимого не собирается")]
    public async Task EmptyRequestIsRefused()
    {
        // Пустой документ выглядел бы как «проверок не было», а на деле означает
        // «спросили не то». Разницу надо назвать, а не показать пустой лист.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Renderer().RenderAsync(new ReportRequest { Runs = [] }));

        Assert.Contains("не из чего строить", error.Message, StringComparison.Ordinal);
    }

    // -------------------------------------------------- развёрнутое и сводное

    [Fact(DisplayName = "Развёрнутые измерения бывают только в техническом отчёте")]
    public async Task OnlyTechnicalExpandsRuns()
    {
        var runs = Enumerable.Range(0, 12).Select(i => Run(i)).ToList();

        var technical = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Technical,
            Runs = runs,
        });

        var act = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Acceptance,
            Runs = runs,
        });

        // Акт со ста восемью развёрнутыми измерениями занимал сто двадцать девять
        // страниц. Такой документ не подписывают, а подшивают не читая.
        Assert.True(
            PageCount(technical.Content) > PageCount(act.Content) * 2,
            $"технический {PageCount(technical.Content)} стр., акт {PageCount(act.Content)} стр.");
    }

    // ------------------------------------------------------------- разделы

    [Fact(DisplayName = "Схема сети попадает в документ")]
    public async Task TopologyIsIncluded()
    {
        var withMap = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Acceptance,
            Runs = [Run()],
            Topology = Topology(),
        });

        var without = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Acceptance,
            Runs = [Run()],
        });

        Assert.True(
            withMap.Content.Length > without.Content.Length,
            "Схема сети не увеличила документ — похоже, её не вложили.");
    }

    [Fact(DisplayName = "Пустая карта раздела не создаёт")]
    public async Task EmptyTopologyAddsNothing()
    {
        var report = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Acceptance,
            Runs = [Run()],
            Topology = new TopologyGraph { Nodes = [], Links = [] },
        });

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(report.Content, 0, 5));
    }

    [Fact(DisplayName = "Раздел о доступности собирается без единого прогона")]
    public async Task ServiceLevelStandsAlone()
    {
        // Отчёт о мониторе — про монитор. Прогонов в нём может не быть вовсе,
        // и это не повод отказываться его строить.
        var report = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.ServiceLevel,
            Runs = [],
            ServiceLevel = Level(),
        });

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(report.Content, 0, 5));
        Assert.StartsWith("storm-sla-", report.SuggestedFileName, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Сравнение с эталоном попадает в документ")]
    public async Task BaselineSectionIsIncluded()
    {
        var withBaseline = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Executive,
            Runs = [Run()],
            Baselines = [Comparison()],
        });

        var without = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Executive,
            Runs = [Run()],
        });

        Assert.True(withBaseline.Content.Length > without.Content.Length);
    }

    [Fact(DisplayName = "Ненадёжный адаптер отмечается в акте")]
    public async Task UntrustedAdapterIsFlagged()
    {
        var flagged = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Acceptance,
            Runs = [Run(adapter: AdapterKind.Virtual)],
        });

        var clean = await Renderer().RenderAsync(new ReportRequest
        {
            Template = ReportTemplate.Acceptance,
            Runs = [Run()],
        });

        // Оговорка меняет смысл всех чисел документа и потому обязана быть в нём.
        Assert.True(
            flagged.Content.Length > clean.Content.Length,
            "Предупреждение о недостоверном адаптере в документ не попало.");
    }
}
