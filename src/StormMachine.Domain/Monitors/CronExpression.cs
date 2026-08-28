using System.Globalization;

namespace StormMachine.Domain.Monitors;

/// <summary>
/// Выражение cron из пяти полей: минуты, часы, день месяца, месяц, день недели.
/// </summary>
/// <remarks>
/// Своё, а не библиотечное, по двум причинам.
/// <para>
/// Первая — доменный слой не имеет зависимостей вообще, и это правило проверяется
/// архитектурным тестом. Вторая важнее: то, что мы публикуемся с обрезкой, уже один раз
/// убило чужой планировщик молча (спайк-06, docs/02-research.md R-15). Разбор строки
/// в набор чисел рефлексии не требует и обрезкой сломан быть не может по устройству.
/// </para>
/// <para>
/// <b>Время локальное.</b> «Каждый день в 3:00» для человека означает три часа ночи
/// по его часам — и в марте, и в октябре. Интервалы, наоборот, абсолютны: «каждые пять
/// минут» это пять минут, сколько бы раз ни переводили стрелки.
/// </para>
/// <para>
/// <b>День месяца и день недели складываются через ИЛИ</b>, если ограничены оба.
/// Это поведение классического Vixie cron, и оно удивляет: <c>0 3 13 * 5</c> сработает
/// и тринадцатого числа, и по пятницам, а не только тринадцатого в пятницу. Мы его
/// повторяем, потому что человек, знающий cron, ждёт именно этого.
/// </para>
/// </remarks>
public sealed class CronExpression
{
    /// <summary>Сколько дней вперёд имеет смысл искать совпадение.</summary>
    /// <remarks>
    /// Четыре года с запасом покрывают високосный цикл. Выражение, не совпадающее
    /// ни разу за это время (например, 30 февраля), не совпадёт никогда — и честнее
    /// сказать об этом при разборе, чем крутить поиск до бесконечности.
    /// </remarks>
    private const int SearchDays = 4 * 366;

    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly string[] DayNames =
        ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _dayOfMonthRestricted;
    private readonly bool _dayOfWeekRestricted;

