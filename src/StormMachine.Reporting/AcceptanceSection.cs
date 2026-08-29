using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Results;
using StormMachine.Domain.Text;

namespace StormMachine.Reporting;

/// <summary>
/// Разделы акта и сводки: реквизиты, обзор, таблица проверок, вывод.
/// </summary>
/// <remarks>
/// Акт — документ, который подписывают, и потому у него другие требования, чем
/// у технического отчёта. В нём должно быть видно <b>кто, что, где и когда</b>
/// проверял, и должно остаться место, где человек напишет вывод и распишется.
/// </remarks>
internal static class AcceptanceSection
{
    private static readonly string[] RunHeaders =
        ["измерение", "цель", "когда", "проб", "потери", "медиана", "итог"];

    public static void ComposeRequisites(IContainer container, ReportRequest request)
    {
        container.Border(1).BorderColor(Colors.Grey.Lighten1).Padding(10).Column(column =>
        {
            column.Spacing(2);

            column.Item().PaddingBottom(4).Text("Реквизиты").FontSize(11).SemiBold();

            RunSection.Field(column, "Заказчик", Or(request.Customer, "не указан"));
            RunSection.Field(column, "Объект", Or(request.Site, "не указан"));
            RunSection.Field(column, "Проверку выполнил", Or(request.Author, "не указан"));

            var period = Period(request.Runs);

            RunSection.Field(column, "Период измерений", period);
            RunSection.Field(
                column,
                "Дата документа",
                DateTimeOffset.Now.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
        });
    }

    /// <summary>
    /// Итог одним абзацем: сколько проверок, сколько с потерями, чем мерили.
    /// </summary>
    /// <remarks>
    /// Считаются факты, а не выводы. «Из семи проверок в двух были потери» — факт;
    /// «сеть работает удовлетворительно» — вывод, и его пишет человек.
    /// </remarks>
    public static void ComposeOverview(IContainer container, ReportRequest request)
    {
        var runs = request.Runs;

        container.Column(column =>
        {
            column.Item().Text("Что проверено").FontSize(12).SemiBold();

            if (runs.Count == 0)
            {
                column.Item().PaddingTop(3)
                    .Text("Отдельных измерений в документе нет — он построен на данных о доступности.")
                    .FontSize(9);

                return;
            }

            var withLoss = runs.Count(r => r.Summary.SentCount > r.Summary.SuccessCount);
            var cancelled = runs.Count(r => r.Summary.State != RunState.Completed);
            var probes = runs.Select(r => r.Summary.ProbeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var targets = runs.Select(r => r.Summary.TargetDisplay).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            var text =
                $"Выполнено измерений: {runs.Count.ToString(CultureInfo.InvariantCulture)}"
                + $" по {Targets(targets)}. Использованы пробы: {string.Join(", ", probes)}. "
                + (withLoss == 0
                    ? "Потерь не зафиксировано ни в одном измерении."
                    : $"Потери зафиксированы в {Measurements(withLoss)}.")
                + (cancelled > 0
                    ? $" Не доведено до конца: {cancelled.ToString(CultureInfo.InvariantCulture)}."
                    : string.Empty);

            column.Item().PaddingTop(3).Text(text).FontSize(9);

            var untrusted = runs.Where(r => !r.Context.IsTimingTrustworthy).ToList();

            if (untrusted.Count > 0)
            {
                // Оговорка идёт в обзор, а не в примечание мелким шрифтом: она меняет
                // смысл всех чисел документа, а не уточняет одно из них.
                column.Item().PaddingTop(5).Border(1).BorderColor(Colors.Orange.Medium)
                    .Background(Colors.Orange.Lighten5).Padding(7)
                    .Text(
                        $"Внимание: {Measurements(untrusted.Count)} выполнено через "
                        + string.Join(
                            ", ",
                            untrusted.Select(r => RunSection.DescribeAdapter(r.Context.AdapterKind))
                                .Distinct(StringComparer.Ordinal))
                        + ". Такой адаптер добавляет собственную задержку и собственный джиттер — "
                        + "абсолютным значениям доверять нельзя, сравнение между запусками остаётся в силе.")
                    .FontSize(8);
            }
        });
    }

    /// <summary>Сводная таблица измерений: строка на прогон.</summary>
    public static void ComposeRunTable(IContainer container, IReadOnlyList<StoredRun> runs)
    {
        container.Column(column =>
        {
            column.Item().Text("Измерения").FontSize(12).SemiBold();

            column.Item().PaddingTop(4).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.4f);
                    columns.RelativeColumn(2.2f);
                    columns.RelativeColumn(1.5f);
                    columns.RelativeColumn(0.8f);
                    columns.RelativeColumn(0.9f);
                    columns.RelativeColumn(1.1f);
                    columns.RelativeColumn(2.2f);
                });

                table.Header(header =>
                {
                    foreach (var title in RunHeaders)
                    {
                        header.Cell().Element(RunSection.HeaderCell).Text(title).FontSize(8).SemiBold();
                    }
                });

                foreach (var run in runs)
                {
                    var summary = run.Summary;
                    var lost = summary.SentCount - summary.SuccessCount;
                    var loss = summary.SentCount == 0 ? 0 : lost * 100.0 / summary.SentCount;

                    table.Cell().Element(RunSection.BodyCell).Text(summary.ProbeName).FontSize(8);
                    table.Cell().Element(RunSection.BodyCell).Text(summary.TargetDisplay).FontSize(8);
                    table.Cell().Element(RunSection.BodyCell)
                        .Text(summary.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture))
                        .FontSize(8);
                    table.Cell().Element(RunSection.BodyCell)
                        .Text(summary.SentCount.ToString(CultureInfo.InvariantCulture)).FontSize(8);

                    table.Cell().Element(RunSection.BodyCell)
                        .Text(loss.ToString("0.#", CultureInfo.InvariantCulture) + " %")
                        .FontSize(8)
                        .FontColor(loss > 0 ? Colors.Red.Darken1 : Colors.Black);

                    table.Cell().Element(RunSection.BodyCell)
                        .Text(summary.MedianMs is { } median
                            ? median.ToString("0.###", CultureInfo.InvariantCulture)
                            : "—")
                        .FontSize(8);

                    table.Cell().Element(RunSection.BodyCell)
                        .Text(RunSection.DescribeState(summary.State, lost)).FontSize(8);
                }
            });

            column.Item().PaddingTop(3).Text(
                    "Медиана — в единицах измерения соответствующей пробы. Подробности каждого "
                    + "измерения — в журнале продукта по идентификатору прогона.")
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
        });
    }

    /// <summary>
    /// Вывод и место подписи.
    /// </summary>
    /// <remarks>
    /// Если оператор вывода не написал, документ так и говорит — пустой строкой
    /// с пояснением. Подставить сюда «сеть в норме» на основании отсутствия потерь
    /// значило бы выдать отсутствие возражений за заключение.
    /// </remarks>
    public static void ComposeConclusion(IContainer container, ReportRequest request)
    {
        container.Column(column =>
        {
            column.Item().Text("Заключение").FontSize(12).SemiBold();

            column.Item().PaddingTop(4).MinHeight(40).Border(1).BorderColor(Colors.Grey.Lighten1).Padding(8)
                .Text(string.IsNullOrWhiteSpace(request.Conclusion)
                    ? "Заключение не заполнено. Продукт его не составляет: он показывает измеренное "
                      + "и вердикты по заданным порогам, а оценку пригодности даёт подписавший."
                    : request.Conclusion)
                .FontSize(9)
                .Italic(string.IsNullOrWhiteSpace(request.Conclusion));

            column.Item().PaddingTop(18).Row(row =>
            {
                row.RelativeItem().Column(left =>
                {
                    left.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Medium);
                    left.Item().PaddingTop(2).Text("подпись исполнителя").FontSize(7.5f)
                        .FontColor(Colors.Grey.Darken1);
                    left.Item().Text(Or(request.Author, string.Empty)).FontSize(8.5f);
                });

                row.ConstantItem(40);

                row.RelativeItem().Column(right =>
                {
                    right.Item().LineHorizontal(0.7f).LineColor(Colors.Grey.Medium);
                    right.Item().PaddingTop(2).Text("подпись заказчика").FontSize(7.5f)
                        .FontColor(Colors.Grey.Darken1);
                    right.Item().Text(Or(request.Customer, string.Empty)).FontSize(8.5f);
                });
            });
        });
    }

    private static string Period(IReadOnlyList<StoredRun> runs)
    {
        if (runs.Count == 0)
        {
            return "не указан";
        }

        var from = runs.Min(r => r.Summary.StartedUtc).ToLocalTime();
        var to = runs.Max(r => r.Summary.StartedUtc).ToLocalTime();

        return from.Date == to.Date
            ? $"{from:dd.MM.yyyy}, с {from:HH:mm} по {to:HH:mm}"
            : $"с {from:dd.MM.yyyy HH:mm} по {to:dd.MM.yyyy HH:mm}";
    }

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Targets(int count) => count switch
    {
        1 => "одной цели",
        _ => $"{count.ToString(CultureInfo.InvariantCulture)} целям",
    };

    private static string Measurements(int count) =>
        Plural.With(count, "измерении", "измерениях", "измерениях");
}
