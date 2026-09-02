using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace StormMachine.App.Controls;

/// <summary>
/// Пункт выпадающего списка, умеющий назвать себя.
/// </summary>
/// <remarks>
/// До этой формы каждая страница описывала пункт своим <c>ItemTemplate</c> прямо
/// в разметке, и одинаковые по смыслу списки разошлись: где-то пункт был строкой,
/// где-то двумя, где-то полной строкой из <c>ToString</c> шире окна. Здесь состав
/// пункта назван один раз: подпись, пояснение и короткая пометка.
/// </remarks>
public interface IOption
{
    /// <summary>Подпись: она же в закрытом поле, она же первой строкой в списке.</summary>
    string Caption { get; }

    /// <summary>Пояснение второй строкой. Пусто — пункт остаётся одной строкой.</summary>
    string? About => null;

    /// <summary>Короткая пометка чипом справа от подписи: «свой», «эталон».</summary>
    string? Note => null;
}

/// <summary>Строка списка: сам пункт и то, что о нём показано.</summary>
/// <remarks>
/// Список работает с этой обёрткой, а не с самим пунктом: тогда шаблон строки
/// пишется один раз с проверяемыми привязками, а пунктом может быть что угодно —
/// и запись, реализующая <see cref="IOption" />, и обычная строка.
/// </remarks>
public sealed record PickerRow(object Item, string Caption, string? About, string? Note)
{
    /// <summary>Текст, по которому пункт ищется: подпись, пояснение и пометка разом.</summary>
    public string Search { get; } = $"{Caption} {About} {Note}";

    /// <summary>Есть ли что показывать второй строкой.</summary>
    public bool HasAbout => !string.IsNullOrWhiteSpace(About);

    /// <summary>Есть ли пометка.</summary>
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
}

/// <summary>
/// Выпадающий список продукта: один на все выборы, с поиском по длинному списку.
/// </summary>
/// <remarks>
/// В клиенте было девятнадцать выпадающих списков трёх разных пород: штатный
/// <c>ComboBox</c> с самодельным шаблоном пункта, голый <c>ComboBox</c> с
/// <c>ToString</c> и поле с подсказками, у которого список был собран руками
/// из <c>Popup</c> с кнопками — четыре копии по тридцать строк. Выглядели они
/// по-разному, вели себя по-разному, и список инвентаря, открытый кнопкой,
/// показывал все двести адресов, не глядя на набранное в поле.
/// <para>
/// Здесь выбор — одна форма: поле с подписью выбранного, карточка списка под ним,
/// строка поиска, когда пунктов много, и внятная фраза, когда показывать нечего.
/// Свободный ввод (поле цели) — тот же элемент с <see cref="AcceptsText" />:
/// набранное фильтрует список, стрелка показывает его целиком.
/// </para>
/// <para>
/// Выбор подтверждается нажатием или Enter, а не перемещением подсветки: стрелками
/// список листают, и выбирать на каждом шаге значило бы менять цель прогона
/// по дороге к нужной строке.
/// </para>
/// </remarks>
public class Picker : TemplatedControl
{
    /// <summary>Откуда берутся пункты.</summary>
    public static readonly StyledProperty<IEnumerable?> ItemsSourceProperty =
        AvaloniaProperty.Register<Picker, IEnumerable?>(nameof(ItemsSource));

    /// <summary>Выбранный пункт — сам объект, а не его обёртка.</summary>
    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<Picker, object?>(
            nameof(SelectedItem),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Набранное в поле со свободным вводом.</summary>
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Picker, string?>(
            nameof(Text),
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Свободный ввод: поле цели, где список — подсказка, а не единственный выбор.</summary>
    public static readonly StyledProperty<bool> AcceptsTextProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(AcceptsText));

    /// <summary>Что написано в поле, пока ничего не выбрано.</summary>
    public static readonly StyledProperty<string?> PlaceholderProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(Placeholder));

