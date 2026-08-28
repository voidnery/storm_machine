using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StormMachine.App.ViewModels;

/// <summary>
/// Страница, умеющая принять цель со стороны.
/// </summary>
/// <remarks>
/// Нужно ради быстрой цели: оператор набирает адрес в палитре и выбирает действие,
/// а не ищет нужный экран, чтобы там снова набрать тот же адрес. Интерфейс узкий
/// намеренно — палитра не должна знать, что страница делает с целью дальше.
/// </remarks>
public interface ITargetAware
{
    void UseTarget(string target);
}

/// <summary>Что палитра предлагает сделать.</summary>
public sealed record PaletteItem(string Group, string Title, string Subtitle, string Route, string? Target);

/// <summary>
/// Палитра команд: <c>Ctrl+K</c>.
/// </summary>
/// <remarks>
/// Разделов семнадцать, и половина из них нужна раз в месяц. Меню для такого набора
/// работает плохо: чтобы выбрать пункт, его надо сначала вспомнить и найти глазами.
/// Палитра меняет порядок — сначала имя, потом список.
/// <para>
/// Вторая её половина важнее первой. Набранный адрес палитра узнаёт как <b>цель</b>
/// и предлагает действия над ней: пинг, трассу, разбор DNS и TLS. Это убирает
/// самый частый лишний шаг продукта — «найти экран, чтобы ввести туда то, что уже набрано».
/// </para>
/// </remarks>
public sealed partial class CommandPaletteViewModel : ObservableObject
{
    /// <summary>Имена, которые продукт понимает без точки в адресе.</summary>
    private static readonly string[] KnownAliases = ["gateway", "шлюз", "localhost", "dns", "router"];

    private readonly IReadOnlyList<NavigationSection> _sections;
    private readonly Action<string, string?> _go;

    public CommandPaletteViewModel(IReadOnlyList<NavigationSection> sections, Action<string, string?> go)
    {
        _sections = sections ?? throw new ArgumentNullException(nameof(sections));
        _go = go ?? throw new ArgumentNullException(nameof(go));

        Rebuild();
    }

    public ObservableCollection<PaletteItem> Items { get; } = [];

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private PaletteItem? _selected;

    public static string Hint =>
        "Начните вводить имя раздела — или адрес узла, чтобы сразу выбрать действие над ним. "
        + "Enter — выполнить, Esc — закрыть.";

    public bool IsEmpty => Items.Count == 0;

    [RelayCommand]
    public void Open()
    {
        Query = string.Empty;
        Rebuild();
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Выполняет выбранное и закрывает палитру.</summary>
    [RelayCommand]
    public void Run()
    {
        if (Selected is not { } item)
        {
            return;
        }

        IsOpen = false;
        _go(item.Route, item.Target);
    }

    [RelayCommand]
    private void MoveDown() => Move(1);

    [RelayCommand]
    private void MoveUp() => Move(-1);

    private void Move(int delta)
    {
        if (Items.Count == 0)
        {
            return;
        }

        var index = Selected is null ? -1 : Items.IndexOf(Selected);

        // Список закольцован: с последней строки вниз — на первую. Упереться
        // в край и не понять, почему клавиша перестала работать, здесь легко.
        Selected = Items[((index + delta) % Items.Count + Items.Count) % Items.Count];
    }

    partial void OnQueryChanged(string value) => Rebuild();

    private void Rebuild()
    {
        var query = Query.Trim();

        Items.Clear();

        foreach (var item in Compose(query))
        {
            Items.Add(item);
        }

        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private IEnumerable<PaletteItem> Compose(string query)
    {
        // Действия над целью идут первыми: если человек набрал адрес, он хочет
        // что-то с ним сделать, а не читать список разделов.
        foreach (var action in TargetActions(query))
        {
            yield return action;
        }

        var sections = _sections
            .Select(s => (Section: s, Score: Score(query, s.Title, s.Description, s.Route)))
            .Where(x => x.Score > int.MinValue)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Section.Title, StringComparer.CurrentCulture);

        foreach (var (section, _) in sections)
        {
            yield return new PaletteItem(
                section.IsReady ? "Раздел" : $"Раздел · {section.Availability}",
                section.Title,
                section.Description,
                section.Route,
                null);
        }
    }

    /// <summary>
    /// Действия над набранной целью.
    /// </summary>
    /// <remarks>
    /// Цель узнаётся по виду строки, а не разбором: разбор принял бы за имя узла
    /// любое слово, и тогда «журнал» превратился бы в предложение пинговать узел
    /// с таким именем.
    /// </remarks>
    private static IEnumerable<PaletteItem> TargetActions(string query)
    {
        if (!LooksLikeTarget(query))
        {
            yield break;
        }

        yield return new PaletteItem("Цель", $"Пинг {query}", "Задержка, джиттер и PDV с живым графиком",
            NavigationMap.Latency, query);

        yield return new PaletteItem("Цель", $"Трасса до {query}", "Traceroute и непрерывный MTR по хопам",
            NavigationMap.Path, query);

        yield return new PaletteItem("Цель", $"Разобрать {query}", "DNS, TLS и HTTP: ответы и тайминги",
            NavigationMap.Inspect, query);

        yield return new PaletteItem("Цель", $"Сценарий по {query}", "Цепочка шагов с разбивкой по фазам",
            NavigationMap.Probes, query);
    }

    private static bool LooksLikeTarget(string query)
    {
        if (query.Length == 0 || query.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return query.Contains('.', StringComparison.Ordinal)
               || query.Contains(':', StringComparison.Ordinal)
               || KnownAliases.Contains(query, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Насколько строка подходит запросу. <see cref="int.MinValue"/> — не подходит.
    /// </summary>
    /// <remarks>
    /// Совпадение по подпоследовательности, а не по подстроке: «крс» находит «карту сети».
    /// Начало слова весит больше середины — иначе точное имя раздела тонуло бы среди
    /// случайных совпадений в описаниях.
    /// </remarks>
    private static int Score(string query, params string[] fields)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        var best = int.MinValue;

        for (var i = 0; i < fields.Length; i++)
        {
            var score = ScoreOne(query, fields[i]);

            if (score == int.MinValue)
            {
                continue;
            }

            // Поля перечислены по убыванию значимости: имя важнее описания,
            // описание важнее адреса раздела.
            best = Math.Max(best, score - (i * 40));
        }

        return best;
    }

    private static int ScoreOne(string query, string field)
    {
        var haystack = field.ToLowerInvariant();
        var needle = query.ToLowerInvariant();

        if (haystack.Contains(needle, StringComparison.Ordinal))
        {
            return 1000 - haystack.IndexOf(needle, StringComparison.Ordinal);
        }

        var at = 0;
        var score = 500;
        var previous = -1;

        foreach (var symbol in needle)
        {
            var found = haystack.IndexOf(symbol, at);

            if (found < 0)
            {
                return int.MinValue;
            }

            // Разрыв между буквами штрафуется: «крс» лучше подходит «карте сети»,
            // чем строке, где те же буквы разбросаны по всему описанию.
            if (previous >= 0)
            {
                score -= Math.Min(found - previous - 1, 20);
            }

            previous = found;
            at = found + 1;
        }

        return score;
    }
}
