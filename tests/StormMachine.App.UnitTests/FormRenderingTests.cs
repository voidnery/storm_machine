using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using StormMachine.App.Controls;
using StormMachine.Domain.Results;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Формы словаря показывают то, что им передали.
/// </summary>
/// <remarks>
/// Шаблон, который не применился, ничего не ломает: контрол просто оказывается пустым
/// местом на экране, и сборка об этом молчит. Именно так пропадает текст, ради которого
/// форма и заводилась, — поэтому проверка смотрит на показанное, а не на собранное.
/// <para>
/// Отдельно проверяется скрытый смысл двух форм: оговорка и статус исчезают с пустым
/// текстом сами. На это рассчитывают страницы — они не привязывают им видимость.
/// </para>
/// </remarks>
[Collection("Headless")]
public sealed class FormRenderingTests(HeadlessSessionFixture fixture)
{
    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact]
    public async Task MethodCard_ShowsThesisAndHidesDetailUntilAsked()
    {
        await _session.Dispatch(
            async () =>
            {
                var card = new MethodCard
                {
                    Thesis = "Тезис виден всегда",
                    Detail = "Обоснование по требованию",
                };

                var window = Open(card);

                Assert.Contains("Тезис виден всегда", ShownTexts(card), StringComparer.Ordinal);
                Assert.DoesNotContain("Обоснование по требованию", VisibleTexts(card), StringComparer.Ordinal);

                card.IsOpen = true;
                window.UpdateLayout();

                Assert.Contains("Обоснование по требованию", VisibleTexts(card), StringComparer.Ordinal);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Caveat_SpeaksOnlyWhenThereIsSomethingToSay()
    {
        await _session.Dispatch(
            async () =>
            {
                var caveat = new Caveat();

                var window = Open(caveat);

                Assert.False(caveat.IsVisible);

                caveat.Text = "Не равно недоступности";
                window.UpdateLayout();

                Assert.True(caveat.IsVisible);
                Assert.Contains("Не равно недоступности", ShownTexts(caveat), StringComparer.Ordinal);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task StatusLine_MarksStateAndHidesWhenSilent()
    {
        await _session.Dispatch(
            async () =>
            {
                var status = new StatusLine();

                var window = Open(status);

                Assert.False(status.IsVisible);

                status.Text = "Прогон завершён.";
                status.State = OperationState.Done;
                window.UpdateLayout();

                Assert.True(status.IsVisible);

                var shown = ShownTexts(status);

                Assert.Contains("Прогон завершён.", shown, StringComparer.Ordinal);
                Assert.Contains("✓", shown, StringComparer.Ordinal);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    /// <summary>Знак вердикта берётся из словаря продукта, а не пишется в разметке.</summary>
    [Fact]
    public async Task VerdictLine_TakesMarkFromProductWording()
    {
        await _session.Dispatch(
            async () =>
            {
                var verdict = new VerdictLine
                {
                    Text = "Цель достигнута.",
                    Level = VerdictLevel.Pass,
                };

                var window = Open(verdict);

                var shown = ShownTexts(verdict);

                Assert.Contains("Цель достигнута.", shown, StringComparer.Ordinal);
                Assert.Contains(VerdictWording.Mark(VerdictLevel.Pass), shown, StringComparer.Ordinal);

                verdict.Level = VerdictLevel.Fail;
                window.UpdateLayout();

                Assert.Contains(VerdictWording.Mark(VerdictLevel.Fail), ShownTexts(verdict), StringComparer.Ordinal);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task TileBadgeAndChip_ShowWhatTheyWereGiven()
    {
        await _session.Dispatch(
            async () =>
            {
                var tile = new StatTile { Caption = "Прогонов", Value = "541" };
                var badge = new ConditionBadge { Label = "интерфейс", Value = "Ethernet" };
                var chip = new CopyChip { Text = "storm runs purge" };

                var panel = new StackPanel();
                panel.Children.Add(tile);
                panel.Children.Add(badge);
                panel.Children.Add(chip);

                var window = Open(panel);

                var shown = ShownTexts(panel);

                Assert.Contains("Прогонов", shown, StringComparer.Ordinal);
                Assert.Contains("541", shown, StringComparer.Ordinal);
                Assert.Contains("интерфейс", shown, StringComparer.Ordinal);
                Assert.Contains("Ethernet", shown, StringComparer.Ordinal);
                Assert.Contains("storm runs purge", shown, StringComparer.Ordinal);

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

    /// <summary>Все тексты внутри контрола — независимо от того, показаны они сейчас.</summary>
    private static List<string> ShownTexts(Control control) =>
        [.. control.GetVisualDescendants().OfType<TextBlock>()
            .Select(t => t.Text)
            .OfType<string>()];

    /// <summary>Только видимое: скрытая развёртка карточки в этот список не попадает.</summary>
    private static List<string> VisibleTexts(Control control) =>
        [.. control.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsVisible)
            .Select(t => t.Text)
            .OfType<string>()];
}
