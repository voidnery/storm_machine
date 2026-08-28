namespace StormMachine.Domain.Capabilities;

/// <summary>
/// Уровень зависимостей возможности.
/// </summary>
/// <remarks>
/// Градация из анализа §3. Нужна не для красоты списка: она отвечает на вопрос,
/// который задают при выборе инструмента, — «что заработает сразу, а за что придётся
/// платить установкой драйверов и выпрашиванием паролей у сетевиков».
/// </remarks>
public enum CapabilityLevel
{
    /// <summary>Уровень 0. Ничего не требует: ни прав, ни драйверов, ни учётных данных.</summary>
    Core,

    /// <summary>Уровень 1. Требует учётных данных оборудования (SNMP).</summary>
    Snmp,

    /// <summary>Уровень 2. Требует установленного Npcap.</summary>
    Capture,
}

/// <summary>
/// Доступна ли возможность прямо сейчас и что мешает.
/// </summary>
/// <remarks>
/// Состояний много, потому что «недоступно» — не ответ. Оператору нужно знать, что
/// именно сделать: перезапустить с правами, поставить драйвер, положить файл базы,
/// сопрячь агента — или ничего, потому что этого пока нет в продукте.
/// </remarks>
public enum CapabilityState
{
    /// <summary>Работает сейчас.</summary>
    Available,

    /// <summary>Работает, но не в полную силу.</summary>
    Limited,

    NeedsElevation,

    NeedsCredentials,

    NeedsDriver,

    /// <summary>Нужен файл базы, который продукт не распространяет.</summary>
    NeedsData,

    /// <summary>Нужна вторая точка измерения.</summary>
    NeedsAgent,

    /// <summary>Запланировано, но ещё не сделано.</summary>
    Planned,
}

/// <summary>
/// Одна возможность продукта с честной картиной её доступности.
/// </summary>
/// <remarks>
/// UX-принцип 6 (docs/01-analysis.md §9.5): <b>недоступное не прячется, а объясняется</b>.
/// Спрятанная возможность выглядит как отсутствующая, и оператор либо ищет её
/// в другом инструменте, либо считает продукт неполным. Показанная с причиной —
/// это задача, которую можно решить.
/// </remarks>
public sealed record Capability
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    /// <summary>Что она даёт — на языке задачи, а не механизма.</summary>
    public required string About { get; init; }

    public required CapabilityLevel Level { get; init; }

    public required CapabilityState State { get; init; }

    /// <summary>Почему состояние именно такое прямо сейчас, на этой машине.</summary>
    public string? Detail { get; init; }

    /// <summary>Что сделать, чтобы заработало. Пусто — делать нечего.</summary>
    public string? HowToEnable { get; init; }

    /// <summary>Куда пойти: адрес сайта или команда.</summary>
    public string? Where { get; init; }

    /// <summary>Итерация, в которой возможность появится. Только для запланированных.</summary>
    public string? Iteration { get; init; }

    public bool IsUsable => State is CapabilityState.Available or CapabilityState.Limited;
}

/// <summary>
/// Что продукт может на этой машине.
/// </summary>
/// <remarks>
/// Сводка считается по фактам, а не по намерениям: права процесса, наличие драйвера,
/// сопряжённые агенты, лежащие рядом файлы баз. Один и тот же выпуск на двух машинах
/// умеет разное, и притворяться иначе значило бы обещать за чужую систему.
/// </remarks>
public sealed record CapabilityReport
{
    public required IReadOnlyList<Capability> Capabilities { get; init; }

    public required bool IsElevated { get; init; }

    /// <summary>Самый высокий уровень, доступный хотя бы частично.</summary>
    public CapabilityLevel Highest =>
        Capabilities.Where(c => c.IsUsable).Select(c => c.Level).DefaultIfEmpty(CapabilityLevel.Core).Max();

    public int UsableCount => Capabilities.Count(c => c.IsUsable);

    public int BlockedCount => Capabilities.Count(c => !c.IsUsable && c.State != CapabilityState.Planned);

    public int PlannedCount => Capabilities.Count(c => c.State == CapabilityState.Planned);

    public IEnumerable<Capability> OfLevel(CapabilityLevel level) => Capabilities.Where(c => c.Level == level);

    /// <summary>Состояние уровня целиком — по худшему из того, что в нём есть.</summary>
    /// <remarks>
    /// Уровень, где работает половина, не «доступен»: оператор, прочитавший «доступно»,
    /// упрётся в неработающую половину в самый неподходящий момент.
    /// </remarks>
    public CapabilityState StateOf(CapabilityLevel level)
    {
        var items = OfLevel(level).ToList();

        if (items.Count == 0)
        {
            return CapabilityState.Planned;
        }

        if (items.All(c => c.State == CapabilityState.Available))
        {
            return CapabilityState.Available;
        }

        if (items.Any(c => c.IsUsable))
        {
            return CapabilityState.Limited;
        }

        // Все недоступны — показываем ту причину, которая встречается чаще прочих:
        // она и есть то, что надо сделать первым.
        return items
            .GroupBy(c => c.State)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;
    }
}
