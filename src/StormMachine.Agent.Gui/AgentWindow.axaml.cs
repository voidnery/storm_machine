using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ScottPlot;
using ScottPlot.Avalonia;

namespace StormMachine.Agent.Gui;

/// <summary>
/// Окно агента с живым графиком измерения.
/// </summary>
/// <remarks>
/// Работа с рисовалкой живёт здесь, а не в модели: типы ScottPlot не должны выходить
/// за пределы представления. Модель отдаёт числа и говорит, что они изменились.
/// Так же устроены графики клиента.
/// </remarks>
public partial class AgentWindow : Window
{
    private readonly AvaPlot? _chart;

    private AgentWindowViewModel? _model;

    public AgentWindow()
    {
        AvaloniaXamlLoader.Load(this);

        _chart = this.FindControl<AvaPlot>("Chart");

        Configure();

        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        _model = DataContext as AgentWindowViewModel;

        if (_model is not null)
        {
            _model.ChartUpdated += OnChartUpdated;
            Redraw();
        }
    }

    private void Unsubscribe()
    {
        if (_model is not null)
        {
            _model.ChartUpdated -= OnChartUpdated;
            _model = null;
        }
    }

    private void OnChartUpdated(object? sender, EventArgs e) => Redraw();

    private void Configure()
    {
        if (_chart is null)
        {
            return;
        }

        var plot = _chart.Plot;

        // Те же цвета, что у графиков клиента: это один продукт, и измерение,
        // показанное на двух машинах по-разному, читается как два разных.
        plot.FigureBackground.Color = Color.FromHex("#232A38");
        plot.DataBackground.Color = Color.FromHex("#1B222E");
        plot.Axes.Color(Color.FromHex("#A9B4C4"));
        plot.Grid.MajorLineColor = Color.FromHex("#2E3746");

        plot.XLabel("снимок");
        plot.YLabel("Мбит/с");
    }

    private void Redraw()
    {
        if (_chart is null || _model is null)
        {
            return;
        }

        var plot = _chart.Plot;

        plot.Clear();

        if (_model.ChartValues.Count > 0)
        {
            // Копия намеренно: список меняется в потоке интерфейса между перерисовками,
            // и отдавать его рисовалке как есть — приглашение к гонке.
            var signal = plot.Add.Signal(_model.ChartValues.ToArray());

            signal.Color = Color.FromHex("#3B82F6");
            signal.LineWidth = 1.5f;

            plot.Axes.AutoScale();
        }

        _chart.Refresh();
    }
}
