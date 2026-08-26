using StormMachine.Domain.Targets;

namespace StormMachine.Domain.Presets;

/// <summary>
/// Именованный тест: проба, цель и параметры.
/// </summary>
/// <remarks>
/// Смысл пресета не в экономии набора текста, а в повторяемости. Измерение, которое
/// нельзя повторить теми же параметрами, не сравнить с прошлым — а сравнение с прошлым
/// и есть то, ради чего продукт существует.
/// <para>
/// Параметры хранятся строками, а не типизированно: их набор задаёт проба своим
/// объявлением, и хранилище не должно знать, что у ICMP есть <c>ttl</c>, а у HTTP —
/// <c>method</c>. Разбор в нужный тип делает сама проба при запуске.
/// </para>
/// </remarks>
public sealed record Preset
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    /// <summary>Имя пробы: <c>ping</c>, <c>http</c>, <c>dns</c>.</summary>
    public required string ProbeName { get; init; }

    public required Target Target { get; init; }

    public IReadOnlyDictionary<string, string?> Parameters { get; init; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>
    /// Версия пресета. Растёт при каждом изменении параметров или цели.
    /// </summary>
    /// <remarks>
    /// Хранить историю версий не требуется: прогон сохраняет фактические параметры,
    /// с которыми выполнялся, и потому самодостаточен. Версия нужна лишь для того,
    /// чтобы было видно — этот результат получен пресетом второй редакции, а сейчас
    /// в библиотеке пятая, и сравнивать их напрямую нельзя.
    /// </remarks>
    public required int Version { get; init; }

    public required DateTimeOffset CreatedUtc { get; init; }

    public required DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Сколько раз пресет запускался. Подсказывает, чем пользуются, а чем нет.</summary>
    public int RunCount { get; init; }

    public DateTimeOffset? LastRunUtc { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Description)
        ? Name
        : $"{Name} — {Description}";

    /// <summary>Сравнивает содержательную часть: изменилось ли то, что влияет на измерение.</summary>
    /// <remarks>
    /// Имя, описание и теги сюда не входят намеренно: переименование пресета не делает
    /// его другим тестом и не должно поднимать версию, иначе счётчик версий перестанет
    /// что-либо значить.
    /// </remarks>
    public bool IsSameMeasurement(Preset other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(ProbeName, other.ProbeName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Target.Kind != other.Target.Kind
            || !string.Equals(Target.Value, other.Target.Value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (Parameters.Count != other.Parameters.Count)
        {
            return false;
        }

        foreach (var (key, value) in Parameters)
        {
            if (!other.Parameters.TryGetValue(key, out var otherValue)
                || !string.Equals(value, otherValue, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>Ошибка в пресете, обнаруженная до сохранения или запуска.</summary>
public sealed record PresetValidationError(string Field, string Message);

/// <summary>Фильтр для списка пресетов.</summary>
public sealed record PresetQuery
{
    public string? ProbeName { get; init; }

    public string? Tag { get; init; }

    /// <summary>Поиск по имени и описанию.</summary>
    public string? Search { get; init; }

    public int Limit { get; init; } = 200;
}
