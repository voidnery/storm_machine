using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;

namespace StormMachine.Reporting;

/// <summary>
/// Раздел о доступности.
/// </summary>
/// <remarks>
/// Главная забота — не дать прочитать числа выгоднее, чем есть. Доступность идёт
/// вместе с <b>покрытием</b>, простой — вместе с точностью его границ, цель — вместе
/// с остатком бюджета ошибок. Цифра «99.8 %» без этого окружения сообщает уверенность,
/// которой у измерения нет, а в документе, который показывают провайдеру, это уже
/// не мелочь.
/// </remarks>
internal static class ServiceLevelSectionRenderer
{
    /// <summary>Сколько делений в полосе состояния.</summary>
    /// <remarks>
    /// Полоса показывает период целиком, поэтому делений фиксированное число,
    /// а не «по проверке»: иначе сутки с проверкой раз в минуту и сутки с проверкой
    /// раз в час выглядели бы полосами разной длины при одном и том же периоде.
    /// </remarks>
    private const int Slots = 60;

    private static readonly string[] IncidentHeaders =
        ["начало", "длительность", "проверок", "что показала проверка"];

    public static void Compose(IContainer container, ServiceLevelSection section)
    {
        var availability = section.Availability;

        container.Column(column =>
        {
            column.Spacing(6);

            column.Item().Text("Доступность за период").FontSize(12).SemiBold();

            column.Item().Text(
                    $"{section.Monitor.Name} — {section.Monitor.Subject} → {section.Monitor.Target.DisplayName}, "
                    + $"{section.Monitor.Schedule.Describe()}")
                .FontSize(9).FontColor(Colors.Grey.Darken2);

            if (availability.Total == 0)
            {
                column.Item().Border(1).BorderColor(Colors.Orange.Medium)
                    .Background(Colors.Orange.Lighten5).Padding(8)
                    .Text("За этот период не было ни одного наблюдения. Числа считать не из чего — "
                          + "это не «100 %», а отсутствие данных.")
                    .FontSize(9);

                return;
            }

            column.Item().Element(x => ComposeNumbers(x, availability));
            column.Item().Element(x => ComposeStrip(x, section));

            if (availability.Incidents.Count > 0)
            {
                column.Item().Element(x => ComposeIncidents(x, availability));
            }

            if (availability.Objective is { } objective)
            {
                column.Item().Element(x => ComposeObjective(x, availability, objective));
            }

            column.Item().Element(ComposeMethod);
        });
    }

    private static void ComposeNumbers(IContainer container, Availability availability)
    {
        container.Row(row =>
        {
            row.RelativeItem().Column(column =>
            {
                column.Spacing(2);

                RunSection.Field(column, "Доступность", $"{Percent(availability.UptimePercent)} "
                                                        + $"от наблюдавшегося времени ({Schedule.Elapsed(availability.Observed)})");

                // Покрытие идёт сразу за доступностью: это первое, что делает её
                // осмысленной или бессмысленной.
                RunSection.Field(column, "Покрытие окна", Percent(availability.Coverage * 100));

                RunSection.Field(
                    column,
                    "Простой",
                    Schedule.Elapsed(availability.Down)
                    + (availability.Resolution > TimeSpan.Zero
                        ? $" (± {Schedule.Elapsed(availability.Resolution)})"
                        : string.Empty));

                RunSection.Field(
                    column,
                    "Проверок",
                    $"{availability.Total.ToString(CultureInfo.InvariantCulture)} "
                    + $"(норма {availability.Ok.ToString(CultureInfo.InvariantCulture)}, "
                    + $"предупреждений {availability.Warn.ToString(CultureInfo.InvariantCulture)}, "
                    + $"отказов {availability.Fail.ToString(CultureInfo.InvariantCulture)})");
            });

            row.ConstantItem(20);

            row.RelativeItem().Column(column =>
            {
                column.Spacing(2);

                RunSection.Field(column, "Инцидентов", availability.Incidents.Count.ToString(CultureInfo.InvariantCulture));

                RunSection.Field(
                    column,
                    "Восстановление",
                    availability.MeanTimeToRecovery is { } mttr
                        ? $"{Schedule.Elapsed(mttr)} в среднем"
                        : "завершённых инцидентов не было");

                RunSection.Field(
                    column,
                    "Наработка",
                    availability.MeanTimeBetweenFailures is { } mtbf
                        ? $"{Schedule.Elapsed(mtbf)} между отказами"
                        : "отказов не было");

                if (availability.Maintenance > TimeSpan.Zero)
                {
                    RunSection.Field(column, "Обслуживание", $"{Schedule.Elapsed(availability.Maintenance)} — исключено");
                }

                if (availability.Unobserved > TimeSpan.Zero)
                {
                    RunSection.Field(column, "Не наблюдали", Schedule.Elapsed(availability.Unobserved));
                }
            });
        });
    }