    /// <summary>Что сказать, когда список пуст: не «нет данных», а что сделать.</summary>
    public static readonly StyledProperty<string?> EmptyProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(Empty));

    /// <summary>С какого числа пунктов список получает строку поиска.</summary>
    public static readonly StyledProperty<int> SearchFromProperty =
        AvaloniaProperty.Register<Picker, int>(nameof(SearchFrom), 8);

    /// <summary>Раскрыт ли список.</summary>
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(IsOpen));

    /// <summary>Набранное в строке поиска.</summary>
    public static readonly StyledProperty<string?> SearchProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(Search));

    /// <summary>Подпись выбранного пункта — то, что видно в закрытом поле.</summary>
    public static readonly StyledProperty<string?> ChosenProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(Chosen));

    /// <summary>Фраза вместо пустого списка.</summary>
    public static readonly StyledProperty<string?> HintProperty =
        AvaloniaProperty.Register<Picker, string?>(nameof(Hint));

    /// <summary>Пусто ли поле: показывать подсказку вместо выбранного.</summary>
    public static readonly StyledProperty<bool> IsBlankProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(IsBlank), true);

    /// <summary>Нужна ли списку строка поиска.</summary>
    public static readonly StyledProperty<bool> HasSearchProperty =
        AvaloniaProperty.Register<Picker, bool>(nameof(HasSearch));

    /// <summary>Показываемые сейчас строки — то, что осталось после поиска.</summary>
    public static readonly DirectProperty<Picker, ObservableCollection<PickerRow>> OptionsProperty =
        AvaloniaProperty.RegisterDirect<Picker, ObservableCollection<PickerRow>>(nameof(Options), o => o.Options);

    /// <summary>Столько миллисекунд после закрытия щелчком мимо нажатие на поле не открывает список.</summary>
    /// <remarks>
    /// Щелчок по полю при раскрытом списке сперва закрывает его сам (light dismiss),
    /// и без этой паузы нажатие тут же открыло бы список заново — поле переставало
    /// закрываться собственным нажатием.
    /// </remarks>
    private const long ReopenGuardMs = 250;

    private ListBox? _list;
    private TextBox? _search;
    private TextBox? _text;
    private Button? _field;
    private Button? _arrow;
    private INotifyCollectionChanged? _watched;
    private long _dismissedAt;
    private int _sourceCount;
    private bool _syncing;

    public Picker() => UpdateState();

    public IEnumerable? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public bool AcceptsText
    {
        get => GetValue(AcceptsTextProperty);
        set => SetValue(AcceptsTextProperty, value);
    }

    public string? Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    public string? Empty
    {
        get => GetValue(EmptyProperty);
        set => SetValue(EmptyProperty, value);
    }

    public int SearchFrom
    {
        get => GetValue(SearchFromProperty);
        set => SetValue(SearchFromProperty, value);
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public string? Search
    {
        get => GetValue(SearchProperty);
        set => SetValue(SearchProperty, value);
    }

    public string? Chosen
    {
        get => GetValue(ChosenProperty);
        private set => SetValue(ChosenProperty, value);
    }

    public string? Hint
    {
        get => GetValue(HintProperty);
        private set => SetValue(HintProperty, value);
    }

    public bool IsBlank
    {
        get => GetValue(IsBlankProperty);
        private set => SetValue(IsBlankProperty, value);
    }

    public bool HasSearch
    {
        get => GetValue(HasSearchProperty);
        private set => SetValue(HasSearchProperty, value);
    }

    public ObservableCollection<PickerRow> Options { get; } = [];

    /// <summary>Как пункт называет себя: своей подписью или, за неимением, собой.</summary>
    public static string CaptionOf(object? item) => item switch
    {
        null => string.Empty,
        IOption option => option.Caption,
        _ => item.ToString() ?? string.Empty,
    };

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        base.OnApplyTemplate(e);

        if (_list is not null)
        {
            _list.PointerReleased -= OnListPointerReleased;
        }

        if (_search is not null)
        {
            _search.TextChanged -= OnSearchTyped;
        }

        if (_text is not null)
        {
            _text.TextChanged -= OnTargetTyped;
        }

        if (_field is not null)
        {
            _field.Click -= OnOpenClick;
        }

        if (_arrow is not null)
        {
            _arrow.Click -= OnOpenClick;
        }

        _list = e.NameScope.Find<ListBox>("PART_List");
        _search = e.NameScope.Find<TextBox>("PART_Search");
        _text = e.NameScope.Find<TextBox>("PART_Text");
        _field = e.NameScope.Find<Button>("PART_Field");
        _arrow = e.NameScope.Find<Button>("PART_Arrow");

        if (_list is not null)
        {
            _list.PointerReleased += OnListPointerReleased;
        }

        if (_search is not null)
        {
            _search.TextChanged += OnSearchTyped;
        }

        if (_text is not null)
        {
            _text.TextChanged += OnTargetTyped;
        }

        if (_field is not null)
        {
            _field.Click += OnOpenClick;
        }

        if (_arrow is not null)
        {
            _arrow.Click += OnOpenClick;
        }

        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);

        base.OnPropertyChanged(change);

        if (change.Property == ItemsSourceProperty)
        {
            Watch(change.GetNewValue<IEnumerable?>());
            Rebuild();
        }
        else if (change.Property == SelectedItemProperty)
        {
            Chosen = CaptionOf(SelectedItem);
            UpdateState();
        }
        else if (change.Property == SearchProperty
                 || change.Property == EmptyProperty
                 || change.Property == SearchFromProperty)
        {
            Rebuild();
        }
        else if (change.Property == TextProperty || change.Property == AcceptsTextProperty)
        {
            UpdateState();
        }
        else if (change.Property == IsOpenProperty)
        {
            if (change.GetNewValue<bool>())
            {
                Highlight();
            }
            else
            {
                _dismissedAt = Environment.TickCount64;
                Search = null;
            }

            UpdateState();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (IsOpen)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;

                case Key.Enter:
                    Commit();
                    e.Handled = true;
                    break;

                case Key.Down when _list is { IsKeyboardFocusWithin: false }:
                    Move(1);
                    e.Handled = true;
                    break;

                case Key.Up when _list is { IsKeyboardFocusWithin: false }:
                    Move(-1);
                    e.Handled = true;
                    break;

                default:
                    break;
            }
        }
        else if (e.Key is Key.Down)
        {
            Open();
            e.Handled = true;
        }

        base.OnKeyDown(e);
    }

    /// <summary>Собирает строки заново: пункты, прошедшие поиск, и фраза для пустоты.</summary>
    private void Rebuild()
    {
        var needle = Search?.Trim();

        _syncing = true;

        try
        {
            Options.Clear();
            _sourceCount = 0;

            foreach (var item in ItemsSource ?? Array.Empty<object>())
            {
                if (item is null)
                {
                    continue;
                }

                _sourceCount++;

                var row = new PickerRow(
                    item,
                    CaptionOf(item),
                    (item as IOption)?.About,
                    (item as IOption)?.Note);

                if (string.IsNullOrEmpty(needle)
                    || row.Search.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    Options.Add(row);
                }
            }
        }
        finally
        {
            _syncing = false;
        }

        Hint = _sourceCount == 0
            ? Empty ?? "Список пуст."
            : Options.Count == 0 ? "Ничего не нашлось." : null;

        Highlight();
        UpdateState();
    }

    /// <summary>Подсвечивает выбранное, чтобы список открылся на нём, а не с начала.</summary>
    private void Highlight()
    {
        if (_list is null)
        {
            return;
        }

        var row = SelectedItem is null
            ? null
            : Options.FirstOrDefault(o => Equals(o.Item, SelectedItem));

        _syncing = true;

        try
        {
            _list.SelectedItem = row;
        }
        finally
        {
            _syncing = false;
        }

        if (row is not null && IsOpen)
        {
            _list.ScrollIntoView(row);
        }
    }

    private void Move(int step)
    {
        if (_list is null || Options.Count == 0)
        {
            return;
        }

        var index = _list.SelectedItem is PickerRow row ? Options.IndexOf(row) : -1;
        var next = Math.Clamp(index + step, 0, Options.Count - 1);

        _syncing = true;

        try
        {
            _list.SelectedItem = Options[next];
        }
        finally
        {
            _syncing = false;
        }

        _list.ScrollIntoView(Options[next]);
    }

    /// <summary>Закрепляет подсвеченное: только по нажатию или Enter, не по перемещению.</summary>
    private void Commit()
    {
        if (_list?.SelectedItem is not PickerRow row)
        {
            Close();
            return;
        }

        SelectedItem = row.Item;

        if (AcceptsText)
        {
            // Свободный ввод: в поле попадает то, что разбирается как цель, — у подсказки
            // инвентаря это голый адрес, а не строка с именем и ролью. Подстановка идёт
            // под флагом: иначе она читается как набор и тут же раскрывает список заново.
            _syncing = true;

            try
            {
                Text = row.Item.ToString();
            }
            finally
            {
                _syncing = false;
            }
        }

        Close();
    }

    /// <summary>
    /// Раскрывает список целиком.
    /// </summary>
    /// <remarks>
    /// Именно целиком: стрелка у поля цели показывает весь инвентарь, а не то,
    /// что осталось от набранного, — иначе после выбора адреса список сужался
    /// до этого же адреса и открывать его было незачем.
    /// </remarks>
    private void Open()
    {
        if (IsOpen || Environment.TickCount64 - _dismissedAt < ReopenGuardMs)
        {
            return;
        }

        Search = null;

        Rebuild();

        IsOpen = true;

        // Фокус переносится после раскрытия: до него частей ещё нет на экране.
        Dispatcher.UIThread.Post(() =>
        {
            if (!IsOpen)
            {
                return;
            }

            if (HasSearch)
            {
                _search?.Focus();
            }
            else if (!AcceptsText)
            {
                _list?.Focus();
            }
        });
    }

    private void Close() => IsOpen = false;

    private void UpdateState()
    {
        // Состояния — обычные свойства, а не псевдоклассы: их видно снаружи, и проверка
        // может спросить у элемента, показывает ли он сейчас поиск, а не гадать по виду.
        HasSearch = !AcceptsText && _sourceCount >= SearchFrom;
        IsBlank = AcceptsText ? string.IsNullOrEmpty(Text) : SelectedItem is null;
    }

    private void Watch(IEnumerable? source)
    {
        if (_watched is not null)
        {
            _watched.CollectionChanged -= OnSourceChanged;
        }

        _watched = source as INotifyCollectionChanged;

        if (_watched is not null)
        {
            _watched.CollectionChanged += OnSourceChanged;
        }
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnOpenClick(object? sender, RoutedEventArgs e)
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

    private void OnSearchTyped(object? sender, TextChangedEventArgs e)
    {
        if (!_syncing && _search is not null)
        {
            Search = _search.Text;
        }
    }

    /// <summary>
    /// Набранное в поле цели фильтрует подсказки — и открывает список само.
    /// </summary>
    /// <remarks>
    /// Проверка на фокус отделяет набор от подстановки: цель, положенную в поле
    /// кнопкой агента или пресетом, оператор не набирал, и раскрывать список
    /// ему в ответ незачем.
    /// </remarks>
    private void OnTargetTyped(object? sender, TextChangedEventArgs e)
    {
        if (_syncing || _text is not { IsFocused: true })
        {
            return;
        }

        Search = _text.Text;

        if (!IsOpen && Options.Count > 0)
        {
            IsOpen = true;
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Source is not Visual source)
        {
            return;
        }

        var item = source.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault();

        if (item?.DataContext is not PickerRow row)
        {
            return;
        }

        _syncing = true;

        try
        {
            if (_list is not null)
            {
                _list.SelectedItem = row;
            }
        }
        finally
        {
            _syncing = false;
        }

        Commit();
    }
}
