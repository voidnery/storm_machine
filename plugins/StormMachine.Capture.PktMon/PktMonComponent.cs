using System.Globalization;
using System.Text.RegularExpressions;

namespace StormMachine.Capture.PktMon;

/// <summary>Точка стека, на которой pktmon умеет слушать.</summary>
public sealed record PktMonComponent(int Id, string? MacAddress, string Name);

/// <summary>
/// Разбор таблицы компонентов <c>pktmon list</c>.
/// </summary>
/// <remarks>
/// Отделён от запуска процесса ради проверок: таблица переводится на язык системы,
/// и единственный способ убедиться, что разбор не держится за русские заголовки, —
/// прогнать его на записанных ответах. Образцы в проверках сняты с живой машины.
/// <para>
/// Опора только на то, что от языка не зависит: число в начале строки —
/// идентификатор, шестнадцатеричная шестёрка через дефисы — MAC. Заголовки колонок
/// не разбираются вовсе.
/// </para>
/// </remarks>
public static partial class PktMonComponents
{
    public static IReadOnlyList<PktMonComponent> Parse(string? listing)
    {
        if (string.IsNullOrWhiteSpace(listing))
        {
            return [];
        }

        var found = new List<PktMonComponent>();

        foreach (var line in listing.Split('\n'))
        {
            var row = line.TrimEnd('\r').Trim();

            if (row.Length == 0)
            {
                continue;
            }

            var number = Leading().Match(row);

            if (!number.Success)
            {
                continue;
            }

            if (!int.TryParse(number.Groups[1].Value, CultureInfo.InvariantCulture, out var id))
            {
                continue;
            }

            var rest = row[number.Length..];
            var mac = Hardware().Match(rest);
            var name = (mac.Success ? rest[(mac.Index + mac.Length)..] : rest).Trim();

            // Строка-разделитель вида «-- --------- ---» числом не начинается,
            // но строка с одним идентификатором и без имени — бывает: у туннелей
            // MAC нет, а слушать их продукт и не собирается.
            found.Add(new PktMonComponent(
                id,
                mac.Success ? Normalize(mac.Value) : null,
                name));
        }

        return found;
    }

    /// <summary>Компоненты, за которыми стоит та же карта.</summary>
    /// <remarks>
    /// Их бывает несколько: на хосте с Hyper-V один MAC принадлежит и физической
    /// карте, и виртуальному коммутатору поверх неё. Слушать надо оба — LLDP идёт
    /// на зарезервированный адрес, который виртуальный коммутатор дальше не передаёт,
    /// и на нём одном соседи не слышны вовсе (спайк-10).
    /// </remarks>
    public static IReadOnlyList<PktMonComponent> WithMac(
        IReadOnlyList<PktMonComponent> components,
        string? mac)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (string.IsNullOrWhiteSpace(mac))
        {
            return [];
        }

        var wanted = Normalize(mac);

        return [.. components.Where(c => c.MacAddress is not null
                                         && string.Equals(c.MacAddress, wanted, StringComparison.Ordinal))];
    }

    internal static string Normalize(string mac) => mac
        .Replace(":", string.Empty, StringComparison.Ordinal)
        .Replace("-", string.Empty, StringComparison.Ordinal)
        .ToUpperInvariant();

    [GeneratedRegex(@"^\s*(\d+)\s")]
    private static partial Regex Leading();

    [GeneratedRegex("[0-9A-Fa-f]{2}(?:[:-][0-9A-Fa-f]{2}){5}")]
    private static partial Regex Hardware();
}
