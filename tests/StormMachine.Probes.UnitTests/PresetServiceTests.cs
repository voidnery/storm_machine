using StormMachine.Application.Abstractions;
using StormMachine.Application.Presets;
using StormMachine.Application.Probes;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Presets;
using StormMachine.Domain.Targets;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки библиотеки пресетов поверх настоящих проб.
/// </summary>
/// <remarks>
/// Проверка пресета опирается на объявление пробы — тот же источник, из которого строятся
/// формы в интерфейсе и ключи командной строки. Здесь это проверяется на живых объявлениях,
/// а не на выдуманных: подделка объявления скрыла бы именно ту ошибку, которую ловим.
/// </remarks>
public sealed class PresetServiceTests
{
    private static PresetService CreateService(out InMemoryPresetStore store)
    {
        var clock = new FakeClock();
        var environment = new FakeEnvironment();
        var resolver = new TargetResolver(environment);

        IProbe[] probes =
        [
            new IcmpProbe(clock, resolver),
            new HttpProbe(clock),
            new DnsProbe(clock, environment),
        ];

        store = new InMemoryPresetStore();
        return new PresetService(store, new FakeRegistry(probes));
    }

    private static Preset Sample(
        string name = "Шлюз",
        string probe = "ping",
        Dictionary<string, string?>? parameters = null) => new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            ProbeName = probe,
            Target = Target.Gateway("шлюз"),
            Parameters = parameters ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = "4",
            },
            Version = 1,
            CreatedUtc = DateTimeOffset.UtcNow,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

    [Fact]
    public void Validate_AcceptsCorrectPreset()
    {
        var service = CreateService(out _);

        Assert.Empty(service.Validate(Sample()));
    }

    [Fact]
    public void Validate_RejectsUnknownProbe()
    {
        var service = CreateService(out _);

        var errors = service.Validate(Sample(probe: "телепатия"));

        Assert.Contains(errors, e => e.Field == nameof(Preset.ProbeName));
    }

    [Fact]
    public void Validate_RejectsUnknownParameter()
    {
        // Неизвестный параметр молча ничего не сделает, и оператор будет думать,
        // что измеряет одно, а измерять другое.
        var service = CreateService(out _);

        var errors = service.Validate(Sample(parameters: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["каунт"] = "4",
        }));

        Assert.Contains(errors, e => e.Field == "каунт");
    }

    [Fact]
    public void Validate_RejectsValueOutOfRange()
    {
        var service = CreateService(out _);

        var errors = service.Validate(Sample(parameters: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["count"] = "0",
        }));

        Assert.Contains(errors, e => e.Field == "count");
    }

    [Fact]
    public void Validate_RejectsEmptyName()
    {
        var service = CreateService(out _);

        var errors = service.Validate(Sample(name: "   "));

        Assert.Contains(errors, e => e.Field == nameof(Preset.Name));
    }

    [Fact]
    public async Task Save_RefusesInvalidPreset()
    {
        var service = CreateService(out _);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveAsync(Sample(probe: "телепатия")));
    }

    [Fact]
    public void FromRequest_TakesParametersFromActualMeasurement()
    {
        // Пресет рождается из измерения, которое только что оказалось полезным,
        // а не из формы, заполненной заранее.
        var request = new ProbeRequest
        {
            Target = Target.Ip("192.168.1.1"),
            Parameters = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = 20,
                ["interval"] = 250,
                ["df"] = true,
            },
        };

        var preset = PresetService.FromRequest("Тест", "ping", request);

        Assert.Equal("20", preset.Parameters["count"]);
        Assert.Equal("250", preset.Parameters["interval"]);
        Assert.Equal("true", preset.Parameters["df"]);
        Assert.Equal(1, preset.Version);
    }

    [Fact]
    public void IsSameMeasurement_IgnoresNameAndDescription()
    {
        var a = Sample("Первое");
        var b = a with { Name = "Второе", Description = "другое", Tags = ["новый тег"] };

        Assert.True(a.IsSameMeasurement(b));
    }

    [Fact]
    public void IsSameMeasurement_NoticesParameterChange()
    {
        var a = Sample();
        var b = a with
        {
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["count"] = "20" },
        };

        Assert.False(a.IsSameMeasurement(b));
    }

    [Fact]
    public async Task ExportImport_RoundTripsPreset()
    {
        var service = CreateService(out _);
        var original = await service.SaveAsync(Sample("Шлюз — быстрая проверка"));

        var json = PresetBundleJson.Write(PresetService.ToBundle([original]));

        // Другая машина — другая библиотека.
        var target = CreateService(out var targetStore);
        var report = await target.ImportAsync(PresetBundleJson.Read(json));

        Assert.Equal(1, report.Added);

        var imported = await targetStore.FindByNameAsync("Шлюз — быстрая проверка");

        Assert.NotNull(imported);
        Assert.Equal(original.ProbeName, imported.ProbeName);
        Assert.Equal(original.Target.Kind, imported.Target.Kind);
        Assert.Equal(original.Parameters["count"], imported.Parameters["count"]);

        // Идентификатор намеренно новый: переносится замысел теста, а не его история.
        Assert.NotEqual(original.Id, imported.Id);
    }

    [Fact]
    public void Export_KeepsCyrillicReadable()
    {
        // Файл задуман человекочитаемым: его открывают, правят и передают коллеге.
        var preset = Sample("Шлюз — быстрая проверка");

        var json = PresetBundleJson.Write(PresetService.ToBundle([preset]));

        Assert.Contains("Шлюз — быстрая проверка", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\\u0428", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Import_UpdatesExistingByName()
    {
        // Библиотека из десяти «Шлюз (1)…(10)» бесполезна.
        var service = CreateService(out var store);
        await service.SaveAsync(Sample("Шлюз"));

        var incoming = PresetService.ToBundle([
            Sample("Шлюз", parameters: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["count"] = "50" }),
        ]);

        var report = await service.ImportAsync(incoming);

        Assert.Equal(1, report.Updated);
        Assert.Equal(0, report.Added);
        Assert.Single(await store.ListAsync(new PresetQuery()));

        var stored = await store.FindByNameAsync("Шлюз");
        Assert.Equal("50", stored!.Parameters["count"]);
    }

    [Fact]
    public async Task Import_KeepsExistingWhenAsked()
    {
        var service = CreateService(out var store);
        await service.SaveAsync(Sample("Шлюз"));

        var incoming = PresetService.ToBundle([
            Sample("Шлюз", parameters: new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["count"] = "50" }),
        ]);

        var report = await service.ImportAsync(incoming, overwrite: false);

        Assert.Equal(1, report.Skipped);

        var stored = await store.FindByNameAsync("Шлюз");
        Assert.Equal("4", stored!.Parameters["count"]);
    }

    [Fact]
    public async Task Import_SkipsBrokenEntryButKeepsRest()
    {
        // Один негодный пресет не должен ронять импорт целиком.
        var service = CreateService(out var store);

        var bundle = PresetService.ToBundle([
            Sample("Хороший"),
            Sample("Плохой", probe: "телепатия"),
        ]);

        var report = await service.ImportAsync(bundle);

        Assert.Equal(1, report.Added);
        Assert.Equal(1, report.Skipped);
        Assert.Single(report.Problems);
        Assert.Contains("Плохой", report.Problems[0], StringComparison.Ordinal);

        Assert.NotNull(await store.FindByNameAsync("Хороший"));
    }

    [Fact]
    public async Task Import_RefusesNewerFormat()
    {
        var service = CreateService(out _);

        var bundle = new PresetBundle
        {
            FormatVersion = PresetBundle.CurrentFormatVersion + 1,
            Presets = [],
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(bundle));

        Assert.Contains("более новой версией", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ подделки

    private sealed class InMemoryPresetStore : IPresetStore
    {
        private readonly Dictionary<Guid, Preset> _items = [];

        public Task<IReadOnlyList<Preset>> ListAsync(PresetQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Preset>>([.. _items.Values]);

        public Task<Preset?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.TryGetValue(id, out var preset) ? preset : null);

        public Task<Preset?> FindByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Values.FirstOrDefault(
                p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)));

        public Task<Preset> SaveAsync(Preset preset, CancellationToken cancellationToken = default)
        {
            var version = _items.TryGetValue(preset.Id, out var existing)
                ? existing.IsSameMeasurement(preset) ? existing.Version : existing.Version + 1
                : 1;

            var stored = preset with { Version = version, UpdatedUtc = DateTimeOffset.UtcNow };
            _items[stored.Id] = stored;

            return Task.FromResult(stored);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_items.Remove(id));

        public Task RecordRunAsync(Guid id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<string>> GetTagsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>([.. _items.Values.SelectMany(p => p.Tags).Distinct()]);
    }

    private sealed class FakeRegistry(IReadOnlyList<IProbe> probes) : IProbeRegistry
    {
        public IReadOnlyList<ProbeDescriptor> Descriptors { get; } = [.. probes.Select(p => p.Descriptor)];

        public bool TryGet(string name, out IProbe probe)
        {
            probe = probes.FirstOrDefault(p =>
                string.Equals(p.Descriptor.Name, name, StringComparison.OrdinalIgnoreCase))!;

            return probe is not null;
        }
    }

    private sealed class FakeClock : IHighResolutionClock
    {
        public double ResolutionNanoseconds => 100;

        public double CalibrationBaselineMs => 0;

        public long GetTimestamp() => 0;

        public double ElapsedMilliseconds(long startTimestamp) => 0;

        public double ElapsedMilliseconds(long startTimestamp, long endTimestamp) => 0;

        public Task CalibrateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeEnvironment : INetworkEnvironment
    {
        public bool IsElevated => false;

        public IReadOnlyList<NetworkAdapter> GetAdapters() => [];

        public NetworkAdapter? GetPrimaryAdapter() => null;
    }
}
