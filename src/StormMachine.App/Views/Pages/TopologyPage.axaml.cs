using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using StormMachine.App.ViewModels;
using StormMachine.App.Views.Controls;
using StormMachine.Domain.Topology;

namespace StormMachine.App.Views.Pages;

/// <summary>
/// Экран карты сети.
/// </summary>
/// <remarks>
/// Выгрузка живёт здесь, а не в модели представления: PNG снимается с самого полотна,
/// а SVG рисуется по его геометрии. И то и другое — свойства показа, а не сети;
/// в выгрузку JSON координаты не входят вовсе.
/// </remarks>
public partial class TopologyPage : UserControl
{
    /// <summary>Разрешение снимка. Больше экранного: карту вставляют в отчёты и печатают.</summary>
    private const double ExportScale = 2.0;

    public TopologyPage()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is not TopologyPageViewModel model)
        {
            return;
        }

        model.ExportImage = SaveAsync;
        model.GraphReplaced += (_, _) => this.FindControl<TopologyCanvas>("Canvas")?.FitToView();
    }

    private Task<bool> SaveAsync(string path)
    {
        var canvas = this.FindControl<TopologyCanvas>("Canvas");

        if (canvas is null || canvas.Placed.Nodes.Count == 0)
        {
            return Task.FromResult(false);
        }

        if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            File.WriteAllText(path, ToSvg(canvas.Placed), Encoding.UTF8);
            return Task.FromResult(true);
        }

        SavePng(canvas, path);

        return Task.FromResult(true);
    }

    /// <summary>
    /// Снимок полотна.
    /// </summary>
    /// <remarks>
    /// Снимается то, что видно на экране, включая текущий масштаб и сдвиг: оператор
    /// выгружает карту после того, как выставил нужный вид, и получить вместо него
    /// что-то другое было бы неожиданностью.
    /// </remarks>
    private static void SavePng(TopologyCanvas canvas, string path)
    {
        var size = new PixelSize(
            Math.Max(1, (int)(canvas.Bounds.Width * ExportScale)),
            Math.Max(1, (int)(canvas.Bounds.Height * ExportScale)));

        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * ExportScale, 96 * ExportScale));

        bitmap.Render(canvas);
        bitmap.Save(path, new PngBitmapEncoderOptions());
    }

    /// <summary>
    /// Векторная выгрузка карты.
    /// </summary>
    /// <remarks>
    /// Пишется вручную, а не через библиотеку: формат нужен ровно в объёме
    /// «прямоугольники, линии и подписи», и различие достоверности выражается
    /// штриховкой — тем же способом, что на экране. Своя разметка это ещё
    /// и гарантия, что выгрузка не разойдётся с показом.
    /// </remarks>
    private static string ToSvg(PlacedGraph placed)
    {
        var svg = new StringBuilder();
        var width = placed.Width + 80;
        var height = placed.Height + 80;

        svg.Append(CultureInfo.InvariantCulture, $"""
            <svg xmlns="http://www.w3.org/2000/svg" width="{N(width)}" height="{N(height)}"
                 viewBox="0 0 {N(width)} {N(height)}">
              <rect width="100%" height="100%" fill="{DesignTokens.HexOf(DesignTokens.Surface)}"/>
              <g transform="translate(40,40)" font-family="Segoe UI, sans-serif">

            """);

        foreach (var link in placed.Links)
        {
            var (stroke, dash) = Stroke(link.Link.Confidence);

            svg.Append(CultureInfo.InvariantCulture, $"""
                    <line x1="{N(link.X1)}" y1="{N(link.Y1)}" x2="{N(link.X2)}" y2="{N(link.Y2)}"
                          stroke="{stroke}" stroke-width="1.5"{dash}>
                      <title>{Escape(link.Link.Because)}</title>
                    </line>

                """);
        }

        foreach (var node in placed.Nodes)
        {
            svg.Append(CultureInfo.InvariantCulture, $"""
                    <g>
                      <rect x="{N(node.Left)}" y="{N(node.Top)}" width="{N(node.Width)}" height="{N(node.Height)}"
                            rx="4" fill="{DesignTokens.HexOf(DesignTokens.Node)}" stroke="{Border(node.Node.Kind)}" stroke-width="1"/>
                      <text x="{N(node.Left + 8)}" y="{N(node.Top + 17)}" font-size="12" fill="{DesignTokens.HexOf(DesignTokens.Text)}">{Escape(node.Node.Label)}</text>
                      <text x="{N(node.Left + 8)}" y="{N(node.Top + 31)}" font-size="9.5" fill="{DesignTokens.HexOf(DesignTokens.TextSecondary)}">{Escape(node.Node.Address ?? string.Empty)}</text>
                    </g>

                """);
        }

        svg.Append("  </g>\n</svg>\n");

        return svg.ToString();
    }

    private static (string Stroke, string Dash) Stroke(LinkConfidence confidence) => confidence switch
    {
        LinkConfidence.Confirmed => (DesignTokens.HexOf(DesignTokens.LinkConfirmed), string.Empty),
        LinkConfidence.Inferred => (DesignTokens.HexOf(DesignTokens.LinkInferred), " stroke-dasharray=\"5,4\""),
        _ => (DesignTokens.HexOf(DesignTokens.LinkAssumed), " stroke-dasharray=\"1.5,4\""),
    };

    private static string Border(TopologyNodeKind kind) => kind switch
    {
        TopologyNodeKind.ThisMachine or TopologyNodeKind.Router => DesignTokens.HexOf(DesignTokens.Accent),
        TopologyNodeKind.Internet => DesignTokens.HexOf(DesignTokens.Warning),
        TopologyNodeKind.Subnet or TopologyNodeKind.HostGroup => DesignTokens.HexOf(DesignTokens.NodeOutline),
        _ => DesignTokens.HexOf(DesignTokens.Divider),
    };

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Escape(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
