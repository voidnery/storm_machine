using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using StormMachine.Application.Abstractions;
using StormMachine.Domain.Measurements;
using StormMachine.Domain.Results;
using StormMachine.Domain.Targets;
using Xunit.Abstractions;

namespace StormMachine.Reporting.UnitTests;

/// <summary>
/// Проверки формирования отчёта.
/// </summary>
/// <remarks>
/// Отчёт — это документ, который предъявляют провайдеру или заказчику. Проверяется
/// не только то, что файл получился, но и то, что в нём есть методика и условия
/// измерения: без них цифры не значат ничего.
/// </remarks>
public sealed class PdfReportRendererTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    private static StoredRun BuildRun(
        ProbeResultShape shape = ProbeResultShape.ScalarSeries,
        int sampleCount = 30,
        bool hasRawSamples = true,
        AdapterKind adapter = AdapterKind.Physical)
    {
        var started = DateTimeOffset.UtcNow.AddMinutes(-1);
        var samples = new List<Sample>();

        for (var i = 0; i < sampleCount; i++)
        {
            var label = shape switch
            {
                ProbeResultShape.PhasedTiming => (i % 3) switch { 0 => "dns", 1 => "connect", _ => "tls" },
                ProbeResultShape.ComparedSeries => i % 2 == 0 ? "192.168.0.1" : "8.8.8.8",
                _ => null,
            };

            var group = shape switch
            {
                ProbeResultShape.PathTrace => (i / 3) + 1,
                ProbeResultShape.PhasedTiming => i / 3,
                _ => (int?)null,
            };

            samples.Add(i % 11 == 10
                ? Sample.Failed(i, started.AddSeconds(i), SampleStatus.Timeout) with { Label = label, Group = group }
                : new Sample
                {
                    Sequence = i,
                    TimestampUtc = started.AddSeconds(i),
                    Value = 0.5 + (i % 7) * 0.3,
                    Status = SampleStatus.Success,
                    Label = label,
                    Group = group,
                    RespondedBy = shape == ProbeResultShape.PathTrace ? $"10.0.0.{(i / 3) + 1}" : null,
                });
        }

        var series = new List<SeriesStatistics> { SeriesBreakdown.WholeRun(samples) };
        if (shape != ProbeResultShape.ScalarSeries)
        {
            series.AddRange(SeriesBreakdown.Compute(shape, samples));
        }

        return new StoredRun
        {
            Summary = new RunSummary
            {
                Id = Guid.NewGuid(),
                Kind = ProbeKind.Icmp,
                ProbeName = "ping",
                Shape = shape,
                TargetDisplay = "шлюз по умолчанию",
                ResolvedAddress = "192.168.200.1",
                StartedUtc = started,
                CompletedUtc = started.AddSeconds(sampleCount),
                State = RunState.Completed,
                SentCount = samples.Count,
                SuccessCount = samples.Count(s => s.IsSuccess),
                MedianMs = 0.8,
                HasRawSamples = hasRawSamples,
            },
            Context = new MeasurementContext
            {
                InterfaceName = "vEthernet (DefaultVirtLan)",
                AdapterKind = adapter,
                InterfaceAddress = "192.168.200.110",
                CalibrationBaselineMs = 0.27,
                ProductVersion = "0.1.0-test",
                Methodology = Methodology.IcmpEcho,
                StartedUtc = started,
            },
            Unit = MeasurementUnit.Milliseconds,
            Target = Target.Gateway("шлюз по умолчанию"),
            Series = series,
            Facts = [ProbeFact.Text("icmp", "Замечание", "проверка формирования отчёта")],
            Samples = hasRawSamples ? samples : [],
            Parameters = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
            {
                ["count"] = sampleCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["interval"] = "1000",
            },
        };
    }

    private static async Task<byte[]> RenderAsync(StoredRun run, bool includeChart = true)
    {
        var renderer = new PdfReportRenderer();

        var report = await renderer.RenderAsync(new ReportRequest
        {
            Run = run,
            Author = "тест",
            IncludeChart = includeChart,
        });

        return report.Content;
    }

    [Fact]
    public async Task Render_ProducesValidPdf()
    {
        var bytes = await RenderAsync(BuildRun());

        _output.WriteLine($"размер отчёта: {bytes.Length / 1024.0:0.0} КБ");

        Assert.True(bytes.Length > 2000, "Отчёт подозрительно мал.");
        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public async Task Render_SuggestsFileNameWithProbeAndDate()
    {
        var renderer = new PdfReportRenderer();
        var report = await renderer.RenderAsync(new ReportRequest { Run = BuildRun() });

        Assert.Equal("pdf", report.FileExtension);
        Assert.StartsWith("storm-ping-", report.SuggestedFileName, StringComparison.Ordinal);
        Assert.EndsWith(".pdf", report.SuggestedFileName, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProbeResultShape.ScalarSeries)]
    [InlineData(ProbeResultShape.PhasedTiming)]
    [InlineData(ProbeResultShape.ComparedSeries)]
    [InlineData(ProbeResultShape.PathTrace)]
    public async Task Render_HandlesEveryResultShape(ProbeResultShape shape)
    {
        // Формы результата несводимы друг к другу, и отчёт обязан переварить любую.
        var bytes = await RenderAsync(BuildRun(shape));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.True(bytes.Length > 2000);
    }

    [Fact]
    public async Task Render_EmbedsCyrillicGlyphs()
    {
        // Самый обидный способ испортить отчёт — собрать его шрифтом без кириллицы:
        // файл откроется, а вместо текста будут пустые прямоугольники.
        var bytes = await RenderAsync(BuildRun());

        var cyrillic = ExtractMappedCyrillic(bytes);

        _output.WriteLine($"кириллических символов в картах шрифтов: {cyrillic.Count}");
        _output.WriteLine($"примеры: {string.Concat(cyrillic.Order().Take(40))}");

        Assert.True(
            cyrillic.Count > 20,
            $"В шрифтах отчёта нашлось лишь {cyrillic.Count} кириллических символов. "
            + "Текст отчёта отрисуется прямоугольниками.");
    }

    [Fact]
    public async Task Render_WorksWithoutRawSamples()
    {
        // Политика хранения удаляет сырые сэмплы, агрегаты остаются.
        // Отчёт по такому прогону обязан формироваться и честно объяснять, почему нет графика.
        var run = BuildRun(hasRawSamples: false);

        var bytes = await RenderAsync(run);

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
        Assert.True(bytes.Length > 2000);
    }

    [Fact]
    public async Task Render_WithoutChart_IsSmaller()
    {
        var run = BuildRun();

        var withChart = await RenderAsync(run);
        var withoutChart = await RenderAsync(run, includeChart: false);

        _output.WriteLine($"с графиком: {withChart.Length / 1024.0:0.0} КБ, без: {withoutChart.Length / 1024.0:0.0} КБ");

        Assert.True(withoutChart.Length < withChart.Length, "Отключение графика не уменьшило файл — график не рисовался?");
    }

    [Fact]
    public async Task Render_TooFewSamples_SkipsChartButStillWorks()
    {
        var run = BuildRun(sampleCount: 1);

        var bytes = await RenderAsync(run);

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(bytes, 0, 5));
    }

    [Fact]
    public async Task Render_IncludesWarningForVirtualAdapter()
    {
        // Предупреждение об окружении обязано попадать в документ: отчёт, снятый через
        // виртуальный коммутатор, без этой оговорки вводит читателя в заблуждение.
        var virtualRun = BuildRun(adapter: AdapterKind.Virtual);
        var physicalRun = BuildRun(adapter: AdapterKind.Physical);

        Assert.NotNull(virtualRun.Context.TimingWarning);
        Assert.Null(physicalRun.Context.TimingWarning);

        var withWarning = await RenderAsync(virtualRun, includeChart: false);
        var without = await RenderAsync(physicalRun, includeChart: false);

        Assert.True(
            withWarning.Length > without.Length,
            "Отчёт с предупреждением не отличается по размеру — предупреждение не попало в документ.");
    }

    /// <summary>
    /// Достаёт из карт шрифтов PDF символы кириллического диапазона.
    /// </summary>
    /// <remarks>
    /// Разбор грубый и намеренно такой: полноценный читатель PDF ради одной проверки —
    /// лишняя зависимость. Достаточно убедиться, что подмножества встроенных шрифтов
    /// содержат отображения в диапазон U+0400…U+04FF.
    /// </remarks>
    private static HashSet<char> ExtractMappedCyrillic(byte[] pdf)
    {
        var text = new StringBuilder(Encoding.Latin1.GetString(pdf));

        foreach (Match match in Regex.Matches(
                     Encoding.Latin1.GetString(pdf),
                     @"stream\r?\n(.*?)endstream",
                     RegexOptions.Singleline))
        {
            var raw = Encoding.Latin1.GetBytes(match.Groups[1].Value);

            try
            {
                using var input = new MemoryStream(raw);
                using var inflate = new ZLibStream(input, CompressionMode.Decompress);
                using var outputStream = new MemoryStream();
                inflate.CopyTo(outputStream);
                text.Append(Encoding.Latin1.GetString(outputStream.ToArray()));
            }
            catch (InvalidDataException)
            {
                // Не все потоки сжаты — изображения и шрифты пропускаем.
            }
        }

        var blob = text.ToString();
        var found = new HashSet<char>();

        foreach (Match section in Regex.Matches(blob, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
        {
            foreach (Match pair in Regex.Matches(section.Groups[1].Value, @"<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4,})>"))
            {
                var code = Convert.ToInt32(pair.Groups[2].Value[..4], 16);

                if (code is >= 0x0400 and <= 0x04FF)
                {
                    found.Add((char)code);
                }
            }
        }

        foreach (Match section in Regex.Matches(blob, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
        {
            foreach (Match range in Regex.Matches(
                         section.Groups[1].Value,
                         @"<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>\s*<([0-9A-Fa-f]{4})>"))
            {
                var from = Convert.ToInt32(range.Groups[1].Value, 16);
                var to = Convert.ToInt32(range.Groups[2].Value, 16);
                var target = Convert.ToInt32(range.Groups[3].Value, 16);

                for (var i = 0; i <= to - from; i++)
                {
                    var code = target + i;

                    if (code is >= 0x0400 and <= 0x04FF)
                    {
                        found.Add((char)code);
                    }
                }
            }
        }

        return found;
    }
}
