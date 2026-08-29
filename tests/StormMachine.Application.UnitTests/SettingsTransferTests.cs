using StormMachine.Application.Profiles;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Перенос настроек между машинами.
/// </summary>
/// <remarks>
/// Один механизм на три однотипных долга — расписание (И-14), эталоны (И-15) и профили
/// (И-16). Проверяется не сериализация как таковая, а решения, которые в неё заложены:
/// что <b>не</b> едет вместе с настройкой и почему.
/// </remarks>
public sealed class SettingsTransferTests
{
    private static Monitor Monitor(string name, Guid? preset = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Subject = "ping",
        Target = Target.Ip("192.168.1.1"),
        Schedule = Schedule.Every(TimeSpan.FromMinutes(5)),
        NextDueUtc = DateTimeOffset.UnixEpoch.AddYears(20),
        PresetId = preset,
    };

    private static NetworkProfile Profile(string name, bool active = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        IsActive = active,
    };

    private static SettingsTransfer Build(FakeMonitorStore monitors, FakeProfileStore profiles) =>
        new(profiles, monitors, new FakeBaselineStore());

    // ------------------------------------------------------------------ выгрузка

    /// <summary>
    /// Активность профиля не переносится.
    /// </summary>
    /// <remarks>
    /// Активен профиль или нет — свойство машины, а не настройки. Приехавший профиль,
    /// объявивший себя активным, молча поменял бы пороги и состав работающих мониторов
    /// на чужой машине — то есть поменял бы смысл её измерений за спиной оператора.
    /// </remarks>
    [Fact]
    public async Task Export_DoesNotCarryTheActiveFlag()
    {
        var profiles = new FakeProfileStore();
        await profiles.SaveAsync(Profile("Офис", active: true));

        var bundle = await Build(new FakeMonitorStore(), profiles).ExportAsync();

        Assert.Single(bundle.Profiles);
        Assert.False(bundle.Profiles[0].IsActive);
    }

    /// <summary>
    /// Назначенный срок не переносится.
    /// </summary>
    /// <remarks>
    /// Он посчитан от часов той машины и на новой оказался бы либо в далёком прошлом —
    /// и планировщик записал бы гору пропусков, которых не было, — либо в будущем.
    /// Назначить его заново умеет сам планировщик.
    /// </remarks>
    [Fact]
    public async Task Export_DropsTheScheduledDue()
    {
        var monitors = new FakeMonitorStore();
        await monitors.SaveAsync(Monitor("Шлюз"));

        var bundle = await Build(monitors, new FakeProfileStore()).ExportAsync();

        Assert.Single(bundle.Monitors);
        Assert.Null(bundle.Monitors[0].NextDueUtc);
    }

    [Fact]
    public async Task Export_OfEmptyMachineIsEmpty()
    {
        var bundle = await Build(new FakeMonitorStore(), new FakeProfileStore()).ExportAsync();

        Assert.True(bundle.IsEmpty);
        Assert.Equal("пусто", bundle.Describe());
    }

    /// <summary>Состав файла называется человеческим языком и со склонением.</summary>
    [Fact]
    public async Task Export_DescribesWhatIsInside()
    {
        var monitors = new FakeMonitorStore();
        await monitors.SaveAsync(Monitor("первый"));
        await monitors.SaveAsync(Monitor("второй"));

        var profiles = new FakeProfileStore();
        await profiles.SaveAsync(Profile("Офис"));

        var bundle = await Build(monitors, profiles).ExportAsync();

        Assert.Equal("1 профиль, 2 монитора", bundle.Describe());
    }

    // ------------------------------------------------------------------ формат

    [Fact]
    public async Task Bundle_SurvivesTheRoundTrip()
    {
        var monitors = new FakeMonitorStore();
        await monitors.SaveAsync(Monitor("Шлюз заказчика"));

        var before = await Build(monitors, new FakeProfileStore()).ExportAsync();
        var after = SettingsTransfer.Read(SettingsTransfer.Write(before));

        Assert.Equal(before.Monitors.Count, after.Monitors.Count);
        Assert.Equal(before.Monitors[0].Id, after.Monitors[0].Id);
        Assert.Equal("Шлюз заказчика", after.Monitors[0].Name);
    }

