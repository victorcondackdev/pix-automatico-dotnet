using System.Globalization;

namespace PixAutomatico.Calendar;

/// <summary>Cycle length of a recurrence. <see cref="PeriodicityNames"/> maps to the API Pix names.</summary>
public enum Periodicity
{
    /// <summary>SEMANAL.</summary>
    Weekly,

    /// <summary>MENSAL.</summary>
    Monthly,

    /// <summary>TRIMESTRAL.</summary>
    Quarterly,

    /// <summary>SEMESTRAL.</summary>
    Semiannual,

    /// <summary>ANUAL.</summary>
    Annual,
}

/// <summary>API Pix names of periodicities.</summary>
public static class PeriodicityNames
{
    /// <summary>The API Pix name (SEMANAL, MENSAL, ...).</summary>
    public static string ToApi(this Periodicity p) => p switch
    {
        Periodicity.Weekly => "SEMANAL",
        Periodicity.Monthly => "MENSAL",
        Periodicity.Quarterly => "TRIMESTRAL",
        Periodicity.Semiannual => "SEMESTRAL",
        Periodicity.Annual => "ANUAL",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>Parses an API Pix name.</summary>
    public static bool TryParse(string? name, out Periodicity p)
    {
        foreach (Periodicity x in Enum.GetValues<Periodicity>())
        {
            if (x.ToApi() == name)
            {
                p = x;
                return true;
            }
        }

        p = default;
        return false;
    }
}

/// <summary>A set of non-business dates.</summary>
public interface IHolidays
{
    /// <summary>Whether the date is a holiday.</summary>
    bool IsHoliday(DateOnly date);
}

/// <summary>
/// The Brazilian national banking calendar: national holidays (including Black
/// Consciousness Day, national since 2024) plus Carnival Monday and Tuesday.
/// </summary>
public sealed class NationalHolidays : IHolidays
{
    private static readonly Dictionary<(int Month, int Day), string> Fixed = new()
    {
        [(1, 1)] = "Confraternização Universal",
        [(4, 21)] = "Tiradentes",
        [(5, 1)] = "Dia do Trabalho",
        [(9, 7)] = "Independência",
        [(10, 12)] = "Nossa Senhora Aparecida",
        [(11, 2)] = "Finados",
        [(11, 15)] = "Proclamação da República",
        [(12, 25)] = "Natal",
    };

    /// <summary>Creates the calendar with optional local holidays.</summary>
    public NationalHolidays(IReadOnlyDictionary<DateOnly, string>? extra = null)
    {
        Extra = extra ?? new Dictionary<DateOnly, string>();
    }

    /// <summary>Extra (local) holidays.</summary>
    public IReadOnlyDictionary<DateOnly, string> Extra { get; }

    /// <summary>The holiday name, or empty.</summary>
    public string Name(DateOnly d)
    {
        if (Extra.TryGetValue(d, out string? s))
        {
            return s;
        }

        if (Fixed.TryGetValue((d.Month, d.Day), out s))
        {
            return s;
        }

        if (d.Month == 11 && d.Day == 20 && d.Year >= 2024)
        {
            return "Dia Nacional de Zumbi e da Consciência Negra";
        }

        DateOnly e = BankCalendar.Easter(d.Year);
        if (d == e.AddDays(-48))
        {
            return "Carnaval (segunda-feira)";
        }

        if (d == e.AddDays(-47))
        {
            return "Carnaval (terça-feira)";
        }

        if (d == e.AddDays(-2))
        {
            return "Sexta-feira Santa";
        }

        return d == e.AddDays(60) ? "Corpus Christi" : "";
    }

    /// <inheritdoc />
    public bool IsHoliday(DateOnly date) => Name(date).Length > 0;
}

/// <summary>Dates, business days and Pix Automático cycle arithmetic.</summary>
public static class BankCalendar
{
    /// <summary>Brasília time (no daylight saving since 2019).</summary>
    public static readonly TimeSpan Brt = TimeSpan.FromHours(-3);

