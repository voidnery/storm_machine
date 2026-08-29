using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Topology;
using StormMachine.Domain.Topology;

namespace StormMachine.App.ViewModels;

/// <summary>Строка списка связей выбранного узла.</summary>
public sealed record LinkRow(string Peer, string Confidence, string Because, bool IsConfirmed);

/// <summary>Строка списка правок оператора.</summary>
public sealed record EditRow(Guid Id, string Text, string? Note, string When);

/// <summary>
/// Экран карты сети.
/// </summary>
/// <remarks>
/// Карта складывается из уже собранного: инвентарь дал устройства, трассировки — внешние
/// пути, сетевое окружение — подсети. Своих измерений экран не делает и потому
/// пересчитывается мгновенно, не трогая чужую сеть.
/// <para>
/// Главное требование к показу — <b>видимая достоверность</b>. У каждой связи есть
/// уровень уверенности и строка «почему», и то и другое доступно оператору: догадка
/// обязана себя объяснять, иначе её нельзя ни проверить, ни оспорить.
/// </para>
/// </remarks>
public sealed partial class TopologyPageViewModel(
    NavigationSection section,
    TopologyService topology,
    IDeviceStore store,
    IFilePicker files,
    ITopologyLayout layout) : PageViewModel(section)
{
    private readonly TopologyService _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly IDeviceStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IFilePicker _files = files ?? throw new ArgumentNullException(nameof(files));

    /// <summary>
    /// Кто считает расположение узлов.
    /// </summary>
    /// <remarks>
    /// Отдаётся полотну через привязку: движок раскладки лежит в инфраструктуре,
    /// а представлению ссылаться на неё запрещено. Ту же раскладку получает отчёт —
    /// поэтому схема в документе совпадает с картой на экране.
    /// </remarks>
    public ITopologyLayout Layout { get; } = layout ?? throw new ArgumentNullException(nameof(layout));

    private readonly List<string> _expanded = [];

    [ObservableProperty]
    private TopologyGraph? _graph;

    [ObservableProperty]
    private TopologyNode? _selectedNode;

    [ObservableProperty]
    private string _summary = string.Empty;

    /// <summary>
    /// Оговорки к карте: где её нельзя читать буквально.
    /// </summary>
    /// <remarks>
    /// Отдельно от сводки намеренно. Сводка говорит, насколько связям можно верить;
    /// оговорка — о другом: связи верны, а вот соседями эти узлы не являются.
    /// Смешать их значило бы утопить второе в первом.
    /// </remarks>
    [ObservableProperty]
    private string? _caveats;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string _statusLine = string.Empty;

    [ObservableProperty]
    private bool _includeExternalPaths = true;

    [ObservableProperty]
    private bool _includeVirtualAdapters = true;

    [ObservableProperty]
    private bool _expandAll;

    /// <summary>
    /// Опрашивать ли оборудование по SNMP.
    /// </summary>
    /// <remarks>
    /// Выключено по умолчанию: опрос идёт по чужой сети и занимает секунды
    /// на устройство. Молча слать трафик к оборудованию заказчика при каждом
    /// взгляде на карту продукт не станет.
    /// </remarks>
    [ObservableProperty]
    private bool _useSnmp;

    /// <summary>Что происходит прямо сейчас: опрос идёт секундами и молчать нельзя.</summary>
    [ObservableProperty]
    private string? _note;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Связи выбранного узла — с уровнем уверенности и объяснением.</summary>
    public ObservableCollection<LinkRow> SelectedLinks { get; } = [];

    /// <summary>Правки оператора — видны и отменяемы поштучно.</summary>
    public ObservableCollection<EditRow> Edits { get; } = [];

    /// <summary>
    /// Первый конец будущей связи.
    /// </summary>
    /// <remarks>
    /// Связь рисуется в два приёма: сначала запоминается один узел, потом выбирается
    /// второй. Перетаскивание было бы естественнее, но оно же и опаснее: случайное
    /// движение мышью на карте из сотни узлов создавало бы связи, которых никто
    /// не рисовал.
    /// </remarks>
    [ObservableProperty]
    private TopologyNode? _pinnedNode;

    [ObservableProperty]
    private string _linkNote = string.Empty;

    public string PinnedCaption => PinnedNode is { } node
        ? $"первый конец: {node.Label}"
        : "первый конец не выбран";

    /// <summary>Событие для представления: карту пора вписать в окно заново.</summary>
    public event EventHandler? GraphReplaced;

    /// <summary>Представление отдаёт разложенную карту для выгрузки.</summary>
    public Func<string, Task<bool>>? ExportImage { get; set; }

    public override async Task ActivateAsync(CancellationToken cancellationToken = default) =>
        await ReloadAsync(cancellationToken).ConfigureAwait(true);

    partial void OnSelectedNodeChanged(TopologyNode? value) => ShowLinksOf(value);

    partial void OnIncludeExternalPathsChanged(bool value)
    {
        _ = value;
        _ = ReloadAsync();
    }

    partial void OnIncludeVirtualAdaptersChanged(bool value)
    {
        _ = value;
        _ = ReloadAsync();
    }

    partial void OnExpandAllChanged(bool value)
    {
        _ = value;
        _ = ReloadAsync();
    }

    partial void OnPinnedNodeChanged(TopologyNode? value)
    {
        _ = value;
        OnPropertyChanged(nameof(PinnedCaption));
    }

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);

    // ------------------------------------------------------------------ правка

    /// <summary>Запоминает выбранный узел как первый конец будущей связи.</summary>
    [RelayCommand]
    private void Pin()
    {
        ErrorMessage = null;

        if (SelectedNode is null)
        {
            ErrorMessage = "Сначала выберите узел на карте.";
            return;
        }

        PinnedNode = SelectedNode;
        StatusLine = $"Запомнен первый конец: {PinnedNode.Label}. Теперь выберите второй и нажмите «Соединить».";
    }

    [RelayCommand]
    private async Task LinkAsync() => await EditAsync(TopologyEditKind.AddLink).ConfigureAwait(true);

    [RelayCommand]
    private async Task UnlinkAsync() => await EditAsync(TopologyEditKind.RemoveLink).ConfigureAwait(true);

    private async Task EditAsync(TopologyEditKind kind)
    {
        ErrorMessage = null;

        if (PinnedNode is not { } from || SelectedNode is not { } to)
        {
            ErrorMessage = "Нужны два узла: запомните первый, затем выберите второй.";
            return;
        }

        if (from.Id == to.Id)
        {
            ErrorMessage = "Это один и тот же узел.";
            return;
        }

        var note = string.IsNullOrWhiteSpace(LinkNote) ? null : LinkNote.Trim();

        var edit = kind == TopologyEditKind.AddLink
            ? TopologyEdit.Link(from.Id, to.Id, Environment.UserName, note)
            : TopologyEdit.Unlink(from.Id, to.Id, Environment.UserName, note);

        await _store.SaveTopologyEditAsync(edit).ConfigureAwait(true);

        LinkNote = string.Empty;
        PinnedNode = null;
        StatusLine = edit.Describe() + ". Правка переживёт пересканирование.";

        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task HideAsync()
    {
        ErrorMessage = null;

        if (SelectedNode is not { } node)
        {
            ErrorMessage = "Выберите узел на карте.";
            return;
        }

        await _store.SaveTopologyEditAsync(TopologyEdit.Hide(node.Id, Environment.UserName)).ConfigureAwait(true);

        StatusLine = $"Узел {node.Label} скрыт. В инвентаре он остаётся — скрыт только на карте.";

        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// Объявляет выбранный узел тем же устройством, что и запомненный.
    /// </summary>
    /// <remarks>
    /// Объединение уходит в инвентарь, а не в карту: устройство одно во всём продукте,
    /// и объединять его дважды в разных местах оператор не должен.
    /// </remarks>
    [RelayCommand]
    private async Task MergeAsync()
    {
        ErrorMessage = null;

        if (PinnedNode is not { } primary || SelectedNode is not { } duplicate)
        {
            ErrorMessage = "Нужны два узла: запомните основной, затем выберите дубль.";
            return;
        }

        try
        {
            await _store.MergeAsync(primary.Id, duplicate.Id, Environment.UserName).ConfigureAwait(true);
        }
        catch (InvalidOperationException ex)
        {
            ErrorMessage = ex.Message;
            return;
        }

        StatusLine = $"{duplicate.Label} присоединён к {primary.Label}. "
                     + "Объединение действует и в списке устройств, и в различиях между сканами.";

        PinnedNode = null;

        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Отменяет правку. Наблюдения при этом не трогаются.</summary>
    [RelayCommand]
    private async Task ForgetAsync(EditRow? row)
    {
        if (row is null)
        {
            return;
        }

        await _store.RemoveTopologyEditAsync(row.Id).ConfigureAwait(true);

        StatusLine = "Правка отменена — карта вернулась к тому, что видит инструмент.";

        await ReloadAsync().ConfigureAwait(true);
    }

    /// <summary>Разворачивает подсеть выбранного узла — свёрнутые устройства станут видны.</summary>
    [RelayCommand]
    private async Task ExpandAsync()
    {
        if (SelectedNode is not { Kind: TopologyNodeKind.HostGroup } group)
        {
            ErrorMessage = "Выберите свёрнутую группу устройств.";
            return;
        }

        // Идентификатор группы — «сеть:CIDR/остальные»; разворачивается её подсеть.
        var id = group.Id;
        var slash = id.LastIndexOf('/');
        var cidr = slash > 0 ? id[..slash].Replace("сеть:", string.Empty, StringComparison.Ordinal) : null;

        if (cidr is null)
        {
            ErrorMessage = "Не удалось понять, какую подсеть разворачивать.";
            return;
        }

        if (!_expanded.Contains(cidr, StringComparer.Ordinal))
        {
            _expanded.Add(cidr);
        }

        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task CollapseAllAsync()
    {
        _expanded.Clear();
        ExpandAll = false;

        await ReloadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ExportJsonAsync()
    {
        if (Graph is not { } graph)
        {
            return;
        }

        var path = await _files.PickSaveAsync("Куда сохранить карту", "storm-topology.json", "json").ConfigureAwait(true);

        if (path is null)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, TopologyDocumentJson.Serialize(graph)).ConfigureAwait(true);
            StatusLine = $"Карта записана: {path}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не сохранено: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportPngAsync() => await ExportImageAsync("png", "PNG").ConfigureAwait(true);

    [RelayCommand]
    private async Task ExportSvgAsync() => await ExportImageAsync("svg", "SVG").ConfigureAwait(true);

    private async Task ExportImageAsync(string extension, string label)
    {
        if (Graph is null || ExportImage is null)
        {
            return;
        }

        var path = await _files.PickSaveAsync($"Куда сохранить карту ({label})", $"storm-topology.{extension}", extension).ConfigureAwait(true);

        if (path is null)
        {
            return;
        }

        try
        {
            StatusLine = await ExportImage(path).ConfigureAwait(true)
                ? $"Карта записана: {path}"
                : "Карта не выгружена: полотно пусто.";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не сохранено: {ex.Message}";
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var graph = await _topology.BuildAsync(
                new TopologyOptions
                {
                    IncludeExternalPaths = IncludeExternalPaths,
                    IncludeVirtualAdapters = IncludeVirtualAdapters,
                    CollapseThreshold = ExpandAll ? int.MaxValue : 12,
                    ExpandedSubnets = [.. _expanded],
                    UseSnmp = UseSnmp,
                },
                note => Note = note,
                cancellationToken).ConfigureAwait(true);

            Graph = graph;
            SelectedNode = null;
            SelectedLinks.Clear();

            Summary = graph.IsEmpty
                ? "Карта пуста. Начните со сканирования в разделе «Обнаружение»."
                : $"Узлов {graph.Nodes.Count}, связей {graph.Links.Count}: "
                  + $"подтверждённых {graph.ConfirmedLinks}, выведенных {graph.InferredLinks} "
                  + $"({Share(graph)} %). Выведенное — не ошибки: без SNMP и захвата пакетов "
                  + "часть связей приходится выводить по правилам, и каждая названа причиной.";

            // Оговорки отдельной строкой и заметно: они говорят не о достоверности
            // связей, а о том, что карту нельзя читать буквально. Связи верны —
            // неверно было бы прочесть их как соседство.
            Caveats = graph.Caveats.Count == 0 ? null : string.Join(Environment.NewLine, graph.Caveats);

            await LoadEditsAsync(cancellationToken).ConfigureAwait(true);

            GraphReplaced?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Карта не построена: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task LoadEditsAsync(CancellationToken cancellationToken)
    {
        var edits = await _store.ListTopologyEditsAsync(cancellationToken).ConfigureAwait(true);
        var aliases = await _store.ListAliasesAsync(cancellationToken).ConfigureAwait(true);

        Edits.Clear();

        foreach (var edit in edits)
        {
            Edits.Add(new EditRow(
                edit.Id,
                edit.Describe(),
                edit.Note,
                edit.AtUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)));
        }

        // Объединения показываются рядом с правками карты, хотя живут в инвентаре:
        // для оператора это одно и то же действие — «я поправил то, что увидел
        // инструмент», и разносить их по разным экранам не за что.
        foreach (var alias in aliases)
        {
            Edits.Add(new EditRow(
                Guid.Empty,
                $"{alias.Alias} присоединён к {alias.Primary}",
                "объединение живёт в инвентаре — отменяется там же",
                alias.AtUtc.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)));
        }
    }

    private static string Share(TopologyGraph graph) =>
        (graph.Links.Count == 0 ? 0 : graph.InferredLinks * 100.0 / graph.Links.Count)
        .ToString("0", CultureInfo.InvariantCulture);

    private void ShowLinksOf(TopologyNode? node)
    {
        SelectedLinks.Clear();

        if (node is null || Graph is not { } graph)
        {
            return;
        }

        var byId = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);

        foreach (var link in graph.Links)
        {
            var peerId = link.From == node.Id ? link.To : link.To == node.Id ? link.From : null;

            if (peerId is null || !byId.TryGetValue(peerId, out var peer))
            {
                continue;
            }

            SelectedLinks.Add(new LinkRow(
                peer.Label,
                Describe(link.Confidence),
                link.Because,
                link.Confidence == LinkConfidence.Confirmed));
        }

        StatusLine = node.Detail is { Length: > 0 } detail
            ? $"{node.Label} — {detail}"
            : node.Label;
    }

    private static string Describe(LinkConfidence confidence) => confidence switch
    {
        LinkConfidence.Confirmed => "подтверждено",
        LinkConfidence.Inferred => "выведено",
        _ => "допущение",
    };
}
