using System.Globalization;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Scenarios;

namespace StormMachine.Domain.Profiles;

/// <summary>
/// По каким приметам профиль узнаёт свою сеть.
/// </summary>
/// <remarks>
/// MAC шлюза надёжнее подсети: адрес 192.168.1.0/24 стоит у половины сетей мира,
/// а MAC конкретного маршрутизатора — только у него. Подсеть остаётся запасным
/// признаком: MAC шлюза не всегда виден, например через VPN.
/// </remarks>
public sealed record NetworkSignature
{
    /// <summary>MAC шлюза по умолчанию. Самая надёжная примета.</summary>
    public string? GatewayMac { get; init; }

    public string? GatewayAddress { get; init; }

    /// <summary>Подсеть в нотации CIDR.</summary>
    public string? Subnet { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(GatewayMac)
        && string.IsNullOrWhiteSpace(GatewayAddress)
        && string.IsNullOrWhiteSpace(Subnet);

    /// <summary>
    /// Насколько уверенно примета указывает на этот профиль.
    /// </summary>
    /// <remarks>
    /// Возвращается вес, а не «да/нет»: совпадение по MAC шлюза и совпадение
    /// по подсети — разной силы утверждения, и складывать их в одно значило бы
    /// приравнять «это точно та сеть» к «это похоже на ту сеть».
    /// </remarks>
    public int Match(NetworkSignature current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var score = 0;

        if (Same(GatewayMac, current.GatewayMac))
        {
            score += 10;
        }

        if (Same(GatewayAddress, current.GatewayAddress))
        {
            score += 3;
        }

        if (Same(Subnet, current.Subnet))
        {
            score += 2;
        }

        return score;
    }

    public string Describe()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(GatewayMac))
        {
            parts.Add($"шлюз {GatewayMac}");
        }
        else if (!string.IsNullOrWhiteSpace(GatewayAddress))
        {
            parts.Add($"шлюз {GatewayAddress}");
        }

        if (!string.IsNullOrWhiteSpace(Subnet))
        {
            parts.Add(Subnet);
        }

        return parts.Count == 0 ? "примет нет" : string.Join(", ", parts);
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Профиль сетевого окружения: «офис», «дом», «объект заказчика».
/// </summary>
/// <remarks>
/// Требование C-12 анализа. Смысл не в удобстве переключения списков, а в том, что
/// <b>измерения из разных мест несопоставимы</b>. Порог 50 мс, разумный в офисе,
/// бессмыслен для канала до филиала через VPN; цели, важные на объекте заказчика,
/// не имеют отношения к домашней сети.
/// <para>
/// Поэтому активный профиль записывается в условия каждого измерения. Через полгода,
/// глядя на журнал, отличить замер у заказчика от замера в офисе иначе будет нечем —
/// а сравнивать их между собой нельзя.
/// </para>
/// </remarks>
public sealed record NetworkProfile
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Цели, важные в этом окружении.</summary>
    public IReadOnlyList<string> Targets { get; init; } = [];

    /// <summary>
    /// Пороги, уместные здесь.
    /// </summary>
    /// <remarks>
    /// Отдельные для каждого профиля, потому что «хорошо» зависит от места:
    /// 5 мс до шлюза в офисе — норма, 5 мс до филиала за тысячу километров —
    /// физически невозможно.
    /// </remarks>
    public IReadOnlyList<Threshold> Thresholds { get; init; } = [];

    /// <summary>Мониторы, работающие в этом профиле.</summary>
    public IReadOnlyList<Guid> Monitors { get; init; } = [];

    /// <summary>Приметы, по которым профиль узнаётся.</summary>
    public NetworkSignature Signature { get; init; } = new();

    public bool IsActive { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public string DisplayName => string.IsNullOrWhiteSpace(Description) ? Name : $"{Name} — {Description}";

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("У профиля должно быть имя.");
        }

        return errors;
    }

    public string Describe()
    {
        var parts = new List<string> { $"целей {Targets.Count.ToString(CultureInfo.InvariantCulture)}" };

        if (Thresholds.Count > 0)
        {
            parts.Add($"порогов {Thresholds.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        if (Monitors.Count > 0)
        {
            parts.Add($"мониторов {Monitors.Count.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(", ", parts);
    }
}

/// <summary>Какой профиль похож на текущую сеть.</summary>
/// <param name="Profile">Профиль.</param>
/// <param name="Score">Вес совпадения примет.</param>
/// <param name="Because">Что именно совпало — показывается человеку.</param>
public sealed record ProfileGuess(NetworkProfile Profile, int Score, string Because);

/// <summary>Подбор профиля по текущей сети.</summary>
public static class ProfileMatcher
{
    /// <summary>Ниже этого веса совпадение не считается узнаванием.</summary>
    /// <remarks>
    /// Совпадения одной подсети мало: 192.168.1.0/24 стоит у половины сетей мира,
    /// и переключить по ней профиль значило бы подменить пороги на основании
    /// самого распространённого совпадения в мире.
    /// </remarks>
    public const int Confident = 5;

    /// <summary>
    /// Ищет профиль, похожий на текущую сеть.
    /// </summary>
    /// <remarks>
    /// Возвращает догадку, а не решение. Переключать профиль сам продукт не должен:
    /// смена профиля меняет пороги и состав работающих мониторов, а делать это молча
    /// значило бы поменять смысл измерений за спиной оператора.
    /// </remarks>
    public static ProfileGuess? Guess(IReadOnlyList<NetworkProfile> profiles, NetworkSignature current)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(current);

        if (current.IsEmpty)
        {
            return null;
        }

        var best = profiles
            .Where(p => !p.Signature.IsEmpty)
            .Select(p => new ProfileGuess(p, p.Signature.Match(current), Because(p.Signature, current)))
            .Where(g => g.Score >= Confident)
            .OrderByDescending(g => g.Score)
            .ToList();

        // Ничья — не догадка: два профиля с одинаковым весом означают, что примет
        // не хватает, и выбрать за человека нельзя.
        return best.Count > 0 && (best.Count == 1 || best[0].Score > best[1].Score) ? best[0] : null;
    }

    private static string Because(NetworkSignature profile, NetworkSignature current)
    {
        var reasons = new List<string>();

        if (Same(profile.GatewayMac, current.GatewayMac))
        {
            reasons.Add($"MAC шлюза {profile.GatewayMac}");
        }

        if (Same(profile.GatewayAddress, current.GatewayAddress))
        {
            reasons.Add($"адрес шлюза {profile.GatewayAddress}");
        }

        if (Same(profile.Subnet, current.Subnet))
        {
            reasons.Add($"подсеть {profile.Subnet}");
        }

        return reasons.Count == 0 ? "совпадений нет" : "совпало: " + string.Join(", ", reasons);
    }

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a)
        && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
