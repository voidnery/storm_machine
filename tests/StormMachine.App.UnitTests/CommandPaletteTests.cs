using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
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
