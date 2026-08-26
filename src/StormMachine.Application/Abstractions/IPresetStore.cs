using StormMachine.Domain.Presets;

namespace StormMachine.Application.Abstractions;

/// <summary>
/// Хранилище пресетов.
/// </summary>
/// <remarks>
/// Живёт в той же базе, что и журнал прогонов: пресет и его результаты — части одной
/// истории, и разносить их по разным файлам значило бы усложнить перенос и резервную
/// копию ради несуществующей выгоды.
/// </remarks>
public interface IPresetStore
{
    Task<IReadOnlyList<Preset>> ListAsync(PresetQuery query, CancellationToken cancellationToken = default);

    Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Ищет пресет по имени без учёта регистра.</summary>
    Task<Preset?> FindByNameAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Создаёт или обновляет пресет.
    /// </summary>
    /// <remarks>
    /// Версия увеличивается только при изменении того, что влияет на измерение:
    /// пробы, цели или параметров. Переименование или правка описания версию не трогают —
    /// иначе счётчик версий перестал бы что-либо значить.
    /// </remarks>
    Task<Preset> SaveAsync(Preset preset, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Отмечает факт запуска: счётчик и время последнего использования.</summary>
    Task RecordRunAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Все теги, встречающиеся в библиотеке.</summary>
    Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default);
}
