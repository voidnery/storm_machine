using System.Net;

namespace StormMachine.Platform.Geo;

/// <summary>Принадлежность адреса: автономная система и страна.</summary>
public sealed record AsnRecord(int? Number, string? Organization, string? Country);

/// <summary>Офлайн-справочник принадлежности адресов.</summary>
public interface IAsnDatabase
{
    /// <summary>Загружена ли база. Отсутствие базы — норма, а не ошибка.</summary>
    bool IsAvailable { get; }

    /// <summary>Каталог, в котором инструмент ищет базы.</summary>
    string Location { get; }

    /// <summary>Что именно загружено — показывается в отчёте вместе с указанием источника.</summary>
    string? Description { get; }

    AsnRecord? Lookup(IPAddress address);
}

/// <summary>
/// Принадлежность адресов по офлайн-базам DB-IP Lite.
/// </summary>
/// <remarks>
/// База <b>не входит в поставку</b>. DB-IP Lite распространяется по лицензии CC BY-SA 4.0:
/// её можно свободно использовать, но производные обязаны наследовать ту же лицензию,
/// а это несовместимо с MIT нашего кода. Поэтому оператор скачивает базу сам, а продукт
/// работает и без неё — та же градация по зависимостям, что для SNMP и захвата пакетов.
/// <para>
/// Файлы ищутся по маске, а не по точному имени: DB-IP выкладывает их с датой в названии
/// (<c>dbip-asn-lite-2026-08.mmdb</c>), и требовать переименования значило бы создать
/// ровно одну возможность ошибиться.
/// </para>
/// </remarks>
public sealed class AsnDatabase : IAsnDatabase
{
    /// <summary>Обязательное указание источника по условиям CC BY-SA 4.0.</summary>
    public const string Attribution = "Данные о принадлежности адресов: DB-IP (db-ip.com), CC BY-SA 4.0";

    private const string AsnMask = "*asn*.mmdb";
    private const string CountryMask = "*country*.mmdb";

    private readonly MaxMindDatabase? _asn;
    private readonly MaxMindDatabase? _country;

    private AsnDatabase(string location, MaxMindDatabase? asn, MaxMindDatabase? country)
    {
        Location = location;
        _asn = asn;
        _country = country;

        var parts = new List<string>(2);

        if (asn is not null)
        {
            parts.Add(asn.Describe());
        }

        if (country is not null)
        {
            parts.Add(country.Describe());
        }

        Description = parts.Count == 0 ? null : string.Join(", ", parts);
    }

    public bool IsAvailable => _asn is not null || _country is not null;

    public string Location { get; }

    public string? Description { get; }

    /// <summary>Каталог, куда оператор кладёт базы.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StormMachine",
        "geo");

    /// <summary>
    /// Открывает базы из каталога. Всегда возвращает объект: отсутствие файлов —
    /// это состояние «данных нет», а не повод отказать в трассировке.
    /// </summary>
    public static AsnDatabase Open(string? directory = null)
    {
        var location = directory ?? DefaultPath();

        if (!Directory.Exists(location))
        {
            return new AsnDatabase(location, null, null);
        }

        return new AsnDatabase(location, TryLoad(location, AsnMask), TryLoad(location, CountryMask));
    }

    public AsnRecord? Lookup(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        var asn = _asn?.Lookup(address);
        var country = _country?.Lookup(address);

        if (asn is null && country is null)
        {
            return null;
        }

        return new AsnRecord(
            ReadAsNumber(asn),
            ReadString(asn, "autonomous_system_organization"),
            ReadCountry(country));
    }

    private static MaxMindDatabase? TryLoad(string directory, string mask)
    {
        // При нескольких файлах берётся последний по имени: у DB-IP имя содержит дату,
        // поэтому последний по алфавиту — самый свежий.
        var file = Directory
            .EnumerateFiles(directory, mask, SearchOption.TopDirectoryOnly)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .LastOrDefault();

        if (file is null)
        {
            return null;
        }

        try
        {
            return MaxMindDatabase.Open(file);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            // Испорченный или чужой файл не должен ронять трассировку: без него
            // остаются адреса и имена, а это уже полезно.
            return null;
        }
    }

    private static int? ReadAsNumber(IReadOnlyDictionary<string, object?>? record) =>
        record?.TryGetValue("autonomous_system_number", out var value) == true
        && value is long number
        && number is > 0 and <= uint.MaxValue
            ? (int)number
            : null;

    private static string? ReadString(IReadOnlyDictionary<string, object?>? record, string key) =>
        record?.TryGetValue(key, out var value) == true && value is string text && text.Length > 0
            ? text
            : null;

    /// <summary>
    /// Достаёт название страны: сначала русское, затем английское, затем код.
    /// </summary>
    private static string? ReadCountry(IReadOnlyDictionary<string, object?>? record)
    {
        if (record?.TryGetValue("country", out var raw) != true
            || raw is not IReadOnlyDictionary<string, object?> country)
        {
            return null;
        }

        if (country.TryGetValue("names", out var namesRaw)
            && namesRaw is IReadOnlyDictionary<string, object?> names)
        {
            if (ReadString(names, "ru") is { } russian)
            {
                return russian;
            }

            if (ReadString(names, "en") is { } english)
            {
                return english;
            }
        }

        return ReadString(country, "iso_code");
    }
}