    /// <summary>Parses YYYY-MM-DD.</summary>
    public static DateOnly ParseDate(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Formats YYYY-MM-DD.</summary>
    public static string Format(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The instant at hh:mm of a date in Brasília time.</summary>
    public static DateTimeOffset At(this DateOnly d, int hour, int minute) => new(d.Year, d.Month, d.Day, hour, minute, 0, Brt);

    /// <summary>The civil date of an instant in Brasília time.</summary>
    public static DateOnly DateOf(DateTimeOffset t) => DateOnly.FromDateTime(t.ToOffset(Brt).DateTime);

    /// <summary>Days from a to b.</summary>
    public static int DaysUntil(this DateOnly a, DateOnly b) => b.DayNumber - a.DayNumber;

    /// <summary>Easter Sunday (Gregorian, anonymous algorithm).</summary>
    public static DateOnly Easter(int year)
    {
        int a = year % 19, b = year / 100, c = year % 100, d = b / 4, e = b % 4;
        int f = (b + 8) / 25, g = (b - f + 1) / 3, h = ((19 * a) + b - d - g + 15) % 30;
        int i = c / 4, k = c % 4, l = (32 + (2 * e) + (2 * i) - h - k) % 7, m = (a + (11 * h) + (22 * l)) / 451;
        int month = (h + l - (7 * m) + 114) / 31;
        int day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>Weekday and not a holiday.</summary>
    public static bool IsBusinessDay(IHolidays? h, DateOnly d)
        => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (h is null || !h.IsHoliday(d));

    /// <summary>d if a business day, else the next one.</summary>
    public static DateOnly NextBusinessDay(IHolidays? h, DateOnly d)
    {
        while (!IsBusinessDay(h, d))
        {
            d = d.AddDays(1);
        }

        return d;
    }

    /// <summary>d if a business day, else the previous one.</summary>
    public static DateOnly PreviousBusinessDay(IHolidays? h, DateOnly d)
    {
        while (!IsBusinessDay(h, d))
        {
            d = d.AddDays(-1);
        }

        return d;
    }

    /// <summary>Nominal date of cycle k: same weekday, or same day of month clamped to month end.</summary>
    public static DateOnly CycleStart(DateOnly start, Periodicity p, int k) => p switch
    {
        Periodicity.Weekly => start.AddDays(7 * k),
        Periodicity.Monthly => start.AddMonths(k),
        Periodicity.Quarterly => start.AddMonths(3 * k),
        Periodicity.Semiannual => start.AddMonths(6 * k),
        Periodicity.Annual => start.AddMonths(12 * k),
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>
    /// Settlement date of cycle k; with adjust, moves to the next business day unless that
    /// reaches the next cycle, in which case it moves back to the previous business day.
    /// </summary>
    public static DateOnly DueDate(IHolidays? h, DateOnly start, Periodicity p, int k, bool adjust)
    {
        DateOnly d = CycleStart(start, p, k);
        if (!adjust || IsBusinessDay(h, d))
        {
            return d;
        }

        DateOnly next = NextBusinessDay(h, d);
        return next < CycleStart(start, p, k + 1) ? next : PreviousBusinessDay(h, d);
    }

    /// <summary>Index of the cycle containing d (-1 before start).</summary>
    public static int CycleOf(DateOnly start, Periodicity p, DateOnly d)
    {
        if (d < start)
        {
            return -1;
        }

        int k;
        if (p == Periodicity.Weekly)
        {
            k = start.DaysUntil(d) / 7;
        }
        else
        {
            int months = p switch { Periodicity.Monthly => 1, Periodicity.Quarterly => 3, Periodicity.Semiannual => 6, _ => 12 };
            k = (((d.Year - start.Year) * 12) + d.Month - start.Month) / months;
            if (k > 0)
            {
                k--;
            }
        }

        while (CycleStart(start, p, k + 1) <= d)
        {
            k++;
        }

        return k;
    }
}
