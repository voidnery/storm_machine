using StormMachine.Domain.Presets;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage.UnitTests;

/// <summary>Проверки библиотеки пресетов.</summary>
public sealed class SqlitePresetStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _databasePath;

    public SqlitePresetStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "storm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "storm.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SqlitePresetStore CreateStore() => new(new SqliteRunStore(new StorageOptions
    {
        DatabasePath = _databasePath,
        ApplyRetentionOnStartup = false,
    }));

    private static Preset Sample(string name = "Шлюз", string probe = "ping", int count = 4)
    {
        var now = DateTimeOffset.UtcNow;

        return new Preset
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = "проверка шлюза",
            ProbeName = probe,
            Target = Target.Gateway("шлюз по умолчанию"),
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["interval"] = "1000",
            },
            Tags = ["сеть", "быстро"],
            Version = 1,
            CreatedUtc = now,
            UpdatedUtc = now,
        };
    }

    [Fact]
    public async Task SaveAndGet_RoundTrips()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(Sample());

        var loaded = await store.GetAsync(saved.Id);

        Assert.NotNull(loaded);
        Assert.Equal("Шлюз", loaded.Name);
        Assert.Equal("ping", loaded.ProbeName);
        Assert.Equal(TargetKind.DefaultGateway, loaded.Target.Kind);
        Assert.Equal("4", loaded.Parameters["count"]);
        Assert.Equal(["сеть", "быстро"], loaded.Tags);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task FindByName_IsCaseInsensitive()
    {
        var store = CreateStore();
        await store.SaveAsync(Sample("Шлюз Офиса"));

        var found = await store.FindByNameAsync("шлюз офиса");

        Assert.NotNull(found);
        Assert.Equal("Шлюз Офиса", found.Name);
    }

    [Fact]
    public async Task SameName_IsRejected()
    {
        // Две записи «Шлюз» и «шлюз» — это ошибка оператора, а не две разные проверки.
        var store = CreateStore();
        await store.SaveAsync(Sample("Шлюз"));

        var duplicate = Sample("шлюз");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(duplicate));

        Assert.Contains("уже есть в библиотеке", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Version_DoesNotGrowWhenMeasurementIsUnchanged()
    {
        // Переименование не делает пресет другим тестом. Если бы версия росла от любого
        // изменения, счётчик версий перестал бы что-либо значить.
        var store = CreateStore();
        var saved = await store.SaveAsync(Sample("Шлюз", count: 4));

        var renamed = saved with { Name = "Шлюз офиса", Description = "другое описание" };
        var again = await store.SaveAsync(renamed);

        Assert.Equal(1, again.Version);
        Assert.Equal("Шлюз офиса", again.Name);
    }

    [Fact]
    public async Task Version_GrowsWhenParametersChange()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(Sample(count: 4));

        var changed = saved with
        {
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = "20",
                ["interval"] = "1000",
            },
        };

        var again = await store.SaveAsync(changed);

        Assert.Equal(2, again.Version);
    }

    [Fact]
    public async Task Version_GrowsWhenTargetChanges()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(Sample());

        var changed = saved with { Target = Target.Ip("8.8.8.8") };
        var again = await store.SaveAsync(changed);

        Assert.Equal(2, again.Version);
    }

    [Fact]
    public async Task CreatedDate_SurvivesUpdate()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(Sample());

        await Task.Delay(20);
        var again = await store.SaveAsync(saved with { Description = "новое описание" });

        Assert.Equal(saved.CreatedUtc.UtcTicks, again.CreatedUtc.UtcTicks);
        Assert.True(again.UpdatedUtc >= saved.UpdatedUtc);
    }

    [Fact]
    public async Task RecordRun_IncrementsCounter()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(Sample());

        await store.RecordRunAsync(saved.Id);
        await store.RecordRunAsync(saved.Id);

        var loaded = await store.GetAsync(saved.Id);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded.RunCount);
        Assert.NotNull(loaded.LastRunUtc);
    }

    [Fact]
    public async Task List_FiltersByProbeAndSearch()
    {
        var store = CreateStore();
        await store.SaveAsync(Sample("Шлюз", "ping"));
        await store.SaveAsync(Sample("Сайт компании", "http"));

        var pings = await store.ListAsync(new PresetQuery { ProbeName = "ping" });
        Assert.Single(pings);

        var search = await store.ListAsync(new PresetQuery { Search = "сайт" });
        Assert.Single(search);
        Assert.Equal("Сайт компании", search[0].Name);
    }

    [Fact]
    public async Task List_FiltersByTag()
    {
        var store = CreateStore();
        await store.SaveAsync(Sample("С тегами"));
        await store.SaveAsync(Sample("Без тегов") with { Tags = [] });

        var tagged = await store.ListAsync(new PresetQuery { Tag = "сеть" });

        Assert.Single(tagged);
        Assert.Equal("С тегами", tagged[0].Name);
    }

    [Fact]
    public async Task GetTags_ReturnsDistinctSorted()
    {
        var store = CreateStore();
        await store.SaveAsync(Sample("Первый") with { Tags = ["бета", "альфа"] });
        await store.SaveAsync(Sample("Второй") with { Tags = ["альфа", "гамма"] });

        var tags = await store.GetTagsAsync();

        Assert.Equal(["альфа", "бета", "гамма"], tags);
    }

    [Fact]
    public async Task Delete_RemovesPreset()
    {
        var store = CreateStore();
        var saved = await store.SaveAsync(Sample());

        Assert.True(await store.DeleteAsync(saved.Id));
        Assert.Null(await store.GetAsync(saved.Id));
    }

    [Fact]
    public async Task SchemaUpgrade_KeepsExistingRuns()
    {
        // Проверка ступенчатого обновления схемы: база версии 1 с прогоном должна
        // дойти до версии 2 и не потерять данные. Иначе обновление продукта
        // стирало бы историю пользователя.
        var v1Options = new StorageOptions { DatabasePath = _databasePath, ApplyRetentionOnStartup = false };
        var runStore = new SqliteRunStore(v1Options);
        await runStore.InitializeAsync();

        Guid runId;
        await using (var writer = await runStore.BeginRunAsync(new Application.Abstractions.RunDescriptor
        {
            Kind = Domain.Results.ProbeKind.Icmp,
            ProbeName = "ping",
            Shape = Domain.Results.ProbeResultShape.ScalarSeries,
            Target = Target.Ip("127.0.0.1"),
            Unit = Domain.Measurements.MeasurementUnit.Milliseconds,
            Context = new Domain.Measurements.MeasurementContext
            {
                InterfaceName = "тест",
                AdapterKind = Domain.Measurements.AdapterKind.Physical,
                CalibrationBaselineMs = 0.2,
                ProductVersion = "test",
                Methodology = Domain.Measurements.Methodology.IcmpEcho,
                StartedUtc = DateTimeOffset.UtcNow,
            },
        }))
        {
            runId = writer.RunId;
            await writer.AppendAsync(Domain.Measurements.Sample.Ok(0, DateTimeOffset.UtcNow, 1.0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        // Повторное открытие проходит через ту же лестницу обновлений.
        var presetStore = CreateStore();
        await presetStore.SaveAsync(Sample());

        var run = await runStore.GetAsync(runId);

        Assert.NotNull(run);
        Assert.Single(run.Samples);
        Assert.Null(run.Summary.PresetId);
    }

    [Fact]
    public async Task RunLinkedToPreset_KeepsPresetReference()
    {
        var runStore = new SqliteRunStore(new StorageOptions
        {
            DatabasePath = _databasePath,
            ApplyRetentionOnStartup = false,
        });

        await runStore.InitializeAsync();

        var presetStore = CreateStore();
        var preset = await presetStore.SaveAsync(Sample());

        Guid runId;
        await using (var writer = await runStore.BeginRunAsync(new Application.Abstractions.RunDescriptor
        {
            Kind = Domain.Results.ProbeKind.Icmp,
            ProbeName = "ping",
            Shape = Domain.Results.ProbeResultShape.ScalarSeries,
            Target = preset.Target,
            Unit = Domain.Measurements.MeasurementUnit.Milliseconds,
            Context = new Domain.Measurements.MeasurementContext
            {
                InterfaceName = "тест",
                AdapterKind = Domain.Measurements.AdapterKind.Physical,
                CalibrationBaselineMs = 0.2,
                ProductVersion = "test",
                Methodology = Domain.Measurements.Methodology.IcmpEcho,
                StartedUtc = DateTimeOffset.UtcNow,
            },
            PresetId = preset.Id,
            PresetVersion = preset.Version,
        }))
        {
            runId = writer.RunId;
            await writer.AppendAsync(Domain.Measurements.Sample.Ok(0, DateTimeOffset.UtcNow, 1.0));
            await writer.CompleteAsync([], null, wasCancelled: false);
        }

        var run = await runStore.GetAsync(runId);

        Assert.NotNull(run);
        Assert.Equal(preset.Id, run.Summary.PresetId);
        Assert.Equal(preset.Version, run.Summary.PresetVersion);

        // Удаление пресета не должно уносить с собой историю измерений.
        await presetStore.DeleteAsync(preset.Id);

        var survived = await runStore.GetAsync(runId);

        Assert.NotNull(survived);
        Assert.Equal(preset.Id, survived.Summary.PresetId);
    }
}
