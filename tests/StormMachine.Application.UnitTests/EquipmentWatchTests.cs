using StormMachine.Application.Abstractions;
using StormMachine.Application.Monitors;
using StormMachine.Domain.Monitors;
using StormMachine.Domain.Results;
using StormMachine.Domain.Scenarios;
using StormMachine.Domain.Targets;
using Monitor = StormMachine.Domain.Monitors.Monitor;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Наблюдение за самим оборудованием.
/// </summary>
/// <remarks>
/// Появилось в И-21: до него монитор наблюдал сеть только снаружи — со своей машины
/// и своими пакетами. Счётчики порта видят то, чего проба увидеть не может: растущие
/// ошибки означают умирающий патч-корд, а снаружи это выглядит просто как «чуть
/// медленнее», потому что повторная передача TCP вытягивает потери и прячет причину.
/// </remarks>
public sealed class EquipmentWatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    private static Monitor Watch(MonitorKind kind, params Threshold[] thresholds) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Порт аплинка",
        Kind = kind,
        Subject = "10.0.0.1",
        Target = Target.Ip("10.0.0.1"),
        Schedule = Schedule.Every(TimeSpan.FromMinutes(5)),
        Thresholds = thresholds,
    };

    private static PortLoadPoint Point(DateTimeOffset at, double inBps = 100_000_000, long faults = 0) => new()
    {
        Device = "10.0.0.1",
        IfIndex = 1,
        IfName = "GigabitEthernet0/1",
        AtUtc = at,
        Interval = TimeSpan.FromSeconds(10),
        InBitsPerSecond = inBps,
        OutBitsPerSecond = 0,
        SpeedBitsPerSecond = 1_000_000_000,
        InErrors = faults,
    };

    // -------------------------------------------------------------------- порт

    /// <summary>
    /// Прекратившийся опрос — это «не знаем», а не «норма».
    /// </summary>
    /// <remarks>
    /// Самое важное свойство этого монитора. История, переставшая пополняться,
    /// не означает, что с портом всё хорошо: она означает, что мы больше не смотрим.
    /// Молчание монитора здесь было бы прямой ложью, а зачёт в доступность — завышением.
    /// </remarks>
    [Fact]
    public void StaleHistory_IsNotSilence()
    {
        var check = EquipmentWatch.EvaluatePort(
            Watch(MonitorKind.PortLoad),
            [Point(Now.AddHours(-5))],
            Now,
            TimeSpan.FromHours(1));

        Assert.Equal(VerdictLevel.Unknown, check.Level);
        Assert.Equal(CheckKind.Missed, check.Kind);
        Assert.Contains("опрос прекратился", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void NoHistoryAtAll_SaysSoPlainly()
    {
        var check = EquipmentWatch.EvaluatePort(Watch(MonitorKind.PortLoad), [], Now, TimeSpan.FromHours(1));

        Assert.Equal(CheckKind.Missed, check.Kind);
        Assert.Contains("опрос не выполнялся", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void FreshHistoryWithinThreshold_Passes()
    {
        var check = EquipmentWatch.EvaluatePort(
            Watch(MonitorKind.PortLoad, new Threshold
            {
                Metric = EquipmentWatch.LoadMetric,
                Comparison = Comparison.AtMost,
                Value = 80,
            }),
            [Point(Now.AddMinutes(-2))],
            Now,
            TimeSpan.FromHours(1));

        Assert.Equal(VerdictLevel.Pass, check.Level);
        Assert.Equal(CheckKind.Measured, check.Kind);
        Assert.Equal(10.0, check.Value!.Value, 3);
    }

    [Fact]
    public void LoadOverThreshold_Fails()
    {
        var check = EquipmentWatch.EvaluatePort(
            Watch(MonitorKind.PortLoad, new Threshold
            {
                Metric = EquipmentWatch.LoadMetric,
                Comparison = Comparison.AtMost,
                Value = 50,
            }),
            [Point(Now.AddMinutes(-1), inBps: 900_000_000)],
            Now,
            TimeSpan.FromHours(1));

        Assert.Equal(VerdictLevel.Fail, check.Level);
        Assert.Contains("нарушен порог", check.Summary, StringComparison.Ordinal);
        Assert.Equal(90.0, check.Value!.Value, 3);
    }

    /// <summary>
    /// Ошибки считаются за всё наблюдаемое окно, а не по последней точке.
    /// </summary>
    /// <remarks>
    /// Разовая ошибка бывает у любого порта; вопрос в том, прибавляются ли они.
    /// Судить по последнему наблюдению значило бы пропустить порт, который сыплет
    /// ошибки понемногу и постоянно, — а это и есть умирающий кабель.
    /// </remarks>
    [Fact]
    public void FaultsAreSummedOverTheWindow()
    {
        var check = EquipmentWatch.EvaluatePort(
            Watch(MonitorKind.PortLoad, new Threshold
            {
                Metric = EquipmentWatch.FaultsMetric,
                Comparison = Comparison.AtMost,
                Value = 5,
            }),
            [
                Point(Now.AddMinutes(-30), faults: 3),
                Point(Now.AddMinutes(-20), faults: 3),
                Point(Now.AddMinutes(-10), faults: 0),
            ],
            Now,
            TimeSpan.FromHours(1));

        Assert.Equal(VerdictLevel.Fail, check.Level);
        Assert.Equal(6, check.Value!.Value);
    }

    /// <summary>Без порогов монитор собирает историю и ни о чём не судит.</summary>
    [Fact]
    public void WithoutThresholds_TheWatchStaysSilent()
    {
        var check = EquipmentWatch.EvaluatePort(
            Watch(MonitorKind.PortLoad),
            [Point(Now.AddMinutes(-1))],
            Now,
            TimeSpan.FromHours(1));

        Assert.Equal(VerdictLevel.Unknown, check.Level);
        Assert.Equal(CheckKind.Measured, check.Kind);
    }

    /// <summary>Без известной скорости порта проценты не выдумываются.</summary>
    [Fact]
    public void WithoutPortSpeed_PercentIsNotInvented()
    {
        var check = EquipmentWatch.EvaluatePort(
            Watch(MonitorKind.PortLoad),
            [Point(Now.AddMinutes(-1)) with { SpeedBitsPerSecond = 0 }],
            Now,
            TimeSpan.FromHours(1));

        Assert.Contains("скорость порта неизвестна", check.Summary, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- DHCP

    private static HeardDhcpServer Server(
        string address,
        string gateway,
        DateTimeOffset firstSeen) => new()
    {
        ServerAddress = address,
        OfferedGateway = gateway,
        FirstSeenUtc = firstSeen,
        LastSeenUtc = Now,
        Sightings = 5,
    };

    /// <summary>
    /// Два сервера сами по себе — не отказ.
    /// </summary>
    /// <remarks>
    /// Решение И-18, которое здесь обязано сохраниться: две законные пары DHCP в одном
    /// домене встречаются не реже подставного сервера, и различить их может только тот,
    /// кто знает свою сеть. Монитор, поднимающий тревогу на само число серверов,
    /// научил бы оператора не обращать на себя внимания.
    /// </remarks>
    [Fact]
    public void TwoKnownServers_AreNotAnIncident()
    {
        var check = EquipmentWatch.EvaluateDhcp(
            Watch(MonitorKind.Dhcp),
            [
                Server("192.168.1.1", "192.168.1.1", Now.AddDays(-100)),
                Server("192.168.1.2", "192.168.1.1", Now.AddDays(-100)),
            ],
            ["192.168.1.1"],
            Now,
            TimeSpan.FromDays(1));

        Assert.Equal(VerdictLevel.Pass, check.Level);
        Assert.Equal(2, check.Value!.Value);
    }

    /// <summary>Незнакомый объявленный шлюз — единственное, что продукт утверждает сам.</summary>
    [Fact]
    public void ServerOfferingAnUnknownGateway_Fails()
    {
        var check = EquipmentWatch.EvaluateDhcp(
            Watch(MonitorKind.Dhcp),
            [
                Server("192.168.1.1", "192.168.1.1", Now.AddDays(-100)),
                Server("192.168.1.99", "10.10.10.1", Now.AddDays(-100)),
            ],
            ["192.168.1.1"],
            Now,
            TimeSpan.FromDays(1));

        Assert.Equal(VerdictLevel.Fail, check.Level);
        Assert.Contains("192.168.1.99", check.Summary, StringComparison.Ordinal);
        Assert.Contains("которого мы не знаем", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>
    /// Новый сервер со знакомым шлюзом — предупреждение, а не отказ.
    /// </summary>
    /// <remarks>
    /// Разница существенная. Сервер, объявляющий чужой шлюз, — проверяемое утверждение
    /// о неправильном; сервер, которого вчера не было, — повод посмотреть. Уравнять их
    /// значило бы поднимать отказ на каждое законное расширение сети.
    /// </remarks>
    [Fact]
    public void NewServerWithKnownGateway_IsAWarningNotAFailure()
    {
        var check = EquipmentWatch.EvaluateDhcp(
            Watch(MonitorKind.Dhcp),
            [
                Server("192.168.1.1", "192.168.1.1", Now.AddDays(-100)),
                Server("192.168.1.2", "192.168.1.1", Now.AddHours(-3)),
            ],
            ["192.168.1.1"],
            Now,
            TimeSpan.FromDays(1));

        Assert.Equal(VerdictLevel.Warn, check.Level);
        Assert.Contains("192.168.1.2", check.Summary, StringComparison.Ordinal);
        Assert.Contains("раньше не слышали", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>Чужой шлюз важнее новизны: если есть и то и другое, говорится о худшем.</summary>
    [Fact]
    public void UnknownGatewayOutranksNovelty()
    {
        var check = EquipmentWatch.EvaluateDhcp(
            Watch(MonitorKind.Dhcp),
            [Server("192.168.1.99", "10.10.10.1", Now.AddHours(-1))],
            ["192.168.1.1"],
            Now,
            TimeSpan.FromDays(1));

        Assert.Equal(VerdictLevel.Fail, check.Level);
    }

    [Fact]
    public void NothingHeard_IsNotSilenceEither()
    {
        var check = EquipmentWatch.EvaluateDhcp(
            Watch(MonitorKind.Dhcp),
            [],
            ["192.168.1.1"],
            Now,
            TimeSpan.FromDays(1));

        Assert.Equal(CheckKind.Missed, check.Kind);
        Assert.Contains("прослушивание не выполнялось", check.Summary, StringComparison.Ordinal);
    }
}
