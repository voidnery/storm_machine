using System.Globalization;

namespace StormMachine.Domain.Snmp;

/// <summary>Состояние порта — <c>ifAdminStatus</c> и <c>ifOperStatus</c>, RFC 2863.</summary>
public enum InterfaceStatus
{
    Unknown = 0,

    Up = 1,

    Down = 2,

    Testing = 3,

    NotPresent = 6,

    /// <summary>Не работает из-за нижележащего интерфейса: подынтерфейс погасшего порта.</summary>
    LowerLayerDown = 7,
}

/// <summary>
/// Порт оборудования — строка <c>ifTable</c>, дополненная из <c>ifXTable</c>.
/// </summary>
/// <remarks>
/// Скорость берётся из <c>ifHighSpeed</c>, когда он есть: <c>ifSpeed</c> 32-разрядный
/// и упирается в 4.29 Гбит/с, отчего десятигигабитный порт по нему неотличим
/// от четырёхгигабитного. Имя предпочитается <c>ifName</c>, описание —
/// <c>ifAlias</c>: первое совпадает с тем, что видно в консоли устройства, второе
/// написал администратор и оно объясняет, куда порт идёт.
/// </remarks>
public sealed record SnmpInterface
{
    /// <summary>Ethernet — <c>ethernetCsmacd</c>, IANA ifType 6.</summary>
    public const int EthernetType = 6;

    public required int Index { get; init; }

    /// <summary>Как порт называет себя: <c>ifName</c>, иначе <c>ifDescr</c>.</summary>
    public required string Name { get; init; }

    /// <summary><c>ifDescr</c> — обычно длиннее и содержит модель.</summary>
    public string? Description { get; init; }

    /// <summary><c>ifAlias</c> — подпись администратора: куда этот порт идёт.</summary>
    public string? Alias { get; init; }

    /// <summary>IANA ifType. 6 — Ethernet, 24 — loopback, 53 — виртуальный, 161 — агрегат.</summary>
    public int Type { get; init; }

    /// <summary>Скорость в битах в секунду. 0 — неизвестна.</summary>
    public long SpeedBitsPerSecond { get; init; }

    public InterfaceStatus AdminStatus { get; init; } = InterfaceStatus.Unknown;

    public InterfaceStatus OperStatus { get; init; } = InterfaceStatus.Unknown;

    public string? PhysicalAddress { get; init; }

    public int Mtu { get; init; }

    /// <summary>Физический порт, а не программная сущность.</summary>
    public bool IsPhysical => Type is EthernetType or 62 or 69 or 117;

    /// <summary>Порт выключен администратором — это не отказ, а решение.</summary>
    public bool IsShutdown => AdminStatus == InterfaceStatus.Down;

    /// <summary>Порт включён, но линка нет. Вот это уже повод посмотреть.</summary>
    public bool IsDark => AdminStatus == InterfaceStatus.Up && OperStatus != InterfaceStatus.Up;

    /// <summary>Как называть порт человеку.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Alias) ? Name : $"{Name} ({Alias})";

    public string DescribeSpeed() => SpeedBitsPerSecond switch
    {
        <= 0 => "скорость неизвестна",
        < 1_000_000 => $"{(SpeedBitsPerSecond / 1_000d).ToString("0.#", CultureInfo.InvariantCulture)} Кбит/с",
        < 1_000_000_000 => $"{(SpeedBitsPerSecond / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)} Мбит/с",
        _ => $"{(SpeedBitsPerSecond / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture)} Гбит/с",
    };

    public string DescribeStatus() => (AdminStatus, OperStatus) switch
    {
        (InterfaceStatus.Down, _) => "выключен администратором",
        (_, InterfaceStatus.Up) => "работает",
        (_, InterfaceStatus.LowerLayerDown) => "нет нижележащего интерфейса",
        (_, InterfaceStatus.NotPresent) => "модуля нет",
        (_, InterfaceStatus.Testing) => "в тестовом режиме",
        (InterfaceStatus.Up, InterfaceStatus.Down) => "включён, линка нет",
        _ => "состояние неизвестно",
    };
}

