using System.Text.Json;
using System.Text.Json.Serialization;

namespace StormMachine.Agent;

/// <summary>Сопряжённый собеседник.</summary>
public sealed record KnownPeer
{
    public required string Thumbprint { get; init; }

    public required string MachineName { get; init; }

    public required string Product { get; init; }

    public required DateTimeOffset PairedUtc { get; init; }

    public DateTimeOffset? LastSeenUtc { get; init; }
}

/// <summary>
/// Кого агент помнит.
/// </summary>
/// <remarks>
/// Обычный файл рядом с личностью, а не база: агенту нечего хранить, кроме десятка
/// отпечатков, и заводить ради них движок хранилища значило бы утяжелить портативный
/// бинарь ровно на то, чем он не пользуется.
/// <para>
/// Файл человекочитаем намеренно. Оператору на площадке случается открыть его глазами,
/// чтобы убедиться, кого именно агент пускает, и двоичный формат сделал бы эту проверку
/// невозможной без второго инструмента.
/// </para>
/// </remarks>
public sealed class PeerBook
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly PeerBookJsonContext Context = new(Options);

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, KnownPeer> _peers = new(StringComparer.OrdinalIgnoreCase);

    public PeerBook(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;

        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), Context.KnownPeerArray) ?? [];

            foreach (var peer in loaded)
            {
                _peers[peer.Thumbprint] = peer;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Повреждённый файл не должен мешать агенту запуститься: он снова начнёт
            // с пустым списком и потребует сопряжения. Молча затирать его нельзя —
            // это единственная запись о том, кому агент доверял.
            File.Move(path, path + ".broken", overwrite: true);
        }
    }

    public IReadOnlyCollection<string> Thumbprints
    {
        get
        {
            lock (_gate)
            {
                return [.. _peers.Keys];
            }
        }
    }

    public IReadOnlyList<KnownPeer> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _peers.Values.OrderBy(p => p.PairedUtc)];
            }
        }
    }

    public void Remember(string thumbprint, string machineName, string product)
    {
        lock (_gate)
        {
            _peers[thumbprint] = _peers.TryGetValue(thumbprint, out var existing)
                ? existing with { MachineName = machineName, Product = product, LastSeenUtc = DateTimeOffset.UtcNow }
                : new KnownPeer
                {
                    Thumbprint = thumbprint,
                    MachineName = machineName,
                    Product = product,
                    PairedUtc = DateTimeOffset.UtcNow,
                    LastSeenUtc = DateTimeOffset.UtcNow,
                };

            Save();
        }
    }

    public void Touch(string thumbprint)
    {
        lock (_gate)
        {
            if (_peers.TryGetValue(thumbprint, out var existing))
            {
                _peers[thumbprint] = existing with { LastSeenUtc = DateTimeOffset.UtcNow };
                Save();
            }
        }
    }

    public bool Forget(string thumbprint)
    {
        lock (_gate)
        {
            if (!_peers.Remove(thumbprint))
            {
                return false;
            }

            Save();

            return true;
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(_peers.Values.ToArray(), Context.KnownPeerArray);

        // Через временный файл: обрыв записи не должен оставить агента без списка
        // доверенных — на площадке это означает поездку.
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
    }
}

[JsonSerializable(typeof(KnownPeer[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class PeerBookJsonContext : JsonSerializerContext;
