using StormMachine.Domain.Reports;
using StormMachine.Domain.Topology;

namespace StormMachine.Application.Abstractions;

/// <summary>Хранилище эталонов.</summary>
/// <remarks>
/// В той же базе, что журнал и всё остальное: эталон ссылается на прогон, с которого
/// снят, и без него превращается в набор чисел неизвестного происхождения.
/// </remarks>
public interface IBaselineStore
{
    Task<IReadOnlyList<Baseline>> ListAsync(BaselineQuery query, CancellationToken cancellationToken = default);

    Task<Baseline?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Ищет по имени, его началу или началу идентификатора.</summary>
    Task<Baseline?> FindAsync(string nameOrId, CancellationToken cancellationToken = default);

    Task SaveAsync(Baseline baseline, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>
/// Расчёт расположения узлов карты.
/// </summary>
/// <remarks>
/// Порт, а не статический метод в клиенте, потому что показов карты стало два:
/// полотно на экране и схема в отчёте. Две раскладки давали бы две разные картины
/// одной сети, а карта, выглядящая в документе иначе, чем на экране, обесценивает
/// и документ, и экран.
/// <para>
/// Реализация тянет чужой движок раскладки, поэтому живёт в инфраструктуре: слою
/// приложения он не нужен, а графическому клиенту ссылаться на инфраструктуру нельзя.
/// </para>
/// </remarks>
public interface ITopologyLayout
{
    PlacedGraph Arrange(TopologyGraph graph);
}
