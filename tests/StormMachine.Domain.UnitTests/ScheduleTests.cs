using StormMachine.Domain.Monitors;

namespace StormMachine.Domain.UnitTests;

/// <summary>
/// Расписание: сроки, окна обслуживания и подсчёт пропущенного.
/// </summary>
/// <remarks>
/// Подсчёт пропущенного — половина приёмки И-14. Продукт обязан отличать опоздание
/// на секунду от восьми часов сна: в первом случае проверка просто выполняется,
/// во втором вступает политика, которую задал человек.
/// </remarks>
public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static DateTimeOffset At(int day, int hour = 0, int minute = 0) =>
        new(2026, 3, day, hour, minute, 0, TimeSpan.Zero);

    // ------------------------------------------------------------------ интервалы

    [Fact(DisplayName = "Интервал отсчитывается от указанного момента")]
    public void IntervalFromMoment()
    {
        var schedule = Schedule.Every(TimeSpan.FromMinutes(5));

        Assert.Equal(At(10, 12, 5), schedule.NextAfter(At(10, 12, 0), Utc));
    }

    [Fact(DisplayName = "Однократный запуск в прошлом больше не назначается")]
    public void OnceInThePast()
    {
        var schedule = Schedule.OnceAt(At(10, 12));

        Assert.Equal(At(10, 12), schedule.NextAfter(At(10, 11), Utc));
        Assert.Null(schedule.NextAfter(At(10, 13), Utc));
    }

    [Theory(DisplayName = "Промежуток разбирается из записи человека")]
    [InlineData("30с", 30)]
    [InlineData("30s", 30)]
    [InlineData("5м", 300)]
    [InlineData("5m", 300)]
    [InlineData("5", 300)]
    [InlineData("2ч", 7200)]
    [InlineData("1д", 86400)]
    public void ParsesInterval(string text, int seconds)
    {
        Assert.True(Schedule.TryParseInterval(text, out var interval));
        Assert.Equal(TimeSpan.FromSeconds(seconds), interval);
    }

    [Theory(DisplayName = "Мусор промежутком не считается")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("скоро")]
    [InlineData("0м")]
    [InlineData("-5м")]
    public void RejectsInterval(string? text) => Assert.False(Schedule.TryParseInterval(text, out _));

    [Fact(DisplayName = "Слишком частый интервал отвергается с объяснением")]
    public void TooFrequent()
    {
        var errors = Schedule.Every(TimeSpan.FromSeconds(5)).Validate();

        Assert.Single(errors);
        Assert.Contains("проба", errors[0], StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ пропуски

    [Fact(DisplayName = "Опоздание внутри одного срока пропуском не считается")]
    public void SmallDelayIsNotMisfire()
    {
        var schedule = Schedule.Every(TimeSpan.FromMinutes(5));

        // Планировщик проснулся на три минуты позже назначенного: ни один срок
        // при этом не потерян, и наверстывать нечего.
        Assert.Equal(0, schedule.MissedSlots(At(10, 12, 0), At(10, 12, 3), Utc));
    }

    [Fact(DisplayName = "Ночь сна считается по числу пропущенных сроков")]
    public void SleepCountsSlots()
    {
        var schedule = Schedule.Every(TimeSpan.FromMinutes(5));

        Assert.Equal(96, schedule.MissedSlots(At(10, 2, 0), At(10, 10, 0), Utc));
    }

    [Fact(DisplayName = "У cron пропуски считаются по совпадениям, а не по времени")]
    public void CronCountsOccurrences()
    {
        var schedule = Schedule.ByCron("0 3 * * *");

        // С понедельника 3:00 до среды 10:00 прошли три срока: пн, вт, ср.
        Assert.Equal(3, schedule.MissedSlots(At(9, 3), At(11, 10), Utc));
    }

    // ------------------------------------------------------- окна обслуживания

    [Fact(DisplayName = "Срок внутри окна обслуживания переносится за окно")]
    public void MaintenanceShiftsNextRun()
    {
        // Окно только по воскресеньям: ночная проверка пропускает воскресенье
        // и выполняется в понедельник, а не пропадает совсем.
        var schedule = Schedule.ByCron("0 3 * * *") with
        {
            Maintenance =
            [
                new MaintenanceWindow
                {
                    Days = [DayOfWeek.Sunday],
                    Start = new TimeOnly(2, 0),
                    End = new TimeOnly(4, 0),
                },
            ],
        };

        // 14 марта 2026 — суббота, 15-е — воскресенье.
        var next = schedule.NextAfter(At(14, 12), Utc);

        Assert.Equal(At(16, 3), next);
    }

    [Fact(DisplayName = "Расписание, целиком накрытое окном, не даёт срока и отвергается проверкой")]
    public void ScheduleFullyInsideMaintenance()
    {
        // Ежедневная проверка в 3:00 при ежедневном окне 2:00–4:00 не выполнится
        // никогда. Вернуть срок внутри окна значило бы пообещать запуск, которого нет.
        var schedule = Schedule.ByCron("0 3 * * *") with
        {
            Maintenance = [new MaintenanceWindow { Start = new TimeOnly(2, 0), End = new TimeOnly(4, 0) }],
        };

        Assert.Null(schedule.NextAfter(At(10, 12), Utc));

        var errors = schedule.Validate();

        Assert.Single(errors);
        Assert.Contains("не выполнится ни разу", errors[0], StringComparison.Ordinal);
    }

    [Fact(DisplayName = "Интервальный монитор в окне обслуживания молчит и возвращается после")]
    public void MaintenanceSkipsInterval()
    {
        var schedule = Schedule.Every(TimeSpan.FromMinutes(30)) with
        {
            Maintenance = [new MaintenanceWindow { Start = new TimeOnly(2, 0), End = new TimeOnly(4, 0) }],
        };

        var next = schedule.NextAfter(At(10, 1, 45), Utc);

        Assert.Equal(At(10, 4, 0), next);
    }

    [Fact(DisplayName = "Окно через полночь принадлежит дню, в котором началось")]
    public void MidnightWindow()
    {
        var window = new MaintenanceWindow
        {
            Days = [DayOfWeek.Saturday],
            Start = new TimeOnly(23, 0),
            End = new TimeOnly(2, 0),
        };

        // 14 марта 2026 — суббота.
        Assert.True(window.Contains(new DateTime(2026, 3, 14, 23, 30, 0, DateTimeKind.Unspecified)));
        Assert.True(window.Contains(new DateTime(2026, 3, 15, 1, 30, 0, DateTimeKind.Unspecified)));
        Assert.False(window.Contains(new DateTime(2026, 3, 15, 23, 30, 0, DateTimeKind.Unspecified)));
    }

    [Theory(DisplayName = "Окно разбирается из записи человека")]
    [InlineData("02:00-04:00", 0)]
    [InlineData("пн-пт 02:00-04:00", 5)]
    [InlineData("будни 02:00-04:00", 5)]
    [InlineData("выходные 01:00-06:00", 2)]
    [InlineData("сб,вс 01:00-06:00", 2)]
    public void ParsesWindow(string text, int days)
    {
        Assert.True(MaintenanceWindow.TryParse(text, out var window));
        Assert.NotNull(window);
        Assert.Equal(days, window!.Days.Count);
    }

    [Fact(DisplayName = "Диапазон дней идёт вперёд через воскресенье")]
    public void WeekWraps()
    {
        Assert.True(MaintenanceWindow.TryParse("пт-пн 01:00-02:00", out var window));

        Assert.Equal(
            [DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday],
            window!.Days);
    }

    [Fact(DisplayName = "Причина окна сохраняется — она попадёт в отчёт")]
    public void WindowReason()
    {
        Assert.True(MaintenanceWindow.TryParse("пн 02:00-04:00 обновление прошивок", out var window));

        Assert.Equal("обновление прошивок", window!.Reason);
    }

    [Theory(DisplayName = "Испорченное окно не разбирается")]
    [InlineData(null)]
    [InlineData("каждую ночь")]
    [InlineData("пн 25:00-26:00")]
    [InlineData("непонедельник 01:00-02:00")]
    public void RejectsWindow(string? text) => Assert.False(MaintenanceWindow.TryParse(text, out _));

    // ------------------------------------------------------------------- словами

    [Theory(DisplayName = "Повторение называется по-русски")]
    [InlineData(60, "каждую минуту")]
    [InlineData(300, "каждые 5 минут")]
    [InlineData(3600, "каждый час")]
    [InlineData(7200, "каждые 2 часа")]
    [InlineData(86400, "каждые сутки")]
    public void RepeatsInWords(int seconds, string expected) =>
        Assert.Equal(expected, Schedule.Repeat(TimeSpan.FromSeconds(seconds)));

    [Theory(DisplayName = "Прошедшее время называется крупными единицами")]
    [InlineData(43, "43 с")]
    [InlineData(420, "7 мин")]
    [InlineData(4457, "1 ч 14 мин")]
    [InlineData(180000, "2 сут 2 ч")]
    public void ElapsedInWords(int seconds, string expected) =>
        Assert.Equal(expected, Schedule.Elapsed(TimeSpan.FromSeconds(seconds)));
}
