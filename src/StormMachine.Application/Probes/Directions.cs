namespace StormMachine.Application.Probes;

/// <summary>
/// Направление измерения скорости — словами, которые видит оператор.
/// </summary>
/// <remarks>
/// До И-24+ выпадающий список показывал <c>upload</c>, <c>download</c>, <c>both</c>:
/// английские значения посреди русской формы (замечание оператора). Названия стали
/// русскими, но старые значения принимаются по-прежнему — пресеты, сценарии и мониторы,
/// заведённые раньше, ломаться от переименования подписи не должны.
/// </remarks>
public static class Directions
{
    public const string Upload = "отдача";

    public const string Download = "приём";

    public const string Both = "обе стороны";

    /// <summary>Обе стороны подряд.</summary>
    public static bool IsBoth(string? value) => Matches(value, Both, "both", "обе");

    /// <summary>От нас наружу.</summary>
    public static bool IsUpload(string? value) => Matches(value, Upload, "upload");

    /// <summary>К нам снаружи.</summary>
    public static bool IsDownload(string? value) => Matches(value, Download, "download");

    private static bool Matches(string? value, params string[] names)
    {
        if (value is null)
        {
            return false;
        }

        var trimmed = value.Trim();

        return names.Any(name => string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase));
    }
}
