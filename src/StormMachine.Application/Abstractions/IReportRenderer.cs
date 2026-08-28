using StormMachine.Domain.Monitors;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Results;
using StormMachine.Domain.Topology;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.Abstractions;

/// <summary>
/// Вид документа.
/// </summary>
/// <remarks>
/// Шаблонов четыре, потому что читателей четыре, и они спрашивают разное. Технический
/// отвечает «что именно измерено»; сводка — «что это значит для дела»; акт — «работа
/// принята, вот основания»; SLA — «выполнено ли обещание за период». Один документ
/// на всех был бы длинным для руководителя и поверхностным для инженера.
/// </remarks>
public enum ReportTemplate
{
    /// <summary>Технический: измерения целиком, с графиками, рядами и фактами.</summary>
    Technical,

    /// <summary>Сводка для решения: итог, главные числа, что делать. Одна-две страницы.</summary>
    Executive,

    /// <summary>Акт тестирования: реквизиты, схема сети, проверки, вывод и место подписи.</summary>
    Acceptance,

    /// <summary>SLA: доступность за период, инциденты, бюджет ошибок.</summary>
    ServiceLevel,
}

/// <summary>Раздел отчёта о доступности.</summary>
/// <param name="Monitor">Монитор, о котором идёт речь.</param>
/// <param name="Availability">Посчитанная доступность за период.</param>
/// <param name="Checks">Проверки периода — из них строится полоса состояния.</param>
public sealed record ServiceLevelSection(
    Monitor Monitor,
    Availability Availability,
    IReadOnlyList<MonitorCheck> Checks);

/// <summary>Что превращать в отчёт.</summary>
/// <remarks>
/// Прогонов может быть несколько: акт приёмки покрывает проверку целиком, а не одно
/// измерение. Один прогон — частный случай, и для него есть <see cref="ForRun"/>.
/// </remarks>
public sealed record ReportRequest
{
    public ReportTemplate Template { get; init; } = ReportTemplate.Technical;

    /// <summary>Заголовок документа. Пусто — берётся из шаблона и содержимого.</summary>
    public string? Title { get; init; }

    /// <summary>Кто сформировал отчёт. Попадает в подпись документа.</summary>
    public string? Author { get; init; }

    /// <summary>Заказчик — в реквизитах акта.</summary>
    public string? Customer { get; init; }

    /// <summary>Объект: площадка, филиал, помещение.</summary>
    public string? Site { get; init; }

    /// <summary>
    /// Вывод, написанный человеком.
    /// </summary>
    /// <remarks>
    /// Продукт вывода за оператора не пишет. Он показывает измеренное и вердикты
    /// по заданным порогам; «сеть пригодна для эксплуатации» — утверждение, за которое
    /// отвечает подписавший, и сочинять его за него было бы подлогом.
    /// </remarks>
    public string? Conclusion { get; init; }

    public IReadOnlyList<StoredRun> Runs { get; init; } = [];

    /// <summary>Схема сети. Пусто — раздела не будет.</summary>
    public TopologyGraph? Topology { get; init; }

    public ServiceLevelSection? ServiceLevel { get; init; }

    /// <summary>Сравнения с эталонами — раздел «было / стало».</summary>
    public IReadOnlyList<BaselineComparison> Baselines { get; init; } = [];

    /// <summary>Рисовать ли графики. Для форм без временного ряда бессмысленно.</summary>
    public bool IncludeCharts { get; init; } = true;

    public static ReportRequest ForRun(
        StoredRun run,
        string? title = null,
        string? author = null,
        bool includeChart = true) => new()
        {
            Template = ReportTemplate.Technical,
            Title = title,
            Author = author,
            Runs = [run],
            IncludeCharts = includeChart,
        };
}

/// <summary>Готовый файл.</summary>
public sealed record RenderedReport
{
    public required byte[] Content { get; init; }

    public required string FileExtension { get; init; }

    public required string SuggestedFileName { get; init; }
}

/// <summary>
/// Формирование отчёта.
/// </summary>
/// <remarks>
/// Интерфейс существует не ради абстракции как таковой, а ради конкретного риска:
/// выбранный движок PDF бесплатен при определённых условиях, и они могут перестать
/// выполняться. За этой границей замена стоит день, без неё — месяц
/// (docs/02-research.md, <c>R-11</c>).
/// <para>
/// Отчёт делается рано, в И-6, намеренно: его форма определяет, какие метаданные
/// обязано сохранять измерение. Отложи мы отчёт к концу — обнаружили бы нехватку данных,
/// когда исправлять дорого.
/// </para>
/// </remarks>
public interface IReportRenderer
{
    /// <summary>Понятное имя формата для интерфейса: «PDF».</summary>
    string Format { get; }

    Task<RenderedReport> RenderAsync(ReportRequest request, CancellationToken cancellationToken = default);
}
