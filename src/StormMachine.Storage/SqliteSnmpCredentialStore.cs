using Microsoft.Data.Sqlite;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Snmp;

namespace StormMachine.Storage;

/// <summary>
/// Наборы учётных данных SNMP в той же базе.
/// </summary>
/// <remarks>
/// Пароли шифруются средствами машины при записи. Перечисление отдаёт их пометкой,
/// а не значением: список учётных данных смотрят чаще всего не затем, чтобы узнать
/// пароль, и показывать его при каждом взгляде — лишний повод его увидеть тому,
/// кто стоит рядом.
/// <para>
/// Настоящие значения выдаёт только <see cref="GetAsync"/> — тому, кто собирается
/// ими воспользоваться. Разница между «показать список» и «взять для работы»
/// проведена в самом хранилище, а не оставлена на усмотрение вызывающего.
/// </para>
/// </remarks>
public sealed class SqliteSnmpCredentialStore(SqliteRunStore runStore, ISecretProtector protector)
    : ISnmpCredentialStore
{
    /// <summary>Чем подменяется пароль в списке.</summary>
    public const string Hidden = "· задан ·";

    private const string Columns =
        "id, name, version, community, user_name, auth_protocol, auth_password, "
        + "privacy_protocol, privacy_password, port, timeout_ms, retries, sort_order, "
        + "created_ticks, updated_ticks";

    private readonly SqliteRunStore _runStore = runStore ?? throw new ArgumentNullException(nameof(runStore));
    private readonly ISecretProtector _protector = protector ?? throw new ArgumentNullException(nameof(protector));

    public async Task<IReadOnlyList<SnmpCredential>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM snmp_credentials ORDER BY sort_order, name;";

        var found = new List<SnmpCredential>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            found.Add(Read(reader, reveal: false));
        }

        return found;
    }

    public async Task<SnmpCredential?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM snmp_credentials WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader, reveal: true) : null;
    }

    /// <summary>Ищет по имени, его началу или началу идентификатора.</summary>
    /// <remarks>
    /// Точное имя выигрывает у совпадения по началу; неоднозначное сокращение —
    /// ошибка, а не догадка. То же правило, что у профилей, мониторов и эталонов.
    /// </remarks>
    public async Task<SnmpCredential?> FindAsync(string nameOrId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrId);

        var needle = nameOrId.Trim();
        var all = await ListAsync(cancellationToken).ConfigureAwait(false);

        var exact = all.FirstOrDefault(c => string.Equals(c.Name, needle, StringComparison.OrdinalIgnoreCase));

        if (exact is not null)
        {
            return await GetAsync(exact.Id, cancellationToken).ConfigureAwait(false);
        }

        var matches = all
            .Where(c => c.Name.StartsWith(needle, StringComparison.OrdinalIgnoreCase)
                        || c.Id.ToString().StartsWith(needle, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            0 => null,
            1 => await GetAsync(matches[0].Id, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException(
                $"«{nameOrId}» подходит сразу нескольким наборам: "
                + string.Join(", ", matches.Select(m => m.Name))
                + ". Уточни имя."),
        };
    }

    public async Task SaveAsync(SnmpCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO snmp_credentials (id, name, version, community, user_name, auth_protocol,
                                          auth_password, privacy_protocol, privacy_password, port,
                                          timeout_ms, retries, sort_order, created_ticks, updated_ticks)
            VALUES ($id, $name, $version, $community, $user, $auth, $authPassword, $privacy,
                    $privacyPassword, $port, $timeout, $retries, $order, $created, $updated)
            ON CONFLICT(id) DO UPDATE SET
                name             = $name,
                version          = $version,
                community        = $community,
                user_name        = $user,
                auth_protocol    = $auth,
                auth_password    = $authPassword,
                privacy_protocol = $privacy,
                privacy_password = $privacyPassword,
                port             = $port,
                timeout_ms       = $timeout,
                retries          = $retries,
                sort_order       = $order,
                updated_ticks    = $updated;
            """;

        command.Parameters.AddWithValue("$id", credential.Id.ToString());
        command.Parameters.AddWithValue("$name", credential.Name);
        command.Parameters.AddWithValue("$version", (int)credential.Version);
        command.Parameters.AddWithValue("$community", Secret(credential.Community));
        command.Parameters.AddWithValue("$user", (object?)credential.UserName ?? DBNull.Value);
        command.Parameters.AddWithValue("$auth", (int)credential.AuthProtocol);
        command.Parameters.AddWithValue("$authPassword", Secret(credential.AuthPassword));
        command.Parameters.AddWithValue("$privacy", (int)credential.PrivacyProtocol);
        command.Parameters.AddWithValue("$privacyPassword", Secret(credential.PrivacyPassword));
        command.Parameters.AddWithValue("$port", credential.Port);
        command.Parameters.AddWithValue("$timeout", (long)credential.Timeout.TotalMilliseconds);
        command.Parameters.AddWithValue("$retries", credential.Retries);
        command.Parameters.AddWithValue("$order", credential.Order);
        command.Parameters.AddWithValue("$created", credential.CreatedUtc.UtcTicks);
        command.Parameters.AddWithValue("$updated", credential.UpdatedUtc.UtcTicks);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM snmp_credentials WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    /// <summary>
    /// Строка сообщества шифруется наравне с паролями.
    /// </summary>
    /// <remarks>
    /// Формально это не пароль: по сети она идёт открытым текстом, и защитой
    /// не является. Но в базе она стоит ровно столько же — знающий её опрашивает
    /// оборудование от имени владельца, — и класть её открытой значило бы делать
    /// различие, которого на практике нет.
    /// </remarks>
    private object Secret(string? value) => string.IsNullOrEmpty(value)
        ? DBNull.Value
        : _protector.Protect(value);

    private SnmpCredential Read(SqliteDataReader reader, bool reveal) => new()
    {
        Id = Guid.Parse(reader.GetString(0)),
        Name = reader.GetString(1),
        Version = (SnmpVersion)reader.GetInt32(2),
        Community = Reveal(reader, 3, reveal),
        UserName = reader.IsDBNull(4) ? null : reader.GetString(4),
        AuthProtocol = (SnmpAuthProtocol)reader.GetInt32(5),
        AuthPassword = Reveal(reader, 6, reveal),
        PrivacyProtocol = (SnmpPrivacyProtocol)reader.GetInt32(7),
        PrivacyPassword = Reveal(reader, 8, reveal),
        Port = reader.GetInt32(9),
        Timeout = TimeSpan.FromMilliseconds(reader.GetInt64(10)),
        Retries = reader.GetInt32(11),
        Order = reader.GetInt32(12),
        CreatedUtc = new DateTimeOffset(reader.GetInt64(13), TimeSpan.Zero),
        UpdatedUtc = new DateTimeOffset(reader.GetInt64(14), TimeSpan.Zero),
    };

    private string? Reveal(SqliteDataReader reader, int column, bool reveal)
    {
        if (reader.IsDBNull(column))
        {
            return null;
        }

        // Пометка вместо пустоты: пустое поле в списке читается как «пароля нет»,
        // и человек начинает искать, куда он делся.
        return reveal ? _protector.Unprotect(reader.GetString(column)) : Hidden;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _runStore.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var connection = new SqliteConnection(_runStore.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        return connection;
    }
}
