using StormMachine.Domain.Agents;

namespace StormMachine.Application.Abstractions;

/// <summary>Агент, объявивший о себе в локальной сети. Ещё не сопряжённый.</summary>
public sealed record DiscoveredAgent(
    string Address,
    int Port,
    string MachineName,
    string? Product,
    string? ThumbprintPrefix,
    bool IsAlreadyPaired);

/// <summary>Что происходит по ходу сопряжения — для показа оператору, пока он ждёт.</summary>
public sealed record PairingProgress(string Message, string? Code, bool IsDone);

/// <summary>
/// Сопряжение и связь с агентами.
/// </summary>
/// <remarks>
/// Два способа сопряжения соответствуют решению оператора, принятому перед И-12:
/// соединение устанавливает любая сторона. <see cref="PairByDialingAsync"/> — когда
/// входящие на площадке разрешены; <see cref="PairByWaitingAsync"/> — когда прав там
/// нет и звонит агент. Выбор делается один раз и запоминается вместе с агентом:
/// отсутствие прав на площадке верно и завтра.
/// </remarks>
public interface IAgentDirectory
{
    /// <summary>Порт управляющего канала по умолчанию. Значение принадлежит протоколу.</summary>
    int DefaultPort { get; }

    /// <summary>Отпечаток собственной личности клиента — его называют агенту при сопряжении.</summary>
    Task<string> GetOwnThumbprintAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteAgent>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> ForgetAsync(string thumbprintOrName, CancellationToken cancellationToken = default);

    Task<RemoteAgent> RenameAsync(string thumbprintOrName, string name, CancellationToken cancellationToken = default);

    /// <summary>Позвонить агенту и сопрячься. Требует разрешённых входящих на его машине.</summary>
    Task<RemoteAgent> PairByDialingAsync(
        string host,
        int port,
        string code,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Подождать, пока агент позвонит сам.
    /// </summary>
    /// <remarks>
    /// Код придумывает сама реализация и сообщает его через <paramref name="progress"/>
    /// до начала ожидания: оператору надо продиктовать его тому, кто стоит у агента,
    /// и заставлять человека ждать неизвестно чего было бы бессмысленно. Придумывать
    /// код снаружи незачем — правила его составления принадлежат протоколу.
    /// </remarks>
    Task<RemoteAgent> PairByWaitingAsync(
        int port,
        IProgress<PairingProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Проверить связь с агентом, ничего не измеряя.</summary>
    Task<RemoteAgent> CheckAsync(string thumbprintOrName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Послушать, кто объявляет о себе в локальной сети.
    /// </summary>
    /// <remarks>
    /// Избавляет от набора адреса и только от этого: сопряжение всё равно требует кода
    /// и сверки отпечатка. Объявлению доверять нельзя — подделать его может кто угодно.
    /// Работает в пределах одной подсети: агент на удалённой площадке так не найдётся.
    /// </remarks>
    Task<IReadOnlyList<DiscoveredAgent>> BrowseAsync(
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