    private CronExpression(
        string text,
        bool[] minutes,
        bool[] hours,
        bool[] daysOfMonth,
        bool[] months,
        bool[] daysOfWeek,
        bool dayOfMonthRestricted,
        bool dayOfWeekRestricted)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
        _dayOfMonthRestricted = dayOfMonthRestricted;
        _dayOfWeekRestricted = dayOfWeekRestricted;
    }

    /// <summary>Исходная строка — её и показываем человеку, а не разобранные наборы.</summary>
    public string Text { get; }

    public static CronExpression Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (fields.Length != 5)
        {
            throw new FormatException(
                $"Выражение «{text}» не разобрано: ожидается пять полей "
                + "«минуты часы день-месяца месяц день-недели», получено "
                + fields.Length.ToString(CultureInfo.InvariantCulture)
                + ". Пример: «*/5 * * * *» — каждые пять минут.");
        }

        var minutes = Field(fields[0], 0, 59, "минуты", null);
        var hours = Field(fields[1], 0, 23, "часы", null);
        var daysOfMonth = Field(fields[2], 1, 31, "день месяца", null);
        var months = Field(fields[3], 1, 12, "месяц", MonthNames);
        var daysOfWeek = Field(fields[4], 0, 7, "день недели", DayNames);

        // Семёрка и ноль — оба воскресенье. Так принято в cron, и человек,
        // написавший «7», имеет в виду именно его.
        if (daysOfWeek[7])
        {
            daysOfWeek[0] = true;
        }

        var expression = new CronExpression(
            text.Trim(),
            minutes,
            hours,
            daysOfMonth,
            months,
            daysOfWeek,
            IsRestricted(fields[2]),
            IsRestricted(fields[4]));

        // Разбор не должен пропускать выражение, которое не сработает никогда:
        // «0 3 30 2 *» — синтаксически безупречное и бессмысленное.
        if (expression.NextOccurrence(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)) is null)
        {
            throw new FormatException(
                $"Выражение «{text}» синтаксически верно, но не совпадает ни с одной датой. "
                + "Так бывает у сочетаний вроде «30 февраля».");
        }

        return expression;
    }

    public static bool TryParse(string? text, out CronExpression? expression)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            expression = null;

            return false;
        }

        try
        {
            expression = Parse(text);

            return true;
        }
        catch (FormatException)
        {
            expression = null;

            return false;
        }
    }

    /// <summary>
    /// Ближайшее совпадение строго после <paramref name="after"/>, в локальном времени.
    /// </summary>
    /// <remarks>
    /// Строго после: иначе расписание, посчитанное сразу после срабатывания, вернуло бы
    /// тот же момент, и монитор ушёл бы в бесконечный цикл на месте.
    /// </remarks>
    public DateTime? NextOccurrence(DateTime after)
    {
        // Секунды и доли отбрасываются: cron живёт минутами, и попытка учесть
        // 30.7 секунды сдвинула бы первое срабатывание на минуту вперёд.
        var moment = new DateTime(after.Year, after.Month, after.Day, after.Hour, after.Minute, 0, after.Kind)
            .AddMinutes(1);

        var day = moment.Date;
        var limit = day.AddDays(SearchDays);

        while (day < limit)
        {
            if (!MatchesDay(day))
            {
                day = day.AddDays(1);
                moment = day;

                continue;
            }

            for (var hour = moment.Hour; hour < 24; hour++)
            {
                if (!_hours[hour])
                {
                    continue;
                }

                var from = hour == moment.Hour ? moment.Minute : 0;

                for (var minute = from; minute < 60; minute++)
                {
                    if (_minutes[minute])
                    {
                        return day.AddHours(hour).AddMinutes(minute);
                    }
                }
            }

            day = day.AddDays(1);
            moment = day;
        }

        return null;
    }

    public override string ToString() => Text;

    private bool MatchesDay(DateTime day)
    {
        if (!_months[day.Month])
        {
            return false;
        }

        var byDate = _daysOfMonth[day.Day];
        var byWeekday = _daysOfWeek[(int)day.DayOfWeek];

        // Оба ограничены — ИЛИ; ограничен один — решает он; ни один — любой день.
        return _dayOfMonthRestricted && _dayOfWeekRestricted
            ? byDate || byWeekday
            : byDate && byWeekday;
    }

    private static bool IsRestricted(string field) =>
        !field.Equals("*", StringComparison.Ordinal) && !field.StartsWith("*/", StringComparison.Ordinal);

    private static bool[] Field(string field, int min, int max, string name, string[]? names)
    {
        var allowed = new bool[max + 1];

        foreach (var part in field.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var step = 1;
            var body = part;
            var slash = part.IndexOf('/', StringComparison.Ordinal);

            if (slash >= 0)
            {
                body = part[..slash];

                if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step)
                    || step <= 0)
                {
                    throw Bad(field, name, "шаг после косой черты не число больше нуля");
                }
            }

            int from;
            int to;

            if (body.Equals("*", StringComparison.Ordinal))
            {
                from = min;
                to = max;
            }
            else
            {
                var dash = body.IndexOf('-', StringComparison.Ordinal);

                if (dash > 0)
                {
                    from = Value(body[..dash], min, max, field, name, names);
                    to = Value(body[(dash + 1)..], min, max, field, name, names);
                }
                else
                {
                    from = Value(body, min, max, field, name, names);

                    // Одиночное число с шагом означает «от него и дальше»: 5/10 — это
                    // 5, 15, 25… Без шага — ровно одно значение.
                    to = slash >= 0 ? max : from;
                }
            }

            if (from > to)
            {
                throw Bad(field, name, "начало диапазона больше конца");
            }

            for (var value = from; value <= to; value += step)
            {
                allowed[value] = true;
            }
        }

        if (Array.IndexOf(allowed, true) < 0)
        {
            throw Bad(field, name, "не задано ни одного значения");
        }

        return allowed;
    }

    private static int Value(string token, int min, int max, string field, string name, string[]? names)
    {
        if (names is not null)
        {
            var index = Array.FindIndex(names, n => n.Equals(token, StringComparison.OrdinalIgnoreCase));

            if (index >= 0)
            {
                // Месяцы нумеруются с единицы, дни недели — с нуля.
                return min == 1 ? index + 1 : index;
            }
        }

        if (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            throw Bad(field, name, $"«{token}» не число");
        }

        if (value < min || value > max)
        {
            throw Bad(field, name, $"{value} вне диапазона {min}–{max}");
        }

        return value;
    }

    private static FormatException Bad(string field, string name, string reason) =>
        new($"Поле «{name}» выражения cron задано как «{field}» — {reason}.");
}
