namespace SharpNinja.Valhalla.Baldr;

/// <summary>Evaluates Valhalla 3.8.3 conditional date/time domains.</summary>
public static class ConditionalTimeDomainEvaluator
{
    public static bool IsActive(
        ulong restriction,
        ulong currentTime,
        uint timeZoneIndex,
        IValhallaTimeZoneResolver? resolver = null)
    {
        resolver ??= ValhallaTimeZoneResolver.Instance;
        if (currentTime == 0 ||
            !resolver.TryResolve(timeZoneIndex, out TimeZoneInfo? timeZone) ||
            timeZone is null)
        {
            return false;
        }

        DateTimeOffset instant;
        try
        {
            instant = DateTimeOffset.FromUnixTimeSeconds(checked((long)currentTime));
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        catch (OverflowException)
        {
            return false;
        }

        DateTime local = TimeZoneInfo.ConvertTime(instant, timeZone).DateTime;
        return IsActive(new TimeDomain(restriction), local);
    }

    internal static bool IsActive(TimeDomain domain, DateTime local)
    {
        bool dowInRange = domain.Dow == 0 ||
            (domain.Dow & DayOfWeekMask(local.DayOfWeek)) != 0;
        if (!dowInRange)
        {
            return false;
        }

        int beginMinutes = (domain.BeginHrs * 60) + domain.BeginMins;
        int endMinutes = (domain.EndHrs * 60) + domain.EndMins;
        int localMinutes = (local.Hour * 60) + local.Minute;
        bool hasTime = beginMinutes != 0 || endMinutes != 0;

        byte beginMonth = domain.BeginMonth;
        byte endMonth = domain.EndMonth;
        byte beginDay = domain.BeginDayDow;
        byte endDay = domain.EndDayDow;
        byte beginWeek = domain.BeginWeek;
        byte endWeek = domain.EndWeek;

        if (domain.Type == TimeDomain.NthDow &&
            beginWeek != 0 && beginDay == 0 && beginMonth == 0)
        {
            beginMonth = (byte)local.Month;
        }

        if (domain.Type == TimeDomain.NthDow &&
            endWeek != 0 && endDay == 0 && endMonth == 0)
        {
            endMonth = (byte)local.Month;
        }

        if (domain.Type == TimeDomain.NthDow &&
            beginWeek != 0 && beginDay == 0 && domain.BeginMonth == 0 &&
            endWeek == 0 && endDay == 0 && domain.EndMonth == 0)
        {
            endMonth = beginMonth;
            beginDay = endDay = SingleDayOfWeek(domain.Dow);
            endWeek = beginWeek;
        }
        else if (domain.Type == TimeDomain.Ymd &&
                 beginMonth != 0 && endMonth != 0 &&
                 beginDay == 0 && endDay == 0)
        {
            beginDay = 1;
            endDay = (byte)DateTime.DaysInMonth(local.Year, endMonth);
        }

        if (domain.Type == TimeDomain.Ymd &&
            beginMonth != 0 && endMonth != 0 &&
            beginDay == 0 && endDay == 0 &&
            beginWeek == 0 && endWeek == 0 &&
            beginMonth == endMonth)
        {
            return local.Month == beginMonth &&
                   (!hasTime || IsTimeInRange(beginMinutes, endMinutes, localMinutes));
        }

        if ((domain.Type == TimeDomain.Ymd &&
             beginMonth != 0 && beginDay != 0) ||
            (domain.Type == TimeDomain.NthDow &&
             beginMonth != 0 && beginDay != 0 &&
             endMonth != 0 && endDay != 0))
        {
            if (!TryCreateRange(
                    domain.Type,
                    local,
                    beginMonth,
                    beginDay,
                    beginWeek,
                    endMonth,
                    endDay,
                    endWeek,
                    out DateTime begin,
                    out DateTime end,
                    out bool edgeCase))
            {
                return false;
            }

            bool dateInRange = edgeCase
                ? local.Date >= begin.Date || local.Date <= end.Date
                : local.Date >= begin.Date && local.Date <= end.Date;
            return dateInRange &&
                   (!hasTime || IsTimeInRange(beginMinutes, endMinutes, localMinutes));
        }

        return !hasTime || IsTimeInRange(beginMinutes, endMinutes, localMinutes);
    }

    private static bool TryCreateRange(
        byte type,
        DateTime local,
        byte beginMonth,
        byte beginDay,
        byte beginWeek,
        byte endMonth,
        byte endDay,
        byte endWeek,
        out DateTime begin,
        out DateTime end,
        out bool edgeCase)
    {
        begin = default;
        end = default;
        edgeCase = false;
        int beginYear = local.Year;
        int endYear = local.Year;
        if (beginMonth == endMonth)
        {
            edgeCase = beginDay > endDay;
        }
        else if (beginMonth > endMonth)
        {
            if (beginMonth > local.Month)
            {
                beginYear--;
            }
            else
            {
                endYear++;
            }
        }

        try
        {
            begin = type == TimeDomain.NthDow && beginWeek is >= 1 and <= 5
                ? NthDayOfWeek(beginYear, beginMonth, beginDay, beginWeek)
                : new DateTime(beginYear, beginMonth, beginDay);
            end = type == TimeDomain.NthDow && endWeek is >= 1 and <= 5
                ? NthDayOfWeek(endYear, endMonth, endDay, endWeek)
                : new DateTime(endYear, endMonth, endDay);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static DateTime NthDayOfWeek(
        int year,
        int month,
        byte valhallaDay,
        byte week)
    {
        DayOfWeek target = (DayOfWeek)(valhallaDay - 1);
        DateTime first = new(year, month, 1);
        int offset = ((int)target - (int)first.DayOfWeek + 7) % 7;
        int day = 1 + offset + ((week - 1) * 7);
        int daysInMonth = DateTime.DaysInMonth(year, month);
        while (day > daysInMonth && week == 5)
        {
            day -= 7;
        }

        return new DateTime(year, month, day);
    }

    private static bool IsTimeInRange(int begin, int end, int value) =>
        begin > end
            ? !(end <= value && value <= begin)
            : begin <= value && value <= end;

    private static byte DayOfWeekMask(DayOfWeek day) =>
        (byte)(1 << (int)day);

    private static byte SingleDayOfWeek(byte mask)
    {
        for (byte index = 0; index < 7; index++)
        {
            if ((mask & (1 << index)) != 0)
            {
                return (byte)(index + 1);
            }
        }

        return 0;
    }
}
