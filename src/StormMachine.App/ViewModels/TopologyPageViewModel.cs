using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StormMachine.App.Services;
using StormMachine.Application.Topology;
using StormMachine.Domain.Topology;

namespace StormMachine.App.ViewModels;

/// <summary>Строка списка связей выбранного узла.</summary>
public sealed record LinkRow(string Peer, string Confidence, string Because, bool IsConfirmed);

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
    IFilePicker files) : PageViewModel(section)
{
    private readonly TopologyService _topology = topology ?? throw new ArgumentNullException(nameof(topology));
    private readonly IFilePicker _files = files ?? throw new ArgumentNullException(nameof(files));

    private readonly List<string> _expanded = [];

    [ObservableProperty]
    private TopologyGraph? _graph;

    [ObservableProperty]
    private TopologyNode? _selectedNode;

    [ObservableProperty]
    private string _summary = string.Empty;

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

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Связи выбранного узла — с уровнем уверенности и объяснением.</summary>
    public ObservableCollection<LinkRow> SelectedLinks { get; } = [];

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

    [RelayCommand]
    private async Task RefreshAsync() => await ReloadAsync().ConfigureAwait(true);

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
                },
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
