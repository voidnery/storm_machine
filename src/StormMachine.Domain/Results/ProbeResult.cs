using StormMachine.Domain.Measurements;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.Results;

/// <summary>Тип пробы. Значения фиксированы — они попадают в хранилище и в экспорт.</summary>
public enum ProbeKind
{
    Icmp = 1,
    TcpConnect = 2,
    Udp = 3,
    Http = 4,
    Dns = 5,
    Tls = 6,
    Traceroute = 7,
    Throughput = 8,
    PathMtu = 9,
}

/// <summary>
/// Результат одной пробы: сырые сэмплы плюс условия измерения.
/// </summary>
/// <remarks>
/// Агрегаты (перцентили, джиттер, MOS) сознательно не входят в этот тип: они вычисляются
/// поверх сэмплов и зависят от методики. Так один и тот же набор сырых данных можно
/// пересчитать другим способом, не теряя исходник.
/// </remarks>
public sealed record ProbeResult
{
    public required Guid Id { get; init; }

    public required ProbeKind Kind { get; init; }

    public required Target Target { get; init; }

    /// <summary>Адрес, в который цель разрешилась на самом деле. Важно для динамических целей.</summary>
    public string? ResolvedAddress { get; init; }

    public required MeasurementContext Context { get; init; }

    public required MeasurementUnit Unit { get; init; }

    public required IReadOnlyList<Sample> Samples { get; init; }

    /// <summary>
    /// Структурные факты: записи DNS, цепочка сертификатов, заголовки HTTP.
    /// </summary>
    /// <remarks>
    /// Второй канал результата рядом с сэмплами. Введён в И-2: половина проб сообщает
    /// не только «сколько миллисекунд», но и «что именно там оказалось», и это не
    /// укладывалось в числовой ряд.
    /// </remarks>
    public IReadOnlyList<ProbeFact> Facts { get; init; } = [];

    public required DateTimeOffset CompletedUtc { get; init; }

    /// <summary>Прогон был прерван оператором. Измеренное до прерывания сохраняется.</summary>
    public bool WasCancelled { get; init; }

    public Verdict? Verdict { get; init; }

    public int SentCount => Samples.Count;

    public int SuccessCount
    {
        get
        {
            var count = 0;
            for (var i = 0; i < Samples.Count; i++)
            {
                if (Samples[i].IsSuccess)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public int LostCount => SentCount - SuccessCount;

    public double LossPercent => SentCount == 0 ? 0 : LostCount * 100.0 / SentCount;
}
