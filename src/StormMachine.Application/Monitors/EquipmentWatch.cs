using System.Globalization;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.Monitors;

/// <summary>
/// Наблюдение за самим оборудованием: счётчики портов и появление серверов DHCP.
/// </summary>
/// <remarks>
/// Появилось в И-21. До него монитор умел только пробы и сценарии, то есть наблюдал сеть
/// <b>снаружи</b> — со своей машины и своими пакетами. Оба наблюдения здесь отвечают
/// на другой вопрос.
/// <para>
/// Счётчики порта видят то, чего не видит ни одна проба: растущие ошибки на порту
/// означают умирающий патч-корд, а снаружи это выглядит просто как «чуть медленнее» —
/// повторная передача TCP вытягивает потери и прячет причину.
/// </para>
/// <para>
/// Наблюдение за DHCP устроено иначе всех прочих: оно следит не за величиной,
/// а за <b>появлением</b>. Вердикта «сервер посторонний» продукт не выносит и здесь —
/// две законные пары в одном домене встречаются не реже подставного сервера. Событие
/// формулируется проверяемо: сервер, которого раньше не слышали, или знакомый сервер,
/// начавший объявлять другой шлюз.
/// </para>
/// </remarks>
public static class EquipmentWatch
{
    /// <summary>Имя параметра монитора с номером порта.</summary>
    public const string PortParameter = "port";

    /// <summary>Метрика загрузки в процентах — по ней и ставят порог.</summary>
    public const string LoadMetric = "load";

    /// <summary>Метрика ошибок и отбросов за наблюдение.</summary>
    public const string FaultsMetric = "faults";

    /// <summary>
    /// Оценивает последнее наблюдение за портом.
    /// </summary>
    /// <remarks>
    /// Монитор порта <b>ничего не опрашивает сам</b>. Он читает историю, которую
    /// наполняет опрос, и это не экономия, а решение: опрос требует учётных данных
    /// и паузы между снимками в десятки секунд, а монитор обязан отвечать быстро
    /// и не держать в себе второй способ разговаривать с оборудованием.
    /// <para>
    /// Отсюда и главное свойство: свежесть наблюдения — часть вердикта. История, которая
    /// перестала пополняться, означает не «всё хорошо», а «мы больше не смотрим»,
    /// и молчание монитора в этом случае было бы ложью.
    /// </para>
    /// </remarks>
    public static MonitorCheck EvaluatePort(
        Monitor monitor,
        IReadOnlyList<PortLoadPoint> history,
        DateTimeOffset now,
        TimeSpan staleAfter)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(history);

