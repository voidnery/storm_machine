using ScottPlot;

namespace StormMachine.App.Views.Controls;

/// <summary>
/// Оформление графиков — одно на продукт.
/// </summary>
/// <remarks>
/// Пока график был один, его вид жил в файле страницы. Со вторым и третьим это
/// перестало годиться: график, разъехавшийся с окном по палитре, выглядит вставленным
/// из другой программы, а два графика, разъехавшиеся между собой, — ещё и неряшливо.
/// Цвета берутся из того же словаря, что и разметка (<see cref="DesignTokens" />),
/// поэтому смена темы двигает и окно, и графики.
/// </remarks>
internal static class PlotLook
{
    /// <summary>
    /// Цвет токена в цвет ScottPlot.
    /// </summary>
    /// <remarks>
    /// У рисовалки графиков свой тип цвета, и кисть Avalonia ей не подходит.
    /// </remarks>
    public static Color Token(string key) => Color.FromARGB(DesignTokens.ColorOf(key).ToUInt32());

    /// <summary>Общий вид: фон, оси, сетка. Подписи осей — дело самой страницы.</summary>
    public static void Apply(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        plot.FigureBackground.Color = Token(DesignTokens.Surface);
        plot.DataBackground.Color = Token(DesignTokens.Surface);
        plot.Axes.Color(Token(DesignTokens.TextSecondary));
        plot.Grid.MajorLineColor = Token(DesignTokens.Panel);
    }

    /// <summary>
    /// Легенда под тёмную тему.
    /// </summary>
    /// <remarks>
    /// Включать её надо ровно один раз, при настройке: <c>ShowLegend</c> добавляет
    /// панель на каждый вызов, и при перерисовке десять раз в секунду легенды
    /// наслаивались друг на друга, съедая половину графика. Настройки берутся
    /// у самой легенды, а не у панели, которую вернул <c>ShowLegend</c>: панель
    /// отвечает за размещение, легенда — за вид.
    /// </remarks>
    public static void ShowLegend(Plot plot, Edge edge = Edge.Top)
    {
        ArgumentNullException.ThrowIfNull(plot);

        plot.ShowLegend(edge);

        plot.Legend.BackgroundColor = Token(DesignTokens.Panel);
        plot.Legend.OutlineColor = Token(DesignTokens.Divider);
        plot.Legend.FontColor = Token(DesignTokens.Text);
        plot.Legend.ShadowColor = Colors.Transparent;
    }
}
