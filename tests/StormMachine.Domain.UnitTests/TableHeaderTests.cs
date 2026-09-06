using StormMachine.Domain.Measurements;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Единица стоит в шапке колонки, а не под таблицей.
/// </summary>
/// <remarks>
/// Замечание оператора: «просто „мин 0.476“, и непонятно, в чём это исчисляется».
/// Подпись под таблицей стояла в другом месте экрана, чем число, а в отчёте
/// на бумаге и вовсе оставалась на другой странице.
/// </remarks>
public sealed class TableHeaderTests
{
    [Fact(DisplayName = "Шапка времени называет миллисекунды")]
    public void TimeColumns_CarryMilliseconds()
    {
        Assert.Equal("мин, мс", TableHeaders.Min);
        Assert.Equal("медиана, мс", TableHeaders.Median);
        Assert.Equal("макс, мс", TableHeaders.Max);
        Assert.Equal("джиттер, мс", TableHeaders.Jitter);
    }

    [Fact(DisplayName = "Потери — в процентах, число проб — без единицы")]
    public void LossIsPercent_AndCountsHaveNoUnit()
    {
        Assert.Equal("потери, %", TableHeaders.Loss);

        // Счёт — не величина: «проб, шт» было бы шумом.
        Assert.Equal("проб", TableHeaders.Sent);
    }

    [Fact(DisplayName = "Единицу приносит проба: у скорости мегабиты")]
    public void SpeedColumns_CarryMegabits()
    {
        Assert.Equal("медиана, Мбит/с", Units.Column("медиана", MeasurementUnit.MegabitsPerSecond));

        // У счёта единицы нет, и приписывать «, шт» не за чем: «медиана, » хуже,
        // чем «медиана».
        Assert.Equal("медиана", Units.Column("медиана", MeasurementUnit.Count));
    }
}
