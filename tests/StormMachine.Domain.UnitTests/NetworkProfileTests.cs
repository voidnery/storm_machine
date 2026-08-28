using StormMachine.Domain.Measurements;
using StormMachine.Domain.Profiles;
using StormMachine.Domain.Reports;
using StormMachine.Domain.Targets;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Профили сетевого окружения.
/// </summary>
/// <remarks>
/// Главное, что здесь закрепляется: продукт узнаёт сеть, но не берётся решать за
/// человека. Смена профиля меняет пороги и состав работающих мониторов, и сделать
/// это по слабой примете значило бы поменять смысл измерений за спиной оператора.
/// </remarks>
public sealed class NetworkProfileTests
{
    private static NetworkProfile Profile(string name, NetworkSignature signature) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Signature = signature,
    };

    private static NetworkSignature Signature(string? mac = null, string? gateway = null, string? subnet = null) =>
        new() { GatewayMac = mac, GatewayAddress = gateway, Subnet = subnet };

    // ------------------------------------------------------------- узнавание

    [Fact(DisplayName = "MAC шлюза узнаёт сеть уверенно")]
    public void GatewayMacIsEnough()
    {
        var office = Profile("офис", Signature(mac: "AA-BB-CC-11-22-33"));

        var guess = ProfileMatcher.Guess([office], Signature(mac: "aa-bb-cc-11-22-33"));

        Assert.NotNull(guess);
        Assert.Equal("офис", guess!.Profile.Name);
        Assert.Contains("MAC шлюза", guess.Because, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Одной подсети для узнавания мало")]
    public void SubnetAloneIsNotEnough()
    {
        // 192.168.1.0/24 стоит у половины сетей мира. Переключить по ней профиль
        // значило бы подменить пороги на основании самого частого совпадения.
        var office = Profile("офис", Signature(subnet: "192.168.1.0/24"));

        Assert.Null(ProfileMatcher.Guess([office], Signature(subnet: "192.168.1.0/24")));
    }

    [Fact(DisplayName = "Адрес шлюза вместе с подсетью узнают сеть")]
    public void GatewayAndSubnetTogether()
    {
        var office = Profile("офис", Signature(gateway: "192.168.1.1", subnet: "192.168.1.0/24"));

        Assert.NotNull(ProfileMatcher.Guess([office], Signature(gateway: "192.168.1.1", subnet: "192.168.1.0/24")));
    }

    [Fact(DisplayName = "Ничья узнаванием не считается")]
    public void TieIsNotAGuess()
    {
        // Два профиля с одинаковым весом означают, что примет не хватает.
        // Выбрать за человека нельзя.
        var first = Profile("офис", Signature(gateway: "192.168.1.1", subnet: "192.168.1.0/24"));
        var second = Profile("филиал", Signature(gateway: "192.168.1.1", subnet: "192.168.1.0/24"));

        Assert.Null(ProfileMatcher.Guess([first, second], Signature(gateway: "192.168.1.1", subnet: "192.168.1.0/24")));
    }

    [Fact(DisplayName = "Более сильная примета побеждает более слабую")]
    public void StrongerSignatureWins()
    {
        var byMac = Profile("объект заказчика", Signature(mac: "AA-BB-CC-11-22-33"));
        var byAddress = Profile("офис", Signature(gateway: "192.168.1.1", subnet: "192.168.1.0/24"));

        var guess = ProfileMatcher.Guess(
            [byAddress, byMac],
            Signature(mac: "AA-BB-CC-11-22-33", gateway: "192.168.1.1", subnet: "192.168.1.0/24"));

        Assert.Equal("объект заказчика", guess!.Profile.Name);
    }

    [Fact(DisplayName = "Профиль без примет в узнавании не участвует")]
    public void ProfileWithoutSignatureIsSkipped()
    {
        var blank = Profile("без примет", new NetworkSignature());

        Assert.Null(ProfileMatcher.Guess([blank], Signature(mac: "AA-BB-CC-11-22-33")));
    }

    [Fact(DisplayName = "Без примет текущей сети узнавать нечего")]
    public void EmptyCurrentSignature()
    {
        var office = Profile("офис", Signature(mac: "AA-BB-CC-11-22-33"));

        Assert.Null(ProfileMatcher.Guess([office], new NetworkSignature()));
    }

    // ------------------------------------------------- профиль как условие

    [Fact(DisplayName = "Смена профиля — тяжёлое расхождение условий")]
    public void ProfileChangeIsSevere()
    {
        // Канал до филиала и канал до шлюза в офисе — разные каналы, а не один
        // канал в разном состоянии. Сравнивать их числа напрямую нельзя.
        var baseline = new Baseline
        {
            Id = Guid.NewGuid(),
            Name = "норма",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Unit = MeasurementUnit.Milliseconds,
            Context = Context("офис"),
            Metrics = [new BaselineMetric("p95", 10, HigherIsBetter: false)],
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 45 },
            Context("объект заказчика"));

        Assert.True(result.HasSevereMismatch);
        Assert.Contains(result.Mismatches, m => m.What == "профиль окружения");
    }

    [Fact(DisplayName = "Появление профиля там, где его не было, тяжёлым не считается")]
    public void ProfileAppearingIsNotSevere()
    {
        // Эталон снят до того, как профили завели. Это не значит, что место
        // изменилось, — значит, что раньше его не записывали.
        var baseline = new Baseline
        {
            Id = Guid.NewGuid(),
            Name = "норма",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Unit = MeasurementUnit.Milliseconds,
            Context = Context(null),
            Metrics = [new BaselineMetric("p95", 10, HigherIsBetter: false)],
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 10 },
            Context("офис"));

        Assert.False(result.HasSevereMismatch);
        Assert.Contains(result.Mismatches, m => m.What == "профиль окружения");
    }

    [Fact(DisplayName = "Тот же профиль расхождения не даёт")]
    public void SameProfileIsNoMismatch()
    {
        var baseline = new Baseline
        {
            Id = Guid.NewGuid(),
            Name = "норма",
            Subject = "ping",
            Target = Target.Ip("192.168.1.1"),
            Unit = MeasurementUnit.Milliseconds,
            Context = Context("офис"),
            Metrics = [new BaselineMetric("p95", 10, HigherIsBetter: false)],
            CapturedUtc = DateTimeOffset.UtcNow,
        };

        var result = BaselineComparer.Compare(
            baseline,
            new Dictionary<string, double> { ["p95"] = 10 },
            Context("офис"));

        Assert.Empty(result.Mismatches);
    }

    // --------------------------------------------------------------- прочее

    [Fact(DisplayName = "Профиль без имени отвергается")]
    public void NameIsRequired()
    {
        var errors = new NetworkProfile { Id = Guid.NewGuid(), Name = "  " }.Validate();

        Assert.Single(errors);
    }

    [Fact(DisplayName = "Приметы описываются человеческим языком")]
    public void SignatureIsDescribed()
    {
        Assert.Equal("примет нет", new NetworkSignature().Describe());
        Assert.Contains("шлюз AA-BB", Signature(mac: "AA-BB-CC-11-22-33").Describe(), StringComparison.Ordinal);
        Assert.Contains("192.168.1.0/24", Signature(subnet: "192.168.1.0/24").Describe(), StringComparison.Ordinal);
    }

    private static MeasurementContext Context(string? profile) => new()
    {
        InterfaceName = "Ethernet",
        AdapterKind = AdapterKind.Physical,
        CalibrationBaselineMs = 0.2,
        ProductVersion = "0.1.0",
        Methodology = Methodology.IcmpEcho,
        Profile = profile,
        StartedUtc = DateTimeOffset.UtcNow,
    };
}
