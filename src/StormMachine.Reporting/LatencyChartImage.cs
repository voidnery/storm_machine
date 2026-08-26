using ScottPlot;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;

namespace StormMachine.Reporting;

/// <summary>
/// График измерения для вставки в отчёт.
/// </summary>
/// <remarks>
/// Оформление светлое, а не тёмное как в приложении, и это не оплошность: отчёт печатают
/// и пересылают, а тёмная заливка на бумаге превращается в чёрный прямоугольник.
/// Поэтому общего кода с живым графиком у него нет — это разные задачи, а не дубль.
/// <para>
/// Используется ядро ScottPlot без Avalonia: рисование в изображение не требует
/// графической подсистемы, и правило «Avalonia только в клиенте» не нарушается.
/// </para>
/// </remarks>
internal static class LatencyChartImage
{
    private const int Width = 1000;
    private const int Height = 380;

    /// <summary>
    /// Рисует ряд измерений. Возвращает <c>null</c>, если рисовать нечего.
    /// </summary>
    public static byte[]? TryRender(StoredRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (run.Samples.Count < 2)
        {
            return null;
        }

        var values = new double[run.Samples.Count];

        for (var i = 0; i < run.Samples.Count; i++)
        {
            var sample = run.Samples[i];

            // Потеря рисуется разрывом, а не нулём: ноль означал бы мгновенный ответ,
            // то есть ровно противоположное произошедшему.
            values[i] = sample.IsSuccess ? sample.Value : double.NaN;
        }

        var plot = new Plot();

        plot.FigureBackground.Color = Colors.White;
        plot.DataBackground.Color = Color.FromHex("#FAFAFA");
        plot.Axes.Color(Color.FromHex("#444444"));
        plot.Grid.MajorLineColor = Color.FromHex("#E4E4E4");

        var signal = plot.Add.Signal(values);
        signal.Color = Color.FromHex("#1D4ED8");
        signal.LineWidth = 1.5f;
        signal.LegendText = DescribeUnit(run.Unit);

        var floor = run.Context.CalibrationBaselineMs;
        if (floor > 0)
        {
            var line = plot.Add.HorizontalLine(floor);
            line.Color = Color.FromHex("#B45309");
            line.LineWidth = 1;
            line.LinePattern = LinePattern.Dashed;
            line.LegendText = "порог достоверности";
        }

        plot.XLabel("проба");
        plot.YLabel(DescribeUnit(run.Unit));
        plot.Axes.AutoScale();
        plot.ShowLegend(Edge.Top);

        return plot.GetImageBytes(Width, Height, ImageFormat.Png);
    }

    private static string DescribeUnit(MeasurementUnit unit) => unit switch
    {
        MeasurementUnit.Milliseconds => "RTT, мс",
        MeasurementUnit.MegabitsPerSecond => "Мбит/с",
        MeasurementUnit.Percent => "%",
        MeasurementUnit.Bytes => "байт",
        _ => "значение",
    };
}
