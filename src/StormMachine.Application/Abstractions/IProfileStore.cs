using StormMachine.Domain.Profiles;

namespace StormMachine.Application.Abstractions;

/// <summary>Хранилище профилей сетевого окружения.</summary>
/// <remarks>
/// Активный профиль — свойство установки, а не сеанса: продукт, забывший при
/// перезапуске, где находится оператор, начал бы мерить чужими порогами.
/// </remarks>
public interface IProfileStore
{
    Task<IReadOnlyList<NetworkProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task<NetworkProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Ищет по имени, его началу или началу идентификатора.</summary>
    Task<NetworkProfile?> FindAsync(string nameOrId, CancellationToken cancellationToken = default);

    /// <summary>Профиль, выбранный сейчас. Пусто — работа без профиля.</summary>
    Task<NetworkProfile?> GetActiveAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(NetworkProfile profile, CancellationToken cancellationToken = default);

    /// <summary>
    /// Делает профиль активным. Пустой идентификатор снимает выбор.
    /// </summary>
    /// <remarks>
    /// Активным может быть только один: два одновременно означали бы два набора
    /// порогов на одно измерение.
    /// </remarks>
    Task ActivateAsync(Guid? id, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
