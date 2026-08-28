using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;

namespace StormMachine.Application.Scenarios;

/// <summary>
/// Готовые сценарии.
/// </summary>
/// <remarks>
/// Пустой конструктор — это предложение оператору спроектировать проверку с нуля,
/// а он открыл продукт не за этим. Шаблоны отвечают на вопросы, которые задают чаще
/// всего, и одновременно показывают, из чего сценарий вообще складывается: их правят,
/// а не изучают документацию.
/// <para>
/// Пороги в шаблонах — не истина, а разумная отправная точка. Их видно, и их меняют.
/// </para>
/// </remarks>
public static class ScenarioTemplates
{
    /// <summary>
    /// Синтетическая транзакция: имя → соединение → TLS → страница.
    /// </summary>
    /// <remarks>
    /// Главный шаблон итерации. Одно число «страница открылась за 460 мс» не говорит
    /// ничего: медленно может быть в разрешении имени, в установке соединения,
    /// в рукопожатии TLS или на сервере. Разбивка по фазам называет виновника.
    /// <para>
    /// Шаги обрываются при отказе: не разрешилось имя — соединяться не с чем,
    /// и проверять сертификат бессмысленно.
    /// </para>
    /// </remarks>
    public static Scenario WebTransaction(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return new Scenario
        {
            Id = Guid.NewGuid(),
            Name = $"Открытие {host}",
            Description = "Синтетическая транзакция: разрешение имени, соединение, TLS, страница. "
                          + "Разбивка по фазам показывает, на каком шаге теряется время.",
            Steps =
            [
                new ScenarioStep
                {
                    Name = "Разрешение имени",
                    ProbeName = "dns",
                    Target = Target.Parse(host),
                    // Только системные резолверы: транзакция измеряет то, что получит
                    // приложение, а оно пойдёт к тому серверу, который настроен в системе.
                    // Подмешать сюда публичные значило бы показать чужую задержку.
                    Parameters = Parameters(("count", 5), ("type", "A"), ("resolvers", "системные")),
                    Thresholds =
                    [
                        // Порог на медиану, а не на p95: первый запрос идёт мимо кэша
                        // и всегда медленнее остальных, и p95 на пяти пробах — это
                        // почти он один. Ругаться на холодный кэш каждый раз значит
                        // приучить оператора не читать предупреждения.
                        Threshold.Parse("p50 < 100", VerdictLevel.Warn),
                        Threshold.Parse("p95 < 500", VerdictLevel.Warn),
                        Threshold.Parse("loss < 1"),
                    ],
                },
                new ScenarioStep
                {
                    Name = "Соединение",
                    ProbeName = "tcp",
                    Target = Target.Parse(host),
                    Parameters = Parameters(("port", 443), ("count", 5), ("interval", 200)),
                    Thresholds =
                    [
                        Threshold.Parse("p95 < 150", VerdictLevel.Warn),
                        Threshold.Parse("loss < 1"),
                    ],
                },
                new ScenarioStep
                {
                    Name = "Сертификат и рукопожатие",
                    ProbeName = "tls",
                    Target = Target.Parse(host),
                    Parameters = Parameters(("port", 443), ("count", 2)),

                    // Две недели — минимум, за который успевают заметить и продлить.
                    // Меньше — уже повод для звонка, а не для записи в план.
                    Thresholds = [Threshold.Parse("Осталось дней >= 14")],
                },
                new ScenarioStep
                {
                    Name = "Страница",
                    ProbeName = "http",
                    Target = Target.Parse(host.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? host
                        : "https://" + host),
                    Parameters = Parameters(("count", 3)),
                    Thresholds =
                    [
                        Threshold.Parse("p95 < 1500", VerdictLevel.Warn),

                        // Отдельно по фазе «первый байт»: это время раздумий сервера,
                        // и оно единственное в водопаде не зависит от канала. Порог
                        // на нём отличает «медленно у них» от «медленно до них» —
                        // а это разные звонки разным людям.
                        Threshold.Parse("p95@ttfb < 800", VerdictLevel.Warn),
                    ],
                },
            ],
        };
    }

    /// <summary>
    /// Сравнение резолверов.
    /// </summary>
    /// <remarks>
    /// Один шаг, а не пять: проба DNS сама опрашивает несколько резолверов и отдаёт
    /// ряд на каждый — форма результата <c>ComparedSeries</c> появилась в И-2 ровно
    /// под этот вопрос. Пять отдельных шагов дали бы пять таблиц вместо одной.
    /// </remarks>
    public static Scenario ResolverComparison(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return new Scenario
        {
            Id = Guid.NewGuid(),
            Name = $"Резолверы для {host}",
            Description = "Пять резолверов в одной таблице: чей ответ быстрее и совпадают ли ответы.",
            Steps =
            [
                new ScenarioStep
                {
                    Name = "Сравнение резолверов",
                    ProbeName = "dns",
                    Target = Target.Parse(host),
                    Parameters = Parameters(
                        ("resolvers", "1.1.1.1,8.8.8.8,9.9.9.9,77.88.8.8,208.67.222.222"),
                        ("count", 5),
                        ("type", "A")),
                    Thresholds = [Threshold.Parse("p95 < 200", VerdictLevel.Warn)],
                },
            ],
        };
    }

    /// <summary>Пригодность канала для телефонии.</summary>
    public static Scenario VoiceReadiness(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        return new Scenario
        {
            Id = Guid.NewGuid(),
            Name = $"Голос до {host}",
            Description = "Выдержит ли канал телефонию: задержка, дрожание, потери и MOS.",
            Steps =
            [
                new ScenarioStep
                {
                    Name = "Непрерывный ping",
                    ProbeName = "ping",
                    Target = Target.Parse(host),
                    Parameters = Parameters(("count", 100), ("interval", 100)),
                    Thresholds =
                    [
                        // Границы из G.114 и практики VoIP: 150 мс в одну сторону,
                        // 30 мс дрожания, процент потерь. MOS 3.6 — граница пригодности.
                        Threshold.Parse("p95 < 150", VerdictLevel.Warn),
                        Threshold.Parse("jitter < 30", VerdictLevel.Warn),
                        Threshold.Parse("loss < 1"),
                        Threshold.Parse("mos >= 3.6"),
                    ],
                },
            ],
        };
    }

    /// <summary>Все шаблоны с коротким описанием — для показа списком.</summary>
    public static IReadOnlyList<(string Key, string Title, string About)> All { get; } =
    [
        ("web", "Открытие сайта", "DNS → соединение → TLS → страница, с разбивкой по фазам"),
        ("dns", "Сравнение резолверов", "Пять резолверов в одной таблице"),
        ("voice", "Готовность к голосу", "Задержка, дрожание, потери и MOS"),
    ];

    public static Scenario Create(string key, string host) => key.ToLowerInvariant() switch
    {
        "web" => WebTransaction(host),
        "dns" => ResolverComparison(host),
        "voice" => VoiceReadiness(host),
        _ => throw new ArgumentException(
            $"Шаблон «{key}» не найден. Доступны: {string.Join(", ", All.Select(t => t.Key))}.",
            nameof(key)),
    };

    private static Dictionary<string, object?> Parameters(params (string Name, object? Value)[] values)
    {
        var parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in values)
        {
            parameters[name] = value;
        }

        return parameters;
    }
}
