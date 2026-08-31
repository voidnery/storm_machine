using System.Text;
using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Проверка целостности и лечение файла базы.
/// </summary>
/// <remarks>
/// Порча здесь настоящая, а не имитированная исключением: в файле затирается страница,
/// на которой лежат сэмплы одного из прогонов, — ровно так выглядело первое боевое
/// повреждение (И-24). Проверять лечение на исключении-подделке значило бы проверять
/// обработчик, а не лечение.
/// </remarks>
public sealed class SqliteMaintenanceTests : IDisposable
{
    private const int PageSize = 4096;

    private readonly string _directory;
    private readonly string _databasePath;

    public SqliteMaintenanceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "storm-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "storm.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Файл мог остаться заблокированным — временный каталог уберёт система.
        }
    }

    [Fact]
    public async Task HealthyDatabase_ChecksClean()
    {
        await WriteRunAsync("ping", samples: 20);

        var health = await CreateMaintenance().CheckAsync();

        Assert.True(health.IsHealthy);
        Assert.Empty(health.Findings);
    }

    [Fact]
    public async Task Repair_OnHealthyDatabase_IsLossless()
    {
        await WriteRunAsync("ping", samples: 25);
        await WriteRunAsync("tcp", samples: 40);
        SqliteConnection.ClearAllPools();

        var report = await CreateMaintenance().RepairAsync();

        Assert.Equal(2, report.RunsKept);
        Assert.Equal(65, report.SamplesKept);
        Assert.Equal(0, report.RunsWithoutSamples);
        Assert.Equal(0, report.RunsLost);
        Assert.Empty(report.PartialTables);

        // Оригинал не удалён, а убран целиком: худший исход лечения —
        // тот же файл в другой папке.
        Assert.True(File.Exists(Path.Combine(report.BackupPath, "storm.db")));

        var health = await CreateMaintenance().CheckAsync();
        Assert.True(health.IsHealthy);
    }

    [Fact]
    public async Task CorruptedSamplesPage_IsFound_AndRepairSalvagesTheRest()
    {
        var damaged = await WriteRunAsync("ping", samples: 300, label: i => $"ПОРЧА-{i:D4}-наполнение-страницы-текстом");
        var intact = await WriteRunAsync("tcp", samples: 30, label: i => $"ЦЕЛОЕ-{i:D4}");
        SqliteConnection.ClearAllPools();

        CorruptPageContaining("ПОРЧА-0150");

        var health = await CreateMaintenance().CheckAsync();
        Assert.False(health.IsHealthy);
        Assert.NotEmpty(health.Findings);

        var report = await CreateMaintenance().RepairAsync();

        // Оба прогона на месте: повреждение точечное, и терять из-за него
        // журнал целиком продукт не имеет права.
        Assert.Equal(2, report.RunsKept);
        Assert.Equal(1, report.RunsWithoutSamples);

        var again = await CreateMaintenance().CheckAsync();
        Assert.True(again.IsHealthy, string.Join("; ", again.Findings));

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM samples WHERE run_id = $run", damaged));
        Assert.Equal(30L, Scalar(connection, "SELECT COUNT(*) FROM samples WHERE run_id = $run", intact));

        // Прогон без сырья честно говорит об этом флагом — продукт покажет его
        // как прогон со свёрнутыми сэмплами, а не как пустой график.
        Assert.Equal(0L, Scalar(connection, "SELECT has_raw_samples FROM runs WHERE id = $run", damaged));
        Assert.Equal(1L, Scalar(connection, "SELECT has_raw_samples FROM runs WHERE id = $run", intact));
    }

    private static long Scalar(SqliteConnection connection, string sql, Guid runId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$run", runId.ToString());
        return (long)command.ExecuteScalar()!;
    }

    /// <summary>Затирает страницу файла, на которой лежит байтовая последовательность метки.</summary>
    private void CorruptPageContaining(string marker)
    {
        var bytes = File.ReadAllBytes(_databasePath);
        var needle = Encoding.UTF8.GetBytes(marker);

        var offset = Find(bytes, needle);
        Assert.True(offset > 0, $"Метка «{marker}» не нашлась в файле базы — стенд собран неверно.");

        var page = offset - (offset % PageSize);
        using var stream = new FileStream(_databasePath, FileMode.Open, FileAccess.Write);
        stream.Position = page;
        stream.Write(Enumerable.Repeat((byte)0xFF, PageSize).ToArray());
    }

    private static int Find(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length && match; j++)
            {
                match = haystack[i + j] == needle[j];
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task<Guid> WriteRunAsync(string probeName, int samples, Func<int, string?>? label = null)
    {
        var store = new SqliteRunStore(new StorageOptions
        {
            DatabasePath = _databasePath,
            ApplyRetentionOnStartup = false,
        });

        await store.InitializeAsync();

        await using var writer = await store.BeginRunAsync(new RunDescriptor
        {
            Kind = ProbeKind.Icmp,
            ProbeName = probeName,
            Shape = ProbeResultShape.ScalarSeries,
            Target = Target.Ip("192.168.1.1"),
            Unit = MeasurementUnit.Milliseconds,
            Context = new MeasurementContext
            {
                InterfaceName = "тестовый",
                AdapterKind = AdapterKind.Physical,
                CalibrationBaselineMs = 0.25,
                ProductVersion = "0.0.0-test",
                Methodology = Methodology.IcmpEcho,
                StartedUtc = DateTimeOffset.UtcNow,
            },
        });

        for (var i = 0; i < samples; i++)
        {
            await writer.AppendAsync(new Sample
            {
                Sequence = i,
                TimestampUtc = DateTimeOffset.UtcNow,
                Value = 1.0 + i,
                Status = SampleStatus.Success,
                Label = label?.Invoke(i),
            });
        }

        await writer.CompleteAsync([], resolvedAddress: null, wasCancelled: false);

        return writer.RunId;
    }

    private SqliteMaintenance CreateMaintenance() => new(new StorageLocationStub(_databasePath));

    private sealed class StorageLocationStub(string path) : IStorageLocation
    {
        public string DatabasePath => path;
    }
}