        var blank = new MonitorCheck
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            StartedUtc = now,
            Summary = string.Empty,
        };

        if (history.Count == 0)
        {
            return blank with
            {
                Level = VerdictLevel.Unknown,
                Kind = CheckKind.Missed,
                Summary = $"Наблюдений за портом устройства {monitor.Subject} нет — опрос не выполнялся.",
            };
        }

        var last = history[^1];
        var age = now - last.AtUtc;

        if (age > staleAfter)
        {
            // Не отказ порта, а отсутствие наблюдения: пометка Missed говорит именно
            // это, и в доступности такая проверка не считается ни за работу, ни за
            // простой. Свести её к «норма» значило бы завысить доступность.
            return blank with
            {
                Level = VerdictLevel.Unknown,
                Kind = CheckKind.Missed,
                Summary = $"Последнее наблюдение {Age(age)} назад — опрос прекратился, "
                          + "и о порте сейчас ничего не известно.",
            };
        }

        var load = Math.Max(last.InPercent ?? 0, last.OutPercent ?? 0);
        var faults = history.Sum(p => p.Faults);

        var breach = Breach(monitor.Thresholds, LoadMetric, load)
                     ?? Breach(monitor.Thresholds, FaultsMetric, faults);

        if (breach is { } violated)
        {
            var actual = string.Equals(violated.Metric, LoadMetric, StringComparison.OrdinalIgnoreCase)
                ? load
                : faults;

            return blank with
            {
                Level = violated.Level,
                Summary = Describe(last, load, faults) + $" — нарушен порог «{violated.Describe()}»",
                Metric = violated.Metric,
                Value = actual,
                Threshold = violated.Value,
            };
        }

        return blank with
        {
            Level = monitor.Thresholds.Count == 0 ? VerdictLevel.Unknown : VerdictLevel.Pass,
            Summary = Describe(last, load, faults),
            Metric = LoadMetric,
            Value = load,
        };
    }

    private static string Describe(PortLoadPoint last, double load, long faults)
    {
        var port = last.IfName is { Length: > 0 } name
            ? $"{name} (порт {last.IfIndex.ToString(CultureInfo.InvariantCulture)})"
            : $"порт {last.IfIndex.ToString(CultureInfo.InvariantCulture)}";

        var loadText = last.SpeedBitsPerSecond > 0
            ? $"загрузка {load.ToString("0.0", CultureInfo.InvariantCulture)} %"
            : "скорость порта неизвестна, загрузка в процентах не считается";

        return faults == 0
            ? $"{port}: {loadText}, ошибок нет"
            : $"{port}: {loadText}, ошибок и отбросов {faults.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Оценивает картину серверов DHCP.
    /// </summary>
    /// <remarks>
    /// Отказом считается ровно два события, и оба проверяемы: услышан сервер, которого
    /// раньше не было, или знакомый сервер объявил шлюз, которого мы не знаем. Число
    /// серверов само по себе вердикта не даёт: у двух законных серверов в одном домене
    /// оно тоже равно двум.
    /// </remarks>
    /// <param name="monitor">Монитор.</param>
    /// <param name="servers">Что услышано за наблюдаемый период.</param>
    /// <param name="knownGateways">Шлюзы, известные системе.</param>
    /// <param name="now">Момент проверки.</param>
    /// <param name="freshAfter">С какого возраста сервер считается новым.</param>
    public static MonitorCheck EvaluateDhcp(
        Monitor monitor,
        IReadOnlyList<HeardDhcpServer> servers,
        IReadOnlyList<string> knownGateways,
        DateTimeOffset now,
        TimeSpan freshAfter)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(knownGateways);

        var blank = new MonitorCheck
        {
            Id = Guid.NewGuid(),
            MonitorId = monitor.Id,
            StartedUtc = now,
            Summary = string.Empty,
        };

        if (servers.Count == 0)
        {
            return blank with
            {
                Level = VerdictLevel.Unknown,
                Kind = CheckKind.Missed,
                Summary = "Серверов DHCP не слышали — прослушивание не выполнялось.",
            };
        }

        var appeared = servers.Where(s => now - s.FirstSeenUtc <= freshAfter).ToList();

        var strangers = servers
            .Where(s => s.OfferedGateway.Length > 0
                        && !knownGateways.Contains(s.OfferedGateway, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (strangers.Count > 0)
        {
            return blank with
            {
                Level = VerdictLevel.Fail,
                Summary = $"Сервер {string.Join(", ", strangers.Select(s => s.ServerAddress))} "
                          + $"объявляет шлюз {string.Join(", ", strangers.Select(s => s.OfferedGateway))}, "
                          + "которого мы не знаем.",
                Metric = "servers",
                Value = servers.Count,
            };
        }

        if (appeared.Count > 0)
        {
            return blank with
            {
                Level = VerdictLevel.Warn,
                Summary = $"Появился сервер {string.Join(", ", appeared.Select(s => s.ServerAddress))}, "
                          + "которого раньше не слышали. Шлюз он объявляет знакомый.",
                Metric = "servers",
                Value = servers.Count,
            };
        }

        return blank with
        {
            Level = VerdictLevel.Pass,
            Summary = servers.Count == 1
                ? $"Один сервер DHCP: {servers[0].ServerAddress}, шлюз знакомый."
                : $"Серверов DHCP {servers.Count.ToString(CultureInfo.InvariantCulture)}, "
                  + "все знакомые и объявляют известные шлюзы.",
            Metric = "servers",
            Value = servers.Count,
        };
    }

    /// <summary>Первый нарушенный порог по названной метрике.</summary>
    private static Threshold? Breach(IReadOnlyList<Threshold> thresholds, string metric, double actual) =>
        thresholds.FirstOrDefault(t =>
            string.Equals(t.Metric, metric, StringComparison.OrdinalIgnoreCase)
            && !t.IsSatisfiedBy(actual));

    private static string Age(TimeSpan age) => age switch
    {
        { TotalDays: >= 1 } => Domain.Text.Plural.With((int)age.TotalDays, "день", "дня", "дней"),
        { TotalHours: >= 1 } => Domain.Text.Plural.With((int)age.TotalHours, "час", "часа", "часов"),
        _ => Domain.Text.Plural.With(Math.Max(1, (int)age.TotalMinutes), "минуту", "минуты", "минут"),
    };
}
