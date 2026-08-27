using System.Globalization;
using ScottPlot;
using StormMachine.Domain.Results;

namespace StormMachine.Reporting;

/// <summary>
/// График маршрута для отчёта: столбик на хоп, цвет по потерям.
/// </summary>
/// <remarks>
/// Линия «значение против номера пробы» для трассировки бессмысленна: сэмплы идут
/// вперемешку по хопам, и получается пила без содержания. Осмысленная картинка здесь
/// одна — профиль задержки вдоль маршрута, на котором видно, где вырастает время
/// и где начинаются потери.
/// <para>
/// Оформление светлое, как и у графика ряда: отчёт печатают.
/// </para>
/// </remarks>
internal static class RouteChartImage
{
    private const int Width = 1000;
    private const int Height = 340;

    /// <summary>Потери, ниже которых хоп считается здоровым.</summary>
    private const double MinorLossPercent = 1.0;

    private static readonly Color HealthyColor = Color.FromHex("#1D4ED8");
    private static readonly Color MinorLossColor = Color.FromHex("#D97706");
    private static readonly Color HeavyLossColor = Color.FromHex("#B91C1C");
    private static readonly Color SilentColor = Color.FromHex("#CBD5E1");

    public static byte[]? TryRender(PathAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        if (analysis.Hops.Count == 0)
        {
            return null;
        }

        var bars = new List<Bar>(analysis.Hops.Count);
        var positions = new List<double>(analysis.Hops.Count);
        var labels = new List<string>(analysis.Hops.Count);
        var anyValue = false;

        foreach (var hop in analysis.Hops)
        {
            positions.Add(hop.Hop);
            labels.Add(hop.Hop.ToString(CultureInfo.InvariantCulture));

            // Молчащий хоп рисуется нулевым столбиком серого цвета: пропуск в ряду
            // читался бы как «здесь ничего нет», хотя хоп в маршруте есть.
            var value = hop.IsSilent ? 0 : hop.Statistics.P50Ms;
            anyValue |= !hop.IsSilent;

            bars.Add(new Bar
            {
                Position = hop.Hop,
                Value = value,
                FillColor = ColorFor(hop),
                Size = 0.7,
            });
        }

        if (!anyValue)
        {
            return null;
        }

        var plot = new Plot();

        plot.FigureBackground.Color = Colors.White;
        plot.DataBackground.Color = Color.FromHex("#FAFAFA");
        plot.Axes.Color(Color.FromHex("#444444"));
        plot.Grid.MajorLineColor = Color.FromHex("#E4E4E4");

        plot.Add.Bars(bars);

        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericManual([.. positions], [.. labels]);
        plot.XLabel("хоп");
        plot.YLabel("медиана RTT, мс");
        plot.Axes.AutoScale();
        plot.Axes.Left.Min = 0;

        plot.Legend.ManualItems.Add(new LegendItem { LabelText = "потерь нет", FillColor = HealthyColor });
        plot.Legend.ManualItems.Add(new LegendItem { LabelText = "потери до 5%", FillColor = MinorLossColor });
        plot.Legend.ManualItems.Add(new LegendItem { LabelText = "потери от 5%", FillColor = HeavyLossColor });
        plot.Legend.ManualItems.Add(new LegendItem { LabelText = "хоп молчит", FillColor = SilentColor });
        plot.ShowLegend(Edge.Top);

        return plot.GetImageBytes(Width, Height, ImageFormat.Png);
    }

    private static Color ColorFor(HopStatistics hop)
    {
        if (hop.IsSilent)
        {
            return SilentColor;
        }

        if (hop.LossPercent >= PathAnalysis.SignificantLossPercent)
        {
            return HeavyLossColor;
        }

        return hop.LossPercent >= MinorLossPercent ? MinorLossColor : HealthyColor;
    }
}
