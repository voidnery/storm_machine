using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Reporting;

/// <summary>
/// Раздел документа об одном прогоне.
/// </summary>
/// <remarks>
/// Выделен из рендерера в И-15, когда шаблонов стало четыре. Прогон описывается
/// одинаково в любом из них — меняется то, что вокруг: реквизиты акта, сводка
/// для руководителя, доступность за период. Разводить четыре описания одного и того же
/// значило бы получить четыре документа, расходящихся в мелочах.
/// <para>
/// Документ обязан отвечать на два вопроса, без которых цифры бесполезны:
/// <b>по какой методике</b> измеряли и <b>в каких условиях</b>. Отчёт со ссылкой на RFC —
/// аргумент в разговоре с провайдером; отчёт без методики — просто картинка
/// (требование C-08a, docs/01-analysis.md §6).
/// </para>
/// </remarks>
internal static class RunSection
{
    private static readonly string[] SeriesHeaders =
        ["ряд", "проб", "потери", "мин", "медиана", "макс", "джиттер"];

    /// <summary>Разбор трассировки, если прогон её содержит.</summary>
    /// <remarks>
    /// Берётся из сырых сэмплов, а когда их уже удалила политика хранения —
    /// из сохранённых агрегатов, чтобы старый отчёт не терял вывод.
    /// </remarks>
    public static PathAnalysis? RouteOf(StoredRun run) =>
        run.Summary.Shape == ProbeResultShape.PathTrace
            ? run.Samples.Count > 0
                ? PathAnalysis.Compute(run.Samples, run.Summary.ResolvedAddress)
                : PathAnalysis.FromSeries(run.Series, run.Summary.ResolvedAddress)
            : null;

    // --------------------------------------------------------------- содержимое

    public static void Compose(IContainer container, StoredRun run, byte[]? chart, PathAnalysis? route)
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
                column.Item().Text(route is not null
                        ? "График не построен: маршрут пуст."
                        : "График не построен: для линии нужно хотя бы два измерения.")
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

            if (route is not null)
            {
                column.Item().Element(x => ComposeRoute(x, route, RouteAnnotations(run.Facts)));
            }
            else if (run.Series.Count > 0)
            {
                column.Item().Element(x => ComposeSeries(x, run));
            }

            var facts = VisibleFacts(run.Facts, route is not null);

            if (facts.Count > 0)
            {
                column.Item().Element(x => ComposeFacts(x, facts));
            }

