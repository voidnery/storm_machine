using StormMachine.Domain.Targets;

namespace StormMachine.Domain.UnitTests;

public sealed class TargetTests
{
    [Theory]
    [InlineData("192.168.1.1", TargetKind.IpAddress)]
    [InlineData("10.0.0.254", TargetKind.IpAddress)]
    [InlineData("example.com", TargetKind.Hostname)]
    [InlineData("server01", TargetKind.Hostname)]
    [InlineData("https://example.com/health", TargetKind.Url)]
    [InlineData("http://10.0.0.1:8080", TargetKind.Url)]
    [InlineData("192.168.1.0/24", TargetKind.Subnet)]
    public void Parse_DetectsKind(string raw, TargetKind expected)
    {
        var target = Target.Parse(raw);

        Assert.Equal(expected, target.Kind);
        Assert.Equal(raw, target.Value);
    }

    [Fact]
    public void Parse_TrimsWhitespace()
    {
        var target = Target.Parse("  192.168.1.1  ");

        Assert.Equal("192.168.1.1", target.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_RejectsEmpty(string raw) =>
        Assert.Throws<ArgumentException>(() => Target.Parse(raw));

    [Fact]
    public void DisplayName_PrefersLabel()
    {
        var labelled = Target.Ip("192.168.1.1", "Шлюз офиса");
        var plain = Target.Ip("192.168.1.1");

        Assert.Equal("Шлюз офиса", labelled.DisplayName);
        Assert.Equal("192.168.1.1", plain.DisplayName);
    }

    [Fact]
    public void DynamicTargets_StoreIntentNotAddress()
    {
        // Пресет «пинговать шлюз» должен оставаться осмысленным в любой сети,
        // поэтому цель хранит намерение, а адрес разрешается при выполнении.
        var gateway = Target.Gateway();

        Assert.Equal(TargetKind.DefaultGateway, gateway.Kind);
        Assert.False(gateway.Value.Contains('.', StringComparison.Ordinal));
    }

    [Fact]
    public void Equality_IsByValue()
    {
        Assert.Equal(Target.Ip("192.168.1.1"), Target.Ip("192.168.1.1"));
        Assert.NotEqual(Target.Ip("192.168.1.1"), Target.Ip("192.168.1.2"));
    }
}
