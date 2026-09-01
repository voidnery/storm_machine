using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace StormMachine.App.Views.Controls;

/// <summary>
/// Доступ к словарю цветовых токенов из кода рисования.
/// </summary>
/// <remarks>
/// Сторож <c>DesignTokenTests</c> запрещает литеральные цвета в разметке, но код им
/// не виден: рисовалка карты держала у себя двадцать кистей, объявленных строками
/// с шестнадцатеричными значениями, и половина из них дублировала токены App.axaml
/// с точностью до цифры. Здесь тот же словарь читается из приложения — источник
/// цвета в продукте остаётся один.
/// <para>
/// Значения кэшируются: тема у продукта одна (тёмная), а поиск ресурса на каждый
/// узел графа при перерисовке карты в тысячу узлов обошёлся бы дорого. Обращения
/// идут только с потока разметки, поэтому обычного словаря достаточно.
/// </para>
/// <para>
/// Ключ, которого нет в словаре, — ошибка сборки продукта, а не повод рисовать
/// наугад: <c>DesignTokenResolutionTests</c> проверяет, что каждый ключ отсюда
/// в App.axaml есть. В работе такой ключ даёт серую кисть, чтобы отсутствие цвета
/// не роняло карту у оператора.
/// </para>
/// </remarks>
internal static class DesignTokens
{
    /// <summary>Поверхность канвы карты.</summary>
    public const string Surface = "SurfaceBrush";

    /// <summary>Заливка узла карты.</summary>
    public const string Node = "NodeBrush";

    /// <summary>Рамка узла без категории.</summary>
    public const string NodeOutline = "NodeOutlineBrush";

    public const string Text = "TextBrush";

    public const string TextSecondary = "TextSecondaryBrush";

    /// <summary>Подложка карточки.</summary>
    public const string Panel = "PanelBrush";

    public const string Accent = "AccentBrush";

    public const string Warning = "WarningBrush";

    public const string Success = "SuccessBrush";

    public const string Danger = "DangerBrush";

    public const string TextMuted = "TextMutedBrush";

    public const string Divider = "DividerBrush";

    /// <summary>Обводка выбранного узла.</summary>
    public const string Selection = "SelectionBrush";

    public const string LinkConfirmed = "LinkConfirmedBrush";

    public const string LinkInferred = "LinkInferredBrush";

    public const string LinkAssumed = "LinkAssumedBrush";

    private static readonly Dictionary<string, IBrush> Cache = new(StringComparer.Ordinal);

    private static readonly IBrush Missing = new SolidColorBrush(Colors.Gray);

    public static IBrush Brush(string key)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var found = Resolve(key) ?? Missing;

        Cache[key] = found;

        return found;
    }

    /// <summary>
    /// Цвет токена — тем рисовалкам, которым нужна не кисть, а значение.
    /// </summary>
    /// <remarks>
    /// График строит ScottPlot, а выгрузку карты в SVG — текстовый писатель:
    /// ни тому, ни другому кисть Avalonia не подходит. Значение всё равно берётся
    /// из общего словаря — иначе график и выгруженный файл разъедутся с экраном,
    /// оставаясь при этом «правильными» каждый по-своему.
    /// </remarks>
    public static Color ColorOf(string key) =>
        Brush(key) is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    /// <summary>Цвет токена строкой <c>#RRGGBB</c> — для SVG.</summary>
    public static string HexOf(string key)
    {
        var colour = ColorOf(key);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}");
    }

    /// <summary>Есть ли такой токен в словаре приложения.</summary>
    public static bool Exists(string key) => Resolve(key) is not null;

    // Полное имя: у продукта есть собственное пространство имён StormMachine.Application,
    // и короткое Application здесь означало бы не то.
    private static IBrush? Resolve(string key) =>
        Avalonia.Application.Current is { } app && app.TryFindResource(key, out var value)
            ? value as IBrush
            : null;
}
