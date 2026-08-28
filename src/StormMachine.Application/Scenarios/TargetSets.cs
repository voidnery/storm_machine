using StormMachine.Application.Abstractions;

namespace StormMachine.Application.Scenarios;

/// <summary>Набор целей: имя, пояснение и то, из чего он получился.</summary>
public sealed record TargetSet
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<string> Targets { get; init; }

    /// <summary>Откуда взялись цели — показывается оператору вместе с результатом.</summary>
    public required string Origin { get; init; }
}

/// <summary>
/// Готовые наборы целей.
/// </summary>
/// <remarks>
/// Одна цель отвечает на вопрос «работает ли это», несколько — на вопрос «дело в нас или
/// в них». Именно второй задают, когда что-то сломалось: пока проверена одна цель, отличить
/// поломку канала от поломки конкретного сервера нечем.
/// <para>
/// Набор «своё» не список, а вычисление: шлюз и резолверы берутся из текущего окружения.
/// Записать их константами значило бы предложить оператору проверять чужую сеть.
/// </para>
/// </remarks>
public static class TargetSets
{
    /// <summary>Приставка для чтения набора из файла: <c>@список.txt</c>.</summary>
    public const char FilePrefix = '@';

    private static readonly string[] PublicTargets = ["example.com", "cloudflare.com", "wikipedia.org"];

    private static readonly string[] PublicResolvers =
        ["1.1.1.1", "8.8.8.8", "9.9.9.9", "77.88.8.8", "208.67.222.222"];

    public static IReadOnlyList<(string Key, string Title, string About)> All { get; } =
    [
        ("своё", "Своя сеть", "шлюз и резолверы текущего адаптера"),
        ("публичные", "Публичные сайты", "три независимых сайта: отличить свою поломку от чужой"),
        ("резолверы", "Публичные резолверы", "пять резолверов, которые сравнивает шаблон dns"),
    ];

    /// <summary>
    /// Разбирает то, что оператор написал в поле цели.
    /// </summary>
    /// <remarks>
    /// Две записи в одном месте намеренно: имя набора и список через запятую. Разводить
    /// их по разным ключам командной строки значило бы заставить помнить, каким ключом
    /// подаётся то, что и так однозначно читается. Третья запись — <c>@файл</c> —
    /// разбирается вызывающим: чтение файла делает тот слой, которому позволено
    /// трогать диск, и в слое сценариев его нет.
    /// </remarks>
    public static TargetSet Resolve(string specification, INetworkEnvironment environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specification);
        ArgumentNullException.ThrowIfNull(environment);

        var text = specification.Trim();
        var named = Named(text, environment);

        if (named is not null)
        {
            return named;
        }

        var targets = text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            throw new ArgumentException("Цель не указана.", nameof(specification));
        }

        return new TargetSet
        {
            Key = text,
            Title = targets.Count == 1 ? targets[0] : $"{targets.Count} целей",
            Targets = targets,
            Origin = "указано в команде",
        };
    }

    private static TargetSet? Named(string key, INetworkEnvironment environment) => key.ToLowerInvariant() switch
    {
        "своё" or "свое" or "own" => Own(environment),
        "публичные" or "public" => new TargetSet
        {
            Key = "публичные",
            Title = "Публичные сайты",
            Targets = PublicTargets,
            Origin = "встроенный набор",
        },
        "резолверы" or "resolvers" => new TargetSet
        {
            Key = "резолверы",
            Title = "Публичные резолверы",
            Targets = PublicResolvers,
            Origin = "встроенный набор",
        },
        _ => null,
    };

    private static TargetSet Own(INetworkEnvironment environment)
    {
        var adapter = environment.GetPrimaryAdapter();

        var targets = new List<string>();

        if (adapter is not null)
        {
            targets.AddRange(adapter.Gateways);
            targets.AddRange(adapter.DnsServers);
        }

        // Пустой набор — не повод для исключения: адаптера с маршрутом по умолчанию
        // может не быть, и это само по себе диагноз, который оператор должен увидеть.
        return new TargetSet
        {
            Key = "своё",
            Title = "Своя сеть",
            Targets = [.. targets.Distinct(StringComparer.OrdinalIgnoreCase)],
            Origin = adapter is null
                ? "адаптера с маршрутом по умолчанию нет — набор пуст"
                : $"шлюз и резолверы адаптера «{adapter.Name}»",
        };
    }

    /// <summary>
    /// Набор из строк списка, прочитанного вызывающим.
    /// </summary>
    /// <remarks>
    /// Комментарии разрешены: список целей живёт дольше одного запуска, и через месяц
    /// строка «10.0.4.7» без пояснения перестаёт что-либо значить.
    /// </remarks>
    public static TargetSet FromLines(string title, string origin, IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var targets = lines
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (targets.Count == 0)
        {
            throw new ArgumentException($"В списке «{origin}» нет ни одной цели.", nameof(lines));
        }

        return new TargetSet
        {
            Key = title,
            Title = title,
            Targets = targets,
            Origin = origin,
        };
    }
}