            column.Item().Element(x => ComposeConditions(x, run));
        });
    }

    public static void ComposeSummary(IContainer container, StoredRun run)
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

    // ------------------------------------------------------------------ маршрут

    private static readonly string[] RouteHeaders =
        ["хоп", "узел", "проб", "потери", "мин", "медиана", "макс", "джиттер", "MOS"];

    /// <summary>
    /// Маршрут: таблица хопов и вывод о том, где начинаются потери.
    /// </summary>
    /// <remarks>
    /// Ради последнего абзаца отчёт и открывают. Таблица показывает, что измерено,
    /// а вывод отвечает на вопрос, с которым идут к провайдеру: на каком узле и в чьей
    /// сети рвётся.
    /// </remarks>
    private static void ComposeRoute(
        IContainer container,
        PathAnalysis route,
        IReadOnlyDictionary<string, string> annotations)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Маршрут").FontSize(11).SemiBold();

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.ConstantColumn(26);
                    columns.RelativeColumn(4);
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                    columns.RelativeColumn();
                });

                table.Header(header =>
                {
                    foreach (var caption in RouteHeaders)
                    {
                        header.Cell().Element(HeaderCell).Text(caption).FontSize(8).SemiBold();
                    }
                });

                foreach (var hop in route.Hops)
                {
                    ComposeHopRow(table, hop, annotations);
                }
            });

            column.Item().PaddingTop(3).Text(
                    "MOS на транзитных хопах считается по задержке и дрожанию, без потерь: "
                    + "потери на транзитном узле означают ограничение его собственных ответов, "
                    + "а не потерю проходящего трафика. Оценка — упрощённая E-модель ITU-T G.107.")
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);

            ComposeRouteChanges(column, route, annotations);
            ComposeRouteVerdict(column, route, annotations);
        });
    }

    private static void ComposeHopRow(
        TableDescriptor table,
        HopStatistics hop,
        IReadOnlyDictionary<string, string> annotations)
    {
        var stats = hop.Statistics;
        var silent = hop.IsSilent;
        var address = hop.Address ?? "*";

        table.Cell().Element(BodyCell).Text(hop.Hop.ToString(CultureInfo.InvariantCulture)).FontSize(8.5f);

        table.Cell().Element(BodyCell).Column(cell =>
        {
            cell.Item().Text(address).FontSize(8.5f);

            if (hop.IsEarlyDestination)
            {
                cell.Item().Text("цель коротким путём").FontSize(7).FontColor(Colors.Grey.Darken1);
            }

            if (Annotation(annotations, address) is { } text)
            {
                cell.Item().Text(text).FontSize(7).FontColor(Colors.Grey.Darken1);
            }

            if (hop.Addresses.Count > 1)
            {
                var others = hop.Addresses.Where(a => !string.Equals(a, address, StringComparison.Ordinal));
                cell.Item().Text($"также отвечали: {string.Join(", ", others)}")
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
            }
        });

        table.Cell().Element(BodyCell).Text(hop.Sent.ToString(CultureInfo.InvariantCulture)).FontSize(8.5f);

        // У хопа с ранним ответом цели в колонке потерь стоит прочерк: доля пакетов,
        // ушедших длинным путём, — не потери, и цифра здесь читалась бы как авария.
        var loss = table.Cell().Element(BodyCell)
            .Text(hop.IsEarlyDestination
                ? "—"
                : $"{hop.LossPercent.ToString("0", CultureInfo.InvariantCulture)} %")
            .FontSize(8.5f);

        if (!silent && !hop.IsEarlyDestination && hop.LossPercent >= PathAnalysis.SignificantLossPercent)
        {
            loss.FontColor("#B91C1C").SemiBold();
        }

        table.Cell().Element(BodyCell).Text(silent ? "—" : F(stats.MinMs)).FontSize(8.5f);
        table.Cell().Element(BodyCell).Text(silent ? "—" : F(stats.P50Ms)).FontSize(8.5f);
        table.Cell().Element(BodyCell).Text(silent ? "—" : F(stats.MaxMs)).FontSize(8.5f);
        table.Cell().Element(BodyCell).Text(silent ? "—" : F(stats.JitterRfc3550Ms)).FontSize(8.5f);
        table.Cell().Element(BodyCell).Text(
                silent || double.IsNaN(hop.Voice.Mos)
                    ? "—"
                    : hop.Voice.Mos.ToString("0.0", CultureInfo.InvariantCulture))
            .FontSize(8.5f);
    }

    private static void ComposeRouteChanges(
        ColumnDescriptor column,
        PathAnalysis route,
        IReadOnlyDictionary<string, string> annotations)
    {
        const int MaxShown = 12;

        if (route.RouteChanges.Count == 0)
        {
            return;
        }

        column.Item().PaddingTop(8).Text($"Смены маршрута: {route.RouteChanges.Count}")
            .FontSize(10).SemiBold();

        foreach (var change in route.RouteChanges.Take(MaxShown))
        {
            var to = Annotation(annotations, change.To) is { } text ? $"{change.To} ({text})" : change.To;
            column.Item().Text($"хоп {change.Hop}: {change.From} → {to}").FontSize(8.5f);
        }

        if (route.RouteChanges.Count > MaxShown)
        {
            column.Item().Text($"…и ещё {route.RouteChanges.Count - MaxShown}")
                .FontSize(8.5f).FontColor(Colors.Grey.Darken1);
        }
    }

    private static void ComposeRouteVerdict(
        ColumnDescriptor column,
        PathAnalysis route,
        IReadOnlyDictionary<string, string> annotations)
    {
        var lines = new List<string>(3);

        if (route.DestinationReached && !double.IsNaN(route.DestinationVoice.Mos))
        {
            var voice = route.DestinationVoice;
            lines.Add($"Качество до цели: {voice.Grade} "
                      + $"(MOS {voice.Mos.ToString("0.00", CultureInfo.InvariantCulture)}, "
                      + $"R {voice.RFactor.ToString("0.0", CultureInfo.InvariantCulture)}).");
        }

        if (route.DegradationPoint is { } point)
        {
            var address = point.Address ?? "неизвестный узел";
            var where = Annotation(annotations, address) is { } text ? $"{address} ({text})" : address;

            lines.Add($"Деградация начинается на хопе {point.Hop}: {where}. Потери "
                      + $"{point.LossPercent.ToString("0.0", CultureInfo.InvariantCulture)} % "
                      + "и держатся до конца маршрута.");
        }
        else if (route.DestinationReached)
        {
            lines.Add("Устойчивых потерь по маршруту нет: до цели пакеты доходят.");
        }

        if (route.SilentHops > 0)
        {
            lines.Add($"Молчащих хопов: {route.SilentHops}. Это не потери — узел может "
                      + "не отвечать на ICMP, но исправно передавать транзитный трафик.");
        }

        if (route.EarlyDestinationHops.Count > 0)
        {
            var shares = route.Hops
                .Where(h => h.IsEarlyDestination)
                .Select(h => $"{h.Hop} ({h.ShortPathPercent.ToString("0.#", CultureInfo.InvariantCulture)} %)");

            lines.Add($"Цель отвечала также с хопов {string.Join(", ", shares)} — в скобках доля пакетов, "
                      + "дошедших коротким путём. Длина пути непостоянна: обычное дело для туннелей MPLS "
                      + "без переноса TTL и балансировки по каналам. Остальные пакеты не потеряны — "
                      + "они дошли длинным путём, до конечной точки.");
        }

        if (lines.Count == 0)
        {
            return;
        }

        column.Item().PaddingTop(8).Background("#F1F5F9").Padding(8).Column(box =>
        {
            box.Spacing(2);
            box.Item().Text("Вывод").FontSize(10).SemiBold();

            foreach (var line in lines)
            {
                box.Item().Text(line).FontSize(8.5f);
            }
        });
    }

    /// <summary>Таблица «адрес → чем известен», собранная пробой в фактах категории route.</summary>
    private static Dictionary<string, string> RouteAnnotations(IReadOnlyList<ProbeFact> facts)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var fact in facts)
        {
            if (string.Equals(fact.Category, HopAnnotation.FactCategory, StringComparison.OrdinalIgnoreCase))
            {
                map[fact.Name] = fact.Value;
            }
        }

        return map;
    }

    private static string? Annotation(IReadOnlyDictionary<string, string> annotations, string address) =>
        annotations.TryGetValue(address, out var text) && text != HopAnnotation.PrivateLabel
            ? text
            : null;

    /// <summary>
    /// Факты, которые нужно показать списком.
    /// </summary>
    /// <remarks>
    /// Для трассировки категория route уже разошлась подписями под адресами хопов.
    /// Повторить её списком значило бы напечатать три десятка строк второй раз.
    /// </remarks>
    private static IReadOnlyList<ProbeFact> VisibleFacts(IReadOnlyList<ProbeFact> facts, bool isRoute) =>
        isRoute
            ? [.. facts.Where(f => !string.Equals(f.Category, HopAnnotation.FactCategory, StringComparison.OrdinalIgnoreCase))]
            : facts;

    private static void ComposeFacts(IContainer container, IReadOnlyList<ProbeFact> facts)
    {
        container.Column(column =>
        {
            column.Item().PaddingBottom(4).Text("Установленные факты").FontSize(11).SemiBold();

            foreach (var fact in facts)
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
    public static void ComposeConditions(IContainer container, StoredRun run)
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

                // Профиль окружения — часть условий, а не подпись: измерения
                // из разных мест несопоставимы, и документ обязан называть место.
                if (context.Profile is { } profile)
                {
                    Field(inner, "Профиль окружения", profile);
                }

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

    // ------------------------------------------------------------------ мелочи

    public static void Field(ColumnDescriptor column, string name, string value)
    {
        column.Item().Row(row =>
        {
            row.ConstantItem(120).Text(name).FontSize(8.5f).FontColor(Colors.Grey.Darken2);
            row.RelativeItem().Text(value).FontSize(8.5f);
        });
    }

    public static IContainer HeaderCell(IContainer container) =>
        container.BorderBottom(1).BorderColor(Colors.Grey.Medium).PaddingVertical(3);

    public static IContainer BodyCell(IContainer container) =>
        container.BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2).PaddingVertical(3);

    private static string F(double value) => Units.Number(value, MeasurementUnit.Milliseconds);

    public static string DescribeState(RunState state, int lost) => state switch
    {
        RunState.Completed when lost == 0 => "завершён без потерь",
        RunState.Completed => "завершён, есть потери",
        RunState.Cancelled => "прерван оператором",
        RunState.Abandoned => "оборван сбоем; измеренное сохранено",
        _ => "выполняется",
    };

    public static string DescribeAdapter(AdapterKind kind) => kind switch
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
