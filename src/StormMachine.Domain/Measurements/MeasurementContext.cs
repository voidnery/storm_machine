namespace StormMachine.Domain.Measurements;

/// <summary>Тип сетевого адаптера. Определяет, можно ли доверять точности измерения.</summary>
public enum AdapterKind
{
    Unknown,
    Physical,
    Wireless,

    /// <summary>Виртуальный коммутатор гипервизора: Hyper-V, VMware, VirtualBox.</summary>
    Virtual,

    Vpn,
    Tunnel,
    Loopback,
}

/// <summary>
/// Условия, в которых выполнено измерение.
/// </summary>
/// <remarks>
/// Без этих данных результаты несопоставимы между запусками и бесполезны в отчёте
/// (принцип 12, docs/01-analysis.md §8.2).
/// <para>
/// Замеры на стенде показали, почему это не формальность: через виртуальный коммутатор
/// Hyper-V p99 оказался в 18 раз выше p50, причём источник шума — не наш код и не сборщик
/// мусора, а сам коммутатор. Без предупреждения оператор припишет джиттер гипервизора
/// своей сети (docs/02-research.md §3.1).
/// </para>
/// </remarks>
public sealed record MeasurementContext
{
    public required string InterfaceName { get; init; }

    public required AdapterKind AdapterKind { get; init; }

    public string? InterfaceAddress { get; init; }

    /// <summary>
    /// Калибровочный базис: накладные расходы измерительного стека, измеренные на loopback
    /// и вычтенные из результата. На стенде — около 0.27 мс.
    /// </summary>
    public required double CalibrationBaselineMs { get; init; }

    public required string ProductVersion { get; init; }

    public required Methodology Methodology { get; init; }

    /// <summary>Внешняя служба, если измерение опиралось на неё (например, «NDT7»).</summary>
    public string? Backend { get; init; }

    /// <summary>
    /// Профиль сетевого окружения, активный на момент измерения.
    /// </summary>
    /// <remarks>
    /// Записывается вместе с остальными условиями и по той же причине: измерения
    /// из разных мест несопоставимы. Через полгода отличить замер у заказчика
    /// от замера в офисе иначе будет нечем — а сравнивать их между собой нельзя.
    /// <para>
    /// Пусто у прогонов, сделанных до появления профилей, и у тех, где профиль
    /// не выбран. Это не ошибка: продукт работает и без профилей.
    /// </para>
    /// </remarks>
    public string? Profile { get; init; }

    public required DateTimeOffset StartedUtc { get; init; }

    /// <summary>
    /// Можно ли доверять абсолютным значениям. Через виртуальный коммутатор, VPN или туннель —
    /// нет: они добавляют собственную задержку и собственный джиттер.
    /// </summary>
    public bool IsTimingTrustworthy =>
        AdapterKind is AdapterKind.Physical or AdapterKind.Wireless or AdapterKind.Loopback;

    /// <summary>
    /// Предупреждение для оператора и для секции «Условия измерения» в отчёте.
    /// <c>null</c>, если условия нормальные.
    /// </summary>
    public string? TimingWarning => AdapterKind switch
    {
        AdapterKind.Virtual =>
            "Измерение идёт через виртуальный коммутатор. Он вносит собственную задержку и джиттер — "
            + "выбросы могут не иметь отношения к тестируемой сети.",
        AdapterKind.Vpn =>
            "Измерение идёт через VPN. Задержка включает шифрование и путь до узла VPN.",
        AdapterKind.Tunnel =>
            "Измерение идёт через туннель. Результат отражает свойства туннеля, а не физической сети.",
        AdapterKind.Unknown =>
            "Тип сетевого адаптера определить не удалось. Точность измерения не гарантирована.",
        _ => null,
    };
}
