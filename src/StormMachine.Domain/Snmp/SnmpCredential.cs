using System.Globalization;

namespace StormMachine.Domain.Snmp;

/// <summary>
/// Версия протокола.
/// </summary>
/// <remarks>
/// Различие не формальное. У первой версии счётчики только 32-разрядные, а такой
/// счётчик октетов на гигабитном порту переполняется за 34 секунды — измерять им
/// загрузку нельзя ничем, кроме удачи. Вторая версия принесла 64-разрядные счётчики
/// и массовое чтение таблиц; третья — единственная, где вообще есть защита.
/// </remarks>
public enum SnmpVersion
{
    /// <summary>RFC 1157. Только 32-разрядные счётчики, чтение таблиц по одному узлу.</summary>
    V1,

    /// <summary>RFC 3416. 64-разрядные счётчики и <c>GETBULK</c>. Практический минимум.</summary>
    V2c,

    /// <summary>RFC 3414. Проверка подлинности и шифрование.</summary>
    V3,
}

/// <summary>Чем подтверждается подлинность сообщения в третьей версии.</summary>
public enum SnmpAuthProtocol
{
    None,

    /// <summary>Устарел. Оставлен ради оборудования, которое не умеет иного.</summary>
    Md5,

    /// <summary>Устарел по тем же причинам, что MD5, но встречается повсеместно.</summary>
    Sha1,

    Sha256,

    Sha384,

    Sha512,
}

/// <summary>Чем шифруется тело сообщения в третьей версии.</summary>
public enum SnmpPrivacyProtocol
{
    None,

    /// <summary>Устарел: 56-разрядный ключ. Оставлен ради старого оборудования.</summary>
    Des,

    Aes128,

    Aes192,

    Aes256,
}

/// <summary>
/// Учётные данные для опроса оборудования.
/// </summary>
/// <remarks>
/// Именованный набор, а не поля в команде: одни и те же данные нужны и разведке,
/// и топологии, и мониторам, а набирать пароль в командной строке — верный способ
/// оставить его в истории оболочки.
/// <para>
/// <b>Пароли здесь лежат открытым текстом ровно столько, сколько живёт объект.</b>
/// В базу они попадают зашифрованными средствами машины: хранилище шифрует их
/// само, а показ подменяет пометкой. Устройство то же, что у паролей почтовых
/// каналов, и по той же причине — базы копируют и присылают в поддержку.
/// </para>
/// </remarks>
public sealed record SnmpCredential
{
    /// <summary>Порт по умолчанию — RFC 3411.</summary>
    public const int DefaultPort = 161;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public required Guid Id { get; init; }

    /// <summary>Имя набора: «свитчи», «ядро», «объект заказчика».</summary>
    public required string Name { get; init; }

    public required SnmpVersion Version { get; init; }

    /// <summary>Строка сообщества для v1 и v2c.</summary>
    public string? Community { get; init; }

    public string? UserName { get; init; }

    public SnmpAuthProtocol AuthProtocol { get; init; } = SnmpAuthProtocol.None;

    public string? AuthPassword { get; init; }

    public SnmpPrivacyProtocol PrivacyProtocol { get; init; } = SnmpPrivacyProtocol.None;

    public string? PrivacyPassword { get; init; }

    public int Port { get; init; } = DefaultPort;

    public TimeSpan Timeout { get; init; } = DefaultTimeout;

    /// <summary>Сколько раз повторить запрос, оставшийся без ответа.</summary>
    public int Retries { get; init; } = 1;

    /// <summary>Порядок перебора: набор с меньшим числом пробуется раньше.</summary>
    public int Order { get; init; }

    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Уровень защиты третьей версии в терминах RFC 3411.
    /// </summary>
    /// <remarks>
    /// Показывается оператору буквально этими словами: между <c>authNoPriv</c>
    /// и <c>authPriv</c> разница в том, читает ли содержимое посторонний в том же
    /// сегменте, и подменять её словом «защищено» нельзя.
    /// </remarks>
    public string SecurityLevel => Version != SnmpVersion.V3
        ? "noAuthNoPriv"
        : AuthProtocol == SnmpAuthProtocol.None
            ? "noAuthNoPriv"
            : PrivacyProtocol == SnmpPrivacyProtocol.None
                ? "authNoPriv"
                : "authPriv";

    /// <summary>
    /// Защищает ли этот набор хоть что-нибудь.
    /// </summary>
    /// <remarks>
    /// Строка сообщества идёт по сети открытым текстом и защитой не является ни в каком
    /// смысле: это метка, а не пароль. Утверждать иначе — вводить в заблуждение того,
    /// кто на основании этого решает, можно ли опрашивать оборудование через транзит.
    /// </remarks>
    public bool IsProtected => Version == SnmpVersion.V3 && AuthProtocol != SnmpAuthProtocol.None;

