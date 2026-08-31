using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Capabilities;
using StormMachine.Application.Profiles;
using StormMachine.Application.Snmp;
using StormMachine.Domain.Capabilities;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Snmp;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Строка списка учётных данных SNMP.</summary>
public sealed record CredentialRow(SnmpCredential Credential)
{
    public string Name => Credential.Name;

    public string About => Credential.Describe();

    public string Order => Credential.Order.ToString(CultureInfo.InvariantCulture);

    /// <summary>Защищает ли набор хоть что-нибудь — видно сразу, без чтения подробностей.</summary>
    public bool IsProtected => Credential.IsProtected;
}

/// <summary>Строка списка профилей.</summary>
public sealed record ProfileRow(NetworkProfile Profile)
{
    public string Name => Profile.Name;

    public string Signature => Profile.Signature.Describe();

    public bool IsActive => Profile.IsActive;

    public string Extras
    {
        get
        {
            var parts = new List<string>();

            if (Profile.Thresholds.Count > 0)
            {
                parts.Add($"порогов {Profile.Thresholds.Count.ToString(CultureInfo.InvariantCulture)}");
            }

            if (Profile.Monitors.Count > 0)
            {
                parts.Add($"мониторов {Profile.Monitors.Count.ToString(CultureInfo.InvariantCulture)}");
            }

            if (Profile.Targets.Count > 0)
            {
                parts.Add($"целей {Profile.Targets.Count.ToString(CultureInfo.InvariantCulture)}");
            }

            return parts.Count == 0 ? Profile.Description ?? "без настроек" : string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Настройки: где продукт находится, чем опрашивает и куда пишет.
/// </summary>
/// <remarks>
/// Экран отвечает на вопросы, которые задают, когда что-то пошло не так:
/// <b>в какой сети мы сейчас</b> и <b>с каким файлом базы мы разговариваем</b>.
/// Сводка возможностей машины жила здесь до И-24 и вынесена во временный раздел
/// «Разработка» по решению оператора: в настройках — настройки.
/// </remarks>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly ProfileService _profiles;
    private readonly IRunStore _runs;
    private readonly ISnmpCredentialStore _credentials;
    private readonly SnmpService _snmp;

    public SettingsPageViewModel(
        NavigationSection section,
        ProfileService profiles,
        IRunStore runs,
        UpdateService updates,
        ISnmpCredentialStore credentials,
        SnmpService snmp,
        IAgentDirectory agents,
        SettingsTransfer transfer,
        IFilePicker picker,
        Application.Storage.RetentionSettings retention)
        : base(section)
    {
        Agents = new AgentsSectionViewModel(agents ?? throw new ArgumentNullException(nameof(agents)));
        Transfer = new TransferSectionViewModel(
            transfer ?? throw new ArgumentNullException(nameof(transfer)),
            picker ?? throw new ArgumentNullException(nameof(picker)));
        Retention = new RetentionSectionViewModel(
            retention ?? throw new ArgumentNullException(nameof(retention)),
            runs ?? throw new ArgumentNullException(nameof(runs)));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _snmp = snmp ?? throw new ArgumentNullException(nameof(snmp));
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
    }

    /// <summary>Форма набора учётных данных SNMP.</summary>
    public SnmpCredentialEditorViewModel Editor { get; } = new();

    /// <summary>
    /// Агенты: сопряжение, список, проверка связи.
    /// </summary>
    /// <remarks>
    /// Живут здесь, а не отдельным разделом навигации, по той же причине, что профили
    /// и учётные данные: это настройка, а не рабочий экран. Оператор заходит сюда,
    /// когда заводит вторую точку измерения, и не возвращается, пока она работает.
    /// </remarks>
    public AgentsSectionViewModel Agents { get; }

    /// <summary>Перенос настроек между машинами: выгрузка и загрузка файла.</summary>
    public TransferSectionViewModel Transfer { get; }

    /// <summary>Политика хранения: горизонты, прикидка и уборка.</summary>
    public RetentionSectionViewModel Retention { get; }

    public ObservableCollection<CredentialRow> Credentials { get; } = [];

    [ObservableProperty]
    private CredentialRow? _selectedCredential;

    [ObservableProperty]
    private bool _isCredentialEditorOpen;

    /// <summary>Узел, на котором проверяют набор.</summary>
    [ObservableProperty]
    private string _probeHost = string.Empty;

    [ObservableProperty]
    private string? _probeResult;

    /// <summary>
    /// Обновление продукта.
    /// </summary>
    /// <remarks>
    /// Живёт в настройках, а не всплывает само: проверка идёт молча, установка —
    /// по команде человека. Подменить версию посреди мониторинга значит разорвать
    /// ряд измерений и не сказать об этом.
    /// </remarks>
    public UpdateService Updates { get; }

    public ObservableCollection<ProfileRow> Profiles { get; } = [];

    [ObservableProperty]
    private ProfileRow? _selectedProfile;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private string _currentSignature = "…";

    [ObservableProperty]
    private string? _guess;

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private string? _errorMessage;

    public static string Version => ProductInfo.Version;

    public string DatabasePath => _runs.Location;

    // Тексты разложены на тезис и обоснование: тезис виден всегда, обоснование —
    // по кнопке карточки. До волны 2 всё это шло сплошными абзацами десятым кеглем,
    // и лучший текст продукта читался как оформление.

    public static string StorageNote =>
        "Путь показан не для полноты: когда журнал выглядит не так, как ожидалось, "
        + "первый вопрос всегда один — с каким файлом мы разговариваем.";

    public static string StorageNoteWhy =>
        "Продукт умеет работать с несколькими базами: проверки не должны попадать "
        + "в рабочую историю. Файл выбирается одним из двух способов ниже.";

    /// <summary>Имя переменной окружения — для чипа, который копируется одним нажатием.</summary>
    public static string StoragePathVariable => StorageEnvironment.PathVariable;

    [RelayCommand]
    private void ToggleCredentialEditor() => IsCredentialEditorOpen = !IsCredentialEditorOpen;

    /// <summary>Заводит набор учётных данных.</summary>
    [RelayCommand]
    private async Task AddCredentialAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        Message = null;

        if (Editor.Build(out var problem) is not { } credential)
        {
            ErrorMessage = problem;

            return;
        }

        await _credentials.SaveAsync(credential, cancellationToken).ConfigureAwait(true);

        Message = $"Набор «{credential.Name}» заведён: {credential.Describe()}";
        IsCredentialEditorOpen = false;

        Editor.Clear();

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteCredentialAsync(CancellationToken cancellationToken)
    {
        if (SelectedCredential is not { } row)
        {
            return;
        }

        await _credentials.DeleteAsync(row.Credential.Id, cancellationToken).ConfigureAwait(true);

        Message = $"Набор «{row.Name}» удалён.";

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Проверяет, отвечает ли узел хоть одним из заведённых наборов.
    /// </summary>
    /// <remarks>
    /// Различить «SNMP выключен» и «учётные данные не те» снаружи нельзя: устройство,
    /// отвергающее запрос, по RFC 3414 §3.2 просто молчит — так задумано, чтобы
    /// молчание не подсказывало подбирающему.
    /// </remarks>
    [RelayCommand]
    private async Task ProbeAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        ProbeResult = null;

        if (string.IsNullOrWhiteSpace(ProbeHost))
        {
            ErrorMessage = "Не задан адрес устройства.";

            return;
        }

        ProbeResult = "Спрашиваю…";

        try
        {
            var reach = await _snmp.ProbeAsync(ProbeHost.Trim(), cancellationToken).ConfigureAwait(true);

            ProbeResult = reach is null
                ? "Не ответил ни одним из заведённых наборов. Различить «SNMP выключен» "
                  + "и «учётные данные не те» снаружи нельзя: отвергнутый запрос устройство "
                  + "оставляет без ответа."
                : $"Отвечает набором «{reach.Credential.Name}»: {reach.System.Name ?? "имя не задано"}, "
                  + $"{reach.System.ShortDescription}, работает {reach.System.DescribeUpTime()}.";
        }
        catch (SnmpException ex)
        {
            ProbeResult = ex.Message;
        }
    }

    public static string SnmpNote =>
        "Наборы пробуются по возрастанию порядка, пока какой-нибудь не подойдёт — "
        + "и только против узла, который назвали вы.";

    public static string SnmpNoteWhy =>
        "Ни словарей, ни обхода подсети в продукте нет: это граница, за которой "
        + "диагностика становится взломом.";

    [RelayCommand]
    private Task CheckUpdateAsync(CancellationToken cancellationToken) => Updates.CheckAsync(cancellationToken);

    [RelayCommand]
    private Task DownloadUpdateAsync(CancellationToken cancellationToken) => Updates.DownloadAsync(cancellationToken);

    [RelayCommand]
    private void ApplyUpdate() => Updates.Apply();

    public static string UpdateNote => "Проверка идёт сама, установка — по команде.";

    public static string UpdateNoteWhy =>
        "Продукт измеряет, и подменить свою версию посреди суточного мониторинга "
        + "значило бы разорвать ряд измерений надвое, не сказав об этом.";

    public static string LicenceNote => "Продукт распространяется по лицензии MIT.";

    public static string LicenceNoteWhy =>
        "Драйвер захвата Npcap не входит в поставку ни при каких условиях: "
        + "его лицензия NPSL это запрещает.";

    public override async Task ActivateAsync(CancellationToken cancellationToken = default)
    {
        await RefreshAsync(cancellationToken).ConfigureAwait(true);
        await Agents.RefreshAsync(cancellationToken).ConfigureAwait(true);
        await Retention.LoadAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Уход с экрана отменяет идущее сопряжение.
    /// </summary>
    /// <remarks>
    /// Ожидание звонка держит слушающий сокет и живёт минутами. Оставить его после
    /// ухода значило бы занять порт: следующая попытка сопряжения упёрлась бы
    /// в «порт занять не удалось» и не объяснила бы, кем.
    /// </remarks>
    public override void Deactivate() => Agents.Dispose();

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;

        try
        {
            var profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(true);
            var chosen = SelectedProfile?.Profile.Id;

            Profiles.Clear();

            foreach (var profile in profiles)
            {
                Profiles.Add(new ProfileRow(profile));
            }

            SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == chosen)
                              ?? Profiles.FirstOrDefault(p => p.IsActive);

            CurrentSignature = _profiles.CurrentSignature().Describe();

            var credentials = await _credentials.ListAsync(cancellationToken).ConfigureAwait(true);

            Credentials.Clear();

            foreach (var credential in credentials)
            {
                Credentials.Add(new CredentialRow(credential));
            }

            OnPropertyChanged(nameof(DatabasePath));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Догадка о текущей сети. Именно догадка: переключение — дело человека.</summary>
    [RelayCommand]
    private async Task DetectAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        Message = null;

        var signature = _profiles.CurrentSignature();

        CurrentSignature = signature.Describe();

        if (signature.IsEmpty)
        {
            Guess = "Узнавать не по чему: ни шлюза, ни подсети определить не удалось.";

            return;
        }

        var guess = await _profiles.DetectAsync(cancellationToken).ConfigureAwait(true);

        if (guess is null)
        {
            Guess = "Похожего профиля нет. Запомните это место кнопкой ниже.";

            return;
        }

        // Продукт не переключает профиль сам: смена профиля меняет пороги и состав
        // работающих мониторов, а делать это молча значит поменять смысл измерений
        // за спиной оператора.
        Guess = guess.Profile.IsActive
            ? $"Похоже на профиль «{guess.Profile.Name}» — {guess.Because}. Он и активен."
            : $"Похоже на профиль «{guess.Profile.Name}» — {guess.Because}. Переключить его нужно вручную.";

        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == guess.Profile.Id) ?? SelectedProfile;
    }

    [RelayCommand]
    private async Task ActivateProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        var changed = await _profiles.ActivateAsync(row.Profile.Id, cancellationToken).ConfigureAwait(true);

        Message = $"Активен профиль «{row.Name}». {Changed(changed)} "
                  + "Имя профиля попадёт в условия каждого следующего измерения.";

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearProfileAsync(CancellationToken cancellationToken)
    {
        var changed = await _profiles.ActivateAsync(null, cancellationToken).ConfigureAwait(true);

        Message = $"Профиль снят: измерения пойдут без пометки о месте. {Changed(changed)}";

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Заводит профиль по приметам сети, в которой машина находится сейчас.</summary>
    [RelayCommand]
    private async Task CaptureHereAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;
        Message = null;

        var name = NewProfileName.Trim();

        if (name.Length == 0)
        {
            ErrorMessage = "Не задано имя профиля.";

            return;
        }

        var signature = _profiles.CurrentSignature();

        if (signature.IsEmpty)
        {
            ErrorMessage = "Примет текущей сети нет: ни шлюза, ни подсети определить не удалось. "
                           + "Профиль без примет заводится, но узнаваться не будет.";
        }

        var profile = new NetworkProfile
        {
            Id = Guid.NewGuid(),
            Name = name,
            Signature = signature,
            CreatedUtc = DateTimeOffset.UtcNow,
        };

        var errors = profile.Validate();

        if (errors.Count > 0)
        {
            ErrorMessage = string.Join("; ", errors);

            return;
        }

        await _profiles.SaveAsync(profile, cancellationToken).ConfigureAwait(true);

        Message = $"Профиль «{name}» заведён по приметам: {signature.Describe()}. "
                  + "Пороги, цели и мониторы добавляются командой storm profiles add.";
        NewProfileName = string.Empty;

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is not { } row)
        {
            return;
        }

        await _profiles.DeleteAsync(row.Profile.Id, cancellationToken).ConfigureAwait(true);

        Message = $"Профиль «{row.Name}» удалён. Мониторы и измерения остались: "
                  + "профиль их не содержал, а только называл.";

        await RefreshAsync(cancellationToken).ConfigureAwait(true);
    }

    private static string Changed(int count) => count == 0
        ? "Состав работающих мониторов не изменился."
        : $"Мониторов переключено: {count.ToString(CultureInfo.InvariantCulture)}.";
}
