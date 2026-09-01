using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Нажимаемое не мельче 32×28.
/// </summary>
/// <remarks>
/// Норма из дизайн-плана. Причина не в моде на крупные кнопки: продукт работает
/// на ноутбуке инженера в шкафу с оборудованием, где мышь лежит на коленке,
/// и промах по кнопке «отменить» рядом с «удалить» стоит дороже сэкономленных
/// восьми пикселей.
/// <para>
/// Проверяются только элементы, положенные на страницу руками: внутренности
/// шаблонов темы (стрелки счётчика, кнопка раскрытия списка) — не наше дело,
/// а невидимое и вовсе не нажимается.
/// </para>
/// </remarks>
[Collection("Headless")]
public sealed class TouchTargetTests(HeadlessSessionFixture fixture)
{
    private const double MinWidth = 32;
    private const double MinHeight = 28;

    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact]
    public async Task EveryClickable_IsLargeEnough()
    {
        var small = await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();
                var factory = services.GetRequiredService<Func<NavigationSection, PageViewModel>>();
                var shell = services.GetRequiredService<MainWindowViewModel>();
                var locator = new ViewLocator();

                var found = new List<string>();

                foreach (var section in NavigationMap.Sections)
                {
                    var page = factory(section);
                    var view = locator.Build(page);
                    view.DataContext = page;

                    var window = new Window
                    {
                        Content = view,
                        DataContext = shell,
                        Width = 1600,
                        Height = 1000,
                    };

                    window.Show();

                    try
                    {
                        window.UpdateLayout();

                        foreach (var control in view.GetVisualDescendants().OfType<Button>())
                        {
                            if (control.TemplatedParent is not null || !control.IsVisible)
                            {
                                continue;
                            }

                            var size = control.Bounds;

                            if (size.Width <= 0 || size.Height <= 0)
                            {
                                continue;
                            }

                            if (size.Width < MinWidth || size.Height < MinHeight)
                            {
                                found.Add(
                                    $"{section.Route}: «{Describe(control)}» "
                                    + $"{size.Width:0}×{size.Height:0}");
                            }
                        }
                    }
                    finally
                    {
                        window.Close();
                    }
                }

                return found;
            },
            CancellationToken.None);

        Assert.True(
            small.Count == 0,
            $"Мишени мельче {MinWidth:0}×{MinHeight:0}:"
            + Environment.NewLine + string.Join(Environment.NewLine, small));
    }

    private static string Describe(ContentControl control) =>
        control.Content?.ToString() is { Length: > 0 } text ? text : control.GetType().Name;
}
