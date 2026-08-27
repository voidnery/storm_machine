using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using StormMachine.Application.Abstractions;

namespace StormMachine.Discovery;

/// <summary>
/// Вендор по префиксу MAC из реестра IEEE.
/// </summary>
/// <remarks>
/// База встроена в сборку ресурсом. Решение сознательное: вендор по MAC входит
/// в уровень 0 — те самые 80% ценности без прав администратора и драйверов, —
/// и требовать ради него скачивания значило бы сломать сценарий «первый запуск
/// за минуту», особенно в изолированной сети, где инструмент нужнее всего.
/// Реестр IEEE публичный, в отличие от Npcap и DB-IP, которые в поставку не входят
/// и входить не могут.
/// <para>
/// Три реестра различаются длиной префикса: MA-L — 24 бита, MA-M — 28, MA-S — 36.
/// Поиск идёт от самого частного к самому общему: мелкому производителю могли выдать
/// кусок внутри чужого блока, и найдись сначала блок — вендор оказался бы чужим.
/// </para>
/// </remarks>
public sealed class OuiCatalog : IOuiCatalog
{
    private const string ResourceName = "StormMachine.Discovery.Resources.oui.tsv.gz";

    /// <summary>Длины префиксов в шестнадцатеричных знаках: MA-S, MA-M, MA-L.</summary>
    private static readonly int[] PrefixLengths = [9, 7, 6];

    private readonly Lazy<Dictionary<string, string>> _entries;

    public OuiCatalog() => _entries = new Lazy<Dictionary<string, string>>(Load);

    public int Count => _entries.Value.Count;

    public string? Lookup(string macAddress)
    {
        if (string.IsNullOrWhiteSpace(macAddress))
        {
            return null;
        }

        var digits = Normalize(macAddress);

        if (digits.Length < PrefixLengths[^1])
        {
            return null;
        }

        foreach (var length in PrefixLengths)
        {
            if (digits.Length >= length && _entries.Value.TryGetValue(digits[..length], out var vendor))
            {
                return vendor;
            }
        }

        return null;
    }

    /// <summary>
    /// Приводит MAC к последовательности шестнадцатеричных знаков.
    /// </summary>
    /// <remarks>
    /// Написаний у MAC много: через дефис, через двоеточие, через точку, слитно.
    /// Требовать одно из них от вызывающей стороны — верный способ получить
    /// «вендор не найден» на ровном месте.
    /// </remarks>
    internal static string Normalize(string macAddress)
    {
        Span<char> buffer = stackalloc char[16];
        var length = 0;

        foreach (var c in macAddress)
        {
            if (length == buffer.Length)
            {
                break;
            }

            if (char.IsAsciiHexDigit(c))
            {
                buffer[length++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    private static Dictionary<string, string> Load()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        using var resource = typeof(OuiCatalog).GetTypeInfo().Assembly.GetManifestResourceStream(ResourceName);

        if (resource is null)
        {
            // Ресурс мог не попасть в сборку при неверной настройке проекта.
            // Инвентарь останется без вендоров, но найдёт устройства.
            return entries;
        }

        using var unpacked = new GZipStream(resource, CompressionMode.Decompress);
        using var reader = new StreamReader(unpacked);

        while (reader.ReadLine() is { } line)
        {
            var separator = line.IndexOf('\t', StringComparison.Ordinal);

            if (separator > 0 && separator < line.Length - 1)
            {
                entries[line[..separator]] = line[(separator + 1)..];
            }
        }

        return entries;
    }

    /// <summary>
    /// Что показать оператору о самой базе.
    /// </summary>
    /// <remarks>
    /// Число без разделителей разрядов намеренно: клиенты собираются
    /// с <c>InvariantGlobalization</c> ради обрезки, и обращение к именованной культуре
    /// падает — не при сборке, а при первом показе у пользователя. Тест на этот метод
    /// поймал ровно такое падение.
    /// </remarks>
    public string Describe() =>
        $"реестр IEEE, {Count.ToString(CultureInfo.InvariantCulture)} записей";
}
