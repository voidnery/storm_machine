using System.Net;
using StormMachine.Platform.Geo;

namespace StormMachine.Platform.UnitTests;

/// <summary>
/// Проверки офлайн-справочника принадлежности адресов.
/// </summary>
/// <remarks>
/// Читатель двоичного формата — то место, где ошибка не падает, а тихо врёт: неверно
/// разобранное дерево вернёт чужую автономную систему, и отчёт обвинит не того оператора.
/// Поэтому проверяется не «не упало», а совпадение с ожидаемой сетью, включая соседние
/// адреса за границей префикса.
/// </remarks>
public sealed class AsnDatabaseTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "storm-asn-" + Guid.NewGuid().ToString("N")[..8]);

    public AsnDatabaseTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Уборка временного каталога не должна ронять прогон тестов.
        }
    }

    private static Dictionary<string, object?> Asn(uint number, string organization) => new()
    {
        ["autonomous_system_number"] = number,
        ["autonomous_system_organization"] = organization,
    };

    private string WriteAsnDatabase(Action<MaxMindDbWriter> fill, string fileName = "dbip-asn-lite-2026-08.mmdb")
    {
        var writer = new MaxMindDbWriter();
        fill(writer);

        var path = Path.Combine(_directory, fileName);
        File.WriteAllBytes(path, writer.Build());

        return path;
    }

    [Fact]
    public void MissingDirectory_IsNotAnError()
    {
        var database = AsnDatabase.Open(Path.Combine(_directory, "нет-такого"));

        Assert.False(database.IsAvailable);
        Assert.Null(database.Description);
        Assert.Null(database.Lookup(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void EmptyDirectory_IsNotAnError()
    {
        var database = AsnDatabase.Open(_directory);

        Assert.False(database.IsAvailable);
        Assert.Null(database.Lookup(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void CorruptFile_IsIgnored()
    {
        // Чужой или битый файл не должен отменять трассировку: без него остаются
        // адреса и имена, и это по-прежнему полезно.
        File.WriteAllBytes(Path.Combine(_directory, "dbip-asn-lite.mmdb"), [1, 2, 3, 4, 5]);

        var database = AsnDatabase.Open(_directory);

        Assert.False(database.IsAvailable);
    }

    [Fact]
    public void FindsNetworkByPrefix()
    {
        WriteAsnDatabase(writer =>
        {
            writer.Add("8.8.8.0", 24, Asn(15169, "Google LLC"));
            writer.Add("1.1.1.0", 24, Asn(13335, "Cloudflare, Inc."));
        });

        var database = AsnDatabase.Open(_directory);

        Assert.True(database.IsAvailable);

        var google = database.Lookup(IPAddress.Parse("8.8.8.8"));
        Assert.NotNull(google);
        Assert.Equal(15169, google.Number);
        Assert.Equal("Google LLC", google.Organization);

        var cloudflare = database.Lookup(IPAddress.Parse("1.1.1.1"));
        Assert.NotNull(cloudflare);
        Assert.Equal(13335, cloudflare.Number);
        Assert.Equal("Cloudflare, Inc.", cloudflare.Organization);
    }

    [Fact]
    public void RespectsPrefixBoundaries()
    {
        // Соседний адрес за границей /24 не должен получить чужую принадлежность:
        // именно так выглядела бы ошибка на бит в обходе дерева.
        WriteAsnDatabase(writer => writer.Add("8.8.8.0", 24, Asn(15169, "Google LLC")));

        var database = AsnDatabase.Open(_directory);

        Assert.NotNull(database.Lookup(IPAddress.Parse("8.8.8.255")));
        Assert.Null(database.Lookup(IPAddress.Parse("8.8.9.0")));
        Assert.Null(database.Lookup(IPAddress.Parse("8.8.7.255")));
    }

    [Fact]
    public void LongestPrefixWins()
    {
        WriteAsnDatabase(writer =>
        {
            writer.Add("203.0.112.0", 22, Asn(64500, "Транзит"));
            writer.Add("203.0.113.0", 24, Asn(64501, "Клиент"));
        });

        var database = AsnDatabase.Open(_directory);

        Assert.Equal(64501, database.Lookup(IPAddress.Parse("203.0.113.5"))?.Number);
        Assert.Equal(64500, database.Lookup(IPAddress.Parse("203.0.114.5"))?.Number);
    }

    [Fact]
    public void ReadsIPv6Networks()
    {
        WriteAsnDatabase(writer => writer.Add("2001:db8::", 32, Asn(64502, "Пример")));

        var database = AsnDatabase.Open(_directory);

        Assert.Equal(64502, database.Lookup(IPAddress.Parse("2001:db8::1"))?.Number);
        Assert.Null(database.Lookup(IPAddress.Parse("2001:db9::1")));
    }

    [Fact]
    public void ReadsCountryDatabaseAlongsideAsn()
    {
        WriteAsnDatabase(writer => writer.Add("8.8.8.0", 24, Asn(15169, "Google LLC")));

        var country = new MaxMindDbWriter { DatabaseType = "test-country" };
        country.Add("8.8.8.0", 24, new Dictionary<string, object?>
        {
            ["country"] = new Dictionary<string, object?>
            {
                ["iso_code"] = "US",
                ["names"] = new Dictionary<string, object?>
                {
                    ["en"] = "United States",
                    ["ru"] = "США",
                },
            },
        });

        File.WriteAllBytes(Path.Combine(_directory, "dbip-country-lite-2026-08.mmdb"), country.Build());

        var database = AsnDatabase.Open(_directory);
        var record = database.Lookup(IPAddress.Parse("8.8.8.8"));

        Assert.NotNull(record);
        Assert.Equal(15169, record.Number);

        // Русское название предпочитается английскому: отчёт читает человек.
        Assert.Equal("США", record.Country);
    }

    [Fact]
    public void DescriptionNamesLoadedDatabases()
    {
        WriteAsnDatabase(writer => writer.Add("8.8.8.0", 24, Asn(15169, "Google LLC")));

        var database = AsnDatabase.Open(_directory);

        Assert.NotNull(database.Description);
        Assert.Contains("test-asn", database.Description, StringComparison.Ordinal);

        // Дата сборки базы обязана быть видна: устаревшая база — частая причина
        // неверной принадлежности, и по описанию это должно быть заметно.
        Assert.Contains("2026", database.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void PicksNewestFileWhenSeveralPresent()
    {
        var older = new MaxMindDbWriter { DatabaseType = "old" };
        older.Add("8.8.8.0", 24, Asn(1, "Старая"));
        File.WriteAllBytes(Path.Combine(_directory, "dbip-asn-lite-2025-01.mmdb"), older.Build());

        WriteAsnDatabase(writer => writer.Add("8.8.8.0", 24, Asn(2, "Новая")), "dbip-asn-lite-2026-08.mmdb");

        var database = AsnDatabase.Open(_directory);

        Assert.Equal(2, database.Lookup(IPAddress.Parse("8.8.8.8"))?.Number);
    }

    [Fact]
    public void UnknownAddress_ReturnsNull()
    {
        WriteAsnDatabase(writer => writer.Add("8.8.8.0", 24, Asn(15169, "Google LLC")));

        var database = AsnDatabase.Open(_directory);

        Assert.Null(database.Lookup(IPAddress.Parse("192.0.2.1")));
    }
}