    /// <summary>Даёт ли версия 64-разрядные счётчики.</summary>
    public bool HasHighCapacityCounters => Version != SnmpVersion.V1;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(Name))
        {
            errors.Add("Не задано имя набора учётных данных.");
        }

        if (Version == SnmpVersion.V3)
        {
            ValidateV3(errors);
        }
        else if (string.IsNullOrWhiteSpace(Community))
        {
            errors.Add("Для версий v1 и v2c нужна строка сообщества.");
        }

        if (Port is < 1 or > 65535)
        {
            errors.Add("Порт вне диапазона 1…65535.");
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromMinutes(1))
        {
            errors.Add("Время ожидания должно быть от нуля до минуты.");
        }

        if (Retries is < 0 or > 5)
        {
            errors.Add("Число повторов должно быть от 0 до 5.");
        }

        return errors;
    }

    private void ValidateV3(List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            errors.Add("Для версии v3 нужно имя пользователя.");
        }

        if (AuthProtocol != SnmpAuthProtocol.None && string.IsNullOrWhiteSpace(AuthPassword))
        {
            errors.Add("Выбрана проверка подлинности, но пароль не задан.");
        }

        if (PrivacyProtocol != SnmpPrivacyProtocol.None && string.IsNullOrWhiteSpace(PrivacyPassword))
        {
            errors.Add("Выбрано шифрование, но пароль не задан.");
        }

        // Шифровать, не подтверждая подлинность, RFC 3414 запрещает: получатель,
        // который не знает, от кого сообщение, не может доверять и его содержимому.
        if (PrivacyProtocol != SnmpPrivacyProtocol.None && AuthProtocol == SnmpAuthProtocol.None)
        {
            errors.Add("Шифрование без проверки подлинности не допускается (RFC 3414 §1.4).");
        }

        // Ключ шифрования выводится из хеша проверки подлинности; хеш короче ключа
        // выводит его дополнением, и стойкость определяется коротким из двух.
        if (PrivacyProtocol == SnmpPrivacyProtocol.Aes256 && AuthProtocol is SnmpAuthProtocol.Md5)
        {
            errors.Add("AES-256 с MD5 бессмыслен: ключ выводится из 128-разрядного хеша.");
        }
    }

    /// <summary>Одна строка для списка.</summary>
    public string Describe() => Version switch
    {
        SnmpVersion.V3 =>
            $"v3, пользователь {UserName}, {SecurityLevel}"
            + (AuthProtocol == SnmpAuthProtocol.None ? string.Empty : $", {Describe(AuthProtocol)}")
            + (PrivacyProtocol == SnmpPrivacyProtocol.None ? string.Empty : $" + {Describe(PrivacyProtocol)}"),
        SnmpVersion.V2c => "v2c, строка сообщества",
        _ => "v1, строка сообщества, только 32-разрядные счётчики",
    } + (Port == DefaultPort ? string.Empty : $", порт {Port.ToString(CultureInfo.InvariantCulture)}");

    public static string Describe(SnmpAuthProtocol protocol) => protocol switch
    {
        SnmpAuthProtocol.Md5 => "MD5",
        SnmpAuthProtocol.Sha1 => "SHA-1",
        SnmpAuthProtocol.Sha256 => "SHA-256",
        SnmpAuthProtocol.Sha384 => "SHA-384",
        SnmpAuthProtocol.Sha512 => "SHA-512",
        _ => "без проверки",
    };

    public static string Describe(SnmpPrivacyProtocol protocol) => protocol switch
    {
        SnmpPrivacyProtocol.Des => "DES",
        SnmpPrivacyProtocol.Aes128 => "AES-128",
        SnmpPrivacyProtocol.Aes192 => "AES-192",
        SnmpPrivacyProtocol.Aes256 => "AES-256",
        _ => "без шифрования",
    };

    /// <summary>
    /// Предупреждения об устаревших алгоритмах.
    /// </summary>
    /// <remarks>
    /// Не запрет: на объекте у заказчика стоит то, что стоит, и отказ работать
    /// со старым оборудованием — не помощь, а отказ от задачи. Но сказать вслух,
    /// что MD5 и DES защитой считать нельзя, продукт обязан.
    /// </remarks>
    public IReadOnlyList<string> Warnings()
    {
        var warnings = new List<string>();

        if (Version == SnmpVersion.V1)
        {
            warnings.Add(
                "Версия v1: счётчики только 32-разрядные. На гигабитном порту такой счётчик "
                + "переполняется за 34 секунды, и загрузку по нему считать нельзя.");
        }

        if (Version != SnmpVersion.V3)
        {
            warnings.Add(
                "Строка сообщества идёт по сети открытым текстом. Это метка, а не пароль: "
                + "любой в том же сегменте прочитает её и повторит запросы от вашего имени.");
        }

        if (AuthProtocol is SnmpAuthProtocol.Md5 or SnmpAuthProtocol.Sha1)
        {
            warnings.Add($"{Describe(AuthProtocol)} устарел. Если оборудование умеет SHA-256 — лучше он.");
        }

        if (PrivacyProtocol == SnmpPrivacyProtocol.Des)
        {
            warnings.Add("DES устарел: 56-разрядный ключ. Если оборудование умеет AES — лучше он.");
        }

        return warnings;
    }
}
