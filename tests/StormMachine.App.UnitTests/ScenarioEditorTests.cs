using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Probes;
using StormMachine.Application.Scenarios;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Конструктор сценариев: собранное сохраняется, непригодное отклоняется словами.
/// </summary>
/// <remarks>
/// Проверка шагов — та же, что у консольного <c>storm scenario step</c>: реестр проб,
/// разбор порогов, валидация параметров пробой. Здесь закрепляется, что экранная
/// обвязка её действительно вызывает и что сохранённое немедленно доступно
/// для запуска через библиотеку сценариев.
/// </remarks>
public sealed class ScenarioEditorTests
{
    [Fact]
    public async Task SavedScenario_AppearsInLibrary_AndIsRunnable()
    {
        await using var services = AppServices.Build();
        var editor = CreateEditor(services, out var refreshes);

        editor.Name = "мой-веб";
        editor.AddStepCommand.Execute(null);
        editor.Steps[0].ProbeName = "ping";
        editor.Steps[0].Target = "127.0.0.1";
        editor.Steps[0].ThresholdsText = "p95 < 100";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);
        Assert.NotNull(editor.Message);
        Assert.Single(refreshes);

        var library = services.GetRequiredService<ScenarioLibrary>();
        var entries = await library.ListAsync();
        Assert.Contains(entries, e => !e.IsTemplate && e.Key == "мой-веб");

        var runnable = await library.CreateAsync("мой-веб", "127.0.0.1");
        Assert.Single(runnable.Steps);
        Assert.Equal("ping", runnable.Steps[0].ProbeName);
        Assert.Single(runnable.Steps[0].Thresholds);
    }

    [Fact]
    public async Task BrokenThresholdAndUnknownProbe_AreExplained()
    {
        await using var services = AppServices.Build();
        var editor = CreateEditor(services, out _);

        editor.Name = "битый";
        editor.AddStepCommand.Execute(null);
        editor.Steps[0].ProbeName = "телепорт";
        editor.AddStepCommand.Execute(null);
        editor.Steps[1].ProbeName = "ping";
        editor.Steps[1].Target = "127.0.0.1";
        editor.Steps[1].ThresholdsText = "как-нибудь побыстрее";

        await editor.SaveCommand.ExecuteAsync(null);

        Assert.NotNull(editor.Error);
        Assert.Contains("Шаг 1", editor.Error, StringComparison.Ordinal);
        Assert.Contains("телепорт", editor.Error, StringComparison.Ordinal);
        Assert.Contains("Шаг 2", editor.Error, StringComparison.Ordinal);
        Assert.Contains("не разобран", editor.Error, StringComparison.Ordinal);

        // Непригодное не сохраняется даже частично.
        var store = services.GetRequiredService<IScenarioStore>();
        await store.InitializeAsync();
        Assert.Null(await store.FindAsync("битый"));
    }

    [Fact]
    public async Task SavingExistingName_UpdatesAndBumpsVersion()
    {
        await using var services = AppServices.Build();
        var editor = CreateEditor(services, out _);

        editor.Name = "растущий";
        editor.AddStepCommand.Execute(null);
        editor.Steps[0].ProbeName = "ping";
        editor.Steps[0].Target = "127.0.0.1";
        await editor.SaveCommand.ExecuteAsync(null);

        editor.AddStepCommand.Execute(null);
        editor.Steps[1].ProbeName = "tcp";
        editor.Steps[1].Target = "127.0.0.1";
        editor.Steps[1].ParametersText = "port=80";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);

        var store = services.GetRequiredService<IScenarioStore>();
        var saved = await store.FindAsync("растущий");

        Assert.NotNull(saved);
        Assert.Equal(2, saved.Version);
        Assert.Equal(2, saved.Steps.Count);
    }

    [Fact]
    public async Task Delete_RemovesOwnScenario()
    {
        await using var services = AppServices.Build();
        var editor = CreateEditor(services, out var refreshes);

        editor.Name = "временный";
        editor.AddStepCommand.Execute(null);
        editor.Steps[0].ProbeName = "ping";
        editor.Steps[0].Target = "127.0.0.1";
        await editor.SaveCommand.ExecuteAsync(null);

        await editor.DeleteCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);
        Assert.Equal(2, refreshes.Count);

        var store = services.GetRequiredService<IScenarioStore>();
        Assert.Null(await store.FindAsync("временный"));
    }

    private static ScenarioEditorViewModel CreateEditor(IServiceProvider services, out List<int> refreshes)
    {
        var calls = new List<int>();
        refreshes = calls;

        return new ScenarioEditorViewModel(
            services.GetRequiredService<IScenarioStore>(),
            services.GetRequiredService<IProbeRegistry>(),
            _ =>
            {
                calls.Add(1);

                return Task.CompletedTask;
            });
    }
}
