using StormMachine.Discovery;

namespace StormMachine.Discovery.UnitTests;

/// <summary>
/// Проверки справочника вендоров.
/// </summary>
/// <remarks>
/// База встроена в сборку, и проверять её нужно именно встроенной: ошибка в имени
/// ресурса или в настройке проекта не ломает сборку, а тихо оставляет столбец вендоров
/// пустым — то есть выглядит как «в этой сети незнакомое оборудование».
/// </remarks>
public sealed class OuiCatalogTests
{
    private readonly OuiCatalog _catalog = new();

    [Fact]
    public void EmbeddedDatabase_IsPresentAndLarge()
    {
        // Порог грубый намеренно: он ловит не изменение реестра, а его отсутствие.
        Assert.True(
            _catalog.Count > 40_000,
            $"В базе {_catalog.Count} записей — похоже, ресурс не попал в сборку.");
    }

    [Theory]
    [InlineData("00-15-5D-C8-B1-09", "Microsoft")]
    [InlineData("00:11:32:E4:70:AA", "Synology")]
    [InlineData("D8-43-AE-5F-BF-B4", "Micro-Star")]
    [InlineData("50a6d88ccff2", "Apple")]
    public void KnownPrefixes_ResolveToTheirVendors(string mac, string expected)
    {
        var vendor = _catalog.Lookup(mac);

        Assert.NotNull(vendor);
        Assert.Contains(expected, vendor, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnyWritingOfTheSameAddress_GivesTheSameAnswer()
    {
        // Написаний у MAC много: через дефис, двоеточие, точку, слитно. Требовать одно
        // из них — верный способ получить «вендор не найден» на ровном месте.
        var expected = _catalog.Lookup("00-15-5D-C8-B1-09");

        Assert.NotNull(expected);
        Assert.Equal(expected, _catalog.Lookup("00:15:5d:c8:b1:09"));
        Assert.Equal(expected, _catalog.Lookup("0015.5dc8.b109"));
        Assert.Equal(expected, _catalog.Lookup("00155DC8B109"));
    }

    [Fact]
    public void PrefixAloneIsEnough() =>
        Assert.NotNull(_catalog.Lookup("00-15-5D"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не адрес")]
    [InlineData("00-15")]
    public void Nonsense_ReturnsNull(string mac) => Assert.Null(_catalog.Lookup(mac));

    [Fact]
    public void LocallyAdministeredAddress_HasNoVendor()
    {
        // У случайного адреса телефона и виртуального адаптера вендора не существует:
        // выдумать его нельзя, и правильный ответ здесь — «нет».
        Assert.Null(_catalog.Lookup("02-00-00-00-00-01"));
    }

    [Fact]
    public void Normalize_KeepsOnlyHexDigits()
    {
        Assert.Equal("00155DC8B109", OuiCatalog.Normalize("00-15-5d:c8.b1 09"));
        Assert.Equal(string.Empty, OuiCatalog.Normalize("—"));
    }

    [Fact]
    public void Describe_NamesTheSource() =>
        Assert.Contains("IEEE", _catalog.Describe(), StringComparison.Ordinal);
}
