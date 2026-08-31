using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Классификатор устройств: догадка не выдаётся за наблюдение.
/// </summary>
/// <remarks>
/// Появился в И-24: устройства раскладываются по категориям. Главные свойства
/// закрепляются здесь: догадка помечена вопросом, правка оператора её перекрывает,
/// а без уверенных признаков категория честно пуста.
/// </remarks>
public sealed class DeviceClassifierTests
{
    private static readonly DateTimeOffset Noon = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "Виртуальный MAC резервирования — маршрутизатор, и это догадка")]
    public void VrrpMac_IsGuessedRouter()
    {
        var device = Build(Evidence.Of(EvidenceSource.ArpRequest, EvidenceKind.MacAddress, "00-00-5E-00-01-C8", Noon));

        Assert.Equal("маршрутизатор", device.Role);
        Assert.True(device.RoleIsGuessed);
        Assert.Equal("маршрутизатор?", device.RoleDisplay);
    }

    [Fact(DisplayName = "Порт печати — принтер")]
    public void PrinterPort_IsGuessedPrinter()
    {
        var device = Build(Evidence.Of(EvidenceSource.TcpConnect, EvidenceKind.OpenPort, "9100", Noon));

        Assert.Equal("принтер", device.Role);
        Assert.True(device.RoleIsGuessed);
    }

    [Fact(DisplayName = "Правка оператора перекрывает догадку и не помечается вопросом")]
    public void OperatorRole_OverridesGuess()
    {
        var device = Build(
            Evidence.Of(EvidenceSource.TcpConnect, EvidenceKind.OpenPort, "9100", Noon),
            Evidence.Of(EvidenceSource.Manual, EvidenceKind.Role, "сервер", Noon));

        Assert.Equal("сервер", device.Role);
        Assert.False(device.RoleIsGuessed);
        Assert.Equal("сервер", device.RoleDisplay);
    }

    [Fact(DisplayName = "Без уверенных признаков категория честно пуста")]
    public void NoStrongSignals_NoGuess()
    {
        // Вендор TP-Link делает всё подряд — по нему роль не угадывается.
        var device = Build(Evidence.Of(EvidenceSource.Oui, EvidenceKind.Vendor, "TP-Link Systems Inc.", Noon));

        Assert.Null(device.Role);
        Assert.Null(device.RoleDisplay);
    }

    private static Device Build(params Evidence[] evidence) =>
        Device.FromEvidence("192.168.1.50", evidence, Noon, Noon, isOnline: true);
}
