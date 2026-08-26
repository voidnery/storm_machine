using StormMachine.Domain.Measurements;
using StormMachine.Domain.Targets;

namespace StormMachine.Application.Probes;

/// <summary>Запрос на выполнение пробы: цель плюс значения объявленных параметров.</summary>
public sealed record ProbeRequest
{
    public required Target Target { get; init; }

    public IReadOnlyDictionary<string, object?> Parameters { get; init; } =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    public T GetParameter<T>(string name, T fallback)
    {
        if (!Parameters.TryGetValue(name, out var raw) || raw is null)
        {
            return fallback;
        }

        return raw is T typed ? typed : (T)Convert.ChangeType(raw, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Ошибка в значениях параметров пробы.</summary>
public sealed record ProbeValidationError(string ParameterName, string Message);

/// <summary>
/// Проба — плагин. Единый интерфейс для всех измерений: ICMP, TCP, HTTP, DNS, throughput.
/// </summary>
/// <remarks>
/// Возвращает <b>поток</b> сэмплов, а не готовый результат, по двум причинам:
/// живой график должен обновляться по ходу измерения, а прерванный прогон обязан
/// сохранить то, что успел измерить (принцип 3, docs/01-analysis.md §8.2).
/// </remarks>
public interface IProbe
{
    ProbeDescriptor Descriptor { get; }

    /// <summary>Проверяет параметры до запуска. Пустой список — всё в порядке.</summary>
    IReadOnlyList<ProbeValidationError> Validate(ProbeRequest request);

    /// <summary>
    /// Выполняет измерение, отдавая сэмплы по мере получения.
    /// Обязана корректно завершаться по <paramref name="cancellationToken"/>.
    /// </summary>
    /// <param name="observer">
    /// Побочный канал для структурных фактов и разрешённого адреса. Пробы, которым нечего
    /// сообщать сверх чисел, им не пользуются.
    /// </param>
    IAsyncEnumerable<Sample> ExecuteAsync(
        ProbeRequest request,
        IProbeObserver observer,
        CancellationToken cancellationToken);
}

/// <summary>Реестр доступных проб. Наполняется через внедрение зависимостей.</summary>
public interface IProbeRegistry
{
    IReadOnlyList<ProbeDescriptor> Descriptors { get; }

    bool TryGet(string name, out IProbe probe);
}
