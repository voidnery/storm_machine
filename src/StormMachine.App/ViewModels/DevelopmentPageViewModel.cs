using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.Application.Capabilities;
using StormMachine.Domain.Capabilities;
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

    public string StateText => DevelopmentPageViewModel.Describe(Capability.State)
                               + (Capability.Iteration is { } iteration ? $" · {iteration}" : string.Empty);

    /// <summary>Цвет точки состояния. Тот же словарь, что у мониторов и вердиктов.</summary>
    public VerdictLevel Level => DevelopmentPageViewModel.LevelOf(Capability.State);
}

/// <summary>Уровень зависимостей со своими возможностями.</summary>
public sealed record CapabilityGroup(string Title, string About, string StateText, IReadOnlyList<CapabilityRow> Items)
{
    public bool HasItems => Items.Count > 0;
}

/// <summary>
/// Временный раздел разработки: сводка возможностей машины.
/// </summary>
/// <remarks>
/// Сводка жила на странице настроек и была вынесена сюда в И-24 по решению оператора:
/// оператору в настройках нужны настройки, а список «что работает, что запланировано» —
/// рабочий инструмент разработки. Раздел уйдёт из навигации при переходе к релизной
/// версии — вычёркиванием одной строки в <see cref="NavigationMap" />; данные под ним
/// (<c>storm capabilities</c>) остаются частью продукта.
/// </remarks>
public sealed partial class DevelopmentPageViewModel(
    NavigationSection section,
    CapabilityInspector capabilities) : PageViewModel(section)
{
    private readonly CapabilityInspector _capabilities =
        capabilities ?? throw new ArgumentNullException(nameof(capabilities));

    public ObservableCollection<CapabilityGroup> Levels { get; } = [];

    [ObservableProperty]
    private string _summary = "…";

    [ObservableProperty]
    private string? _errorMessage;

    public static string Note =>
        "Раздел временный: сводка нужна разработке, и при переходе к релизной версии "
        + "он уйдёт из навигации. В консоли то же самое показывает storm capabilities — "
        + "эта команда остаётся.";

    public static string SummaryNote =>
        "Сводка считается по фактам этой машины: правам процесса, наличию драйвера, "
        + "сопряжённым агентам. Один и тот же выпуск на двух машинах умеет разное.";

    public override Task ActivateAsync(CancellationToken cancellationToken = default) =>
        RefreshAsync(cancellationToken);

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
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            ErrorMessage = ex.Message;
        }
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
}
