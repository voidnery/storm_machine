using StormMachine.Domain.Measurements;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки вокруг требования «условия измерения обязаны быть видны».
/// Появилось не из теории: на стенде виртуальный коммутатор Hyper-V дал p99
/// в 18 раз выше p50 (docs/02-research.md §3.1).
/// </summary>
public sealed class MeasurementContextTests
{
    private static MeasurementContext Context(AdapterKind kind) => new()
    {
        InterfaceName = "test",
        AdapterKind = kind,
        CalibrationBaselineMs = 0.27,
        ProductVersion = "0.1.0",
        Methodology = Methodology.IcmpEcho,
        StartedUtc = DateTimeOffset.UnixEpoch,
    };

    [Theory]
    [InlineData(AdapterKind.Physical)]
    [InlineData(AdapterKind.Wireless)]
    [InlineData(AdapterKind.Loopback)]
    public void TrustworthyAdapters_ProduceNoWarning(AdapterKind kind)
    {
        var context = Context(kind);

        Assert.True(context.IsTimingTrustworthy);
        Assert.Null(context.TimingWarning);
    }

    [Theory]
    [InlineData(AdapterKind.Virtual)]
    [InlineData(AdapterKind.Vpn)]
    [InlineData(AdapterKind.Tunnel)]
    [InlineData(AdapterKind.Unknown)]
    public void UntrustworthyAdapters_WarnOperator(AdapterKind kind)
    {
        var context = Context(kind);

        Assert.False(context.IsTimingTrustworthy);
        Assert.False(string.IsNullOrWhiteSpace(context.TimingWarning));
    }

    [Fact]
    public void Methodology_IsAlwaysPresent()
    {
        // Отчёт без методики — просто картинка (требование C-08a).
        var context = Context(AdapterKind.Physical);

        Assert.NotNull(context.Methodology);
        Assert.Equal("RFC 792", context.Methodology.Reference);
        Assert.Contains("RFC 792", context.Methodology.ToString(), StringComparison.Ordinal);
    }
}
