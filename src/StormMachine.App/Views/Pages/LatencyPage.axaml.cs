using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ScottPlot;
using ScottPlot.Avalonia;
using StormMachine.App.ViewModels;
using StormMachine.App.Views.Controls;

namespace StormMachine.App.Views.Pages;

/// <summary>
/// Экран задержки с живым графиком.
/// </summary>
/// <remarks>
/// Работа с графиком живёт здесь, а не в модели представления, намеренно: типы ScottPlot
/// не должны выходить за пределы слоя представления. Модель отдаёт числа и сообщает,
/// что они изменились; чем именно их нарисовать — дело этого файла.
/// </remarks>
public partial class LatencyPage : UserControl
{
    private AvaPlot? _chart;
    private LatencyPageViewModel? _viewModel;

    public LatencyPage()
    {
        AvaloniaXamlLoader.Load(this);

        _chart = this.FindControl<AvaPlot>("Chart");
        ConfigurePlot();

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        _viewModel = DataContext as LatencyPageViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ChartUpdated += OnChartUpdated;
            Redraw();
        }
    }

    private void Unsubscribe()
    {
        if (_viewModel is not null)
        {
            _viewModel.ChartUpdated -= OnChartUpdated;
            _viewModel = null;
        }
    }

    private void OnChartUpdated(object? sender, EventArgs e) => Redraw();

    /// <summary>Цвет токена в цвет ScottPlot — общий с остальными графиками.</summary>
    private static Color Token(string key) => PlotLook.Token(key);

    private void ConfigurePlot()
    {
        if (_chart is null)
        {
            return;
        }

        var plot = _chart.Plot;

        PlotLook.Apply(plot);

        plot.XLabel("проба");
        plot.YLabel("время оборота (RTT), мс");

        PlotLook.ShowLegend(plot);
    }

    private void Redraw()
    {
        if (_chart is null || _viewModel is null)
        {
            return;
        }

        var values = _viewModel.ChartValues;
        var plot = _chart.Plot;

        plot.Clear();

        if (values.Count > 0)
        {
            // Копия делается намеренно: список меняется в потоке интерфейса между
            // перерисовками, и отдавать его наружу как есть — приглашение к гонке.
            var ys = values.ToArray();

            var signal = plot.Add.Signal(ys);
            signal.Color = Token(DesignTokens.Accent);
            signal.LineWidth = 1.5f;
            signal.LegendText = "время оборота";

            // Порог разрешения рисуется линией: значения ниже него неотличимы
            // от собственной работы измерительного стека, и это должно быть видно,
            // а не подразумеваться.
            if (_viewModel.FloorMs > 0)
            {
                var floor = plot.Add.HorizontalLine(_viewModel.FloorMs);
                floor.Color = Token(DesignTokens.Warning);
                floor.LineWidth = 1;
                floor.LinePattern = LinePattern.Dashed;

                // Пояснение вынесено в легенду, а не в подпись самой линии: подпись
                // ScottPlot рисует поверх оси Y и она перекрывает и шкалу, и данные.
                floor.LegendText = "порог часов — ниже него измерять нечем";
            }

            plot.Axes.AutoScale();
        }

        _chart.Refresh();
    }
}
