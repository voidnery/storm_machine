using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Каждая страница открывается в headless-окне, и её визуальное дерево осматривается.
/// </summary>
/// <remarks>
/// Закрывает долг «ViewModels покрыты сборкой страниц, но не поведением» ровно с той
/// стороны, с которой он выстрелил в И-24: оператор открыл собранный клиент и за минуты
/// нашёл дыры из пробелов посреди текстов. Сборка и разметка были зелёными — смотреть
/// надо на то, что показано, а не на то, что скомпилировалось.
/// </remarks>
[Collection("Headless")]
public sealed class PageRenderingTests(HeadlessSessionFixture fixture)
{
    private readonly HeadlessUnitTestSession _session = fixture.Session;

    /// <summary>
    /// Страницы рендерятся, тексты без дыр, у каждой кнопки есть команда.
    /// </summary>
    /// <remarks>
    /// Три и более пробела подряд в показанном тексте — дыра: столько не пишут руками,
    /// столько вклеивает перенос строки в разметке или склейка строк в коде.
    /// Кнопка без команды — мёртвая: нажимается и не делает ничего.
    /// </remarks>
    [Fact]
    public async Task EveryPage_RendersCleanly()
    {
        var failures = await _session.Dispatch(
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

                    // Окно несёт модель оболочки, как в настоящем клиенте: часть команд
                    // страниц привязана к ней через $parent[Window] — на голом окне
                    // они выглядели бы мёртвыми, не будучи такими.
                    var window = new Window
                    {
                        Content = view,
                        DataContext = shell,
                        Width = 1280,
                        Height = 850,
                    };
                    window.Show();

                    try
                    {
                        window.UpdateLayout();

                        foreach (var text in view.GetVisualDescendants().OfType<TextBlock>())
                        {
                            if (text.Text is { } shown && shown.Contains("   ", StringComparison.Ordinal))
                            {
                                found.Add($"{section.Route}: дыра из пробелов в «{Shorten(shown)}»");
                            }
                        }

                        // Только чистые Button, положенные на страницу руками:
                        // CheckBox и ToggleButton живут привязкой IsChecked, кнопка
                        // с Flyout открывает меню, а кнопки из шаблонов (раскрытие
                        // пароля, спиннеры) — дело темы, а не страницы.
                        foreach (var button in view.GetVisualDescendants().OfType<Button>())
                        {
                            if (button.GetType() == typeof(Button)
                                && button.TemplatedParent is null
                                && button.Flyout is null
                                && button.Command is null)
                            {
                                found.Add($"{section.Route}: кнопка «{button.Content}» без команды");
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
            failures.Count == 0,
            "Осмотр страниц нашёл:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    /// <summary>Оболочка открывается с настоящей моделью: навигация, статус, первая страница.</summary>
    [Fact]
    public async Task MainWindow_OpensWithRealShell()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();

                var window = new Views.MainWindow
                {
                    DataContext = services.GetRequiredService<MainWindowViewModel>(),
                };

                window.Show();

                try
                {
                    window.UpdateLayout();

                    var model = (MainWindowViewModel)window.DataContext!;
                    Assert.Equal(NavigationMap.Sections.Count, model.Sections.Count);
                    Assert.NotNull(model.CurrentPage);
                }
                finally
                {
                    window.Close();
                }

                return true;
            },
            CancellationToken.None);
    }

    private static string Shorten(string text) =>
        text.Length <= 60 ? text : text[..60] + "…";
}
