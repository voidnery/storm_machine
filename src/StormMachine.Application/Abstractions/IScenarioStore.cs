using StormMachine.Domain.Scenarios;

namespace StormMachine.Application.Abstractions;

/// <summary>
/// Хранилище сценариев, собранных оператором.
/// </summary>
/// <remarks>
/// Появилось в И-22 и закрыло долг И-11: на экране выбирался готовый шаблон, а собрать
/// свою цепочку из произвольных проб можно было только правкой кода.
/// <para>
/// Шаблоны при этом остаются и остаются зашитыми. Они — начало разговора, а не
/// ограничение: половина собранных сценариев родится из шаблона, который поправили,
/// и держать их в базе значило бы дать оператору испортить то, к чему всегда можно
/// вернуться.
/// </para>
/// </remarks>
public interface IScenarioStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Scenario>> ListAsync(CancellationToken cancellationToken = default);

    Task<Scenario?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Находит по имени или началу идентификатора.</summary>
    Task<Scenario?> FindAsync(string nameOrId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохраняет сценарий.
    /// </summary>
    /// <remarks>
    /// Версия растёт при каждом изменении шагов — так же, как у пресета. Прогон,
    /// сделанный второй редакцией, и прогон пятой сравнивать напрямую нельзя,
    /// и видно это должно быть по номеру, а не по датам.
    /// </remarks>
    Task SaveAsync(Scenario scenario, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
