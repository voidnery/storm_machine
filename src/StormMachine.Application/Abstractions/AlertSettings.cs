namespace StormMachine.Application.Abstractions;

/// <summary>
/// Ключи настроек каналов.
/// </summary>
/// <remarks>
/// Собраны в одном месте, потому что их набирают руками: <c>storm alerts set</c>
/// подсказывает по этому же списку, и разъехаться две копии не могут.
/// <para>
/// Живут в слое приложения, а не рядом с каналами: ключ настройки — это договор
/// между продуктом и оператором, и называть его должен уметь клиент, которому
/// ссылаться на инфраструктуру запрещено архитектурным правилом.
/// </para>
/// </remarks>
public static class AlertSettings
{
    public const string WebhookUrl = "alerts.webhook.url";

    /// <summary>Заголовок авторизации целиком, если приёмник его требует.</summary>
    public const string WebhookAuthorization = "alerts.webhook.authorization";

    public const string SmtpHost = "alerts.smtp.host";

    public const string SmtpPort = "alerts.smtp.port";

    /// <summary>Шифровать ли соединение. По умолчанию да.</summary>
    public const string SmtpTls = "alerts.smtp.tls";

    public const string SmtpUser = "alerts.smtp.user";

    public const string SmtpPassword = "alerts.smtp.password";

    public const string SmtpFrom = "alerts.smtp.from";

    /// <summary>Получатели через запятую.</summary>
    public const string SmtpTo = "alerts.smtp.to";

    /// <summary>
    /// Одна настройка канала: ключ, человеческое название, пояснение и секретность.
    /// </summary>
    /// <remarks>
    /// Название добавлено, когда каналы стало можно настроить мышью: <c>alerts.smtp.from</c>
    /// годится как ключ в консоли, но как подпись поля в форме это не название,
    /// а идентификатор. Оба живут рядом по той же причине, по которой рядом живут
    /// ключ и пояснение: форма и консоль обязаны называть одно и то же одинаково.
    /// </remarks>
    public sealed record AlertSetting(string Key, string Title, string About, bool IsSecret)
    {
        /// <summary>Настройка относится к почте, а не к webhook: форма группирует их отдельно.</summary>
        public bool IsMail => Key.StartsWith("alerts.smtp.", StringComparison.Ordinal);
    }

    /// <summary>Все ключи с пояснением и пометкой секрета — для подсказки, показа и формы.</summary>
    public static IReadOnlyList<AlertSetting> All { get; } =
    [
        new(WebhookUrl, "Адрес", "Адрес, куда слать POST с JSON.", false),
        new(WebhookAuthorization, "Заголовок Authorization",
            "Значение заголовка Authorization, если приёмник его требует.", true),
        new(SmtpHost, "Сервер почты", "Сервер исходящей почты.", false),
        new(SmtpPort, "Порт", "587 для STARTTLS, 465 для неявного TLS, 25 для внутреннего релея.", false),
        new(SmtpTls, "Шифровать соединение", "Да или нет. По умолчанию да.", false),
        new(SmtpUser, "Пользователь", "Пусто — без аутентификации.", false),
        new(SmtpPassword, "Пароль", "Хранится зашифрованным средствами Windows.", true),
        new(SmtpFrom, "От кого", "Адрес отправителя.", false),
        new(SmtpTo, "Кому", "Получатели через запятую.", false),
    ];
}
