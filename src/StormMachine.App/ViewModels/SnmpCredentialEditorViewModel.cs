using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using StormMachine.Domain.Snmp;

namespace StormMachine.App.ViewModels;

/// <summary>Версия протокола — выбор в форме.</summary>
public sealed record VersionOption(SnmpVersion Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Алгоритм проверки подлинности — выбор в форме.</summary>
public sealed record AuthOption(SnmpAuthProtocol Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>Алгоритм шифрования — выбор в форме.</summary>
public sealed record PrivacyOption(SnmpPrivacyProtocol Value, string Title)
{
    public override string ToString() => Title;
}

/// <summary>
/// Форма набора учётных данных SNMP.
/// </summary>
/// <remarks>
/// Пароли вводятся полями со скрытым текстом и в предпросмотр не попадают. В консоли
/// их спрашивают отдельно от команды по той же причине: набранный ключом пароль
/// остаётся в истории оболочки, в списке процессов и в логах терминала.
/// <para>
/// Предупреждения об устаревших алгоритмах показываются <b>до</b> сохранения, а не
/// после: сказать «MD5 защитой считать нельзя» имеет смысл тогда, когда человек ещё
/// выбирает, а не когда уже выбрал.
/// </para>
/// </remarks>
public sealed partial class SnmpCredentialEditorViewModel : ObservableObject
{
    public SnmpCredentialEditorViewModel()
    {
        Versions =
        [
            new(SnmpVersion.V2c, "v2c — строка сообщества"),
            new(SnmpVersion.V3, "v3 — пользователь, проверка, шифрование"),
            new(SnmpVersion.V1, "v1 — только 32-разрядные счётчики"),
        ];

        Auths =
        [
            new(SnmpAuthProtocol.Sha256, "SHA-256"),
            new(SnmpAuthProtocol.Sha384, "SHA-384"),
            new(SnmpAuthProtocol.Sha512, "SHA-512"),
            new(SnmpAuthProtocol.Sha1, "SHA-1 (устарел)"),
            new(SnmpAuthProtocol.Md5, "MD5 (устарел)"),
            new(SnmpAuthProtocol.None, "без проверки"),
        ];

        Privacies =
        [
            new(SnmpPrivacyProtocol.Aes128, "AES-128"),
            new(SnmpPrivacyProtocol.Aes192, "AES-192"),
            new(SnmpPrivacyProtocol.Aes256, "AES-256"),
            new(SnmpPrivacyProtocol.Des, "DES (устарел)"),
            new(SnmpPrivacyProtocol.None, "без шифрования"),
        ];

        Version = Versions[0];
        Auth = Auths[0];
        Privacy = Privacies[0];
    }

    public IReadOnlyList<VersionOption> Versions { get; }

    public IReadOnlyList<AuthOption> Auths { get; }

    public IReadOnlyList<PrivacyOption> Privacies { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private VersionOption _version;

    [ObservableProperty]
    private string _community = string.Empty;

    [ObservableProperty]
    private string _userName = string.Empty;

    [ObservableProperty]
    private AuthOption _auth;

    [ObservableProperty]
    private string _authPassword = string.Empty;

    [ObservableProperty]
    private PrivacyOption _privacy;

    [ObservableProperty]
    private string _privacyPassword = string.Empty;

    [ObservableProperty]
    private int _port = SnmpCredential.DefaultPort;

    [ObservableProperty]
    private int _order;

    [ObservableProperty]
    private string? _error;

    public bool IsV3 => Version.Value == SnmpVersion.V3;

    public bool IsCommunity => !IsV3;

    public bool NeedsAuthPassword => IsV3 && Auth.Value != SnmpAuthProtocol.None;

    public bool NeedsPrivacyPassword => IsV3 && Privacy.Value != SnmpPrivacyProtocol.None;

    /// <summary>Что получится, сказанное словами, — с предупреждениями и без паролей.</summary>
    public string Preview
    {
        get
        {
            var credential = Build(out var problem);

            if (credential is null)
            {
                return problem ?? "заполните имя набора";
            }

            var warnings = credential.Warnings();

            return credential.Describe()
                   + (warnings.Count == 0 ? string.Empty : "\n\n! " + string.Join("\n! ", warnings));
        }
    }

    partial void OnVersionChanged(VersionOption value)
    {
        OnPropertyChanged(nameof(IsV3));
        OnPropertyChanged(nameof(IsCommunity));
        OnPropertyChanged(nameof(NeedsAuthPassword));
        OnPropertyChanged(nameof(NeedsPrivacyPassword));
        Refresh();
    }

    partial void OnAuthChanged(AuthOption value)
    {
        OnPropertyChanged(nameof(NeedsAuthPassword));
        Refresh();
    }

    partial void OnPrivacyChanged(PrivacyOption value)
    {
        OnPropertyChanged(nameof(NeedsPrivacyPassword));
        Refresh();
    }

    partial void OnNameChanged(string value) => Refresh();

    partial void OnUserNameChanged(string value) => Refresh();

    partial void OnCommunityChanged(string value) => Refresh();

    partial void OnAuthPasswordChanged(string value) => Refresh();

    partial void OnPrivacyPasswordChanged(string value) => Refresh();

    /// <summary>
    /// Собирает набор из формы. Пусто — форма ещё не годится.
    /// </summary>
    /// <remarks>
    /// Одна и та же сборка используется и для предпросмотра, и для сохранения: иначе
    /// показанное в форме и записанное в базу разошлись бы, и разошлись бы незаметно.
    /// </remarks>
    public SnmpCredential? Build(out string? problem)
    {
        problem = null;

        if (string.IsNullOrWhiteSpace(Name))
        {
            problem = "Не задано имя набора.";

            return null;
        }

        var credential = new SnmpCredential
        {
            Id = Guid.NewGuid(),
            Name = Name.Trim(),
            Version = Version.Value,
            Community = IsCommunity ? Blank(Community) : null,
            UserName = IsV3 ? Blank(UserName) : null,
            AuthProtocol = IsV3 ? Auth.Value : SnmpAuthProtocol.None,
            AuthPassword = NeedsAuthPassword ? Blank(AuthPassword) : null,
            PrivacyProtocol = IsV3 ? Privacy.Value : SnmpPrivacyProtocol.None,
            PrivacyPassword = NeedsPrivacyPassword ? Blank(PrivacyPassword) : null,
            Port = Port,
            Order = Order,
        };

        var errors = credential.Validate();

        if (errors.Count > 0)
        {
            problem = string.Join("; ", errors);

            return null;
        }

        return credential;
    }

    public void Clear()
    {
        Name = string.Empty;
        Community = string.Empty;
        UserName = string.Empty;
        AuthPassword = string.Empty;
        PrivacyPassword = string.Empty;
        Port = SnmpCredential.DefaultPort;
        Order = 0;
        Error = null;
    }

    public static string PasswordNote =>
        "Пароли хранятся зашифрованными средствами Windows и привязаны к учётной записи.";

    public static string PasswordNoteWhy =>
        "Перенос установки на другую машину их не переносит — и не восстанавливает.";

    public static string CommunityNote =>
        "Строка сообщества идёт по сети открытым текстом. Это метка, а не пароль: "
        + "любой в том же сегменте прочитает её и повторит запросы от вашего имени.";

    private void Refresh()
    {
        _ = Build(out var problem);

        Error = problem;
        OnPropertyChanged(nameof(Preview));
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Подпись порта: показывается, только когда он не стандартный.</summary>
    public string PortHint => Port == SnmpCredential.DefaultPort
        ? "161 — как у всех"
        : $"нестандартный порт {Port.ToString(CultureInfo.InvariantCulture)}";

    partial void OnPortChanged(int value)
    {
        OnPropertyChanged(nameof(PortHint));
        Refresh();
    }
}
