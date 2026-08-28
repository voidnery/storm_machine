using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Reporting;

/// <summary>Прогон в переносимом виде — то, что уходит в JSON.</summary>
/// <remarks>
/// Условия измерения идут первыми и всегда. Ряд чисел без интерфейса, методики
/// и порога достоверности нельзя ни повторить, ни сопоставить, а забирают его
/// именно за этим.
/// </remarks>
public sealed record ExportedRun
{
    public required string Id { get; init; }

    public required string Probe { get; init; }

    public required string Target { get; init; }

    public string? ResolvedAddress { get; init; }

    public required string Unit { get; init; }

    public required string StartedUtc { get; init; }

    public string? CompletedUtc { get; init; }

    public required string State { get; init; }

    public required int Sent { get; init; }

    public required int Received { get; init; }

    public required ExportedContext Context { get; init; }

    public required IReadOnlyList<ExportedSeries> Series { get; init; }

    public required IReadOnlyList<ExportedFact> Facts { get; init; }

    /// <summary>Сырые измерения. Пусто, если их уже удалила политика хранения.</summary>
    public required IReadOnlyList<ExportedSample> Samples { get; init; }

    /// <summary>Сказано прямо, а не выведено из пустого списка.</summary>
    public required bool HasRawSamples { get; init; }
}

/// <summary>Условия измерения в выгрузке.</summary>
public sealed record ExportedContext
{
    public required string Interface { get; init; }

    public required string AdapterKind { get; init; }

    public string? InterfaceAddress { get; init; }

    public required double CalibrationBaselineMs { get; init; }

    public required bool TimingTrustworthy { get; init; }

    public required string Methodology { get; init; }

    public string? MethodologyReference { get; init; }

    public string? Backend { get; init; }

    /// <summary>Профиль окружения, активный на момент измерения.</summary>
    public string? Profile { get; init; }

    public required string ProductVersion { get; init; }
}

public sealed record ExportedSeries
{
    public required string Label { get; init; }

    public required int Sent { get; init; }

    public required int Received { get; init; }

    public double? MinMs { get; init; }

    public double? MedianMs { get; init; }

    public double? P95Ms { get; init; }

    public double? MaxMs { get; init; }

    public double? JitterMs { get; init; }

    public required double LossPercent { get; init; }
}

public sealed record ExportedFact(string Category, string Name, string Value, double? Numeric, bool IsWarning);

public sealed record ExportedSample
{
    public required int Sequence { get; init; }

    public required string TimestampUtc { get; init; }

    public double? Value { get; init; }

    public required string Status { get; init; }

    public string? Label { get; init; }

    public int? Group { get; init; }

    public string? RespondedBy { get; init; }
}

