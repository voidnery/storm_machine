using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StormMachine.Domain.Reports;

namespace StormMachine.Reporting;

/// <summary>
/// Раздел «было / стало».
/// </summary>
/// <remarks>
/// Сравнение с эталоном — единственный способ ответить на вопрос, ради которого
/// измерения и повторяют: стало лучше или хуже. Но у него есть условие, без которого
/// ответ ничего не стоит: сравниваемое должно быть сопоставимо. Поэтому расхождения
/// условий печатаются <b>рядом с числами</b>, а не примечанием в конце: «стало на 40 %
/// быстрее» при смене Wi-Fi на кабель — не улучшение канала, а смена способа смотреть
/// на него.
/// </remarks>
internal static class BaselineSection
{
    private static readonly string[] Headers = ["метрика", "эталон", "сейчас", "изменение", "оценка"];

    public static void Compose(IContainer container, IReadOnlyList<BaselineComparison> comparisons)
    {
        container.Column(column =>
        {
            column.Spacing(8);

            column.Item().Text("Сравнение с эталоном").FontSize(12).SemiBold();

            foreach (var comparison in comparisons)
            {
                column.Item().Element(x => ComposeOne(x, comparison));
            }
        });
    }

    private static void ComposeOne(IContainer container, BaselineComparison comparison)
    {
        container.Column(column =>
        {
            column.Spacing(4);

            column.Item().Text($"{comparison.Baseline.Name} — {comparison.Baseline.Describe()}")
                .FontSize(10).SemiBold();

            // Тяжёлое расхождение условий идёт до таблицы: если читать числа нельзя,
            // сказать об этом надо раньше, чем их покажут.
            if (comparison.Mismatches.Count > 0)
            {
                column.Item().Element(x => ComposeMismatches(x, comparison));
            }

            if (comparison.Changes.Count == 0)
            {
                column.Item().Text("Ни одна метрика эталона не найдена в текущем измерении — сравнивать нечего.")
                    .FontSize(8.5f).Italic();

                return;
            }

            column.Item().Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.4f);
                    columns.RelativeColumn(1.2f);
                    columns.RelativeColumn(1.2f);
                    columns.RelativeColumn(1.2f);
                    columns.RelativeColumn(3.4f);
                });

                table.Header(header =>
                {
                    foreach (var title in Headers)
                    {
                        header.Cell().Element(RunSection.HeaderCell).Text(title).FontSize(8).SemiBold();
                    }
                });

                foreach (var change in comparison.Changes)
                {
                    var unit = comparison.Baseline.Unit;

                    table.Cell().Element(RunSection.BodyCell).Text(change.Name).FontSize(8);
                    table.Cell().Element(RunSection.BodyCell)
                        .Text(change.Before.ToString("0.###", CultureInfo.InvariantCulture)).FontSize(8);
                    table.Cell().Element(RunSection.BodyCell)
                        .Text(change.After.ToString("0.###", CultureInfo.InvariantCulture)).FontSize(8);

                    table.Cell().Element(RunSection.BodyCell)
                        .Text(change.Percent is { } percent
                            ? $"{(percent >= 0 ? "+" : string.Empty)}{percent.ToString("0.#", CultureInfo.InvariantCulture)} %"
                            : "—")
                        .FontSize(8);

                    table.Cell().Element(RunSection.BodyCell)
                        .Text(Verdict(change))
                        .FontSize(8)
                        .FontColor(change.Direction switch
                        {
                            ChangeDirection.Better => Colors.Green.Darken2,
                            ChangeDirection.Worse => Colors.Red.Darken1,
                            _ => Colors.Grey.Darken1,
                        });

                    _ = unit;
                }
            });

            column.Item().Row(row =>
            {
                row.RelativeItem().Text($"Итог: {comparison.Verdict}")
                    .FontSize(9)
                    .SemiBold()
                    .FontColor(comparison.WorseCount > 0 ? Colors.Red.Darken1 : Colors.Black);

                row.ConstantItem(220).AlignRight()
                    .Text($"эталон снят {comparison.Baseline.CapturedUtc.ToLocalTime():dd.MM.yyyy HH:mm}")
                    .FontSize(7.5f).FontColor(Colors.Grey.Darken1);
            });

            if (comparison.Missing.Count > 0)
            {
                column.Item().Text(
                        "В текущем измерении не оказалось метрик эталона: "
                        + string.Join(", ", comparison.Missing)
                        + ". Возможно, измеряли другой пробой.")
                    .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
            }

            column.Item().Text(
                    $"Изменением считается сдвиг больше {BaselineComparer.SignificantPercent.ToString("0", CultureInfo.InvariantCulture)} % "
                    + "и больше порога достоверности измерения. Меньшее — разброс, "
                    + "с которым сеть расходится сама с собой.")
                .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
        });
    }

    private static void ComposeMismatches(IContainer container, BaselineComparison comparison)
    {
        var severe = comparison.HasSevereMismatch;

        container.Border(1)
            .BorderColor(severe ? Colors.Orange.Medium : Colors.Grey.Lighten1)
            .Background(severe ? Colors.Orange.Lighten5 : Colors.Grey.Lighten5)
            .Padding(7)
            .Column(column =>
            {
                column.Spacing(1);

                column.Item().Text(severe
                        ? "Условия изменились так, что числа напрямую несопоставимы"
                        : "Условия измерения отличаются от эталонных")
                    .FontSize(8.5f).SemiBold();

                foreach (var mismatch in comparison.Mismatches)
                {
                    column.Item().Text($"· {mismatch.What}: было «{mismatch.Before}», стало «{mismatch.After}»")
                        .FontSize(8);
                }

                if (severe)
                {
                    column.Item().PaddingTop(2).Text(
                            "Сравнение показано полностью — запрещать его продукт не берётся, — "
                            + "но приписывать разницу сети без проверки этих расхождений нельзя.")
                        .FontSize(8).Italic();
                }
            });
    }

    private static string Verdict(MetricChange change) => change.Direction switch
    {
        ChangeDirection.Better => "лучше",
        ChangeDirection.Worse => "хуже",
        _ => change.Insignificance ?? "без изменений",
    };
}
