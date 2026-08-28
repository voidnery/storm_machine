using StormMachine.Domain.Monitors;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.Abstractions;

/// <summary>Что канал получает для отправки.</summary>
/// <param name="Monitor">Монитор — из него берутся имя, цель и правило.</param>
/// <param name="Event">Событие: подъём, снятие, напоминание.</param>
/// <param name="Check">Проверка, на которой всё случилось.</param>
public sealed record AlertNotification(Monitor Monitor, AlertEvent Event, MonitorCheck Check)
{
    /// <summary>Заголовок одной строкой — то, что видно в уведомлении и в теме письма.</summary>
    public string Subject => Event.Action switch
    {
        AlertAction.Raised => $"Storm Machine: «{Monitor.Name}» — отказ",
        AlertAction.Cleared => $"Storm Machine: «{Monitor.Name}» — норма",
        _ => $"Storm Machine: «{Monitor.Name}» — всё ещё отказ",
    };

    /// <summary>Тело сообщения человеческим языком.</summary>
    public string Body =>
        $"""
         Монитор: {Monitor.Name}
         Цель:    {Monitor.Target.Value}
         Событие: {Event.ActionText}
         Причина: {Event.Reason}
         Проверка: {Check.Summary}
         Время:   {Event.AtUtc.LocalDateTime:dd.MM.yyyy HH:mm:ss}
         """;
}

/// <summary>
/// Канал доставки алертов.
/// </summary>
/// <remarks>
/// Порт, а не иерархия классов: каналы живут в разных слоях. Звук и всплывающее
/// уведомление — дело графического клиента, webhook и почта — инфраструктуры,
/// а запись в ленту работает и без клиента вообще.
/// <para>
/// Ошибку доставки канал обязан выбросить, а не проглотить. Канал, молча
/// не отправивший письмо, хуже отсутствующего: на него рассчитывают.
/// </para>
/// </remarks>
public interface IAlertChannel
{
    /// <summary>Имя, которым канал называют в правиле: <c>звук</c>, <c>webhook</c>, <c>почта</c>.</summary>
    string Name { get; }

    /// <summary>Как канал показывается человеку.</summary>
    string Title { get; }

    /// <summary>
    /// Настроен ли канал по последним прочитанным настройкам.
    /// </summary>
    /// <remarks>
    /// Снимок, а не запрос: настройки лежат в базе, а свойство синхронное. Перед тем
    /// как ему верить, вызывают <see cref="RefreshAsync"/> — иначе канал, настроенный
    /// минуту назад в консоли, в запущенном клиенте остался бы «ненастроенным».
    /// </remarks>
    bool IsConfigured { get; }

    /// <summary>Чего не хватает, если канал не настроен.</summary>
    string? MissingConfiguration { get; }

    /// <summary>Перечитывает настройки канала.</summary>
    Task RefreshAsync(CancellationToken cancellationToken = default);

    Task SendAsync(AlertNotification notification, CancellationToken cancellationToken = default);
}
