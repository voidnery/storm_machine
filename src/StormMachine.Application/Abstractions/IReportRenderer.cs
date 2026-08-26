using StormMachine.Domain.Results;

namespace StormMachine.Application.Abstractions;

/// <summary>Что превращать в отчёт.</summary>
public sealed record ReportRequest
{
    public required StoredRun Run { get; init; }

    /// <summary>Заголовок документа. Пусто — берётся из пробы и цели.</summary>
    public string? Title { get; init; }

    /// <summary>Кто сформировал отчёт. Попадает в подпись документа.</summary>
    public string? Author { get; init; }

    /// <summary>Рисовать ли график. Для форм без временного ряда бессмысленно.</summary>
    public bool IncludeChart { get; init; } = true;
}

/// <summary>Готовый документ.</summary>
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