    /// <summary>
    /// Полоса состояния: период слева направо, деление — отрезок времени.
    /// </summary>
    /// <remarks>
    /// Три состояния и <b>четвёртое серое</b>: время, которое никто не наблюдал.
    /// Закрасить его зелёным было бы удобнее и было бы враньём — полоса тогда
    /// показывала бы работу там, где о сети не известно ничего.
    /// </remarks>
    private static void ComposeStrip(IContainer container, ServiceLevelSection section)
    {
        var availability = section.Availability;
        var span = availability.ToUtc - availability.FromUtc;

        if (span <= TimeSpan.Zero)
        {
            return;
        }

        var slots = new VerdictLevel?[Slots];
        var kinds = new CheckKind[Slots];
        var ordered = section.Checks.OrderBy(c => c.StartedUtc).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var check = ordered[i];
            var until = i + 1 < ordered.Count ? ordered[i + 1].StartedUtc : availability.ToUtc;

            var from = (int)Math.Clamp((check.StartedUtc - availability.FromUtc) / span * Slots, 0, Slots - 1);
            var to = (int)Math.Clamp((until - availability.FromUtc) / span * Slots, 0, Slots - 1);

            for (var slot = from; slot <= to; slot++)
            {
                // Худшее в делении побеждает: отказ на десять минут внутри часа
                // не должен исчезнуть под соседними удачными проверками.
                if (check.Kind != CheckKind.Measured)
                {
                    if (slots[slot] is null)
                    {
                        kinds[slot] = check.Kind;
                    }

                    continue;
                }

                slots[slot] = slots[slot] is { } current && current > check.Level ? current : check.Level;
                kinds[slot] = CheckKind.Measured;
            }
        }

