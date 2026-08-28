using StormMachine.Domain.Discovery;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Проверки порядка адресов.
/// </summary>
/// <remarks>
/// Мелочь, стоившая настоящего дефекта. Строковое сравнение ставит
/// <c>192.168.200.254</c> раньше <c>192.168.200.3</c>, потому что знак «2» меньше «3».
/// Для сортировки списка это неопрятно, а для выбора основного адреса устройства —
/// прямая ошибка: у объединённого узла главным оказывался адрес, который человек
/// назвал бы последним.
/// </remarks>
public sealed class IpAddressOrderTests
{
    private static readonly string[] Unsorted =
        ["192.168.200.254", "192.168.200.3", "192.168.200.10", "192.168.200.9"];

    private static readonly string[] Expected =
        ["192.168.200.3", "192.168.200.9", "192.168.200.10", "192.168.200.254"];

    [Fact]
    public void SortsNumericallyNotAlphabetically() =>
        Assert.Equal(Expected, Unsorted.OrderBy(IpAddressOrder.Of).ToArray());

    [Fact]
    public void Lowest_PicksTheNumericallySmallest() =>
        Assert.Equal("192.168.200.3", IpAddressOrder.Lowest(Unsorted));

    [Fact]
    public void Lowest_OfEmptyIsNull() => Assert.Null(IpAddressOrder.Lowest([]));

    [Fact]
    public void Lowest_OfSingleIsThatOne() =>
        Assert.Equal("10.0.0.1", IpAddressOrder.Lowest(["10.0.0.1"]));

    [Fact]
    public void UnparseableGoesLast()
    {
        // Неразобранный адрес не должен становиться основным, вытеснив настоящий.
        Assert.Equal("10.0.0.1", IpAddressOrder.Lowest(["не адрес", "10.0.0.1"]));
        Assert.Equal(uint.MaxValue, IpAddressOrder.Of("не адрес"));
        Assert.Equal(uint.MaxValue, IpAddressOrder.Of(null));
    }

    [Fact]
    public void OnlyUnparseable_StillYieldsSomethingAndAlwaysTheSame()
    {
        // Устройство не должно остаться вовсе без основного адреса из-за того,
        // что его адрес не разобрался. И выбор обязан быть одинаковым при каждом
        // пересчёте — иначе различия между сканами покажут перестановку.
        var straight = IpAddressOrder.Lowest(["первый", "второй"]);
        var reversed = IpAddressOrder.Lowest(["второй", "первый"]);

        Assert.NotNull(straight);
        Assert.Equal(straight, reversed);
    }

    [Fact]
    public void IPv6GoesLast()
    {
        // Продукт меряет IPv4; адрес IPv6 в наборе не должен вытеснять его
        // с места основного.
        Assert.Equal("10.0.0.1", IpAddressOrder.Lowest(["::1", "10.0.0.1"]));
    }

    [Fact]
    public void BoundaryAddresses_KeepTheirOrder()
    {
        Assert.True(IpAddressOrder.Of("0.0.0.0") < IpAddressOrder.Of("1.0.0.0"));
        Assert.True(IpAddressOrder.Of("255.255.255.254") < IpAddressOrder.Of("255.255.255.255"));
        Assert.True(IpAddressOrder.Of("9.255.255.255") < IpAddressOrder.Of("10.0.0.0"));
    }
}
