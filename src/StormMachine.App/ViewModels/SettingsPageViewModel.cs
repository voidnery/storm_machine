using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Capabilities;
using StormMachine.Application.Profiles;
using StormMachine.Domain.Capabilities;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Results;

namespace StormMachine.App.ViewModels;

/// <summary>Одна возможность в списке.</summary>
public sealed record CapabilityRow(Capability Capability)
{
    public string Title => Capability.Title;

    public string About => Capability.About;

    public string? Detail => Capability.Detail;

    public string? HowToEnable => Capability.HowToEnable;

    public string? Where => Capability.Where;

    public string StateText => SettingsPageViewModel.Describe(Capability.State)
                               + (Capability.Iteration is { } iteration ? $" · {iteration}" : string.Empty);

    /// <summary>Цвет точки состояния. Тот же словарь, что у мониторов и вердиктов.</summary>
    public VerdictLevel Level => SettingsPageViewModel.LevelOf(Capability.State);
}

/// <summary>Уровень зависимостей со своими возможностями.</summary>
public sealed record CapabilityGroup(string Title, string About, string StateText, IReadOnlyList<CapabilityRow> Items)
{
    public bool HasItems => Items.Count > 0;
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
/// Настройки: что продукт может здесь, где он находится и куда пишет.
/// </summary>
/// <remarks>
/// Экран отвечает на три вопроса, которые задают, когда что-то пошло не так:
/// <b>что вообще доступно на этой машине</b>, <b>в какой сети мы сейчас</b> и
/// <b>с каким файлом базы мы разговариваем</b>. Все три ответа продукт обязан давать
/// сам: догадываться о них по косвенным признакам — работа, которую нельзя перекладывать
/// на человека.
/// </remarks>
public sealed partial class SettingsPageViewModel : PageViewModel
{
    private readonly CapabilityInspector _capabilities;
    private readonly ProfileService _profiles;
    private readonly IRunStore _runs;

    public SettingsPageViewModel(
        NavigationSection section,
        CapabilityInspector capabilities,
        ProfileService profiles,
        IRunStore runs,
        UpdateService updates)
        : base(section)
    {
        _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        Updates = updates ?? throw new ArgumentNullException(nameof(updates));
    }

    /// <summary>
    /// Обновление продукта.
    /// </summary>
    /// <remarks>
    /// Живёт в настройках, а не всплывает само: проверка идёт молча, установка —
    /// по команде человека. Подменить версию посреди мониторинга значит разорвать
    /// ряд измерений и не сказать об этом.
    /// </remarks>
    public UpdateService Updates { get; }

    public ObservableCollection<CapabilityGroup> Levels { get; } = [];

    public ObservableCollection<ProfileRow> Profiles { get; } = [];

    [ObservableProperty]
    private ProfileRow? _selectedProfile;

    [ObservableProperty]
    private string _newProfileName = string.Empty;

    [ObservableProperty]
    private string _summary = "…";

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

    public static string StorageHint =>
        $"Другой файл базы задаётся переменной {StorageEnvironment.PathVariable} или ключом "
        + "--база у консоли. Путь показан не для полноты: когда журнал выглядит не так, "
        + "как ожидалось, первый вопрос всегда один — с каким файлом мы разговариваем.";

    [RelayCommand]
    private Task CheckUpdateAsync(CancellationToken cancellationToken) => Updates.CheckAsync(cancellationToken);

    [RelayCommand]
    private Task DownloadUpdateAsync(CancellationToken cancellationToken) => Updates.DownloadAsync(cancellationToken);

    [RelayCommand]
    private void ApplyUpdate() => Updates.Apply();

    public static string UpdateNote =>
        "Проверка идёт сама, установка — по команде. Продукт измеряет, и подменить "
        + "свою версию посреди суточного мониторинга значило бы разорвать ряд измерений "
        + "надвое, не сказав об этом.";

    public static string LicenceNote =>
        "Продукт распространяется по лицензии MIT. Драйвер захвата Npcap не входит "
        + "в поставку ни при каких условиях: его лицензия NPSL это запрещает.";

