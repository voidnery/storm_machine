using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Snmp;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Хранилище учётных данных SNMP.
/// </summary>
/// <remarks>
/// Главное, что здесь закрепляется: <b>список не показывает пароли</b>. Пароль,
/// видный в выводе команды, не становится безопаснее оттого, что в базе он зашифрован,
/// а список учётных данных смотрят чаще всего не затем, чтобы узнать пароль.
/// Настоящие значения выдаются только тому, кто собирается ими воспользоваться.
/// </remarks>
public sealed class SqliteSnmpCredentialStoreTests : IDisposable
{
    /// <summary>Обратимая подстановка вместо шифрования: предмет проверки не она.</summary>
    private sealed class ReversibleProtector : ISecretProtector
    {
        public string Protect(string plain) => "!" + new string([.. plain.Reverse()]);

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith('!') ? new string([.. protectedValue[1..].Reverse()]) : null;
    }

    private readonly string _directory;
    private readonly string _databasePath;
    private readonly ReversibleProtector _protector = new();

    public SqliteSnmpCredentialStoreTests()
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

    private SqliteSnmpCredentialStore CreateStore() =>
        new(new SqliteRunStore(new StorageOptions { DatabasePath = _databasePath }), _protector);

    private static SnmpCredential V2c(string name, string community = "s3cret", int order = 0) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Version = SnmpVersion.V2c,
        Community = community,
        Order = order,
    };

    private static SnmpCredential V3(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Version = SnmpVersion.V3,
        UserName = "storm",
        AuthProtocol = SnmpAuthProtocol.Sha256,
        AuthPassword = "пароль-проверки",
        PrivacyProtocol = SnmpPrivacyProtocol.Aes128,
        PrivacyPassword = "пароль-шифрования",
    };

    [Fact(DisplayName = "Список не показывает ни строку сообщества, ни пароли")]
    public async Task ListHidesSecrets()
    {
        var store = CreateStore();

        await store.SaveAsync(V2c("свитчи"));
        await store.SaveAsync(V3("ядро"));

        var all = await store.ListAsync();

        Assert.Equal(2, all.Count);
        Assert.All(all, c => Assert.NotEqual("s3cret", c.Community));
        Assert.All(all, c => Assert.NotEqual("пароль-проверки", c.AuthPassword));

        // Пометка, а не пустота: пустое поле читается как «пароля нет»,
        // и человек начинает искать, куда он делся.
        var hidden = all.Single(c => c.Name == "свитчи");

        Assert.Equal(SqliteSnmpCredentialStore.Hidden, hidden.Community);
    }

    [Fact(DisplayName = "Взятый для работы набор несёт настоящие пароли")]
    public async Task GetRevealsSecrets()
    {
        var store = CreateStore();
        var credential = V3("ядро");

        await store.SaveAsync(credential);

        var loaded = await store.GetAsync(credential.Id);

        Assert.NotNull(loaded);
        Assert.Equal("пароль-проверки", loaded!.AuthPassword);
        Assert.Equal("пароль-шифрования", loaded.PrivacyPassword);
        Assert.Equal(SnmpAuthProtocol.Sha256, loaded.AuthProtocol);
        Assert.Equal("storm", loaded.UserName);
    }

    [Fact(DisplayName = "Набор переживает закрытие продукта")]
    public async Task SurvivesRestart()
    {
        var credential = V2c("свитчи") with { Port = 1610, Retries = 3, Timeout = TimeSpan.FromSeconds(7) };

        await CreateStore().SaveAsync(credential);

        SqliteConnection.ClearAllPools();

        var loaded = await CreateStore().GetAsync(credential.Id);

        Assert.NotNull(loaded);
        Assert.Equal("s3cret", loaded!.Community);
        Assert.Equal(1610, loaded.Port);
        Assert.Equal(3, loaded.Retries);
        Assert.Equal(TimeSpan.FromSeconds(7), loaded.Timeout);
    }

    [Fact(DisplayName = "Строка сообщества в базе лежит зашифрованной")]
    public async Task CommunityIsEncryptedAtRest()
    {
        // Формально это не пароль — по сети она идёт открытым текстом. Но в базе
        // она стоит ровно столько же: знающий её опрашивает оборудование
        // от имени владельца.
        await CreateStore().SaveAsync(V2c("свитчи"));

        await using var connection = new SqliteConnection($"Data Source={_databasePath}");
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT community FROM snmp_credentials;";

        var stored = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(stored);
        Assert.DoesNotContain("s3cret", stored, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Наборы перечисляются в порядке перебора")]
    public async Task ListRespectsOrder()
    {
        var store = CreateStore();

        await store.SaveAsync(V2c("доступ", order: 5));
        await store.SaveAsync(V2c("ядро", order: 1));

        var all = await store.ListAsync();

        Assert.Equal("ядро", all[0].Name);
        Assert.Equal("доступ", all[1].Name);
    }

    [Fact(DisplayName = "Неоднозначное сокращение — ошибка, а не догадка")]
    public async Task AmbiguousPrefixThrows()
    {
        var store = CreateStore();

        await store.SaveAsync(V2c("свитчи доступа"));
        await store.SaveAsync(V2c("свитчи ядра"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => store.FindAsync("свитчи"));

        Assert.Contains("Уточни имя", error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Найденный по имени набор отдаётся с паролями")]
    public async Task FindRevealsSecrets()
    {
        var store = CreateStore();

        await store.SaveAsync(V2c("свитчи"));

        var found = await store.FindAsync("свит");

        Assert.NotNull(found);
        Assert.Equal("s3cret", found!.Community);
    }

    [Fact(DisplayName = "Удаление набора не задевает соседей")]
    public async Task DeleteRemovesOnlyOne()
    {
        var store = CreateStore();
        var first = V2c("свитчи");

        await store.SaveAsync(first);
        await store.SaveAsync(V2c("ядро"));

        Assert.True(await store.DeleteAsync(first.Id));
        Assert.False(await store.DeleteAsync(first.Id));

        var all = await store.ListAsync();

        Assert.Single(all);
        Assert.Equal("ядро", all[0].Name);
    }
}