/// <summary>
/// Снимок счётчиков порта в один момент.
/// </summary>
/// <remarks>
/// Счётчики SNMP растут от загрузки устройства и сами по себе не значат ничего:
/// «17 ошибок» — это 17 ошибок за всё время работы, может быть, за три года.
/// Смысл появляется только в разнице двух снимков, и снимок обязан нести время
/// и <c>sysUpTime</c>, иначе разницу не с чем соотнести.
/// </remarks>
public sealed record InterfaceCounters
{
    public required int Index { get; init; }

    public required DateTimeOffset AtUtc { get; init; }

    /// <summary>Время работы устройства. Уменьшилось — устройство перезагрузилось.</summary>
    public required TimeSpan SysUpTime { get; init; }

    /// <summary>Счётчики взяты из <c>ifXTable</c> и 64-разрядные.</summary>
    public required bool AreHighCapacity { get; init; }

    public long InOctets { get; init; }

    public long OutOctets { get; init; }

    public long InPackets { get; init; }

    public long OutPackets { get; init; }

    public long InErrors { get; init; }

    public long OutErrors { get; init; }

    public long InDiscards { get; init; }

    public long OutDiscards { get; init; }
}

/// <summary>Почему разницу счётчиков посчитать нельзя.</summary>
public enum LoadRefusal
{
    /// <summary>Считается.</summary>
    None,

    /// <summary>Между снимками устройство перезагрузилось: счётчики начались заново.</summary>
    Rebooted,

    /// <summary>Счётчик пошёл назад — переполнение. Сколько раз, узнать нельзя.</summary>
    Wrapped,

    /// <summary>Снимки в неверном порядке или совпадают по времени.</summary>
    BadInterval,

    /// <summary>Скорость порта неизвестна — загрузку в процентах не от чего считать.</summary>
    UnknownSpeed,
}

/// <summary>
/// Загрузка и ошибки порта за промежуток между двумя снимками.
/// </summary>
/// <remarks>
/// Отдельный тип, а не поля в порту: это <b>измерение</b>, у него есть промежуток
/// и есть условия, при которых оно недействительно. Смешать его со свойствами порта
/// значило бы потерять и то, и другое.
/// </remarks>
public sealed record InterfaceLoad
{
    public required int Index { get; init; }

    public required TimeSpan Interval { get; init; }

    public required double InBitsPerSecond { get; init; }

    public required double OutBitsPerSecond { get; init; }

    public long InErrors { get; init; }

    public long OutErrors { get; init; }

    public long InDiscards { get; init; }

    public long OutDiscards { get; init; }

    public long InPackets { get; init; }

    public long OutPackets { get; init; }

    /// <summary>Скорость порта, от которой считалась загрузка. 0 — неизвестна.</summary>
    public long SpeedBitsPerSecond { get; init; }

    /// <summary>Загрузка входящего направления в процентах. <c>null</c> — скорость неизвестна.</summary>
    public double? InPercent => SpeedBitsPerSecond > 0 ? InBitsPerSecond / SpeedBitsPerSecond * 100 : null;

    public double? OutPercent => SpeedBitsPerSecond > 0 ? OutBitsPerSecond / SpeedBitsPerSecond * 100 : null;

    /// <summary>
    /// Доля ошибок среди входящих кадров, в частях на миллион.
    /// </summary>
    /// <remarks>
    /// В долях, а не в штуках: сто ошибок на десять миллионов кадров и сто ошибок
    /// на тысячу — разные события, и различать их обязан инструмент, а не человек
    /// в уме. Умирающий патч-корд виден именно здесь.
    /// </remarks>
    public double? InErrorsPerMillion => InPackets + InErrors > 0
        ? (double)InErrors / (InPackets + InErrors) * 1_000_000
        : null;

    public double? OutErrorsPerMillion => OutPackets + OutErrors > 0
        ? (double)OutErrors / (OutPackets + OutErrors) * 1_000_000
        : null;

    /// <summary>
    /// Загрузка выше сотни процентов.
    /// </summary>
    /// <remarks>
    /// Само по себе это не бывает — значит, врёт что-то одно: заявленная скорость
    /// порта или счётчики. Показать 140% без оговорки значило бы предъявить
    /// оператору невозможное число как измерение.
    /// </remarks>
    public bool IsImplausible => InPercent > 100.5 || OutPercent > 100.5;
}

