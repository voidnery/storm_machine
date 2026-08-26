using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Reporting;

/// <summary>
/// Отчёт об измерении в PDF.
/// </summary>
/// <remarks>
/// Документ обязан отвечать на два вопроса, без которых цифры бесполезны:
/// <b>по какой методике</b> измеряли и <b>в каких условиях</b>. Отчёт со ссылкой на RFC —
/// аргумент в разговоре с провайдером; отчёт без методики — просто картинка
/// (требование C-08a, docs/01-analysis.md §6).
/// </remarks>
public sealed class PdfReportRenderer : IReportRenderer
{
    private static readonly string[] SeriesHeaders =
        ["ряд", "проб", "потери", "мин", "медиана", "макс", "джиттер"];

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

        var run = request.Run;
        var chart = request.IncludeChart ? LatencyChartImage.TryRender(run) : null;

        var document = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(1.6f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontSize(9).FontFamily(Fonts.Calibri));

                page.Header().Element(header => ComposeHeader(header, request));
                page.Content().Element(content => ComposeContent(content, run, chart));
                page.Footer().Element(footer => ComposeFooter(footer, request));
            });
        });

        var bytes = document.GeneratePdf();

        var name = $"storm-{run.Summary.ProbeName}-{run.Summary.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}.pdf";

        return Task.FromResult(new RenderedReport
        {
            Content = bytes,
            FileExtension = "pdf",
            SuggestedFileName = name,
        });
    }

    // ------------------------------------------------------------------ шапка

    private static void ComposeHeader(IContainer container, ReportRequest request)
    {
        var run = request.Run;
        var title = request.Title ?? "Отчёт об измерении";

        container.Column(column =>
        {
            column.Item().Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().Text(title).FontSize(17).SemiBold();
                    left.Item().Text($"{run.Summary.ProbeName} → {run.Summary.TargetDisplay}")
                        .FontSize(11).FontColor(Colors.Grey.Darken2);
                });

                row.ConstantItem(150).AlignRight().Column(right =>
                {
                    right.Item().AlignRight().Text(ProductInfo.Name).FontSize(11).SemiBold();
                    right.Item().AlignRight().Text($"версия {run.Context.ProductVersion}")
                        .FontSize(8).FontColor(Colors.Grey.Darken1);
                });
            });

            column.Item().PaddingTop(6).LineHorizontal(1).LineColor(Colors.Grey.Lighten1);
        });
    }

    // --------------------------------------------------------------- содержимое

    private static void ComposeContent(IContainer container, StoredRun run, byte[]? chart)
    {
        container.PaddingVertical(12).Column(column =>
        {
            column.Spacing(14);

            column.Item().Element(x => ComposeSummary(x, run));

            if (run.Context.TimingWarning is { } warning)
            {
                column.Item().Element(x => ComposeWarning(x, warning));
            }

            if (chart is not null)
            {
                column.Item().Image(chart).FitWidth();
            }
            else if (run.Summary.HasRawSamples)
            {
                column.Item().Text("График не построен: для линии нужно хотя бы два измерения.")
                    .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
            }
            else
            {
                // «Подробности состарились» и «измерений не было» — разные вещи,
                // и отчёт обязан их различать.
                column.Item().Text(
                        "Сырые измерения удалены политикой хранения — график не строится. "
                        + "Агрегаты ниже сохранены полностью.")
                    .FontSize(8).Italic().FontColor(Colors.Grey.Darken1);
            }

            if (run.Series.Count > 0)
            {
                column.Item().Element(x => ComposeSeries(x, run));
            }

            if (run.Facts.Count > 0)
            {
                column.Item().Element(x => ComposeFacts(x, run));
            }

            column.Item().Element(x => ComposeConditions(x, run));
        });
    }

    private static void ComposeSummary(IContainer container, StoredRun run)
    {
        var summary = run.Summary;

        container.Background(Colors.Grey.Lighten4).Padding(10).Row(row =>
        {
            row.RelativeItem().Column(left =>
            {
                left.Spacing(2);
                Field(left, "Начало", summary.StartedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture));

                if (summary.Duration is { } duration)
                {
                    Field(left, "Длительность", $"{duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} с");
                }

                Field(left, "Состояние", DescribeState(summary.State, summary.LostCount));

                if (summary.ResolvedAddress is { } resolved)
                {
                    Field(left, "Адрес", resolved);
                }
            });

            row.RelativeItem().Column(right =>
            {
                right.Spacing(2);
                Field(right, "Отправлено", summary.SentCount.ToString(CultureInfo.InvariantCulture));
                Field(right, "Получено", summary.SuccessCount.ToString(CultureInfo.InvariantCulture));
                Field(right, "Потери", $"{summary.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)} %");

                if (summary.MedianMs is { } median)
                {
                    Field(right, "Медиана", $"{median.ToString("0.000", CultureInfo.InvariantCulture)} мс");
                }
            });
        });
    }

    private static void ComposeWarning(IContainer container, string warning)
    {
        container
            .Background("#FFF7E6")
            .BorderLeft(3)
            .BorderColor("#D97706")
            .Padding(8)
            .Text(warning)
            .FontSize(8.5f)
            .FontColor("#92400E");
    }

    private static void ComposeSeries(IContainer container, StoredRun run)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Измерения").FontSize(11).SemiBold();

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(3);
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                });

                table.Header(header =>
                {
                    foreach (var caption in SeriesHeaders)
                    {
                        header.Cell().Element(HeaderCell).Text(caption).FontSize(8).SemiBold();
                    }
                });

                foreach (var series in run.Series)
                {
                    var stats = series.Statistics;
                    var empty = stats.SampleCount == 0;

                    table.Cell().Element(BodyCell).Text(series.Label).FontSize(8.5f);
                    table.Cell().Element(BodyCell).Text(series.SentCount.ToString(CultureInfo.InvariantCulture)).FontSize(8.5f);
                    table.Cell().Element(BodyCell).Text($"{series.LossPercent.ToString("0", CultureInfo.InvariantCulture)} %").FontSize(8.5f);
                    table.Cell().Element(BodyCell).Text(empty ? "—" : F(stats.MinMs)).FontSize(8.5f);
                    table.Cell().Element(BodyCell).Text(empty ? "—" : F(stats.P50Ms)).FontSize(8.5f);
                    table.Cell().Element(BodyCell).Text(empty ? "—" : F(stats.MaxMs)).FontSize(8.5f);
                    table.Cell().Element(BodyCell).Text(empty ? "—" : F(stats.JitterRfc3550Ms)).FontSize(8.5f);
                }
            });

            column.Item().PaddingTop(3).Text(
                    "Джиттер вычисляется по RFC 3550 §6.4.1 и не является стандартным отклонением.")
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
        });
    }

    private static void ComposeFacts(IContainer container, StoredRun run)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Установленные факты").FontSize(11).SemiBold();

            foreach (var fact in run.Facts)
            {
                column.Item().Row(row =>
                {
                    row.ConstantItem(150).Text(fact.Name).FontSize(8.5f).FontColor(Colors.Grey.Darken2);

                    var value = row.RelativeItem().Text(fact.Value).FontSize(8.5f);

                    if (fact.IsWarning)
                    {
                        value.FontColor("#92400E").SemiBold();
                    }
                });
            }
        });
    }

    /// <summary>
    /// Методика и условия измерения.
    /// </summary>
    /// <remarks>
    /// Обязательная часть документа. Без указания интерфейса, порога достоверности
    /// и версии продукта два отчёта, снятых в разное время, несопоставимы — а сравнение
    /// с прошлым и есть то, ради чего отчёт делается.
    /// </remarks>
    private static void ComposeConditions(IContainer container, StoredRun run)
    {
        var context = run.Context;

        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Методика и условия измерения").FontSize(11).SemiBold();

            column.Item().Background(Colors.Grey.Lighten4).Padding(10).Column(inner =>
            {
                inner.Spacing(2);

                Field(inner, "Методика", context.Methodology.ToString());

                if (context.Methodology.Url is { } url)
                {
                    Field(inner, "Источник", url);
                }

                Field(inner, "Интерфейс", $"{context.InterfaceName} ({DescribeAdapter(context.AdapterKind)})");

                if (context.InterfaceAddress is { } address)
                {
                    Field(inner, "Адрес интерфейса", address);
                }

                Field(inner, "Порог достоверности",
                    $"{context.CalibrationBaselineMs.ToString("0.000", CultureInfo.InvariantCulture)} мс — "
                    + "значения ниже неотличимы от собственной работы измерительного стека");

                Field(inner, "Версия продукта", context.ProductVersion);

                if (run.Parameters.Count > 0)
                {
                    Field(inner, "Параметры пробы", string.Join(", ",
                        run.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal)
                            .Select(p => $"{p.Key}={p.Value ?? "—"}")));
                }
            });
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

    private static void Field(ColumnDescriptor column, string name, string value)
    {
        column.Item().Row(row =>
        {
            row.ConstantItem(120).Text(name).FontSize(8.5f).FontColor(Colors.Grey.Darken2);
            row.RelativeItem().Text(value).FontSize(8.5f);
        });
    }

    private static IContainer HeaderCell(IContainer container) =>
        container.BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(3);

    private static IContainer BodyCell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3);

    private static string F(double value) => value.ToString("0.000", CultureInfo.InvariantCulture);

    private static string DescribeState(RunState state, int lost) => state switch
    {
        RunState.Completed when lost == 0 => "завершён без потерь",
        RunState.Completed => "завершён, есть потери",
        RunState.Cancelled => "прерван оператором",
        RunState.Abandoned => "оборван сбоем; измеренное сохранено",
        _ => "выполняется",
    };

    private static string DescribeAdapter(AdapterKind kind) => kind switch
    {
        AdapterKind.Physical => "физический",
        AdapterKind.Wireless => "беспроводной",
        AdapterKind.Virtual => "виртуальный коммутатор",
        AdapterKind.Vpn => "VPN",
        AdapterKind.Tunnel => "туннель",
        AdapterKind.Loopback => "loopback",
        _ => "тип не определён",
    };
}
