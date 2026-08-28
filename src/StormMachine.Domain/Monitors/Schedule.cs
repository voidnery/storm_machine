using System.Globalization;

namespace StormMachine.Domain.Monitors;

/// <summary>Как повторяется проверка.</summary>
public enum ScheduleKind
{
    /// <summary>Один раз в назначенный момент.</summary>
    Once,

    /// <summary>Через равные промежутки.</summary>
    Every,

    /// <summary>По выражению cron.</summary>
    Cron,
}

/// <summary>
/// Что делать с проверками, пропущенными, пока продукт не работал.
/// </summary>
/// <remarks>
/// Настольный продукт живёт на машине, которую выключают, усыпляют и перезагружают.
/// Монитор с интервалом в пять минут за ночь пропустит около сотни проверок — и вопрос
/// «догонять или нет» задать обязан продукт, а не оператор постфактум.
/// </remarks>
public enum MisfirePolicy
{
    /// <summary>Пропущенное не наверстывать: посчитать следующий срок от текущего момента.</summary>
    Skip,

    /// <summary>Выполнить один раз при старте, дальше — по расписанию.</summary>
    RunOnce,
}

/// <summary>
/// Окно обслуживания: время, когда проверки не идут.
/// </summary>
/// <remarks>
/// Окно исключается и из запусков, и из расчёта доступности. Считать плановые работы
/// простоем — значит завышать нарушение SLA; считать их временем работы — занижать.
/// Правильный ответ третий: этого времени в знаменателе нет.
/// </remarks>
public sealed record MaintenanceWindow
{
    /// <summary>Дни недели. Пусто — каждый день.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    public required TimeOnly Start { get; init; }

    public required TimeOnly End { get; init; }

    /// <summary>Зачем окно заведено — попадает в отчёт рядом с исключённым временем.</summary>
    public string? Reason { get; init; }

    /// <summary>Окно переходит через полночь: 23:00–02:00.</summary>
    public bool SpansMidnight => End < Start;

    public bool Contains(DateTime local)
    {
        var time = TimeOnly.FromDateTime(local);

        if (SpansMidnight)
        {
            // Окно 23:00–02:00 принадлежит дню, в котором оно началось: проверка
            // в 01:00 вторника попадает в окно, объявленное на понедельник.
            return time >= Start
                ? Matches(local.DayOfWeek)
                : time < End && Matches(local.AddDays(-1).DayOfWeek);
        }

        return time >= Start && time < End && Matches(local.DayOfWeek);
    }

    /// <summary>Момент, когда окно закончится, если <paramref name="local"/> внутри него.</summary>
    public DateTime EndAfter(DateTime local)
    {
        if (!Contains(local))
        {
            return local;
        }

        var time = TimeOnly.FromDateTime(local);

        if (SpansMidnight && time >= Start)
        {
            return local.Date.AddDays(1).Add(End.ToTimeSpan());
        }

        return local.Date.Add(End.ToTimeSpan());
    }

