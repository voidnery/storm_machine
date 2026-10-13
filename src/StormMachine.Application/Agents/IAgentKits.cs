namespace StormMachine.Application.Agents;

/// <summary>Что положить в комплект и для кого.</summary>
public sealed record AgentKitRequest
{
    public required AgentPlatform Platform { get; init; }

    /// <summary>Куда положить готовый файл.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Отпечаток клиента — его сверяют на том конце, чтобы не сопрячься не с тем.</summary>
    public required string ClientThumbprint { get; init; }

    /// <summary>Адреса этой машины: по ним агент дозванивается обратно.</summary>
    public IReadOnlyList<string> ClientAddresses { get; init; } = [];

    public required int Port { get; init; }

    /// <summary>
    /// Как зовут эту машину — чтобы человек на том конце понимал, с кем он говорит.
    /// </summary>
    /// <remarks>
    /// Приходит снаружи, а не берётся из окружения процесса: у сервера это чужая
    /// машина в стойке, и ядру знать её имя неоткуда (<c>ServerReadinessTests</c>).
    /// </remarks>
    public required string ClientName { get; init; }
}

/// <summary>Что получилось.</summary>
public sealed record AgentKit(string Path, long Bytes, IReadOnlyList<string> Contents);

/// <summary>
/// Что продукт может выдать на чужой конец.
/// </summary>
/// <remarks>
/// Порт, а не класс: бинари лежат на диске рядом с продуктом, а диск — дело
/// инфраструктуры. Ядру нужно знать только, какие платформы оно вправе предлагать:
/// пункт, который нельзя выдать, — это обещание, которого продукт не выполнит.
/// </remarks>
public interface IAgentBinaries
{
    /// <summary>Есть ли у продукта агент для этой платформы.</summary>
    bool Has(AgentPlatform platform);
}

/// <summary>
/// Сборка комплекта агента — одним файлом, который увозят на другой конец.
/// </summary>
/// <remarks>
/// Порт по той же причине, что и <see cref="StormMachine.Application.Abstractions.IReportRenderer"/>:
/// результат — файл на диске, а запись файлов живёт за портом. Окно и консоль
/// пользуются одним и тем же сборщиком — иначе площадка без графики теряет
/// возможность, а не удобство.
/// </remarks>
public interface IAgentKitBuilder
{
    Task<AgentKit> BuildAsync(AgentKitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Имя написанного внутри комплекта — на него ссылаются и окно, и консоль.</summary>
    static string ReadmeName => "ПРОЧТИ.txt";
}