    /// <summary>Кириллица остаётся читаемой: файл открывают и правят руками.</summary>
    [Fact]
    public async Task Bundle_StaysHumanReadable()
    {
        var profiles = new FakeProfileStore();
        await profiles.SaveAsync(Profile("Офис заказчика"));

        var json = SettingsTransfer.Write(await Build(new FakeMonitorStore(), profiles).ExportAsync());

        Assert.Contains("Офис заказчика", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u041e", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Файл из будущего отвергается объяснимо, а не разбирается неверно.
    /// </summary>
    /// <remarks>
    /// То же решение, что у пресетов: версия формата указана явно именно затем, чтобы
    /// файл, сохранённый будущей версией, был отвергнут с внятным текстом. Молча
    /// разобрать его наполовину — худший из исходов.
    /// </remarks>
    [Fact]
    public async Task BundleFromTheFuture_IsRefusedWithAnExplanation()
    {
        var transfer = Build(new FakeMonitorStore(), new FakeProfileStore());

        var future = new SettingsBundle { FormatVersion = SettingsBundle.CurrentFormatVersion + 1 };

        var ex = await Assert.ThrowsAsync<FormatException>(() => transfer.ImportAsync(future));

        Assert.Contains("более новой версией", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ загрузка

    /// <summary>
    /// Повторная загрузка того же файла обновляет, а не задваивает.
    /// </summary>
    /// <remarks>
    /// Опознание идёт по идентификатору, а не по имени: имя меняют, а настройка
    /// остаётся той же. По имени повторная загрузка после переименования завела бы
    /// вторую копию, и оператор получил бы два монитора, проверяющих одно и то же.
    /// </remarks>
    [Fact]
    public async Task ImportingTwice_UpdatesInsteadOfDuplicating()
    {
        var source = new FakeMonitorStore();
        await source.SaveAsync(Monitor("Шлюз"));

        var bundle = await Build(source, new FakeProfileStore()).ExportAsync();

        var target = new FakeMonitorStore();
        var transfer = Build(target, new FakeProfileStore());

        var first = await transfer.ImportAsync(bundle);
        var second = await transfer.ImportAsync(bundle);

        Assert.Equal(1, first.Added);
        Assert.Equal(0, second.Added);
        Assert.Equal(1, second.Updated);
        Assert.Single(await target.ListAsync());
    }

    /// <summary>С ключом «не трогать» существующее остаётся нетронутым.</summary>
    [Fact]
    public async Task ImportWithKeep_LeavesExistingAlone()
    {
        var source = new FakeMonitorStore();
        await source.SaveAsync(Monitor("Шлюз"));

        var bundle = await Build(source, new FakeProfileStore()).ExportAsync();

        var target = new FakeMonitorStore();
        var transfer = Build(target, new FakeProfileStore());

        await transfer.ImportAsync(bundle);
        var second = await transfer.ImportAsync(bundle, overwrite: false);

        Assert.Equal(0, second.Updated);
        Assert.Equal(1, second.Skipped);
    }

    /// <summary>
    /// Приехавший профиль неактивен, даже если на этой машине активен другой.
    /// </summary>
    /// <remarks>
    /// Продукт узнаёт сеть, но не переключает профиль сам — решение И-16. Перенос
    /// не должен становиться лазейкой, через которую профиль всё-таки переключается.
    /// </remarks>
    [Fact]
    public async Task ImportedProfile_ArrivesInactive()
    {
        var source = new FakeProfileStore();
        await source.SaveAsync(Profile("Офис", active: true));

        var bundle = await Build(new FakeMonitorStore(), source).ExportAsync();

        var target = new FakeProfileStore();
        await Build(new FakeMonitorStore(), target).ImportAsync(bundle);

        var arrived = (await target.ListAsync()).Single();

        Assert.False(arrived.IsActive);
    }

    /// <summary>
    /// Ссылка на пресет обнуляется: библиотека едет своим механизмом.
    /// </summary>
    /// <remarks>
    /// Монитор от этого не ломается — параметры лежат в нём самом, — но показывать
    /// родство с пресетом, которого на этой машине нет, значило бы обещать связь,
    /// по которой некуда перейти.
    /// </remarks>
    [Fact]
    public async Task ImportedMonitor_ForgetsItsPreset()
    {
        var source = new FakeMonitorStore();
        await source.SaveAsync(Monitor("Шлюз", preset: Guid.NewGuid()));

        var bundle = await Build(source, new FakeProfileStore()).ExportAsync();

        var target = new FakeMonitorStore();
        await Build(target, new FakeProfileStore()).ImportAsync(bundle);

        Assert.Null((await target.ListAsync()).Single().PresetId);
    }

    /// <summary>
    /// Одна непригодная запись не отменяет остальные, но и не молчит.
    /// </summary>
    /// <remarks>
    /// Перенести девять настроек из десяти полезнее, чем отказаться от всех. Но
    /// оператор обязан узнать, чего у него не появилось, — иначе он обнаружит
    /// отсутствие монитора через неделю и не поймёт, куда тот делся.
    /// </remarks>
    [Fact]
    public async Task BrokenEntry_IsSkippedAndNamed()
    {
        var target = new FakeMonitorStore();
        var transfer = Build(target, new FakeProfileStore());

        var bundle = new SettingsBundle
        {
            Monitors =
            [
                Monitor("хороший"),

                // Пустое имя — монитор не проходит собственную проверку.
                Monitor("хороший") with { Id = Guid.NewGuid(), Name = "  " },
            ],
        };

        var report = await transfer.ImportAsync(bundle);

        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Skipped);
        Assert.Single(report.Problems);
        Assert.Single(await target.ListAsync());
    }

    /// <summary>Оговорка про секреты названа и не пуста: её печатают оба клиента.</summary>
    [Fact]
    public void SecretsNote_NamesWhatDoesNotTravel()
    {
        Assert.Contains("SNMP", SettingsTransfer.SecretsNote, StringComparison.Ordinal);
        Assert.Contains("не расшифруются", SettingsTransfer.SecretsNote, StringComparison.Ordinal);
    }
}