/// <summary>
/// Разница двух снимков счётчиков.
/// </summary>
/// <remarks>
/// Все причины отказа считать разницу собраны здесь и названы вслух. Молча вернуть
/// правдоподобное число там, где счётчик переполнился, — худшее, что может сделать
/// измерительный инструмент: ошибку в разы никто не заметит.
/// </remarks>
public static class InterfaceLoadCalculator
{
    /// <summary>Предел 32-разрядного счётчика октетов, байт.</summary>
    public const long Counter32Limit = 4_294_967_296L;

    public static InterfaceLoad? Between(
        InterfaceCounters before,
        InterfaceCounters after,
        long speedBitsPerSecond,
        out LoadRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        refusal = LoadRefusal.None;

        var interval = after.AtUtc - before.AtUtc;

        if (interval <= TimeSpan.Zero)
        {
            refusal = LoadRefusal.BadInterval;

            return null;
        }

        // Время работы устройства уменьшилось — оно перезагрузилось, и все счётчики
        // начались с нуля. Разница между «до» и «после» здесь означала бы не трафик,
        // а расстояние до перезагрузки.
        if (after.SysUpTime < before.SysUpTime)
        {
            refusal = LoadRefusal.Rebooted;

            return null;
        }

        if (Backwards(before, after))
        {
            // Переполнение. Поправить нельзя: сколько раз счётчик обернулся, в снимке
            // не написано, а на гигабите 32-разрядный оборачивается за 34 секунды.
            refusal = LoadRefusal.Wrapped;

            return null;
        }

        var seconds = interval.TotalSeconds;

        return new InterfaceLoad
        {
            Index = after.Index,
            Interval = interval,
            InBitsPerSecond = (after.InOctets - before.InOctets) * 8 / seconds,
            OutBitsPerSecond = (after.OutOctets - before.OutOctets) * 8 / seconds,
            InErrors = after.InErrors - before.InErrors,
            OutErrors = after.OutErrors - before.OutErrors,
            InDiscards = after.InDiscards - before.InDiscards,
            OutDiscards = after.OutDiscards - before.OutDiscards,
            InPackets = after.InPackets - before.InPackets,
            OutPackets = after.OutPackets - before.OutPackets,
            SpeedBitsPerSecond = speedBitsPerSecond,
        };
    }

    /// <summary>
    /// За сколько 32-разрядный счётчик октетов обернётся на такой скорости.
    /// </summary>
    /// <remarks>
    /// Отсюда берётся ограничение на промежуток опроса. Гигабит — 34 секунды,
    /// десять гигабит — три с половиной. Опрашивать реже, чем вдвое чаще этого срока,
    /// бессмысленно: разница между «оборот» и «два оборота» не восстанавливается.
    /// </remarks>
    public static TimeSpan? WrapHorizon(long speedBitsPerSecond)
    {
        if (speedBitsPerSecond <= 0)
        {
            return null;
        }

        return TimeSpan.FromSeconds(Counter32Limit * 8.0 / speedBitsPerSecond);
    }

    /// <summary>
    /// Годится ли промежуток опроса для 32-разрядных счётчиков.
    /// </summary>
    /// <remarks>
    /// Правило вдвое чаще оборота — не запас на всякий случай: если между опросами
    /// умещается один оборот, то умещается и два, а различить их нечем.
    /// </remarks>
    public static bool IsIntervalSafe(TimeSpan interval, long speedBitsPerSecond, bool highCapacity)
    {
        if (highCapacity)
        {
            // 64 разряда на 100 Гбит/с оборачиваются за 46 лет. Об этом можно не думать.
            return true;
        }

        var horizon = WrapHorizon(speedBitsPerSecond);

        return horizon is null || interval <= horizon.Value / 2;
    }

    public static string Describe(LoadRefusal refusal) => refusal switch
    {
        LoadRefusal.Rebooted => "устройство перезагрузилось между опросами — счётчики начались заново",
        LoadRefusal.Wrapped => "счётчик переполнился между опросами — сколько раз, узнать нельзя",
        LoadRefusal.BadInterval => "снимки идут не по порядку или сделаны в один момент",
        LoadRefusal.UnknownSpeed => "скорость порта неизвестна — считать загрузку не от чего",
        _ => "считается",
    };

    private static bool Backwards(InterfaceCounters before, InterfaceCounters after) =>
        after.InOctets < before.InOctets
        || after.OutOctets < before.OutOctets
        || after.InPackets < before.InPackets
        || after.OutPackets < before.OutPackets
        || after.InErrors < before.InErrors
        || after.OutErrors < before.OutErrors;
}