        container.Column(column =>
        {
            column.Item().PaddingTop(4).Height(16).Row(row =>
            {
                for (var i = 0; i < Slots; i++)
                {
                    row.RelativeItem().PaddingRight(0.6f).Background(Colour(slots[i], kinds[i]));
                }
            });

            column.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text(availability.FromUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture))
                    .FontSize(7).FontColor(Colors.Grey.Darken1);

                row.RelativeItem().AlignRight()
                    .Text(availability.ToUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture))
                    .FontSize(7).FontColor(Colors.Grey.Darken1);
            });

            column.Item().PaddingTop(2).Text(
                    "Полоса: зелёное — норма, жёлтое — предупреждение, красное — отказ, "
                    + "серое — время, которое продукт не наблюдал, светло-серое — обслуживание.")
                .FontSize(7).Italic().FontColor(Colors.Grey.Darken1);
        });
    }

    private static string Colour(VerdictLevel? level, CheckKind kind) => kind switch
    {
        CheckKind.Maintenance => Colors.Grey.Lighten2,
        CheckKind.Missed => Colors.Grey.Medium,
        _ => level switch
        {
            VerdictLevel.Pass => Colors.Green.Medium,
            VerdictLevel.Warn => Colors.Orange.Medium,
            VerdictLevel.Fail => Colors.Red.Medium,
            _ => Colors.Grey.Medium,
        },
    };

    private static void ComposeIncidents(IContainer container, Availability availability)
    {
        container.Column(column =>
        {
            column.Item().PaddingTop(4).Text("Инциденты").FontSize(10).SemiBold();

            column.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(columns =>
                {
                    columns.RelativeColumn(1.6f);
                    columns.RelativeColumn(1.2f);
                    columns.RelativeColumn(0.8f);
                    columns.RelativeColumn(4f);
                });

                table.Header(header =>
                {
                    foreach (var title in IncidentHeaders)
                    {
                        header.Cell().Element(RunSection.HeaderCell).Text(title).FontSize(8).SemiBold();
                    }
                });

                foreach (var incident in availability.Incidents.Take(20))
                {
                    table.Cell().Element(RunSection.BodyCell)
                        .Text(incident.StartedUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture))
                        .FontSize(8);

                    table.Cell().Element(RunSection.BodyCell)
                        .Text(incident.IsOpen ? "идёт" : Schedule.Elapsed(incident.Duration))
                        .FontSize(8);

                    table.Cell().Element(RunSection.BodyCell)
                        .Text(incident.Checks.ToString(CultureInfo.InvariantCulture)).FontSize(8);

                    table.Cell().Element(RunSection.BodyCell).Text(incident.Summary).FontSize(8);
                }
            });

            if (availability.Incidents.Count > 20)
            {
                column.Item().PaddingTop(2)
                    .Text($"Показаны первые 20 из {availability.Incidents.Count.ToString(CultureInfo.InvariantCulture)}.")
                    .FontSize(7.5f).Italic().FontColor(Colors.Grey.Darken1);
            }
        });
    }

    private static void ComposeObjective(
        IContainer container,
        Availability availability,
        ServiceLevelObjective objective)
    {
        var met = availability.IsMet;

        container.Border(1)
            .BorderColor(met == false ? Colors.Red.Medium : Colors.Grey.Lighten1)
            .Background(met == false ? Colors.Red.Lighten5 : Colors.Grey.Lighten5)
            .Padding(8)
            .Column(column =>
            {
                column.Spacing(2);

                column.Item().Text("Цель по доступности").FontSize(10).SemiBold();

                RunSection.Field(column, "Цель", objective.Describe());
                RunSection.Field(column, "Итог", met switch
                {
                    true => "выполняется",
                    false => "НАРУШЕНА",
                    _ => "оценить не по чему",
                });

                if (availability.ErrorBudget is { } budget)
                {
                    RunSection.Field(
                        column,
                        "Бюджет ошибок",
                        $"{Schedule.Elapsed(budget)} допустимо, израсходовано "
                        + $"{Percent(availability.ErrorBudgetUsedPercent ?? 0)}, осталось "
                        + $"{Schedule.Elapsed(availability.ErrorBudgetLeft ?? TimeSpan.Zero)}");
                }

                if (availability.CoverageNotice is { } notice)
                {
                    column.Item().PaddingTop(3).Text(notice).FontSize(8).Italic();
                }
            });
    }

    private static void ComposeMethod(IContainer container)
    {
        container.Column(column =>
        {
            column.Item().PaddingTop(4).Text("Как считалось").FontSize(10).SemiBold();

            column.Item().PaddingTop(2).Text(
                    "Доступность считается по времени, а не по числу проверок: «99 из 100 проверок "
                    + "прошли» и «недоступно 1 % времени» совпадают только при ровном интервале. "
                    + "Время, которое продукт не наблюдал, и время плановых работ исключены "
                    + "из знаменателя целиком — подставить туда «работало» или «не работало» "
                    + "значило бы выдумать данные. Доля наблюдавшегося времени показана "
                    + "отдельной строкой «Покрытие окна».")
                .FontSize(8);

            column.Item().PaddingTop(2).Text(
                    "Состояние известно только в моменты проверок, поэтому границы простоя "
                    + "определены с точностью до интервала между ними — это и указано знаком «±».")
                .FontSize(8);
        });
    }

    private static string Percent(double value) =>
        value.ToString(value >= 99.9 ? "0.###" : "0.##", CultureInfo.InvariantCulture) + " %";
}
