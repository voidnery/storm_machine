using Avalonia.Headless;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Прогонщик пробы: форма из паспорта и настоящий прогон через полный конвейер.
/// </summary>
/// <remarks>
/// Страницы «Локальные тесты» и «Скорость и качество» доделаны в И-24 из заглушек;
/// здесь закрепляется, что общий прогонщик собирает форму из объявления пробы
/// (принцип 1) и что нажатие «Запустить» доходит до оркестратора и возвращает
/// ряды с фактами — на настоящем ping до loopback, а не на подделке.
/// </remarks>
[Collection("Headless")]
public sealed class ProbeRunnerTests(HeadlessSessionFixture fixture)
{
    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact]
    public async Task Form_IsBuiltFromProbePassport()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();
                var runner = CreateRunner(services, "ping");

                // У ping объявлены count, interval и timeout — поля обязаны появиться
                // без единой строчки формы, написанной руками.
                Assert.True(runner.Fields.Count >= 3);
                Assert.Contains(runner.Fields, f => f.Parameter.Name == "count");
                Assert.All(runner.Fields, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));

                return true;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task BrokenTarget_IsExplainedWithoutRunning()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();
                var runner = CreateRunner(services, "ping");

                runner.Target = "   ";

                await runner.StartCommand.ExecuteAsync(null);

                Assert.NotNull(runner.Error);
                Assert.False(runner.IsRunning);

                return true;
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task RealPingOverLoopback_YieldsSeriesAndStatus()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();
                var runner = CreateRunner(services, "ping");

                runner.Target = "127.0.0.1";
                runner.Save = false;
                runner.Fields.First(f => f.Parameter.Name == "count").Number = 2;
                runner.Fields.First(f => f.Parameter.Name == "interval").Number = 50;

                var finished = new TaskCompletionSource();
                runner.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(runner.IsRunning) && !runner.IsRunning)
                    {
                        finished.TrySetResult();
                    }
                };

                await runner.StartCommand.ExecuteAsync(null);
                Assert.Null(runner.Error);

                await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

                Assert.Null(runner.Error);
                Assert.NotEmpty(runner.Series);
                Assert.StartsWith("Завершено", runner.Status, StringComparison.Ordinal);

                return true;
            },
            CancellationToken.None);
    }

    private static ProbeRunnerViewModel CreateRunner(IServiceProvider services, params string[] probes) =>
        new(
            services.GetRequiredService<RunnerService>(),
            services.GetRequiredService<IProbeRegistry>(),
            services.GetRequiredService<IRunStore>(),
            services.GetRequiredService<IAgentDirectory>(),
            probes);
}
