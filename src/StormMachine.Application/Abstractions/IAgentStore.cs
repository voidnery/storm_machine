using StormMachine.Domain.Agents;

namespace StormMachine.Application.Abstractions;

/// <summary>
/// Хранилище сопряжённых агентов и собственной личности клиента.
/// </summary>
/// <remarks>
/// Личность клиента лежит рядом с агентами не для удобства: потеряв её, клиент теряет
/// все сопряжения разом, потому что для агентов он станет незнакомцем с другим отпечатком.
/// Хранить их порознь значило бы допустить состояние, в котором список агентов есть,
/// а подключиться к ним нельзя.
/// </remarks>
public interface IAgentStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>Контейнер личности клиента. <c>null</c> — личности ещё нет.</summary>
    Task<byte[]?> LoadIdentityAsync(CancellationToken cancellationToken = default);

    Task SaveIdentityAsync(byte[] container, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RemoteAgent>> ListAsync(CancellationToken cancellationToken = default);

    Task<RemoteAgent?> FindAsync(string thumbprintOrName, CancellationToken cancellationToken = default);

    Task SaveAsync(RemoteAgent agent, CancellationToken cancellationToken = default);

    Task<bool> ForgetAsync(string thumbprint, CancellationToken cancellationToken = default);
}
