using StormMachine.Application.Abstractions;

namespace StormMachine.Storage.UnitTests;

/// <summary>
/// Настройки и обращение с секретами.
/// </summary>
/// <remarks>
/// Проверяется не шифрование — оно дело платформы, — а то, что хранилище им
/// действительно пользуется и не показывает секрет в списке. Пароль, видный
/// в выводе команды, не становится безопаснее от того, что в базе он зашифрован.
/// </remarks>
public sealed class SqliteSettingsStoreTests : IDisposable
{
    /// <summary>Обратимая подстановка вместо шифрования: предмет проверки не она.</summary>
    private sealed class ReversibleProtector : ISecretProtector
    {
        public int Calls { get; private set; }

        public string Protect(string plain)
        {
            Calls++;

            return "!" + new string([.. plain.Reverse()]);
        }

        public string? Unprotect(string protectedValue) =>
            protectedValue.StartsWith('!') ? new string([.. protectedValue[1..].Reverse()]) : null;
    }

    private readonly string _directory;
    private readonly string _databasePath;
    private readonly ReversibleProtector _protector = new();

    public SqliteSettingsStoreTests()
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
            // Файл мог остаться заблокированным — временный каталог уберёт система.
        }
    }

    private SqliteSettingsStore CreateStore() =>
        new(new SqliteRunStore(new StorageOptions { DatabasePath = _databasePath }), _protector);

    [Fact(DisplayName = "Обычная настройка возвращается как записана")]
    public async Task PlainRoundTrip()
    {
        var store = CreateStore();

        await store.SetAsync(AlertSettings.WebhookUrl, "https://example.test/hook");

        Assert.Equal("https://example.test/hook", await store.GetAsync(AlertSettings.WebhookUrl));
        Assert.Equal(0, _protector.Calls);
    }

    [Fact(DisplayName = "Секрет шифруется при записи и расшифровывается при чтении")]
    public async Task SecretRoundTrip()
    {
        var store = CreateStore();

        await store.SetAsync(AlertSettings.SmtpPassword, "пароль", secret: true);

        Assert.Equal(1, _protector.Calls);
        Assert.Equal("пароль", await store.GetAsync(AlertSettings.SmtpPassword));
    }

    [Fact(DisplayName = "В списке секрет не показывается ни целиком, ни частями")]
    public async Task SecretIsNeverListed()
    {
        var store = CreateStore();

        await store.SetAsync(AlertSettings.SmtpPassword, "пароль", secret: true);
        await store.SetAsync(AlertSettings.SmtpHost, "smtp.example.test");

        var entries = await store.ListAsync("alerts.");
        var password = entries.Single(e => e.Key == AlertSettings.SmtpPassword);

        // «Пароль начинается на qwe» помогает подобравшему больше, чем владельцу.
        Assert.True(password.IsSecret);
        Assert.Equal(SqliteSettingsStore.SecretMask, password.Value);
        Assert.DoesNotContain("пароль", password.Value, StringComparison.Ordinal);

        Assert.Equal("smtp.example.test", entries.Single(e => e.Key == AlertSettings.SmtpHost).Value);
    }

    [Fact(DisplayName = "Нерасшифровываемый секрет возвращается как отсутствующий")]
    public async Task ForeignSecretReadsAsMissing()
    {
        var store = CreateStore();

        await store.SetAsync(AlertSettings.SmtpPassword, "пароль", secret: true);

        // База пришла с другой машины: значение зашифровано не нами. Это не поломка,
        // а обычный случай — секрет просто надо задать заново.
        var foreign = new SqliteSettingsStore(
            new SqliteRunStore(new StorageOptions { DatabasePath = _databasePath }),
            new AlienProtector());

        Assert.Null(await foreign.GetAsync(AlertSettings.SmtpPassword));
    }

    private sealed class AlienProtector : ISecretProtector
    {
        public string Protect(string plain) => plain;

        public string? Unprotect(string protectedValue) => null;
    }

    [Fact(DisplayName = "Удаление убирает настройку")]
    public async Task Remove()
    {
        var store = CreateStore();

        await store.SetAsync(AlertSettings.WebhookUrl, "https://example.test/hook");

        Assert.True(await store.RemoveAsync(AlertSettings.WebhookUrl));
        Assert.Null(await store.GetAsync(AlertSettings.WebhookUrl));
        Assert.False(await store.RemoveAsync(AlertSettings.WebhookUrl));
    }

    [Fact(DisplayName = "Повторная запись заменяет значение")]
    public async Task Overwrite()
    {
        var store = CreateStore();

        await store.SetAsync(AlertSettings.SmtpPort, "587");
        await store.SetAsync(AlertSettings.SmtpPort, "465");

        Assert.Equal("465", await store.GetAsync(AlertSettings.SmtpPort));
    }
}
