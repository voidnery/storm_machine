using StormMachine.Domain.Discovery;
using StormMachine.Domain.Snmp;

namespace StormMachine.Application.Abstractions;

/// <summary>Один узел дерева в том виде, в каком его показывают человеку.</summary>
/// <param name="Oid">Числовой идентификатор: <c>1.3.6.1.2.1.1.1.0</c>.</param>
/// <param name="Type">Тип значения по протоколу: <c>OctetString</c>, <c>Counter64</c>.</param>
public sealed record SnmpVariable(string Oid, string Type, string Value);

/// <summary>
/// Что пошло не так при опросе оборудования.
/// </summary>
/// <remarks>
/// Отдельный тип, потому что причины различаются по тому, что делать дальше.
/// «Не ответило» — возможно, SNMP выключен или закрыт списком доступа. «Отказало
/// в доступе» — учётные данные не те. Смешать их в одно «не удалось» значит заставить
/// человека перебирать оба варианта вслепую.
/// </remarks>
public sealed class SnmpException(string message, SnmpFailure reason, Exception? inner = null)
    : Exception(message, inner)
{
    public SnmpFailure Reason { get; } = reason;
}

/// <summary>Почему опрос не удался.</summary>
public enum SnmpFailure
{
    /// <summary>Устройство молчит: порт закрыт, SNMP выключен или список доступа не пускает.</summary>
    NoAnswer,

    /// <summary>Ответ пришёл, но учётные данные не подошли.</summary>
    Rejected,

    /// <summary>Узла с таким именем или адресом нет.</summary>
    UnknownHost,

    /// <summary>Устройство ответило, но такой ветки у него нет.</summary>
    NoSuchObject,

    /// <summary>Ответ пришёл искажённым или не разобрался.</summary>
    BadAnswer,
}

/// <summary>
/// Опрос оборудования по SNMP. Порт: реализация живёт в инфраструктуре.
/// </summary>
/// <remarks>
/// Только чтение. Записи (<c>SET</c>) в продукте нет и не планируется: инструмент
/// диагностики, умеющий менять конфигурацию оборудования, — это уже другой инструмент,
/// с другой ценой ошибки и другими требованиями к правам. Граница проведена здесь,
/// в порту, а не в договорённости между разработчиками.
/// </remarks>
public interface ISnmpClient
{
    /// <summary>Системная группа. <c>null</c> — устройство не ответило этими данными.</summary>
    Task<SnmpSystem?> GetSystemAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Порты из <c>ifTable</c>, дополненные из <c>ifXTable</c>.</summary>
    Task<IReadOnlyList<SnmpInterface>> GetInterfacesAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Снимок счётчиков всех портов в один момент.</summary>
    Task<IReadOnlyList<InterfaceCounters>> GetCountersAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Соседи по LLDP и CDP.</summary>
    Task<IReadOnlyList<LinkNeighbor>> GetNeighborsAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Таблица пересылки: какой MAC на каком порту.</summary>
    Task<IReadOnlyList<ForwardingEntry>> GetForwardingAsync(
        string host,
        SnmpCredential credential,
        CancellationToken cancellationToken = default);

    /// <summary>Обход произвольной ветки — для случаев, которых продукт не предусмотрел.</summary>
    Task<IReadOnlyList<SnmpVariable>> WalkAsync(
        string host,
        SnmpCredential credential,
        string oid,
        int limit = 512,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Наборы учётных данных SNMP.
/// </summary>
/// <remarks>
/// Пароли шифруются на входе и никогда не отдаются наружу в открытом виде через
/// перечисление: <see cref="ListAsync"/> возвращает их с пометкой вместо значения,
/// а настоящие значения выдаёт только <see cref="GetAsync"/> — тому, кто собирается
/// ими воспользоваться.
/// </remarks>
public interface ISnmpCredentialStore
{
    /// <summary>Все наборы. Пароли заменены пометкой.</summary>
    Task<IReadOnlyList<SnmpCredential>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Набор с настоящими паролями — для опроса.</summary>
    Task<SnmpCredential?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Ищет по имени, его началу или началу идентификатора.</summary>
    Task<SnmpCredential?> FindAsync(string nameOrId, CancellationToken cancellationToken = default);

    Task SaveAsync(SnmpCredential credential, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
