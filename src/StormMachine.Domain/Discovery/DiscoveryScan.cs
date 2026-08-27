namespace StormMachine.Domain.Discovery;

/// <summary>Одно сканирование сети.</summary>
public sealed record DiscoveryScan
{
    public required Guid Id { get; init; }

    /// <summary>Что сканировали — в том виде, как задал оператор.</summary>
    public required string Range { get; init; }

    /// <summary>Через какой интерфейс. Без этого два скана несопоставимы.</summary>
    public required string InterfaceName { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Сколько адресов опрошено.</summary>
    public required int Probed { get; init; }

    public required bool WasCancelled { get; init; }

    public IReadOnlyList<Device> Devices { get; init; } = [];

    public int Responded => Devices.Count(d => d.IsOnline);

    public int WithMac => Devices.Count(d => d.MacAddress is not null);

    public TimeSpan? Duration => CompletedUtc is { } completed ? completed - StartedUtc : null;
}

/// <summary>Ход сканирования — для показа, пока оно идёт.</summary>
public sealed record DiscoveryProgress
{
    public required int Probed { get; init; }

    public required int Total { get; init; }

    public required int Found { get; init; }

    /// <summary>Устройство, найденное только что. <c>null</c> — просто движение счётчика.</summary>
    public Device? Device { get; init; }

    public double Percent => Total == 0 ? 0 : Probed * 100.0 / Total;
}

/// <summary>Запись о выполненном активном действии.</summary>
/// <remarks>
/// Сканирование чужой сети — активное действие, и продукт обязан вести его журнал:
/// требование раздела «Этика» в README. Журнал ведётся не для галочки — он отвечает
/// на вопрос «кто и когда трогал эту сеть», который рано или поздно задают.
/// </remarks>
public sealed record AuditEntry
{
    public required Guid Id { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>Что сделано: <c>discovery</c>, <c>probe</c>.</summary>
    public required string Action { get; init; }

    /// <summary>По какой цели или диапазону.</summary>
    public required string Target { get; init; }

    /// <summary>Кто запустил — учётная запись Windows.</summary>
    public required string Operator { get; init; }

    /// <summary>Подробности: интерфейс, темп, число адресов.</summary>
    public string? Details { get; init; }
}
