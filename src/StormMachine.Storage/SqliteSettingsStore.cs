using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;

namespace StormMachine.Storage;

/// <summary>Настройки в той же базе, что всё остальное.</summary>
/// <remarks>
/// Секреты шифруются <see cref="ISecretProtector"/> при записи и расшифровываются
/// при чтении. В списке они не показываются никогда — ни целиком, ни частями:
/// «пароль начинается на qwe» помогает подобравшему больше, чем владельцу.
/// </remarks>
public sealed class SqliteSettingsStore(SqliteRunStore runStore, ISecretProtector protector) : ISettingsStore
{
    /// <summary>Что видно вместо секрета.</summary>
    public const string SecretMask = "задан";

    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    private readonly ISecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT value, secret FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0))
        {
            return null;
        }

        var value = reader.GetString(0);

        // Не расшифровалось — значит база пришла с другой машины или из-под другой
        // учётной записи. Это не повреждение: секрет просто надо задать заново.
        return reader.GetInt32(1) != 0 ? _protector.Unprotect(value) : value;
    }

    public async Task SetAsync(
        string key,
        string? value,
        bool secret = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO settings (key, value, secret, updated_ticks)
            VALUES ($key, $value, $secret, $updated)
            ON CONFLICT(key) DO UPDATE SET value = $value, secret = $secret, updated_ticks = $updated;
            """;

        var stored = secret && value is not null ? _protector.Protect(value) : value;

        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", (object?)stored ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret", secret ? 1 : 0);
        command.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM settings WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<SettingEntry>> ListAsync(
        string? prefix = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        if (string.IsNullOrWhiteSpace(prefix))
        {
            command.CommandText = "SELECT key, value, secret FROM settings ORDER BY key;";
        }
        else
        {
            command.CommandText = "SELECT key, value, secret FROM settings WHERE key LIKE $prefix ORDER BY key;";
            command.Parameters.AddWithValue("$prefix", prefix + "%");
        }

        var entries = new List<SettingEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var isSecret = reader.GetInt32(2) != 0;

            entries.Add(new SettingEntry(
                reader.GetString(0),
                isSecret ? SecretMask : reader.IsDBNull(1) ? null : reader.GetString(1),
                isSecret));
        }

        return entries;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _runStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(_runStore.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