/// <summary>
/// Выгрузка прогона в CSV, JSON и PNG.
/// </summary>
/// <remarks>
/// Отдельно от отчёта: отчёт объясняет, выгрузка отдаёт. Смешать их значило бы
/// тащить движок PDF ради одной таблицы или потерять оговорки в документе.
/// </remarks>
public sealed class RunExporter : IRunExporter
{
    private static readonly ExportJsonContext Json = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });

    public IReadOnlyList<ExportFormat> Formats { get; } = [ExportFormat.Csv, ExportFormat.Json, ExportFormat.Png];

    public Task<RenderedReport> ExportAsync(
        StoredRun run,
        ExportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        cancellationToken.ThrowIfCancellationRequested();

        var (content, extension) = format switch
        {
            ExportFormat.Csv => (Csv(run), "csv"),
            ExportFormat.Json => (JsonBytes(run), "json"),
            _ => (Png(run), "png"),
        };

        var name = $"storm-{run.Summary.ProbeName}-"
                   + $"{run.Summary.StartedUtc.ToLocalTime():yyyyMMdd-HHmmss}.{extension}";

        return Task.FromResult(new RenderedReport
        {
            Content = content,
            FileExtension = extension,
            SuggestedFileName = name,
        });
    }

    /// <summary>
    /// Сырые измерения таблицей.
    /// </summary>
    /// <remarks>
    /// Разделитель — точка с запятой, а не запятая. Русский Excel по умолчанию читает
    /// именно её, а файл, открывшийся одной колонкой, для получателя равен файлу,
    /// который не открылся. Числа при этом пишутся с точкой: это данные, а не показ.
    /// <para>
    /// Первыми идут строки условий, помеченные «#». Excel их покажет, а разбор строкой
    /// пропустит — и в обоих случаях числа не окажутся без объяснения, откуда они.
    /// </para>
    /// </remarks>
    private static byte[] Csv(StoredRun run)
    {
        var text = new StringBuilder();
        var summary = run.Summary;
        var context = run.Context;

        text.AppendLine($"# Storm Machine {context.ProductVersion}");
        text.AppendLine($"# проба;{summary.ProbeName}");
        text.AppendLine($"# цель;{summary.TargetDisplay}");
        text.AppendLine($"# адрес;{summary.ResolvedAddress ?? "—"}");
        text.AppendLine($"# начато;{summary.StartedUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        text.AppendLine($"# интерфейс;{context.InterfaceName};{RunSection.DescribeAdapter(context.AdapterKind)}");
        text.AppendLine(
            CultureInfo.InvariantCulture,
            $"# порог достоверности, мс;{context.CalibrationBaselineMs:0.###}");
        if (context.Profile is { } profile)
        {
            text.AppendLine($"# профиль окружения;{profile}");
        }

        text.AppendLine($"# методика;{context.Methodology.Name}"
                        + (context.Methodology.Reference is { } reference ? $";{reference}" : string.Empty));

        if (!context.IsTimingTrustworthy)
        {
            text.AppendLine("# ВНИМАНИЕ;измерение выполнено через адаптер, добавляющий собственную задержку;"
                            + "абсолютным значениям доверять нельзя");
        }

        text.AppendLine();

        if (run.Samples.Count == 0)
        {
            // Пустой файл заставил бы получателя гадать, что случилось.
            text.AppendLine("# сырые измерения удалены политикой хранения; ниже — сохранённые агрегаты");
            text.AppendLine();
            text.AppendLine("ряд;отправлено;получено;потери_проц;мин_мс;медиана_мс;p95_мс;макс_мс;джиттер_мс");

            foreach (var series in run.Series)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"{series.Label};{series.SentCount};{series.SuccessCount};{series.LossPercent:0.###};"
                    + $"{Number(series.Statistics.MinMs)};{Number(series.Statistics.P50Ms)};"
                    + $"{Number(series.Statistics.P95Ms)};{Number(series.Statistics.MaxMs)};"
                    + $"{Number(series.Statistics.JitterRfc3550Ms)}");
            }

            return Encoding.UTF8.GetBytes(text.ToString());
        }

        text.AppendLine("номер;время;значение;состояние;ряд;группа;ответил");

        foreach (var sample in run.Samples)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"{sample.Sequence};{sample.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss.fff};"
                + $"{Number(sample.Value)};{sample.Status};{sample.Label};"
                + $"{sample.Group?.ToString(CultureInfo.InvariantCulture)};{sample.RespondedBy}");
        }

        // Кодировка с меткой: без неё русский Excel читает UTF-8 как кодовую страницу
        // системы и вместо «потери» показывает кракозябры.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    private static byte[] JsonBytes(StoredRun run) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ToExported(run), Json.ExportedRun));

    private static byte[] Png(StoredRun run)
    {
        var route = RunSection.RouteOf(run);
        var chart = route is not null ? RouteChartImage.TryRender(route) : LatencyChartImage.TryRender(run);

        return chart ?? throw new InvalidOperationException(
            run.Summary.HasRawSamples
                ? "График не построен: для линии нужно хотя бы два измерения."
                : "График не построен: сырые измерения удалены политикой хранения.");
    }

    internal static ExportedRun ToExported(StoredRun run) => new()
    {
        Id = run.Summary.Id.ToString(),
        Probe = run.Summary.ProbeName,
        Target = run.Summary.TargetDisplay,
        ResolvedAddress = run.Summary.ResolvedAddress,
        Unit = run.Unit.ToString(),
        StartedUtc = run.Summary.StartedUtc.ToString("O", CultureInfo.InvariantCulture),
        CompletedUtc = run.Summary.CompletedUtc?.ToString("O", CultureInfo.InvariantCulture),
        State = run.Summary.State.ToString(),
        Sent = run.Summary.SentCount,
        Received = run.Summary.SuccessCount,
        HasRawSamples = run.Summary.HasRawSamples,
        Context = new ExportedContext
        {
            Interface = run.Context.InterfaceName,
            AdapterKind = run.Context.AdapterKind.ToString(),
            InterfaceAddress = run.Context.InterfaceAddress,
            CalibrationBaselineMs = run.Context.CalibrationBaselineMs,
            TimingTrustworthy = run.Context.IsTimingTrustworthy,
            Methodology = run.Context.Methodology.Name,
            MethodologyReference = run.Context.Methodology.Reference,
            Backend = run.Context.Backend,
            Profile = run.Context.Profile,
            ProductVersion = run.Context.ProductVersion,
        },
        Series =
        [
            .. run.Series.Select(s => new ExportedSeries
            {
                Label = s.Label,
                Sent = s.SentCount,
                Received = s.SuccessCount,
                MinMs = s.Statistics.MinMs,
                MedianMs = s.Statistics.P50Ms,
                P95Ms = s.Statistics.P95Ms,
                MaxMs = s.Statistics.MaxMs,
                JitterMs = s.Statistics.JitterRfc3550Ms,
                LossPercent = s.LossPercent,
            }),
        ],
        Facts =
        [
            .. run.Facts.Select(f => new ExportedFact(f.Category, f.Name, f.Value, f.Numeric, f.IsWarning)),
        ],
        Samples =
        [
            .. run.Samples.Select(s => new ExportedSample
            {
                Sequence = s.Sequence,
                TimestampUtc = s.TimestampUtc.ToString("O", CultureInfo.InvariantCulture),
                Value = s.IsSuccess ? s.Value : null,
                Status = s.Status.ToString(),
                Label = s.Label,
                Group = s.Group,
                RespondedBy = s.RespondedBy,
            }),
        ],
    };

    private static string Number(double? value) =>
        value is { } number ? number.ToString("0.###", CultureInfo.InvariantCulture) : string.Empty;
}

/// <summary>
/// Контекст сериализации выгрузки.
/// </summary>
/// <remarks>
/// Сгенерирован исходниками: клиенты публикуются с обрезкой, и рефлексивная
/// сериализация при ней не собирается вовсе. Настройки задаются экземпляру контекста,
/// а не отдельным объектом при вызове.
/// </remarks>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(ExportedRun))]
internal sealed partial class ExportJsonContext : JsonSerializerContext
{
}
