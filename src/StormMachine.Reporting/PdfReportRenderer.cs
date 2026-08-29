using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Results;

namespace StormMachine.Reporting;

/// <summary>
/// Отчёт в PDF: четыре шаблона поверх одних и тех же измерений.
/// </summary>
/// <remarks>
/// Шаблонов четыре, потому что читателей четыре, и они спрашивают разное. Технический
/// отвечает «что именно измерено»; сводка — «что это значит для дела»; акт — «работа
/// принята, вот основания»; SLA — «выполнено ли обещание за период».
/// <para>
/// Общее у всех — то, без чего цифры бесполезны: <b>методика</b> и <b>условия
/// измерения</b>. Отчёт со ссылкой на RFC — аргумент в разговоре с провайдером;
/// отчёт без методики — просто картинка (требование C-08a, docs/01-analysis.md §6).
/// </para>
/// <para>
/// Ни один шаблон не пишет вывод за оператора. Продукт показывает измеренное и вердикты
/// по заданным порогам; «сеть пригодна для эксплуатации» — утверждение, за которое
/// отвечает подписавший.
/// </para>
/// </remarks>
public sealed class PdfReportRenderer(ITopologyLayout layout) : IReportRenderer
{
    private readonly ITopologyLayout _layout = layout ?? throw new ArgumentNullException(nameof(layout));

    static PdfReportRenderer()
    {
        // Community-лицензия: покрывает проекты с открытым исходным кодом.
        // Устанавливается здесь, а не в клиенте, чтобы её нельзя было забыть,
        // подключив библиотеку в другом месте.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public string Format => "PDF";

    public Task<RenderedReport> RenderAsync(ReportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Runs.Count == 0 && request.ServiceLevel is null)
        {
            throw new InvalidOperationException(
                "Отчёт не из чего строить: нет ни прогонов, ни данных о доступности.");
        }

        var diagram = request.Topology is { IsEmpty: false } topology
            ? TopologyDiagramImage.TryRender(_layout.Arrange(topology))
            : null;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.6f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily(Fonts.Calibri));