    /// <summary>
    /// Разбирает запись вида <c>пн-пт 02:00-04:00 обновления</c>.
    /// </summary>
    /// <remarks>
    /// Дни необязательны — без них окно ежедневное. Всё, что осталось после времени,
    /// становится причиной: она попадёт в отчёт рядом с исключённым временем, и лучше
    /// пусть будет написана вольно, чем не написана вовсе.
    /// </remarks>
    public static bool TryParse(string? text, out MaintenanceWindow? window)
    {
        window = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var at = Array.FindIndex(parts, p => p.Contains('-', StringComparison.Ordinal) && p.Contains(':', StringComparison.Ordinal));

        if (at < 0)
        {
            return false;
        }

        var span = parts[at].Split('-', 2);

        if (!TimeOnly.TryParseExact(span[0], "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !TimeOnly.TryParseExact(span[1], "H:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return false;
        }

        var days = at > 0 ? ParseDays(string.Join(' ', parts[..at])) : [];

        if (days is null)
        {
            return false;
        }

        window = new MaintenanceWindow
        {
            Days = days,
            Start = start,
            End = end,
            Reason = at + 1 < parts.Length ? string.Join(' ', parts[(at + 1)..]) : null,
        };

        return true;
    }

    private static List<DayOfWeek>? ParseDays(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();

        if (normalized is "ежедневно" or "каждый день" or "*")
        {
            return [];
        }

        if (normalized is "выходные")
        {
            return [DayOfWeek.Saturday, DayOfWeek.Sunday];
        }

        if (normalized is "будни" or "рабочие")
        {
            return [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        }

        var days = new List<DayOfWeek>();

        foreach (var token in normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = token.Split('-', 2);
            var from = DayByName(range[0]);

            if (from is null)
            {
                return null;
            }

            if (range.Length == 1)
            {
                days.Add(from.Value);

                continue;
            }

            var to = DayByName(range[1]);

            if (to is null)
            {
                return null;
            }

            // Диапазон идёт вперёд с переходом через воскресенье: «пт-пн» — это
            // пятница, суббота, воскресенье и понедельник, а не пустота.
            for (var i = 0; i < 7; i++)
            {
                var day = (DayOfWeek)(((int)from.Value + i) % 7);

                days.Add(day);

                if (day == to.Value)
                {
                    break;
                }
            }
        }

        return days.Count == 0 ? null : days;
    }

    private static DayOfWeek? DayByName(string token) => token.Trim() switch
    {
        "пн" or "понедельник" or "mon" => DayOfWeek.Monday,
        "вт" or "вторник" or "tue" => DayOfWeek.Tuesday,
        "ср" or "среда" or "wed" => DayOfWeek.Wednesday,
        "чт" or "четверг" or "thu" => DayOfWeek.Thursday,
        "пт" or "пятница" or "fri" => DayOfWeek.Friday,
        "сб" or "суббота" or "sat" => DayOfWeek.Saturday,
        "вс" or "воскресенье" or "sun" => DayOfWeek.Sunday,
        _ => null,
    };

    public string Describe()
    {
        var days = Days.Count == 0
            ? "ежедневно"
            : string.Join(", ", Days.Select(ShortDay));

        return $"{days} {Start:HH\\:mm}–{End:HH\\:mm}"
               + (string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" ({Reason})");
    }

    private bool Matches(DayOfWeek day) => Days.Count == 0 || Days.Contains(day);

    private static string ShortDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "пн",
        DayOfWeek.Tuesday => "вт",
        DayOfWeek.Wednesday => "ср",
        DayOfWeek.Thursday => "чт",
        DayOfWeek.Friday => "пт",
        DayOfWeek.Saturday => "сб",
        _ => "вс",
    };
}

/// <summary>
/// Расписание проверки.
/// </summary>
/// <remarks>
/// <b>Интервалы абсолютны, cron — локален.</b> «Каждые пять минут» это пять минут
/// прошедшего времени; «каждый день в 3:00» это три часа ночи по часам оператора,
/// и при переводе стрелок сохраняется именно это, а не длина промежутка.
/// </remarks>
public sealed record Schedule
{
    /// <summary>Самый частый допустимый интервал.</summary>
    /// <remarks>
    /// Тридцать секунд — не техническое ограничение, а граница смысла. Монитор
    /// не заменяет непрерывное измерение: ping с интервалом секунда — это проба,
    /// а не расписание, и запускать её планировщиком значит городить очередь задач
    /// вместо одного долгого прогона.
    /// </remarks>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(30);

    public required ScheduleKind Kind { get; init; }

    /// <summary>Момент однократного запуска.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Промежуток между запусками.</summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>Выражение cron в виде строки — хранится как написано человеком.</summary>
    public string? Cron { get; init; }

    public MisfirePolicy Misfire { get; init; } = MisfirePolicy.Skip;

    public IReadOnlyList<MaintenanceWindow> Maintenance { get; init; } = [];

    public static Schedule Every(TimeSpan interval, MisfirePolicy misfire = MisfirePolicy.Skip) => new()
    {
        Kind = ScheduleKind.Every,
        Interval = interval,
        Misfire = misfire,
    };

