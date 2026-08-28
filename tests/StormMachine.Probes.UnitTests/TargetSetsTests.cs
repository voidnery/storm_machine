using StormMachine.Application.Abstractions;
using StormMachine.Application.Scenarios;
using StormMachine.Domain.Measurements;

namespace StormMachine.Probes.UnitTests;

/// <summary>
/// Проверки наборов целей.
/// </summary>
/// <remarks>
/// Набор «своё» вычисляется, а не хранится: шлюз и резолверы берутся из текущего
/// окружения. Записать их константами значило бы предложить оператору проверять
/// чужую сеть — и тест закрепляет именно это, подставляя окружение вручную.
/// </remarks>
public sealed class TargetSetsTests
{
    private sealed class FakeEnvironment(NetworkAdapter? adapter) : INetworkEnvironment
    {
        public IReadOnlyList<NetworkAdapter> GetAdapters() => adapter is null ? [] : [adapter];

        public NetworkAdapter? GetPrimaryAdapter() => adapter;

        public bool IsElevated => false;
    }

    private static FakeEnvironment Environment(params string[] servers) =>
        new FakeEnvironment(new NetworkAdapter
        {
            Id = "test",
            Name = "Ethernet",
            Description = "test",
            Kind = AdapterKind.Physical,
            IPv4Address = "10.0.0.5",
            PrefixLength = 24,
            Gateways = servers.Length > 0 ? [servers[0]] : [],
            DnsServers = [.. servers.Skip(1)],
            IsUp = true,
        });

    [Fact]
    public void Own_TakesGatewayAndResolversFromEnvironment()
    {
        var set = TargetSets.Resolve("своё", Environment("10.0.0.1", "10.0.0.2", "10.0.0.3"));

        Assert.Equal(["10.0.0.1", "10.0.0.2", "10.0.0.3"], set.Targets);
        Assert.Contains("Ethernet", set.Origin, StringComparison.Ordinal);
    }

    [Fact]
    public void Own_WithoutAdapter_IsEmptyAndSaysWhy()
    {
        // Пустой набор — не повод для исключения: отсутствие адаптера с маршрутом
        // по умолчанию само по себе диагноз, и оператор должен его увидеть.
        var set = TargetSets.Resolve("своё", new FakeEnvironment(null));

        Assert.Empty(set.Targets);
        Assert.Contains("маршрутом по умолчанию", set.Origin, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltInSets_AreListed()
    {
        var keys = TargetSets.All.Select(t => t.Key).ToList();

        Assert.Contains("публичные", keys);
        Assert.Contains("резолверы", keys);
    }

    [Fact]
    public void InlineList_IsSplitAndDeduplicated()
    {
        var set = TargetSets.Resolve("a.com, b.com ,a.com", Environment("10.0.0.1"));

        Assert.Equal(["a.com", "b.com"], set.Targets);
        Assert.Equal("указано в команде", set.Origin);
    }

    [Fact]
    public void SingleTarget_KeepsItsName()
    {
        var set = TargetSets.Resolve("example.com", Environment("10.0.0.1"));

        Assert.Equal("example.com", Assert.Single(set.Targets));
        Assert.Equal("example.com", set.Title);
    }

    [Fact]
    public void FromLines_DropsCommentsAndBlanks()
    {
        var set = TargetSets.FromLines(
            "цели",
            "файл цели.txt",
            ["# шлюзы филиалов", "10.0.4.7  # Тверь", string.Empty, "  10.0.5.7", "10.0.4.7"]);

        Assert.Equal(["10.0.4.7", "10.0.5.7"], set.Targets);
    }

    [Fact]
    public void FromLines_EmptyListIsAnError() =>
        Assert.Throws<ArgumentException>(() => TargetSets.FromLines("пусто", "файл", ["# только комментарий"]));

    [Fact]
    public void Blank_IsRejected() =>
        Assert.Throws<ArgumentException>(() => TargetSets.Resolve("  ", Environment("10.0.0.1")));
}
