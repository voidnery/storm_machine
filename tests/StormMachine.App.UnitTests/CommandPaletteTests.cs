using Avalonia;
using Avalonia.Headless;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Палитра команд закрывается щелчком мимо себя.
/// </summary>
/// <remarks>
/// До этого выйти из неё можно было единственным способом — клавишей Escape.
/// Окно, накрывшее весь экран и не отпускающее мышь, ловит в тупик того, кто открыл
/// его мышью же (замечание оператора). Проверка сторожит обе половины правила:
/// щелчок по затемнению закрывает, щелчок внутри карточки — нет, иначе выбор строки
/// в списке закрывал бы палитру раньше, чем срабатывал.
/// </remarks>
[Collection("Headless")]
public sealed class CommandPaletteTests(HeadlessSessionFixture fixture)
{
    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact]
    public async Task ClickOutside_ClosesPalette()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();
                var shell = services.GetRequiredService<MainWindowViewModel>();

                var window = new Views.MainWindow { DataContext = shell };
                window.Show();

                try
                {
                    shell.Palette.Open();
                    window.UpdateLayout();

                    Assert.True(shell.Palette.IsOpen);

                    // Низ окна: карточка палитры прижата к верху, там только затемнение.
                    window.MouseDown(new Point(60, window.Bounds.Height - 60), MouseButton.Left);
                    window.MouseUp(new Point(60, window.Bounds.Height - 60), MouseButton.Left);

                    Assert.False(shell.Palette.IsOpen);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    /// <summary>Щелчок по строке — переход: список, умеющий только выделять, сломан.</summary>
    [Fact]
    public async Task ClickOnItem_NavigatesAndCloses()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();
                var shell = services.GetRequiredService<MainWindowViewModel>();

                var window = new Views.MainWindow { DataContext = shell };
                window.Show();

                try
                {
                    var first = shell.SelectedSection;

                    shell.Palette.Open();
                    shell.Palette.Query = "журнал";
                    window.UpdateLayout();

                    Assert.NotEmpty(shell.Palette.Items);

                    var list = window.GetVisualDescendants().OfType<ListBox>()
                        .First(l => l.Name == "PaletteList");

                    var row = list.GetVisualDescendants().OfType<ListBoxItem>().First();
                    var point = row.Bounds.Center;
                    var inWindow = row.TranslatePoint(new Point(point.X - row.Bounds.X, point.Y - row.Bounds.Y), window);

                    Assert.NotNull(inWindow);

                    window.MouseDown(inWindow!.Value, MouseButton.Left);
                    window.MouseUp(inWindow.Value, MouseButton.Left);

                    Assert.False(shell.Palette.IsOpen);
                    Assert.NotEqual(first?.Route, shell.SelectedSection?.Route);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task ClickInside_KeepsPaletteOpen()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();
                var shell = services.GetRequiredService<MainWindowViewModel>();

                var window = new Views.MainWindow { DataContext = shell };
                window.Show();

                try
                {
                    shell.Palette.Open();
                    window.UpdateLayout();

                    // Поле ввода палитры: карточка шириной 720 по центру, отступ сверху 90.
                    var inside = new Point(window.Bounds.Width / 2, 120);

                    window.MouseDown(inside, MouseButton.Left);
                    window.MouseUp(inside, MouseButton.Left);

                    Assert.True(shell.Palette.IsOpen);
                }
                finally
                {
                    window.Close();
                }
            },
            CancellationToken.None);
    }
}