    public static Schedule ByCron(string cron, MisfirePolicy misfire = MisfirePolicy.Skip) => new()
    {
        Kind = ScheduleKind.Cron,
        Cron = cron,
        Misfire = misfire,
    };

    public static Schedule OnceAt(DateTimeOffset at) => new()
    {
        Kind = ScheduleKind.Once,
        At = at,
    };

    /// <summary>Проверяет расписание целиком и возвращает список ошибок.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        switch (Kind)
        {
            case ScheduleKind.Once when At is null:
                errors.Add("Для однократного запуска нужен момент.");

                break;

            case ScheduleKind.Every when Interval is null:
                errors.Add("Для повторяющегося запуска нужен интервал.");

                break;

            case ScheduleKind.Every when Interval is { } value && value < MinimumInterval:
                errors.Add(
                    "Интервал меньше "
                    + MinimumInterval.TotalSeconds.ToString("0", CultureInfo.InvariantCulture)
                    + " с. Для более частых измерений есть проба — она меряет непрерывно, "
                    + "а не выстраивает очередь запусков.");

                break;

            case ScheduleKind.Cron when !CronExpression.TryParse(Cron, out _):
                errors.Add(
                    Cron is null
                        ? "Для запуска по cron нужно выражение."
                        : $"Выражение cron «{Cron}» не разобрано.");

                break;

            default:
                break;
        }

        foreach (var window in Maintenance.Where(w => w.Start == w.End))
        {
            errors.Add($"Окно обслуживания {window.Describe()} нулевой длины.");
        }

        // Расписание, целиком накрытое окнами обслуживания, синтаксически безупречно
        // и бессмысленно — как «30 февраля» у cron. Сказать об этом надо здесь,
        // а не оставить монитор молча не работающим.
        if (errors.Count == 0
            && Maintenance.Count > 0
            && Kind != ScheduleKind.Once
            && NextAfter(DateTimeOffset.UtcNow) is null)
        {
            errors.Add(
                "Расписание целиком попадает в окна обслуживания — проверка не выполнится ни разу. "
                + "Сдвинь окно или расписание.");
        }

        return errors;
    }

    /// <summary>
    /// Следующий срок строго после указанного момента, с учётом окон обслуживания.
    /// </summary>
    /// <remarks>
    /// Возвращает <see langword="null"/>, если срока больше нет: однократный запуск
    /// уже состоялся, либо выражение cron не совпадёт в обозримом будущем.
    /// </remarks>
    public DateTimeOffset? NextAfter(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;

        var candidate = FirstAfter(after, zone);

        // Попавший в окно обслуживания срок сдвигается, а не отбрасывается. Куда именно —
        // зависит от вида расписания. У интервала сетки нет, и естественный ответ —
        // конец окна: проверки возобновляются, как только работы закончились. У cron
        // сетка есть, и её надо соблюсти: берётся первое совпадение с конца окна.
        for (var guard = 0; guard < 64 && candidate is { } moment; guard++)
        {
            var local = TimeZoneInfo.ConvertTime(moment, zone).DateTime;
            var window = Maintenance.FirstOrDefault(w => w.Contains(local));

            if (window is null)
            {
                return moment;
            }

            var resumes = ToOffset(window.EndAfter(local), zone);

            candidate = Kind == ScheduleKind.Cron ? FirstAfter(resumes.AddSeconds(-1), zone) : resumes;
        }

        // Сюда попадает расписание, целиком накрытое окнами: ежедневная проверка
        // в 3:00 при ежедневном окне 2:00–4:00 не выполнится никогда. Вернуть при этом
        // срок внутри окна значило бы пообещать запуск, которого не будет.
        return null;
    }

    /// <summary>Идёт ли обслуживание прямо сейчас.</summary>
    public MaintenanceWindow? MaintenanceAt(DateTimeOffset moment, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;

        var local = TimeZoneInfo.ConvertTime(moment, zone).DateTime;

        return Maintenance.FirstOrDefault(w => w.Contains(local));
    }

