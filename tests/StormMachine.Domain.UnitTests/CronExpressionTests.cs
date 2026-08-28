using StormMachine.Domain.Monitors;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Разбор и вычисление cron.
/// </summary>
/// <remarks>
/// Проверяется поведение, а не реализация: те же ответы обязана давать любая
/// правильная реализация cron. Поэтому здесь есть и случаи, которые удивляют людей —
/// сложение дня месяца с днём недели через ИЛИ и «30 февраля».
/// </remarks>
public sealed class CronExpressionTests
{
    private static DateTime At(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Unspecified);

    [Fact(DisplayName = "Каждые пять минут даёт следующую пятиминутку")]
    public void EveryFiveMinutes()
    {
        var cron = CronExpression.Parse("*/5 * * * *");

        Assert.Equal(At(2026, 3, 10, 12, 5), cron.NextOccurrence(At(2026, 3, 10, 12, 0)));
        Assert.Equal(At(2026, 3, 10, 12, 5), cron.NextOccurrence(At(2026, 3, 10, 12, 3)));
        Assert.Equal(At(2026, 3, 10, 13, 0), cron.NextOccurrence(At(2026, 3, 10, 12, 57)));
    }

    [Fact(DisplayName = "Совпадение ищется строго после указанного момента")]
    public void StrictlyAfter()
    {
        var cron = CronExpression.Parse("0 3 * * *");

        // Иначе монитор, пересчитавший срок сразу после срабатывания, получил бы
        // тот же момент и закрутился бы на месте.
        Assert.Equal(At(2026, 3, 11, 3), cron.NextOccurrence(At(2026, 3, 10, 3)));
    }

    [Fact(DisplayName = "Секунды отбрасываются, а не округляются вверх")]
    public void SecondsIgnored()
    {
        var cron = CronExpression.Parse("* * * * *");
        var moment = new DateTime(2026, 3, 10, 12, 0, 45, DateTimeKind.Unspecified);

        Assert.Equal(At(2026, 3, 10, 12, 1), cron.NextOccurrence(moment));
    }

    [Fact(DisplayName = "День месяца и день недели складываются через ИЛИ")]
    public void DayOfMonthOrDayOfWeek()
    {
        // Поведение классического cron: сработает и тринадцатого, и по пятницам.
        var cron = CronExpression.Parse("0 3 13 * 5");

        Assert.Equal(At(2026, 3, 13, 3), cron.NextOccurrence(At(2026, 3, 12, 4)));
        Assert.Equal(At(2026, 3, 20, 3), cron.NextOccurrence(At(2026, 3, 13, 4)));
    }

    [Fact(DisplayName = "Ограничен только день недели — день месяца не сужает")]
    public void OnlyDayOfWeek()
    {
        var cron = CronExpression.Parse("30 2 * * 1");
        var next = cron.NextOccurrence(At(2026, 3, 10, 0));

        Assert.Equal(DayOfWeek.Monday, next!.Value.DayOfWeek);
        Assert.Equal(At(2026, 3, 16, 2, 30), next);
    }

    [Theory(DisplayName = "Имена месяцев и дней принимаются")]
    [InlineData("0 0 1 JAN *")]
    [InlineData("0 0 * * MON")]
    [InlineData("0 0 * * mon-fri")]
    public void Names(string text) => Assert.NotNull(CronExpression.Parse(text).NextOccurrence(At(2026, 1, 1)));

    [Fact(DisplayName = "Воскресенье это и 0, и 7")]
    public void SundayIsZeroAndSeven()
    {
        var byZero = CronExpression.Parse("0 0 * * 0").NextOccurrence(At(2026, 3, 10));
        var bySeven = CronExpression.Parse("0 0 * * 7").NextOccurrence(At(2026, 3, 10));

        Assert.Equal(byZero, bySeven);
        Assert.Equal(DayOfWeek.Sunday, byZero!.Value.DayOfWeek);
    }

    [Fact(DisplayName = "Диапазон с шагом")]
    public void RangeWithStep()
    {
        var cron = CronExpression.Parse("0 8-18/2 * * *");

        Assert.Equal(At(2026, 3, 10, 8), cron.NextOccurrence(At(2026, 3, 10, 7, 30)));
        Assert.Equal(At(2026, 3, 10, 10), cron.NextOccurrence(At(2026, 3, 10, 8, 1)));
        Assert.Equal(At(2026, 3, 11, 8), cron.NextOccurrence(At(2026, 3, 10, 18, 1)));
    }

    [Fact(DisplayName = "Перечисление")]
    public void List()
    {
        var cron = CronExpression.Parse("0,30 9 * * *");

        Assert.Equal(At(2026, 3, 10, 9, 0), cron.NextOccurrence(At(2026, 3, 10, 8, 59)));
        Assert.Equal(At(2026, 3, 10, 9, 30), cron.NextOccurrence(At(2026, 3, 10, 9, 0)));
    }

    [Theory(DisplayName = "Испорченное выражение отвергается с объяснением")]
    [InlineData("30 2")]
    [InlineData("* * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 25 * * *")]
    [InlineData("*/0 * * * *")]
    [InlineData("abc * * * *")]
    [InlineData("30-10 * * * *")]
    public void Rejected(string text)
    {
        var error = Assert.Throws<FormatException>(() => CronExpression.Parse(text));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact(DisplayName = "Выражение, которое не сработает никогда, отвергается при разборе")]
    public void NeverMatches()
    {
        // Синтаксически безупречно и бессмысленно. Молча принять его значило бы
        // завести монитор, который не проверит ничего и не скажет об этом.
        var error = Assert.Throws<FormatException>(() => CronExpression.Parse("0 3 30 2 *"));

        Assert.Contains("не совпадает ни с одной датой", error.Message, StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Разбор сохраняет исходную строку")]
    public void KeepsText() => Assert.Equal("0 3 * * *", CronExpression.Parse(" 0 3 * * * ").Text);

    [Fact(DisplayName = "TryParse не бросает на мусоре")]
    public void TryParseSafe()
    {
        Assert.False(CronExpression.TryParse(null, out _));
        Assert.False(CronExpression.TryParse("  ", out _));
        Assert.False(CronExpression.TryParse("каждый день", out _));
        Assert.True(CronExpression.TryParse("0 0 * * *", out var ok));
        Assert.NotNull(ok);
    }

    [Fact(DisplayName = "Високосный год: 29 февраля находится")]
    public void LeapDay()
    {
        var cron = CronExpression.Parse("0 0 29 2 *");

        Assert.Equal(At(2028, 2, 29), cron.NextOccurrence(At(2026, 3, 1)));
    }
}
