using System.Globalization;

namespace StormMachine.App.Services;

/// <summary>
/// Номер выпуска по SemVer 2.0: <c>X.Y.Z</c> и, если есть, суффикс <c>-alpha.N</c>.
/// </summary>
/// <remarks>
/// Свой разбор, а не библиотека: нужен ровно порядок номеров, и ради него тянуть пакет
/// с чужими правилами обрезки незачем. Порядок суффиксов — по стандарту: числовые части
/// сравниваются как числа, иначе «alpha.10» оказалась бы старше «alpha.9» только
/// по счастливой случайности длины, а выпуск без суффикса старше любого с суффиксом.
/// </remarks>
internal readonly record struct ReleaseVersion(int Major, int Minor, int Patch, string Prerelease)
    : IComparable<ReleaseVersion>
{
    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>Разбирает номер; ведущая «v» тега и метаданные после «+» отбрасываются.</summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();

        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }

        var plus = value.IndexOf('+', StringComparison.Ordinal);

        if (plus >= 0)
        {
            value = value[..plus];
        }

        var dash = value.IndexOf('-', StringComparison.Ordinal);
        var core = dash >= 0 ? value[..dash] : value;
        var prerelease = dash >= 0 ? value[(dash + 1)..] : string.Empty;

        var parts = core.Split('.');

        if (parts.Length != 3
            || !TryNumber(parts[0], out var major)
            || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch))
        {
            return false;
        }

        if (dash >= 0 && !prerelease.Split('.').All(IsIdentifier))
        {
            return false;
        }

        version = new ReleaseVersion(major, minor, patch, prerelease);

        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));

        if (core != 0)
        {
            return core;
        }

        if (IsPrerelease != other.IsPrerelease)
        {
            return IsPrerelease ? -1 : 1;
        }

        var mine = Prerelease.Split('.');
        var theirs = other.Prerelease.Split('.');

        for (var i = 0; i < Math.Min(mine.Length, theirs.Length); i++)
        {
            var step = CompareIdentifier(mine[i], theirs[i]);

            if (step != 0)
            {
                return step;
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    public static bool operator <(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(ReleaseVersion left, ReleaseVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = TryNumber(left, out var leftNumber);
        var rightNumeric = TryNumber(right, out var rightNumber);

        return (leftNumeric, rightNumeric) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right),
        };
    }

    private static bool TryNumber(string text, out int number) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);

    private static bool IsIdentifier(string part) =>
        part.Length > 0 && part.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
}
