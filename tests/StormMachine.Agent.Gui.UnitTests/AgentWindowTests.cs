using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using StormMachine.Agent;
using StormMachine.Agent.Gui;
using StormMachine.Protocol;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace StormMachine.Agent.Gui.UnitTests;

/// <summary>Сборка окна агента для headless-прогона.</summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AgentApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

[CollectionDefinition("Headless")]
public sealed class HeadlessTests : ICollectionFixture<HeadlessSessionFixture>;

public sealed class HeadlessSessionFixture : IDisposable
{
    public HeadlessUnitTestSession Session { get; } =
        HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder));

    public void Dispose() => Session.Dispose();
}

/// <summary>
/// Окно агента показывает то, ради чего его открыли.
/// </summary>
/// <remarks>
/// Агент не поднимается: проверяется разбор происходящего и показ, а не сеть.
/// Слушать настоящий порт в проверке значило бы требовать от неё свободного порта
/// и прав — и ловить чужого агента на той же машине.
/// </remarks>
[Collection("Headless")]
public sealed class AgentWindowTests(HeadlessSessionFixture fixture)
{
    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact(DisplayName = "Окно собирается и показывает отпечаток и состояние")]
    public async Task Window_ShowsFingerprintAndState()
    {
        await _session.Dispatch(
            async () =>
            {
                var model = new AgentWindowViewModel();
                var window = new AgentWindow { DataContext = model };

                window.Show();
                window.UpdateLayout();

                var texts = Texts(window);

                // Шаблон, который не применился, оставил бы окно пустым, и сборка
                // об этом молчала бы.
                Assert.Contains(texts, t => t.Contains("Агент Storm Machine", StringComparison.Ordinal));
                Assert.Contains(texts, t => t.Contains("Сопряжённые клиенты", StringComparison.Ordinal));

                window.Close();

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact(DisplayName = "Снимки измерения ложатся в график, итог — в список")]
    public async Task Samples_FeedTheChartAndTheList()
    {
        await _session.Dispatch(
            async () =>
            {
                var model = new AgentWindowViewModel();
                var redrawn = 0;

                model.ChartUpdated += (_, _) => redrawn++;

                model.Accept(Moment(AgentMomentKind.TestStarted, null));
                model.Accept(Moment(AgentMomentKind.TestSample, Sample(94.2, final: false)));
                model.Accept(Moment(AgentMomentKind.TestSample, Sample(61.7, final: false)));
                model.Accept(Moment(AgentMomentKind.TestFinished, Sample(78.0, final: true)));

                // Точки — за отрезок между снимками, а не средние: просадка
                // с 94 до 62 обязана быть видна на графике.
                Assert.Equal([94.2, 61.7], model.ChartValues);
                Assert.True(redrawn >= 3);

                var done = Assert.Single(model.Measurements);

                Assert.Equal("Пропускная способность", done.Test);
                Assert.Contains("78", done.Result, StringComparison.Ordinal);
                Assert.Null(model.Measuring);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    [Fact(DisplayName = "Код сопряжения показывается и гаснет после сопряжения")]
    public async Task Code_AppearsAndGoesAfterPairing()
    {
        await _session.Dispatch(
            async () =>
            {
                var model = new AgentWindowViewModel();

                model.Accept(new AgentMoment { Kind = AgentMomentKind.CodeIssued, Text = "1234 5678" });

                Assert.Equal("1234 5678", model.Code);
                Assert.NotNull(model.CodeNote);

                // Код одноразовый: оставить его на экране после сопряжения значило бы
                // предложить сопрячься по нему второму.
                model.Accept(new AgentMoment { Kind = AgentMomentKind.PeerPaired, Peer = "ноутбук" });

                Assert.Null(model.Code);

                await Task.CompletedTask;
            },
            CancellationToken.None);
    }

    private static AgentMoment Moment(AgentMomentKind kind, TestSnapshot? snapshot) => new()
    {
        Kind = kind,
        Peer = "ноутбук",
        Test = TestKind.TcpThroughput,
        Snapshot = snapshot,
    };

    private static TestSnapshot Sample(double mbps, bool final) => new()
    {
        Id = Guid.Empty,
        ElapsedSeconds = 1,
        Bytes = 1_000_000,
        Packets = 700,
        Mbps = mbps,
        IsFinal = final,
    };

    private static List<string> Texts(Visual root) =>
    [
        .. root.GetSelfAndVisualDescendants()
            .OfType<TextBlock>()
            .Select(t => t.Text ?? string.Empty)
            .Where(t => t.Length > 0),
    ];
}
