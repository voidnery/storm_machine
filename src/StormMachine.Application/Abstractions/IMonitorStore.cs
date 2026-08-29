using StormMachine.Domain.Monitors;
// Псевдоним обязателен: System.Threading.Monitor попадает в область видимости
// неявными using и перекрывает наш тип. Менять доменное имя из-за этого нельзя —
// «монитор» это слово продукта, а не наша выдумка.
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.Abstractions;

/// <summary>
/// Хранилище мониторов, их состояния, проверок и алертов.
/// </summary>
/// <remarks>
/// В той же базе, что журнал прогонов и сопряжения агентов, — по той же причине:
/// резервная копия одного файла обязана возвращать работающую установку целиком.
/// Расписание без журнала — обещания без истории, журнал без расписания — история
/// без объяснения, откуда она взялась.
/// </remarks>
public interface IMonitorStore
{
    Task<IReadOnlyList<Monitor>> ListAsync(CancellationToken cancellationToken = default);

    Task<Monitor?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Ищет по имени или началу идентификатора — тем, что человек видит в списке.</summary>
    Task<Monitor?> FindAsync(string nameOrId, CancellationToken cancellationToken = default);

    Task SaveAsync(Monitor monitor, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Переписывает назначенный срок.
    /// </summary>
    /// <remarks>
    /// Отдельной операцией, а не через <see cref="SaveAsync"/>: срок меняется на каждой
    /// проверке, и записывать вместе с ним определение монитора значило бы считать
    /// его отредактированным несколько раз в час.
    /// </remarks>
    Task SetNextDueAsync(Guid id, DateTimeOffset? nextDueUtc, CancellationToken cancellationToken = default);

    /// <summary>
    /// Забирает наступивший срок себе. <c>false</c> — его уже забрал кто-то другой.
    /// </summary>
    /// <remarks>
    /// Появилось в И-21 вместе со службой мониторов, и без этого служба была бы
    /// нерабочей по устройству. До неё второй планировщик над той же базой был
    /// экзотикой — «storm monitors watch» рядом с открытым окном; со службой это
    /// <b>обычная</b> конфигурация: служба наблюдает всегда, клиент открывают,
    /// когда надо посмотреть.
    /// <para>
    /// Защита «не запускать второй раз то, что уже идёт» у планировщика есть,
    /// но живёт она в памяти его экземпляра и про соседний процесс ничего не знает.
    /// Два планировщика увидели бы один наступивший срок и оба выполнили бы проверку:
    /// в журнал легли бы два прогона вместо одного, а правило оповещения посчитало бы
    /// два отказа подряд там, где случился один. Гистерезис на этом сработал бы
    /// раньше времени.
    /// </para>
    /// <para>
    /// Отсюда условие в запросе: срок сдвигается <b>только</b> если он всё ещё тот,
    /// который наблюдался. Проигравший видит ноль изменённых строк и просто уходит —
    /// проверку сделает выигравший. Одной атомарной операции достаточно, координация
    /// между процессами не нужна.
    /// </para>
    /// </remarks>
    /// <param name="id">Монитор.</param>
    /// <param name="expectedDueUtc">Срок, который наблюдался при чтении списка.</param>
    /// <param name="nextDueUtc">Куда сдвинуть срок, забрав его.</param>
    Task<bool> TryClaimDueAsync(
        Guid id,
        DateTimeOffset expectedDueUtc,
        DateTimeOffset? nextDueUtc,
        CancellationToken cancellationToken = default);

    Task<MonitorStatus> GetStatusAsync(Guid id, CancellationToken cancellationToken = default);

    Task SaveStatusAsync(Guid id, MonitorStatus status, CancellationToken cancellationToken = default);

    Task AppendCheckAsync(MonitorCheck check, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MonitorCheck>> ListChecksAsync(
        CheckQuery query,
        CancellationToken cancellationToken = default);

    Task AppendAlertAsync(AlertEvent alert, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AlertEvent>> ListAlertsAsync(
        AlertQuery query,
        CancellationToken cancellationToken = default);
}
