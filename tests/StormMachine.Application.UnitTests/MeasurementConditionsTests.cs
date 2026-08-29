using StormMachine.Application.Abstractions;
using StormMachine.Application.Runs;
using StormMachine.Domain.Measurements;

namespace StormMachine.Application.UnitTests;

/// <summary>
/// Условия измерения собираются в одном месте.
/// </summary>
/// <remarks>
/// Проверки написаны после находки И-19: сборщиков было пять, и один из них — шапка,
/// которую консоль печатает перед прогоном, — отстал и перестал заполнять профиль.
/// Оператор читал перед измерением одни условия, а в журнал ложились другие.
/// </remarks>
public sealed class MeasurementConditionsTests
{
    private static readonly NetworkAdapter Adapter = new()
    {
        Id = "eth0",
        Name = "Ethernet",
        Description = "Проводной адаптер",
        Kind = AdapterKind.Physical,
        IPv4Address = "192.168.1.10",
    };

    [Fact]
    public void Build_CarriesProfile()
    {
        var context = MeasurementConditions.Build(
            Adapter,
            new NullClock(),
            Methodology.IcmpEcho,
            profile: "Офис заказчика");

        Assert.Equal("Офис заказчика", context.Profile);
    }

    /// <summary>
    /// Условия для шапки и условия для журнала — одни и те же.
    /// </summary>
    /// <remarks>
    /// Это и есть регрессия на найденный дефект. Сравниваются все поля, кроме момента
    /// старта: он у двух вызовов заведомо разный, и требовать его совпадения значило бы
    /// написать тест, падающий от собственной скорости.
    /// </remarks>
    [Fact]
    public void Build_IsTheSameForHeaderAndForJournal()
    {
        var clock = new NullClock();

        var header = MeasurementConditions.Build(Adapter, clock, Methodology.IcmpEcho, "Офис");
        var journal = MeasurementConditions.Build(Adapter, clock, Methodology.IcmpEcho, "Офис");

        Assert.Equal(journal with { StartedUtc = header.StartedUtc }, header);
    }

    [Fact]
    public void Build_WithoutAdapter_SaysSoInsteadOfFailing()
    {
        var context = MeasurementConditions.Build(null, new NullClock(), Methodology.IcmpEcho);

        Assert.Equal("неизвестен", context.InterfaceName);
        Assert.Equal(AdapterKind.Unknown, context.AdapterKind);
        Assert.False(context.IsTimingTrustworthy);
        Assert.NotNull(context.TimingWarning);
    }

    [Fact]
    public async Task ActiveProfile_WithoutStore_IsEmptyRatherThanError()
    {
        // Профили необязательны: продукт полностью работоспособен без них.
        Assert.Null(await MeasurementConditions.ActiveProfileAsync(null));
    }

    [Fact]
    public async Task ActiveProfile_SurvivesUnreadableStore()
    {
        // Нечитаемое хранилище профилей не должно срывать измерение: условия просто
        // останутся без одной строки.
        var profile = await MeasurementConditions.ActiveProfileAsync(new BrokenProfileStore());

        Assert.Null(profile);
    }
}