    public override async Task ActivateAsync(CancellationToken cancellationToken = default) =>
        await RefreshAsync(cancellationToken).ConfigureAwait(true);

    [RelayCommand]
    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        ErrorMessage = null;

        try
        {
            var report = await _capabilities.InspectAsync(cancellationToken).ConfigureAwait(true);

            Levels.Clear();

            foreach (var group in Build(report))
            {
                Levels.Add(group);
            }

            Summary =
                $"Работает {report.UsableCount.ToString(CultureInfo.InvariantCulture)}, "
                + $"упирается в условия {report.BlockedCount.ToString(CultureInfo.InvariantCulture)}, "
                + $"запланировано {report.PlannedCount.ToString(CultureInfo.InvariantCulture)}. "
                + (report.IsElevated
                    ? "Продукт запущен с правами администратора."
                    : "Продукт запущен без прав администратора.");

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

    /// <summary>
    /// Возможности, разложенные по уровням зависимостей.
    /// </summary>
    /// <remarks>
    /// Уровень показывается целиком, даже если в нём всё недоступно: недоступное
    /// не прячется, а объясняется. Спрятанный уровень выглядит как отсутствующий,
    /// и оператор идёт искать его в другом инструменте.
    /// </remarks>
    private static IEnumerable<CapabilityGroup> Build(CapabilityReport report)
    {
        foreach (var level in Enum.GetValues<CapabilityLevel>())
        {
            var items = report.OfLevel(level)
                .OrderBy(c => c.State)
                .ThenBy(c => c.Title, StringComparer.CurrentCulture)
                .Select(c => new CapabilityRow(c))
                .ToList();

            yield return new CapabilityGroup(
                TitleOf(level),
                AboutOf(level),
                Describe(report.StateOf(level)),
                items);
        }
    }

    private static string TitleOf(CapabilityLevel level) => level switch
    {
        CapabilityLevel.Core => "Уровень 0 — работает у всех",
        CapabilityLevel.Snmp => "Уровень 1 — учётные данные оборудования",
        _ => "Уровень 2 — драйвер захвата",
    };

    private static string AboutOf(CapabilityLevel level) => level switch
    {
        CapabilityLevel.Core => "Ни прав, ни драйверов, ни паролей. Это тот продукт, "
                                + "который достаётся любому оператору сразу после установки.",
        CapabilityLevel.Snmp => "Нужны сообщества или учётные записи SNMP на оборудовании. "
                                + "Их выдают сетевики, и выдают не всегда.",
        _ => "Нужен Npcap. Продукт его не распространяет ни при каких условиях: "
             + "лицензия NPSL это запрещает. Ставится вручную с npcap.com.",
    };

    internal static string Describe(CapabilityState state) => state switch
    {
        CapabilityState.Available => "работает",
        CapabilityState.Limited => "работает не в полную силу",
        CapabilityState.NeedsElevation => "нужны права администратора",
        CapabilityState.NeedsCredentials => "нужны учётные данные",
        CapabilityState.NeedsDriver => "нужен драйвер захвата",
        CapabilityState.NeedsData => "нужен файл базы",
        CapabilityState.NeedsAgent => "нужна вторая точка измерения",
        _ => "запланировано",
    };

    /// <summary>
    /// Цвет состояния.
    /// </summary>
    /// <remarks>
    /// Красным помечается только то, что сломано. Возможность, упирающаяся в права
    /// или драйвер, не сломана — она ждёт решения, которое оператор может принять,
    /// и красный на ней означал бы неисправность там, где её нет.
    /// </remarks>
    internal static VerdictLevel LevelOf(CapabilityState state) => state switch
    {
        CapabilityState.Available => VerdictLevel.Pass,
        CapabilityState.Planned => VerdictLevel.Unknown,
        _ => VerdictLevel.Warn,
    };

    private static string Changed(int count) => count == 0
        ? "Состав работающих мониторов не изменился."
        : $"Мониторов переключено: {count.ToString(CultureInfo.InvariantCulture)}.";
}
