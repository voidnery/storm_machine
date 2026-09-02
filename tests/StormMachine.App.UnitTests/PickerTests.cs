using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.VisualTree;
using StormMachine.App.Controls;
using StormMachine.App.ViewModels;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Выпадающий список — один на весь продукт.
/// </summary>
/// <remarks>
/// До И-24+ выборов было три породы: <c>ComboBox</c> со своим шаблоном пункта на каждой
/// странице, голый <c>ComboBox</c> и поле цели с самодельным списком из кнопок. Здесь
/// проверяется то, ради чего они сведены в один элемент: подпись выбранного, поиск
/// по списку, подтверждение выбора нажатием — и правило поля цели, где в поле обязан
/// попасть разбираемый адрес, а не строка с именем и ролью.
/// </remarks>
[Collection("Headless")]
public sealed class PickerTests(HeadlessSessionFixture fixture)
{
    private static readonly string[] Three = ["первый", "второй", "третий"];

    private readonly HeadlessUnitTestSession _session = fixture.Session;

    /// <summary>Закрытое поле показывает подпись выбранного, а не всю строку пункта.</summary>
    [Fact]
    public async Task ChosenOption_IsShownByItsCaption()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker
                {
                    ItemsSource = new[]
                    {
                        new InspectorOption("dns", "DNS-инспектор", "Задержка резолверов и расхождения."),
                        new InspectorOption("tls", "TLS-инспектор", "Цепочка, сроки, версия протокола."),
                    },
                };

                var window = Open(picker);

                picker.SelectedItem = ((InspectorOption[])picker.ItemsSource!)[1];
                window.UpdateLayout();

                Assert.Equal("TLS-инспектор", picker.Chosen);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Поиск идёт и по пояснению.
    /// </summary>
    /// <remarks>
    /// Оператор помнит не название пункта, а то, что тот делает: «резолвер» обязан
    /// найти DNS-инспектор, хотя в подписи этого слова нет.
    /// </remarks>
    [Fact]
    public async Task Search_LooksAtTheExplanationToo()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker
                {
                    ItemsSource = new[]
                    {
                        new InspectorOption("dns", "DNS-инспектор", "Задержка резолверов и расхождения."),
                        new InspectorOption("tls", "TLS-инспектор", "Цепочка, сроки, версия протокола."),
                    },
                };

                var window = Open(picker);

                picker.Search = "резолвер";
                window.UpdateLayout();

                Assert.Single(picker.Options);
                Assert.Equal("DNS-инспектор", picker.Options[0].Caption);

                picker.Search = "чего такого нет";
                window.UpdateLayout();

                Assert.Empty(picker.Options);
                Assert.Equal("Ничего не нашлось.", picker.Hint);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>Выбор закрепляется нажатием, а не перемещением подсветки.</summary>
    /// <remarks>
    /// Стрелками список листают. Если бы каждый шаг подсветки менял выбранное,
    /// дорога до нужной строки меняла бы цель прогона по пути.
    /// </remarks>
    [Fact]
    public async Task Highlight_DoesNotChooseUntilConfirmed()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker { ItemsSource = Three };

                var window = Open(picker);

                Press(picker, Key.Down);
                window.UpdateLayout();

                Assert.True(picker.IsOpen);

                Press(picker, Key.Down);
                Press(picker, Key.Down);
                window.UpdateLayout();

                Assert.Null(picker.SelectedItem);

                Press(picker, Key.Enter);
                window.UpdateLayout();

                Assert.Equal("второй", picker.SelectedItem);
                Assert.False(picker.IsOpen);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>
    /// В поле цели попадает адрес, а не строка списка.
    /// </summary>
    /// <remarks>
    /// В списке подсказка выглядит как «192.168.200.53 · NAS · сервер?» — по имени
    /// её и ищут. Но разбирается как цель только адрес, и подставить туда всю строку
    /// значило бы отправить прогон на имя узла, которого нет.
    /// </remarks>
    [Fact]
    public async Task FreeInput_TakesTheAddressAndNotTheWholeRow()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker
                {
                    AcceptsText = true,
                    ItemsSource = new[]
                    {
                        new TargetSuggestion("192.168.200.53", "192.168.200.53 · NAS · сервер?"),
                    },
                };

                var window = Open(picker);

                Press(picker, Key.Down);
                Press(picker, Key.Down);
                Press(picker, Key.Enter);
                window.UpdateLayout();

                Assert.Equal("192.168.200.53", picker.Text);
                Assert.False(picker.IsOpen);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Щелчок по строке выбирает и закрывает список.
    /// </summary>
    /// <remarks>
    /// Так им пользуются на самом деле. Тот же промах уже был у палитры команд:
    /// список умел выделять строку и ничего не делал, пока не нажмут Enter.
    /// </remarks>
    [Fact]
    public async Task ClickOnRow_ChoosesAndCloses()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker { ItemsSource = Three, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };

                var window = Open(picker);

                Press(picker, Key.Down);
                window.UpdateLayout();

                Assert.True(picker.IsOpen);

                var row = window.GetVisualDescendants().OfType<ListBoxItem>()
                    .FirstOrDefault(i => i.DataContext is PickerRow { Caption: "третий" });

                Assert.NotNull(row);

                var point = row!.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window);

                Assert.NotNull(point);

                window.MouseDown(point!.Value, MouseButton.Left);
                window.MouseUp(point.Value, MouseButton.Left);

                Assert.Equal("третий", picker.SelectedItem);
                Assert.False(picker.IsOpen);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>Пустой список говорит, что сделать, а не показывает чёрный прямоугольник.</summary>
    [Fact]
    public async Task EmptyList_SaysWhatToDo()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker
                {
                    ItemsSource = Array.Empty<string>(),
                    Empty = "Инвентарь пуст — просканируйте сеть в «Обнаружении».",
                };

                var window = Open(picker);
                window.UpdateLayout();

                Assert.Equal("Инвентарь пуст — просканируйте сеть в «Обнаружении».", picker.Hint);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>
    /// Строка поиска появляется на длинном списке и не мешает на коротком.
    /// </summary>
    /// <remarks>
    /// Поиск по трём пунктам — лишний элемент и лишний шаг: пункты видны все сразу.
    /// По двум сотням адресов инвентаря без него не обойтись.
    /// </remarks>
    [Fact]
    public async Task SearchRow_AppearsOnlyWhenTheListIsLong()
    {
        await _session.Dispatch(
            async () =>
            {
                var picker = new Picker { ItemsSource = Three };

                var window = Open(picker);

                Assert.False(picker.HasSearch);

                picker.ItemsSource = Enumerable.Range(1, 40).Select(i => $"адрес {i}").ToArray();
                window.UpdateLayout();

                Assert.True(picker.HasSearch);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    private static void Press(Picker picker, Key key) =>
        picker.RaiseEvent(new KeyEventArgs
        {
            RoutedEvent = InputElement.KeyDownEvent,
            Key = key,
            Source = picker,
        });

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
