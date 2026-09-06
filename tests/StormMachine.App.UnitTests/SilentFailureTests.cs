using Avalonia.Headless;
using Microsoft.Extensions.DependencyInjection;
using StormMachine.App.Services;
using StormMachine.App.ViewModels;
using StormMachine.Application.Abstractions;
using StormMachine.Application.Storage;
using StormMachine.Domain.Results;

namespace StormMachine.App.UnitTests;

/// <summary>
/// Сбой, которого страница не ждала, обязан быть назван.
/// </summary>
/// <remarks>
/// Оператор сообщил (04.09.2026): в собранном клиенте «Внешние пробы» на шаблоне
/// «Открытие сайта» «не работают». Разница в коде между его рабочей сборкой и той,
/// на которую он жалуется, — одна проба UDP, к сценарию отношения не имеющая;
/// значит, дело в окружении той машины. Но узнать это было неоткуда, и вот почему:
/// команда страницы — <c>Task</c>, <c>AsyncRelayCommand</c> исключение из неё
/// не бросает, а кладёт в задачу; страница ловила только разбор ввода; приёмника
/// журнала у клиента не было вовсе. Три слоя тишины подряд.
///
/// Здесь закрепляется первый из них: отказ хранилища доходит до экрана словами.
/// Проверяется именно неожиданный отказ — предвиденные (пустой набор, неразобранная
/// цель) страница объясняла и раньше.
/// </remarks>
[Collection("Headless")]
public sealed class SilentFailureTests(HeadlessSessionFixture fixture)
{
    private readonly HeadlessUnitTestSession _session = fixture.Session;

    [Fact(DisplayName = "Отказ базы в сценарии объясняется, а не пропадает")]
    public async Task ScenarioRun_WithBrokenStore_ExplainsInsteadOfDoingNothing()
    {
        await _session.Dispatch(
            async () =>
            {
                await using var services = AppServices.Build();

                var section = NavigationMap.Sections.First(s => s.Route == NavigationMap.Probes);
                var page = ActivatorUtilities.CreateInstance<ProbesPageViewModel>(
                    services,
                    section,
                    new BrokenStore(services.GetRequiredService<IRunStore>()));

                page.Target = "127.0.0.1";
                page.Save = true;

                await page.RunCommand.ExecuteAsync(null);

                // Раньше здесь было ровно ничего: ни ошибки, ни хода, ни следа.
                Assert.NotNull(page.Error);
                Assert.Contains("журнал", page.Error, StringComparison.OrdinalIgnoreCase);
                Assert.False(page.IsRunning);

                return true;
            },
            CancellationToken.None);
    }

    /// <summary>Хранилище, которое не открывается. Остальное — как у настоящего.</summary>
    private sealed class BrokenStore(IRunStore inner) : IRunStore
    {
        public string Location => inner.Location;

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("файл базы занят другим процессом");

        public Task<IRunWriter> BeginRunAsync(RunDescriptor descriptor, CancellationToken cancellationToken = default) =>
            inner.BeginRunAsync(descriptor, cancellationToken);

        public Task<IReadOnlyList<RunSummary>> ListAsync(RunQuery query, CancellationToken cancellationToken = default) =>
            inner.ListAsync(query, cancellationToken);

        public Task<StoredRun?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetAsync(id, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            inner.DeleteAsync(id, cancellationToken);

        public Task<RetentionReport> ApplyRetentionAsync(
            RetentionPolicy policy,
            bool dryRun = false,
            CancellationToken cancellationToken = default) =>
            inner.ApplyRetentionAsync(policy, dryRun, cancellationToken);

        public Task<StorageUsage> GetUsageAsync(CancellationToken cancellationToken = default) =>
            inner.GetUsageAsync(cancellationToken);
    }
}
