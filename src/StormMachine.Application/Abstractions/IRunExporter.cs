using StormMachine.Domain.Results;

namespace StormMachine.Application.Abstractions;

/// <summary>
/// Формат выгрузки.
/// </summary>
/// <remarks>
/// Три, и каждый отвечает своему получателю. CSV открывают в таблице и считают сами.
/// JSON забирает чужая программа. PNG вставляют в письмо. Отчёт PDF ни один из них
/// не заменяет: он объясняет, а эти — отдают.
/// </remarks>
public enum ExportFormat
{
    Csv,

    Json,

    Png,
}

/// <summary>
/// Выгрузка прогона в машиночитаемый вид.
/// </summary>
/// <remarks>
/// Отдельно от <see cref="IReportRenderer"/> намеренно: отчёт — это документ
/// с выводами и оговорками, выгрузка — сырые числа без них. Смешать их значило бы
/// либо тащить движок PDF ради CSV, либо потерять оговорки в отчёте.
/// <para>
/// В выгрузку всегда попадают условия измерения. Ряд чисел без интерфейса, методики
/// и порога достоверности нельзя ни повторить, ни сопоставить, а именно за этим
/// его и забирают.
/// </para>
/// </remarks>
public interface IRunExporter
{
    IReadOnlyList<ExportFormat> Formats { get; }

    Task<RenderedReport> ExportAsync(
        StoredRun run,
        ExportFormat format,
        CancellationToken cancellationToken = default);
}
