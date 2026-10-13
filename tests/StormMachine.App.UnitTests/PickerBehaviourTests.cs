using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using StormMachine.App.Controls;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Поведение выпадающего списка, а не его вид.
/// </summary>
/// <remarks>
/// Три замечания оператора подряд пришли на один элемент: список не открывался
/// с первого нажатия, прокрутка над раскрытым списком крутила страницу и закрывала
/// его, а поиск был не у всех длинных списков. Первые два — следствие одной правки,
/// сделанной предыдущей итерацией вслепую: закрывать список на любое
/// <c>ScrollChanged</c>. Событие приходит и от перестройки раскладки — то есть
/// от самого раскрытия.
/// <para>
/// Поэтому проверки смотрят на поведение в собранном окне: заметить такое
/// на разметке нельзя, и нашёл это человек, а не сборка.
/// </para>
/// </remarks>
[Collection("Headless")]
public sealed class PickerBehaviourTests(HeadlessSessionFixture fixture)
{
    private static readonly string[] Three = ["первый", "второй", "третий"];

    private static readonly string[] Two = ["первый", "второй"];

    private static readonly string[] Short = ["да", "нет"];

    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact(DisplayName = "Перестройка раскладки не закрывает раскрытый список")]
    public async Task LayoutChange_DoesNotCloseTheList()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker { ItemsSource = Three };
                var scroller = new ScrollViewer { Content = new StackPanel { Children = { picker } } };

                Open(scroller);

                picker.IsOpen = true;

                // Ровно то, что приходит от раскрытия списка: содержимое страницы
                // стало выше, сдвиг не менялся.
                scroller.RaiseEvent(new ScrollChangedEventArgs(
                    ScrollViewer.ScrollChangedEvent,
                    extentDelta: new Vector(0, 120),
                    offsetDelta: default,
                    viewportDelta: default));

                Assert.True(picker.IsOpen);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact(DisplayName = "Настоящая прокрутка страницы список закрывает")]
    public async Task RealScroll_ClosesTheList()
    {
        await _session.Dispatch(
            async () =>
            {
                // Список висит поверх страницы и за ней не едет: оставить его
                // открытым значит предложить выбор, оторванный от своего поля.
                var picker = new Picker { ItemsSource = Two };
                var scroller = new ScrollViewer { Content = new StackPanel { Children = { picker } } };

                Open(scroller);

                picker.IsOpen = true;

                scroller.RaiseEvent(new ScrollChangedEventArgs(
                    ScrollViewer.ScrollChangedEvent,
                    extentDelta: default,
                    offsetDelta: new Vector(0, 40),
                    viewportDelta: default));

                Assert.False(picker.IsOpen);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact(DisplayName = "Поиск есть у любого длинного списка, в том числе со свободным вводом")]
    public async Task LongList_AlwaysHasSearch()
    {
        await _session.Dispatch(
            async () =>
            {
                var many = Enumerable.Range(1, 40).Select(i => $"192.168.200.{i}").ToArray();

                var chooser = new Picker { ItemsSource = many };
                var typed = new Picker { ItemsSource = many, AcceptsText = true };

                Open(new StackPanel { Children = { chooser, typed } });

                Assert.True(chooser.HasSearch);

                // Раньше здесь было false: считалось, что набранное в самом поле
                // и есть поиск. Для двухсот адресов инвентаря это значило искать,
                // разрушая набранную цель.
                Assert.True(typed.HasSearch);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact(DisplayName = "Короткому списку строка поиска не нужна")]
    public async Task ShortList_HasNoSearch()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker { ItemsSource = Short };

                Open(picker);

                Assert.False(picker.HasSearch);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    private static Window Open(Control content)
    {
        var window = new Window
        {
            Content = content,
            Width = 600,
            Height = 400,
        };

        window.Show();
        window.UpdateLayout();

        return window;
    }
}