    /// <summary>
    /// Сколько сроков пропущено между назначенным и текущим моментом.
    /// </summary>
    /// <remarks>
    /// Ноль или один — обычное опоздание: планировщик проснулся на секунду позже,
    /// и запуск просто выполняется. Больше — машина не работала, и это уже случай
    /// для <see cref="MisfirePolicy"/>.
    /// </remarks>
    public int MissedSlots(DateTimeOffset due, DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        if (due >= now)
        {
            return 0;
        }

        if (Kind == ScheduleKind.Every && Interval is { } interval && interval > TimeSpan.Zero)
        {
            return (int)Math.Min(int.MaxValue, (now - due).Ticks / interval.Ticks);
        }

        if (Kind == ScheduleKind.Once)
        {
            return 1;
        }

        var missed = 0;
        var moment = due;

        // Считать больше тысячи бессмысленно: и сотня, и десять тысяч означают
        // одно и то же — «продукт не работал долго».
        while (missed < 1000 && NextAfter(moment, zone) is { } next && next < now)
        {
            missed++;
            moment = next;
        }

        return missed + 1;
    }

    public string Describe() => Kind switch
    {
        ScheduleKind.Once => At is { } at ? $"однократно {at.LocalDateTime:dd.MM.yyyy HH:mm}" : "однократно",
        ScheduleKind.Every => Interval is { } interval ? Repeat(interval) : "с интервалом",
        _ => $"по расписанию «{Cron}»",
    };

    /// <summary>
    /// Разбирает промежуток, записанный человеком: <c>30с</c>, <c>5м</c>, <c>2ч</c>, <c>7д</c>.
    /// </summary>
    /// <remarks>
    /// Живёт в домене, а не в консоли, потому что то же самое нужно графическому
    /// клиенту. Латинские суффиксы приняты наравне с русскими: набирать <c>5m</c>
    /// на английской раскладке быстрее, и заставлять переключаться незачем.
    /// Голое число — минуты: так это записывают чаще всего.
    /// </remarks>
    public static bool TryParseInterval(string? text, out TimeSpan interval)
    {
        interval = TimeSpan.Zero;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim().ToLowerInvariant();
        var digits = trimmed.TrimEnd('с', 'м', 'ч', 'д', 'н', 's', 'm', 'h', 'd', 'w', ' ');

        if (!double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return false;
        }

        var suffix = trimmed[digits.Length..].Trim();

        interval = suffix switch
        {
            "с" or "s" => TimeSpan.FromSeconds(value),
            "ч" or "h" => TimeSpan.FromHours(value),
            "д" or "d" => TimeSpan.FromDays(value),
            "н" or "w" => TimeSpan.FromDays(value * 7),
            "" or "м" or "m" => TimeSpan.FromMinutes(value),
            _ => TimeSpan.Zero,
        };

        return interval > TimeSpan.Zero;
    }

    /// <summary>
    /// Прошедшее время словами: «43 с», «7 мин», «1 ч 20 мин», «2 сут 3 ч».
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Humanize"/>: тот описывает настроенный интервал и потому
    /// вправе ждать целых единиц. Прошедшее время целым не бывает почти никогда,
    /// и «4457 секунд» вместо «1 ч 14 мин» читатель в уме не переводит.
    /// </remarks>
    public static string Elapsed(TimeSpan span)
    {
        var value = span < TimeSpan.Zero ? TimeSpan.Zero : span;

        if (value.TotalSeconds < 60)
        {
            return $"{((int)value.TotalSeconds).ToString(CultureInfo.InvariantCulture)} с";
        }

        if (value.TotalMinutes < 60)
        {
            return $"{((int)value.TotalMinutes).ToString(CultureInfo.InvariantCulture)} мин";
        }

        if (value.TotalHours < 24)
        {
            var minutes = value.Minutes;

            return minutes == 0
                ? $"{((int)value.TotalHours).ToString(CultureInfo.InvariantCulture)} ч"
                : $"{((int)value.TotalHours).ToString(CultureInfo.InvariantCulture)} ч "
                  + $"{minutes.ToString(CultureInfo.InvariantCulture)} мин";
        }

        var days = (int)value.TotalDays;
        var hours = value.Hours;

        return hours == 0
            ? $"{days.ToString(CultureInfo.InvariantCulture)} сут"
            : $"{days.ToString(CultureInfo.InvariantCulture)} сут {hours.ToString(CultureInfo.InvariantCulture)} ч";
    }

