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

    /// <summary>Все ключи с пояснением и пометкой секрета — для подсказки и для показа.</summary>
    public static IReadOnlyList<(string Key, string About, bool IsSecret)> All { get; } =
    [
        (WebhookUrl, "Адрес, куда слать POST с JSON.", false),
        (WebhookAuthorization, "Значение заголовка Authorization, если приёмник его требует.", true),
        (SmtpHost, "Сервер исходящей почты.", false),
        (SmtpPort, "Порт: 587 для STARTTLS, 465 для неявного TLS, 25 для внутреннего релея.", false),
        (SmtpTls, "Шифровать соединение: да или нет. По умолчанию да.", false),
        (SmtpUser, "Имя пользователя. Пусто — без аутентификации.", false),
        (SmtpPassword, "Пароль. Хранится зашифрованным средствами Windows.", true),
        (SmtpFrom, "Адрес отправителя.", false),
        (SmtpTo, "Получатели через запятую.", false),
    ];
}
