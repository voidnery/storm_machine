using StormMachine.Domain.Agents;
using StormMachine.Storage;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Проверки хранилища агентов.
/// </summary>
/// <remarks>
/// Ключ — отпечаток, а не адрес, и это главное, что здесь закрепляется: агент,
/// сменивший адрес или имя машины, обязан остаться тем же агентом. Заведи хранилище
/// вторую запись — и оператор увидел бы в списке двух агентов там, где машина одна,
/// а сопряжение при этом сохранилось бы только у одного из них.
/// </remarks>
public sealed class SqliteAgentStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"storm-agents-{Guid.NewGuid():N}.db");

    private SqliteAgentStore Store() => new(new SqliteRunStore(new StorageOptions
    {
        DatabasePath = _path,
        ApplyRetentionOnStartup = false,
    }));

    private static RemoteAgent Agent(string thumbprint, string machine = "СТЕНД", string? address = "10.0.0.7") => new()
    {
        Thumbprint = thumbprint,
        MachineName = machine,
        Product = "storm-agent/0.1.0",
        Address = address,
        Port = 47820,
        Direction = AgentDirection.ClientDials,
        PairedUtc = DateTimeOffset.UnixEpoch,
        LastSeenUtc = DateTimeOffset.UnixEpoch,
        Capabilities = ["tcp-throughput", "udp-quality"],
    };

    [Fact]
    public async Task SaveAndList_KeepsEveryField()
    {
        var store = Store();
        await store.InitializeAsync();
        await store.SaveAsync(Agent("AAAA1111"));

        var agent = Assert.Single(await store.ListAsync());

        Assert.Equal("AAAA1111", agent.Thumbprint);
        Assert.Equal("СТЕНД", agent.MachineName);
        Assert.Equal("10.0.0.7", agent.Address);
        Assert.Equal(47820, agent.Port);
        Assert.Equal(AgentDirection.ClientDials, agent.Direction);
        Assert.Equal(["tcp-throughput", "udp-quality"], agent.Capabilities);
    }

    [Fact]
    public async Task ChangedAddress_IsStillTheSameAgent()
    {
        var store = Store();
        await store.InitializeAsync();

        await store.SaveAsync(Agent("AAAA1111", address: "10.0.0.7"));
        await store.SaveAsync(Agent("AAAA1111", machine: "ПЕРЕИМЕНОВАН", address: "10.0.4.19"));

        var agent = Assert.Single(await store.ListAsync());

        Assert.Equal("10.0.4.19", agent.Address);
        Assert.Equal("ПЕРЕИМЕНОВАН", agent.MachineName);
    }

    [Fact]
    public async Task Alias_SurvivesReconnection()
    {
        // Имя дал оператор. Переподключение агента не должно его затирать: агент
        // о своём псевдониме не знает и прислать его не может.
        var store = Store();
        await store.InitializeAsync();

        await store.SaveAsync(Agent("AAAA1111") with { Alias = "Тверь, касса" });
        await store.SaveAsync(Agent("AAAA1111"));

        var agent = Assert.Single(await store.ListAsync());

        Assert.Equal("Тверь, касса", agent.Alias);
        Assert.Equal("Тверь, касса", agent.DisplayName);
    }

    [Fact]
    public async Task Find_AcceptsThumbprintPrefixNameAndAlias()
    {
        var store = Store();
        await store.InitializeAsync();
        await store.SaveAsync(Agent("AAAA1111BBBB2222") with { Alias = "касса" });

        Assert.NotNull(await store.FindAsync("AAAA"));
        Assert.NotNull(await store.FindAsync("aaaa1111"));
        Assert.NotNull(await store.FindAsync("СТЕНД"));
        Assert.NotNull(await store.FindAsync("касса"));
        Assert.Null(await store.FindAsync("ZZZZ"));
    }

    [Fact]
    public async Task Find_RefusesToGuessBetweenTwo()
    {
        // Выбрать за человека, к какому из двух агентов он обращался, нельзя:
        // измерение ушло бы не туда, и он бы этого не заметил.
        var store = Store();
        await store.InitializeAsync();
        await store.SaveAsync(Agent("AAAA1111", "ПЕРВЫЙ"));
        await store.SaveAsync(Agent("AAAA2222", "ВТОРОЙ"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync("AAAA"));

        Assert.Contains("Уточни отпечаток", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forget_RemovesOnlyThatAgent()
    {
        var store = Store();
        await store.InitializeAsync();
        await store.SaveAsync(Agent("AAAA1111", "ПЕРВЫЙ"));
        await store.SaveAsync(Agent("BBBB2222", "ВТОРОЙ"));

        Assert.True(await store.ForgetAsync("AAAA1111"));
        Assert.False(await store.ForgetAsync("AAAA1111"));

        var left = Assert.Single(await store.ListAsync());
        Assert.Equal("BBBB2222", left.Thumbprint);
    }

    [Fact]
    public async Task Identity_IsStoredNextToPairings()
    {
        // Личность и сопряжения — части одного целого: потеряв личность, клиент
        // становится незнакомцем для всех агентов, и список сопряжений без неё
        // указывает на связи, которых уже нет.
        var store = Store();
        await store.InitializeAsync();

        Assert.Null(await store.LoadIdentityAsync());

        await store.SaveIdentityAsync([1, 2, 3, 4]);

        Assert.Equal([1, 2, 3, 4], await store.LoadIdentityAsync());
    }

    [Fact]
    public async Task Identity_IsReplacedNotDuplicated()
    {
        var store = Store();
        await store.InitializeAsync();

        await store.SaveIdentityAsync([1, 2, 3]);
        await store.SaveIdentityAsync([9, 9, 9]);

        Assert.Equal([9, 9, 9], await store.LoadIdentityAsync());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