    /// <summary>Промежуток словами: «5 минут», «2 часа», «сутки».</summary>
    public static string Humanize(TimeSpan interval)
    {
        var (count, one, few, many) = Split(interval);

        return count == 1 ? one : $"{count.ToString(CultureInfo.InvariantCulture)} {Plural(count, one, few, many)}";
    }

    /// <summary>
    /// Повторение словами: «каждую минуту», «каждые 5 минут», «каждый час».
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Humanize"/>, потому что единственное число требует
    /// другого слова: «каждые 1 минуту» по-русски не говорят.
    /// </remarks>
    public static string Repeat(TimeSpan interval)
    {
        var (count, one, few, many) = Split(interval);

        if (count != 1)
        {
            return $"каждые {count.ToString(CultureInfo.InvariantCulture)} {Plural(count, one, few, many)}";
        }

        return one switch
        {
            "час" => "каждый час",
            "сутки" => "каждые сутки",
            "минута" => "каждую минуту",
            _ => "каждую секунду",
        };
    }

    private static (int Count, string One, string Few, string Many) Split(TimeSpan interval)
    {
        if (interval.TotalDays >= 1 && interval.TotalDays % 1 == 0)
        {
            return ((int)interval.TotalDays, "сутки", "суток", "суток");
        }

        if (interval.TotalHours >= 1 && interval.TotalHours % 1 == 0)
        {
            return ((int)interval.TotalHours, "час", "часа", "часов");
        }

        if (interval.TotalMinutes >= 1 && interval.TotalMinutes % 1 == 0)
        {
            return ((int)interval.TotalMinutes, "минута", "минуты", "минут");
        }

        return ((int)interval.TotalSeconds, "секунда", "секунды", "секунд");
    }

    private static string Plural(int count, string one, string few, string many)
    {
        var tens = count % 100;

        if (tens is >= 11 and <= 14)
        {
            return many;
        }

        return (count % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many,
        };
    }

    private DateTimeOffset? FirstAfter(DateTimeOffset after, TimeZoneInfo zone)
    {
        switch (Kind)
        {
            case ScheduleKind.Once:
                return At > after ? At : null;

            case ScheduleKind.Every:
                return Interval is { } interval && interval > TimeSpan.Zero ? after + interval : null;

            default:
                if (!CronExpression.TryParse(Cron, out var cron) || cron is null)
                {
                    return null;
                }

                var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;

                return cron.NextOccurrence(local) is { } next ? ToOffset(next, zone) : null;
        }
    }

    /// <summary>
    /// Локальное время в момент времени с учётом перевода стрелок.
    /// </summary>
    /// <remarks>
    /// Весной час пропадает: 02:30 не существует, и ночная проверка, назначенная
    /// на это время, обязана состояться, а не исчезнуть на год. Осенью час повторяется,
    /// и берётся <b>первое</b> из двух вхождений — то есть большее смещение: человек
    /// задавал время по часам, и первое совпадение с показанием часов и есть ответ.
    /// </remarks>
    private static DateTimeOffset ToOffset(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            // Час пропал целиком: переносим к первой существующей минуте после разрыва.
            var shifted = unspecified.AddHours(1);

            while (zone.IsInvalidTime(shifted))
            {
                shifted = shifted.AddMinutes(1);
            }

            unspecified = shifted;
        }

        var offset = zone.IsAmbiguousTime(unspecified)
            ? zone.GetAmbiguousTimeOffsets(unspecified).Max()
            : zone.GetUtcOffset(unspecified);

        return new DateTimeOffset(unspecified, offset);
    }
}
