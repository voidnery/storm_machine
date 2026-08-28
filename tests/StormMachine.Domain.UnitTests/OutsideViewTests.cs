using StormMachine.Domain.Outside;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки того, что продукт говорит о взгляде снаружи.
/// </summary>
/// <remarks>
/// Проверяется не форматирование, а адресат вывода. «IPv6 не работает» — бесполезная
/// строка: нет адреса — вопрос к провайдеру, нет AAAA — вопрос к владельцу имени,
/// есть и то и другое, но нет связности — вопрос к маршрутизации. Это три разных
/// разговора с тремя разными людьми, и тесты закрепляют, что продукт их различает.
/// </remarks>
public sealed class OutsideViewTests
{
    private static Ipv6Readiness Readiness(bool address, bool aaaa, bool reachable, string? failure = null) => new()
    {
        HasGlobalAddress = address,
        GlobalAddress = address ? "2a06:98c1:3122:8000::" : null,
        ResolvesAaaa = aaaa,
        AaaaAddress = aaaa ? "2606:2800:220:1:248:1893:25c8:1946" : null,
        Reachable = reachable,
        Failure = failure,
    };

    [Fact]
    public void Ipv6_NoAddress_PointsAtProvider()
    {
        var readiness = Readiness(address: false, aaaa: true, reachable: false);

        Assert.False(readiness.IsReady);
        Assert.Contains("провайдер", readiness.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ipv6_NoAaaa_PointsAtTheTarget()
    {
        var readiness = Readiness(address: true, aaaa: false, reachable: false);

        Assert.Contains("AAAA", readiness.Describe(), StringComparison.Ordinal);
        Assert.DoesNotContain("провайдер", readiness.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ipv6_NoConnection_NamesTheFailure()
    {
        var readiness = Readiness(address: true, aaaa: true, reachable: false, failure: "TimedOut");

        Assert.Contains("TimedOut", readiness.Describe(), StringComparison.Ordinal);
    }

    [Fact]
    public void Ipv6_AllThreeConditions_IsReady() =>
        Assert.True(Readiness(address: true, aaaa: true, reachable: true).IsReady);

    [Fact]
    public void Nat_EndpointIndependent_SaysDirectConnectionUsuallyWorks()
    {
        var view = new OutsideView { Mapping = NatMapping.EndpointIndependent };

        Assert.True(view.IsBehindNat);
        Assert.Contains("Прямое соединение между узлами обычно устанавливается",
            view.DescribeMapping(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nat_AddressDependent_SaysRelayIsNeeded()
    {
        var view = new OutsideView { Mapping = NatMapping.AddressDependent };

        Assert.Contains("TURN", view.DescribeMapping(), StringComparison.Ordinal);
    }

    [Fact]
    public void Nat_SingleServer_RefusesToConclude()
    {
        // Один ответ показывает трансляцию, но не её поведение. Назвать её при этом
        // «симметричной» или «конусом» значило бы выдать догадку за измерение.
        var view = new OutsideView { Mapping = NatMapping.Undetermined };

        Assert.Contains("не определено", view.DescribeMapping(), StringComparison.Ordinal);
        Assert.True(view.IsBehindNat);
    }

    [Fact]
    public void Nat_None_IsNotBehindNat()
    {
        var view = new OutsideView { Mapping = NatMapping.None };

        Assert.False(view.IsBehindNat);
    }

    [Fact]
    public void FilteringIsAlwaysDeclaredUntested() =>
        Assert.Contains("RFC 5780", OutsideView.FilteringNotTested, StringComparison.Ordinal);
}