                page.Header().Element(header => ComposeHeader(header, request));
                page.Content().Element(content => ComposeContent(content, request, diagram));
                page.Footer().Element(footer => ComposeFooter(footer, request));
            });
        });

        return Task.FromResult(new RenderedReport
        {
            Content = document.GeneratePdf(),
            FileExtension = "pdf",
            SuggestedFileName = FileName(request),
        });
    }

    // ------------------------------------------------------------------ шапка

    private static void ComposeHeader(IContainer container, ReportRequest request)
    {
        var title = request.Title ?? DefaultTitle(request.Template);
        var subject = Subject(request);
        var version = (request.Runs.Count > 0 ? request.Runs[0].Context.ProductVersion : null) ?? ProductInfo.Version;

        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(title).FontSize(17).SemiBold();

                    if (subject is not null)
                    {
                        left.Item().Text(subject).FontSize(11).FontColor(Colors.Grey.Darken2);
                    }
                });

                row.ConstantItem(150).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(ProductInfo.Name).FontSize(11).SemiBold();
                    right.Item().AlignRight().Text($"версия {version}")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });

            column.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    // --------------------------------------------------------------- содержимое

    private static void ComposeContent(IContainer container, ReportRequest request, byte[]? diagram)
    {
        container.PaddingVertical(12).Column(column =>
        {
            column.Spacing(14);

            if (request.Template == ReportTemplate.Acceptance)
            {
                column.Item().Element(x => AcceptanceSection.ComposeRequisites(x, request));
            }

            if (request.Template is ReportTemplate.Executive or ReportTemplate.Acceptance)
            {
                column.Item().Element(x => AcceptanceSection.ComposeOverview(x, request));
            }

            if (request.ServiceLevel is { } level)
            {
                column.Item().Element(x => ServiceLevelSectionRenderer.Compose(x, level));
            }

            if (request.Baselines.Count > 0)
            {
                column.Item().Element(x => BaselineSection.Compose(x, request.Baselines));
            }

            if (diagram is not null)
            {
                column.Item().Element(x => ComposeTopology(x, diagram, request.Topology?.Caveats ?? []));
            }

            ComposeRuns(column, request);

            if (request.Template is ReportTemplate.Acceptance)
            {
                column.Item().Element(x => AcceptanceSection.ComposeConclusion(x, request));
            }
        });
    }

    /// <summary>
    /// Разделы про измерения.
    /// </summary>
    /// <remarks>
    /// Разворачивает каждый прогон целиком — с графиками, рядами, фактами и условиями —
    /// только технический отчёт. Он для инженера, который разбирается.
    /// <para>
    /// Сводка, акт и SLA получают сжатую таблицу. Это не экономия места: акт со ста
    /// восемью развёрнутыми измерениями занимает сто двадцать девять страниц, и такой
    /// документ не подписывают, а подшивают не читая. Подробности при этом не теряются —
    /// каждый прогон назван, и технический отчёт по нему строится отдельной командой.
    /// </para>
    /// </remarks>
    private static void ComposeRuns(ColumnDescriptor column, ReportRequest request)
    {
        if (request.Runs.Count == 0)
        {
            return;
        }

        if (request.Template != ReportTemplate.Technical)
        {
            column.Item().Element(x => AcceptanceSection.ComposeRunTable(x, request.Runs));

            return;
        }

        foreach (var run in request.Runs)
        {
            var route = RunSection.RouteOf(run);
            var chart = request.IncludeCharts
                ? route is not null ? RouteChartImage.TryRender(route) : LatencyChartImage.TryRender(run)
                : null;

            if (request.Runs.Count > 1)
            {
                column.Item().PaddingTop(6).Text($"{run.Summary.ProbeName} → {run.Summary.TargetDisplay}")
                    .FontSize(12).SemiBold();
            }

            column.Item().Element(x => RunSection.Compose(x, run, chart, route));
        }
    }

    private static void ComposeTopology(IContainer container, byte[] diagram, IReadOnlyList<string> caveats)
    {
        container.Column(column =>
        {
            column.Item().Text("Схема сети").FontSize(12).SemiBold();

            column.Item().PaddingTop(4).Image(diagram).FitWidth();

            // Легенда обязательна: различие достоверности — главное, что карта
            // сообщает, и без объяснения три вида линий читаются как оформление.
            column.Item().PaddingTop(4).Text(
                    "Линии: сплошная — связь подтверждена измерением; штриховая — выведена "
                    + "из наблюдений; точечная — допущение. Схема показывает то, что продукт "
                    + "увидел с этой машины, а не паспортную схему сети.")
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);

            // Оговорки идут в отчёт наравне со схемой. Отчёт читают без продукта
            // под рукой, и «эти узлы в разных VLAN» — то, чего по картинке не видно
            // и о чём спросить будет некого.
            foreach (var caveat in caveats)
            {
                column.Item().PaddingTop(3).Text(caveat)
                    .FontSize(7.5f).SemiBold().FontColor(Colors.Orange.Darken2);
            }
        });
    }

    private static void ComposeFooter(IContainer container, ReportRequest request)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);

            column.Item().Row(row =>
            {
                var stamp = DateTimeOffset.Now.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
                var author = string.IsNullOrWhiteSpace(request.Author) ? string.Empty : $" · {request.Author}";

                row.RelativeItem().Text($"Сформировано {stamp}{author}")
                    .FontSize(7.5f).FontColor(Colors.Grey.Darken1);

                row.ConstantItem(80).AlignRight().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(7.5f).FontColor(Colors.Grey.Darken1));
                    text.CurrentPageNumber();
                    text.Span(" / ");
                    text.TotalPages();
                });
            });
        });
    }

    // ------------------------------------------------------------------ мелочи

    private static string DefaultTitle(ReportTemplate template) => template switch
    {
        ReportTemplate.Executive => "Сводка по результатам проверки",
        ReportTemplate.Acceptance => "Акт тестирования сети",
        ReportTemplate.ServiceLevel => "Отчёт о доступности",
        _ => "Отчёт об измерении",
    };

    private static string? Subject(ReportRequest request)
    {
        if (request.ServiceLevel is { } level)
        {
            return $"{level.Monitor.Name} → {level.Monitor.Target.DisplayName}";
        }

        return request.Runs.Count switch
        {
            0 => null,
            1 => $"{request.Runs[0].Summary.ProbeName} → {request.Runs[0].Summary.TargetDisplay}",
            var count => $"измерений: {count.ToString(CultureInfo.InvariantCulture)}",
        };
    }

    private static string FileName(ReportRequest request)
    {
        var kind = request.Template switch
        {
            ReportTemplate.Executive => "сводка",
            ReportTemplate.Acceptance => "акт",
            ReportTemplate.ServiceLevel => "sla",
            _ => request.Runs.Count == 1 ? request.Runs[0].Summary.ProbeName : "отчёт",
        };

        var moment = request.Runs.Count == 1
            ? request.Runs[0].Summary.StartedUtc.ToLocalTime()
            : DateTimeOffset.Now;

        return $"storm-{kind}-{moment:yyyyMMdd-HHmmss}.pdf";
    }
}
