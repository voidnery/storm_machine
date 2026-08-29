using StormMachine.Domain.Capture;

namespace StormMachine.Application.Abstractions;

/// <summary>Почему захват недоступен.</summary>
public enum CaptureRefusal
{
    /// <summary>Доступен.</summary>
    None,

    /// <summary>Драйвер захвата не установлен.</summary>
    NoDriver,

    /// <summary>Драйвер есть, но не пускает: Npcap умеет ограничивать доступ администраторами.</summary>
    NeedsElevation,

    /// <summary>Драйвер есть, а подходящих адаптеров нет.</summary>
    NoAdapters,
}

/// <summary>Что делать при прослушивании.</summary>
public sealed record CaptureOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Слушать кадры соседства: LLDP и CDP.</summary>
    public bool Neighbors { get; init; } = true;

    /// <summary>Слушать ответы DHCP.</summary>
    public bool Dhcp { get; init; } = true;

    /// <summary>
    /// Верхняя граница по кадрам.
    /// </summary>
    /// <remarks>
    /// Не защита от объёма — фильтр и так отсекает почти всё. Защита от случая,
    /// когда фильтр не применился: без предела прослушивание в нагруженной сети
    /// съело бы память за секунды.
    /// </remarks>
    public int FrameLimit { get; init; } = 100_000;
}

/// <summary>
/// Пассивное прослушивание сети. Порт: реализация живёт в плагине захвата.
/// </summary>
/// <remarks>
/// <b>Только слушать.</b> Ни одного кадра в сеть продукт не отправляет: ни поддельного
/// ARP, ни запроса DHCP, ни попытки заставить оборудование объявиться. Та же граница,
/// что у SNMP без записи, и по той же причине — инструмент диагностики не должен уметь
/// менять то, что измеряет.
/// <para>
/// Драйвер захвата (Npcap) продукт <b>не распространяет ни при каких условиях</b>:
/// лицензия NPSL это запрещает. Отсюда устройство порта: он обязан уметь честно
/// сказать «меня нет» и назвать причину, а не падать при первом обращении.
/// </para>
/// </remarks>
public interface ICaptureProvider
{
    /// <summary>Есть ли драйвер и пускает ли он нас.</summary>
    /// <remarks>
    /// Спрашивается до всякой работы и не бросает исключений: экран возможностей
    /// вызывает это при каждом открытии, и падать там нельзя.
    /// </remarks>
    CaptureRefusal Availability { get; }

    /// <summary>Версия драйвера, если он есть, — для показа в возможностях.</summary>
    string? DriverDescription { get; }

    /// <summary>Адаптеры, на которых можно слушать. Пусто, если драйвера нет.</summary>
    IReadOnlyList<CaptureAdapter> Adapters();

    /// <summary>Слушает заданное время и возвращает то, что услышал.</summary>
    Task<CaptureResult> ListenAsync(
        CaptureAdapter adapter,
        CaptureOptions options,
        CancellationToken cancellationToken = default);
}
