using System.Globalization;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.TickGenerators;
using StormMachine.App.ViewModels;
using StormMachine.App.Views.Controls;

namespace StormMachine.App.Views.Pages;

/// <summary>
/// Дашборд с графиками тренда.
/// </summary>
/// <remarks>
/// Рисование живёт здесь, а не в модели представления, — по тому же правилу, что
/// и на странице задержки: типы рисовалки за слой представления не выходят. Модель
/// отдаёт точки и сообщает, что они сменились.
/// <para>
/// Графиков два, и они разделены намеренно. Задержка и доля ответов — разные
/// величины с разными осями; положенные на одну, они дают картинку, из которой
/// не читается ни то ни другое. Ось времени у них общая, и потому провал одного
/// виден против всплеска другого — это и есть то, ради чего их смотрят рядом.
/// </para>
/// </remarks>
public partial class DashboardPage : UserControl
{
    private readonly AvaPlot? _latency;
    private readonly AvaPlot? _answers;

    private DashboardPageViewModel? _viewModel;

    public DashboardPage()
    {
        AvaloniaXamlLoader.Load(this);

        _latency = this.FindControl<AvaPlot>("Latency");
        _answers = this.FindControl<AvaPlot>("Answers");

        Configure();

        DataContextChanged += OnDataContextChanged;
        DetachedFromVisualTree += (_, _) => Unsubscribe();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        Unsubscribe();

        _viewModel = DataContext as DashboardPageViewModel;

        if (_viewModel is not null)
        {
            _viewModel.TrendUpdated += OnTrendUpdated;
            Redraw();
        }
    }

    private void Unsubscribe()
    {
        if (_viewModel is not null)
        {
            _viewModel.TrendUpdated -= OnTrendUpdated;
            _viewModel = null;
        }
    }

    private void OnTrendUpdated(object? sender, EventArgs e) => Redraw();

    private void Configure()
    {
        if (_latency is { } latency)
        {
            PlotLook.Apply(latency.Plot);
            latency.Plot.YLabel("медиана, мс");
            RussianDates(latency.Plot);
        }

        if (_answers is { } answers)
        {
            PlotLook.Apply(answers.Plot);
            answers.Plot.YLabel("ответов, %");
            RussianDates(answers.Plot);

            // Ось ответов закреплена от нуля до ста: автомасштаб растягивал бы
            // разницу между 99 % и 100 % на весь график, и мелкая потеря выглядела
            // бы обвалом. Здесь важно обратное — видеть, что ответы в норме.
            answers.Plot.Axes.SetLimitsY(0, 105);
        }
    }

    /// <summary>
    /// Даты на оси — как везде в продукте: «31.08 10:58».
    /// </summary>
    /// <remarks>
    /// Рисовалка по умолчанию печатает их форматом системы, и на английской
    /// локали это «08/31/2026 10:58:00» — чужой порядок полей в русском окне,
    /// да ещё и вчетверо длиннее нужного.
    /// </remarks>
    private static void RussianDates(Plot plot)
    {
        var axis = plot.Axes.DateTimeTicksBottom();

        if (axis.TickGenerator is DateTimeAutomatic generator)
        {
            generator.LabelFormatter = date => date.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Перерисовка по новым точкам.
    /// </summary>
    /// <remarks>
    /// Точки берутся копией: список меняется в потоке интерфейса между перерисовками,
    /// и отдавать его рисовалке как есть — приглашение к гонке.
    /// </remarks>
    private void Redraw()
    {
        if (_viewModel is null)
        {
            return;
        }

        var points = _viewModel.Trend.ToArray();

        // Ось времени у обоих графиков одна и та же, посчитанная по всем точкам.
        // Своя ось у каждого выглядела бы так же, а читалась бы неверно: провал
        // ответов оказывался бы под другим местом линии задержки, чем на самом деле.
        var (from, to) = Span(points);

        DrawLatency(points, from, to);
        DrawAnswers(points, from, to);
    }

    /// <summary>Границы общей оси времени; пустой ряд отдаёт сегодняшние сутки.</summary>
    private static (double From, double To) Span(TrendPoint[] points)
    {
        if (points.Length == 0)
        {
            return (DateTime.Today.ToOADate(), DateTime.Today.AddDays(1).ToOADate());
        }

        var from = points.Min(p => p.When.LocalDateTime).ToOADate();
        var to = points.Max(p => p.When.LocalDateTime).ToOADate();

        // Один прогон — не отрезок: без запаса ось схлопнется в точку.
        return from < to ? (from, to) : (from - 0.5, to + 0.5);
    }

    private void DrawLatency(IReadOnlyList<TrendPoint> points, double from, double to)
    {
        if (_latency is null)
        {
            return;
        }

        var plot = _latency.Plot;
        plot.Clear();

        var measured = points.Where(p => p.MedianMs is not null).ToList();

        if (measured.Count > 0)
        {
            var xs = measured.Select(p => p.When.LocalDateTime.ToOADate()).ToArray();
            var ys = measured.Select(p => p.MedianMs!.Value).ToArray();

            var line = plot.Add.ScatterLine(xs, ys);
            line.Color = PlotLook.Token(DesignTokens.Accent);
            line.LineWidth = 1.5f;
            line.MarkerSize = 5;

            plot.Axes.AutoScaleY();
            plot.Axes.SetLimitsX(from, to);
        }

        _latency.Refresh();
    }

    private void DrawAnswers(IReadOnlyList<TrendPoint> points, double from, double to)
    {
        if (_answers is null)
        {
            return;
        }

        var plot = _answers.Plot;
        plot.Clear();

        var answered = points.Where(p => p.SuccessShare is not null).ToList();

        if (answered.Count > 0)
        {
            var xs = answered.Select(p => p.When.LocalDateTime.ToOADate()).ToArray();
            var ys = answered.Select(p => p.SuccessShare!.Value * 100).ToArray();

            var line = plot.Add.ScatterLine(xs, ys);

            // Цвет предупреждения, а не общий: у этого графика хорошая новость —
            // прямая линия по верху, и любой провал должен бросаться в глаза.
            line.Color = PlotLook.Token(DesignTokens.Warning);
            line.LineWidth = 1.5f;
            line.MarkerSize = 4;

            plot.Axes.SetLimitsX(from, to);
            plot.Axes.SetLimitsY(0, 105);
        }

        _answers.Refresh();
    }
}
