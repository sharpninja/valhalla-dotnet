using System.Globalization;
using System.Text.RegularExpressions;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Parses OSM conditional time expressions into Valhalla 3.8.3 TimeDomain words.
/// </summary>
/// <remarks>
/// Faithful managed port of mjolnir/timeparsing.cc at Valhalla commit a60c7cb.
/// Unsupported or malformed expressions return no domains so callers can fail closed.
/// </remarks>
internal static partial class OsmConditionalTimeDomainParser
{
    private const byte AllDaysOfWeek = 127;

    [GeneratedRegex(
        "(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) (?:(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Mon|Mo|Tues|Tue|Tu|Weds|Wed|We|Thurs|Thur|Th|Fri|Fr|Sat|Sa|Sun|Su)(\\[-?[0-9]\\]))-(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) (\\d{1,2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BeginWeekdayOfMonthRegex();

    [GeneratedRegex(
        "(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) (\\d{1,2})-(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) (?:(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Mon|Mo|Tues|Tue|Tu|Weds|Wed|We|Thurs|Thur|Th|Fri|Fr|Sat|Sa|Sun|Su)(\\[-?[0-9]\\]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EndWeekdayOfMonthRegex();

    [GeneratedRegex(
        "(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) (?:(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Mon|Mo|Tues|Tue|Tu|Weds|Wed|We|Thurs|Thur|Th|Fri|Fr|Sat|Sa|Sun|Su)(\\[-?[0-9]\\]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeekdayOfMonthRegex();

    [GeneratedRegex(
        "(?:(Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday|Mon|Mo|Tues|Tue|Tu|Weds|Wed|We|Thurs|Thur|Th|Fri|Fr|Sat|Sa|Sun|Su)(\\[-?[0-9]\\]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeekdayOfEveryMonthRegex();

    [GeneratedRegex(
        "(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) (\\d{1,2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthDayRegex();

    [GeneratedRegex(
        "(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) (\\d{1,2})-(\\d{1,2})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RangeWithinMonthRegex();

    [GeneratedRegex(
        "(?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)) - (?:(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MonthRangeRegex();

    internal static IReadOnlyList<ulong> Parse(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        var values = new List<ulong>();
        var domain = new TimeDomain();
        try
        {
            string condition = expression
                .Replace("(", string.Empty, StringComparison.Ordinal)
                .Replace(")", string.Empty, StringComparison.Ordinal)
                .Trim();
            if (condition.StartsWith("PH", StringComparison.Ordinal) ||
                condition.StartsWith("SH", StringComparison.Ordinal))
            {
                return values;
            }

            condition = NormalizeDateSyntax(condition);
            int found = condition.IndexOf(",PH", StringComparison.Ordinal);
            if (found >= 0)
            {
                condition = condition.Remove(found, 3);
            }

            found = condition.IndexOf("PH,", StringComparison.Ordinal);
            if (found >= 0)
            {
                condition = condition.Remove(found, 3);
            }

            string[] components = Split(condition, ' ');
            if (components.Length == 1 &&
                condition.Contains('#', StringComparison.Ordinal) &&
                condition.Count(character => character == '#') == 1)
            {
                components = Split(condition, '#');
            }

            if (components.Length == 1 &&
                components[0].Contains('-', StringComparison.Ordinal) &&
                components[0].Contains(':', StringComparison.Ordinal))
            {
                foreach (string timeRange in Split(components[0], ','))
                {
                    if (!TryApplyTimeRange(ref domain, timeRange))
                    {
                        return [];
                    }

                    values.Add(domain.TdValue);
                }

                return values;
            }

            foreach (string rawComponent in components)
            {
                string component = rawComponent.Trim();
                string[] tokens;
                bool isRange = false;
                bool isDate = false;
                bool isNthWeek = false;
                bool endsNthWeek = false;

                if (component.Contains(',', StringComparison.Ordinal))
                {
                    tokens = Split(component, ',');
                }
                else if (component.Contains('-', StringComparison.Ordinal))
                {
                    tokens = Split(component, '-');
                    isRange = true;
                    if (tokens.Length != 0 &&
                        component.Contains('#', StringComparison.Ordinal) &&
                        component.Contains('[', StringComparison.Ordinal) &&
                        component.Contains(']', StringComparison.Ordinal))
                    {
                        isDate = true;
                        isNthWeek = true;
                        tokens = tokens.SelectMany(token => Split(token, '#')).ToArray();
                    }
                    else if (tokens.Length != 0 &&
                             component.Contains('#', StringComparison.Ordinal))
                    {
                        isDate = true;
                        tokens = tokens.SelectMany(token => Split(token, '#')).ToArray();
                    }
                }
                else if (component.Contains('#', StringComparison.Ordinal) &&
                         component.Contains('[', StringComparison.Ordinal) &&
                         component.Contains(']', StringComparison.Ordinal))
                {
                    isDate = true;
                    isNthWeek = true;
                    tokens = Split(component, '#');
                }
                else if (component.Contains('#', StringComparison.Ordinal))
                {
                    isDate = true;
                    tokens = Split(component, '#');
                }
                else
                {
                    tokens = [component];
                }

                byte firstMonth = GetMonth(tokens[0]);
                if (firstMonth != 0)
                {
                    for (int index = 0; index < tokens.Length; index++)
                    {
                        string token = tokens[index];
                        if (tokens.Length == 4 && isDate && isRange)
                        {
                            domain.SetType(false);
                            domain.SetBeginMonth(firstMonth);
                            domain.SetBeginDayDow(ParseByte(tokens[1]));
                            domain.SetEndMonth(GetMonth(tokens[2]));
                            domain.SetEndDayDow(ParseByte(tokens[3]));
                            break;
                        }

                        if (tokens.Length == 3 && isDate && isRange)
                        {
                            domain.SetType(false);
                            domain.SetBeginMonth(firstMonth);
                            domain.SetBeginDayDow(ParseByte(tokens[1]));
                            domain.SetEndMonth(domain.BeginMonth);
                            domain.SetEndDayDow(ParseByte(tokens[2]));
                            break;
                        }

                        if (tokens.Length == 2)
                        {
                            domain.SetBeginMonth(firstMonth);
                            byte endMonth = GetMonth(tokens[1]);
                            if (endMonth != 0)
                            {
                                domain.SetType(false);
                                domain.SetEndMonth(endMonth);
                            }
                            else if (isDate)
                            {
                                domain.SetType(false);
                                domain.SetBeginDayDow(ParseByte(tokens[1]));
                                domain.SetEndMonth(domain.BeginMonth);
                                domain.SetEndDayDow(domain.BeginDayDow);
                            }
                            else
                            {
                                return [];
                            }

                            break;
                        }

                        if (tokens.Length == 1)
                        {
                            domain.SetType(false);
                            domain.SetBeginMonth(firstMonth);
                            domain.SetEndMonth(firstMonth);
                            break;
                        }

                        if (isNthWeek)
                        {
                            byte month = GetMonth(token);
                            if (month != 0)
                            {
                                domain.SetType(true);
                                if (domain.BeginMonth == 0)
                                {
                                    domain.SetDow(AllDaysOfWeek);
                                    domain.SetBeginMonth(month);
                                    if (!isRange)
                                    {
                                        domain.SetEndMonth(domain.BeginMonth);
                                    }
                                }
                                else
                                {
                                    domain.SetEndMonth(month);
                                    if (isRange &&
                                        isDate &&
                                        index != tokens.Length - 1)
                                    {
                                        if (!tokens[^1].Contains('[', StringComparison.Ordinal))
                                        {
                                            domain.SetEndDayDow(ParseByte(tokens[^1]));
                                            break;
                                        }

                                        endsNthWeek = true;
                                    }
                                }
                            }
                            else
                            {
                                byte dayOfWeek = GetDayOfWeek(token);
                                if (dayOfWeek != 0)
                                {
                                    if (domain.BeginDayDow == 0)
                                    {
                                        domain.SetBeginDayDow(dayOfWeek);
                                    }
                                    else
                                    {
                                        domain.SetEndDayDow(dayOfWeek);
                                    }
                                }
                                else if (token.Contains('[', StringComparison.Ordinal) &&
                                         token.Contains(']', StringComparison.Ordinal))
                                {
                                    byte week = ParseByte(
                                        token.Replace("[", string.Empty, StringComparison.Ordinal)
                                             .Replace("]", string.Empty, StringComparison.Ordinal));
                                    if (domain.BeginWeek == 0 && !endsNthWeek)
                                    {
                                        domain.SetBeginWeek(week);
                                        if (!isRange)
                                        {
                                            domain.SetEndWeek(domain.BeginWeek);
                                        }
                                    }
                                    else
                                    {
                                        domain.SetEndWeek(week);
                                    }
                                }
                                else if (isDate &&
                                         isRange &&
                                         domain.BeginMonth != 0 &&
                                         domain.EndMonth == 0)
                                {
                                    domain.SetBeginDayDow(ParseByte(token));
                                }
                            }
                        }
                    }
                }
                else
                {
                    byte firstDow = GetDayOfWeek(tokens[0]);
                    if (firstDow != 0)
                    {
                        if (!isRange)
                        {
                            if (domain.Type == TimeDomain.NthDow)
                            {
                                domain.SetDow(0);
                            }

                            foreach (string token in tokens)
                            {
                                domain.SetDow(checked((byte)(
                                    domain.Dow + GetDayOfWeekMask(token))));
                            }

                            if (components.Length == 2 &&
                                components[1].Contains('[', StringComparison.Ordinal) &&
                                components[1].Contains(']', StringComparison.Ordinal))
                            {
                                domain.SetType(true);
                                domain.SetBeginWeek(ParseByte(
                                    components[1]
                                        .Replace("[", string.Empty, StringComparison.Ordinal)
                                        .Replace("]", string.Empty, StringComparison.Ordinal)));
                                break;
                            }
                        }
                        else if (tokens.Length == 2)
                        {
                            if (domain.Type == TimeDomain.NthDow)
                            {
                                domain.SetDow(0);
                            }

                            byte begin = GetDayOfWeek(tokens[0]);
                            byte end = GetDayOfWeek(tokens[1]);
                            if (begin > end)
                            {
                                while (begin <= 7)
                                {
                                    domain.SetDow(checked((byte)(
                                        domain.Dow + (1 << (begin - 1)))));
                                    begin++;
                                }

                                begin = 1;
                            }

                            while (begin <= end)
                            {
                                domain.SetDow(checked((byte)(
                                    domain.Dow + (1 << (begin - 1)))));
                                begin++;
                            }
                        }
                        else
                        {
                            return [];
                        }
                    }
                    else
                    {
                        bool applied = false;
                        foreach (string timeRange in tokens)
                        {
                            if (timeRange.Contains('-', StringComparison.Ordinal) &&
                                timeRange.Contains(':', StringComparison.Ordinal))
                            {
                                if (!TryApplyTimeRange(ref domain, timeRange))
                                {
                                    return [];
                                }

                                values.Add(domain.TdValue);
                                applied = true;
                            }
                            else if (isRange && tokens.Length == 2)
                            {
                                if (!TryApplyTimeRange(
                                        ref domain,
                                        string.Join('-', tokens)))
                                {
                                    return [];
                                }

                                values.Add(domain.TdValue);
                                applied = true;
                                break;
                            }
                        }

                        if (!applied &&
                            tokens.Any(token =>
                                token.Contains(':', StringComparison.Ordinal)))
                        {
                            return [];
                        }
                    }
                }
            }

            if (values.Count == 0 && domain.TdValue != 0)
            {
                values.Add(domain.TdValue);
            }
        }
        catch (Exception exception) when (
            exception is FormatException or
            OverflowException or
            InvalidOperationException or
            ArgumentOutOfRangeException)
        {
            return [];
        }

        return values;
    }

    private static string NormalizeDateSyntax(string condition)
    {
        if (BeginWeekdayOfMonthRegex().IsMatch(condition))
        {
            condition = BeginWeekdayOfMonthRegex().Replace(
                condition,
                "$1#$2#$3-$4#$5");
            return condition.Replace("[-1]", "[5]", StringComparison.Ordinal);
        }

        if (EndWeekdayOfMonthRegex().IsMatch(condition))
        {
            condition = EndWeekdayOfMonthRegex().Replace(
                condition,
                "$1#$2-$3#$4#$5");
            return condition.Replace("[-1]", "[5]", StringComparison.Ordinal);
        }

        if (WeekdayOfMonthRegex().IsMatch(condition))
        {
            condition = WeekdayOfMonthRegex().Replace(condition, "$1#$2#$3");
            return condition.Replace("[-1]", "[5]", StringComparison.Ordinal);
        }

        if (WeekdayOfEveryMonthRegex().IsMatch(condition))
        {
            condition = WeekdayOfEveryMonthRegex().Replace(condition, "$1#$2");
            return condition.Replace("[-1]", "[5]", StringComparison.Ordinal);
        }

        if (MonthDayRegex().IsMatch(condition))
        {
            return MonthDayRegex().Replace(condition, "$1#$2");
        }

        if (RangeWithinMonthRegex().IsMatch(condition))
        {
            return RangeWithinMonthRegex().Replace(condition, "$1#$2-$1#$3");
        }

        return MonthRangeRegex().IsMatch(condition)
            ? MonthRangeRegex().Replace(condition, "$1-$2")
            : condition;
    }

    private static bool TryApplyTimeRange(
        ref TimeDomain domain,
        string timeRange)
    {
        string[] bounds = Split(timeRange, '-');
        if (bounds.Length != 2 ||
            !TryParseClock(bounds[0], out byte beginHours, out byte beginMinutes) ||
            !TryParseClock(bounds[1], out byte endHours, out byte endMinutes))
        {
            return false;
        }

        domain.SetBeginHrs(beginHours);
        domain.SetBeginMins(beginMinutes);
        domain.SetEndHrs(endHours);
        domain.SetEndMins(endMinutes);
        return true;
    }

    private static bool TryParseClock(
        string value,
        out byte hours,
        out byte minutes)
    {
        hours = 0;
        minutes = 0;
        string[] parts = value.Split(':');
        return parts.Length == 2 &&
            byte.TryParse(
                parts[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out hours) &&
            byte.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out minutes) &&
            hours <= 24 &&
            minutes <= 60;
    }

    private static string[] Split(string value, char separator) =>
        value.Split(
            separator,
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);

    private static byte ParseByte(string value) =>
        byte.Parse(
            value.Trim().TrimEnd(':'),
            NumberStyles.None,
            CultureInfo.InvariantCulture);

    private static byte GetDayOfWeekMask(string value)
    {
        byte day = GetDayOfWeek(value);
        return day == 0 ? (byte)0 : checked((byte)(1 << (day - 1)));
    }

    private static byte GetDayOfWeek(string value) =>
        value.Trim().TrimEnd(':').ToUpperInvariant() switch
        {
            "SUNDAY" or "SUN" or "SU" => 1,
            "MONDAY" or "MON" or "MO" => 2,
            "TUESDAY" or "TUES" or "TUE" or "TU" => 3,
            "WEDNESDAY" or "WEDS" or "WED" or "WE" => 4,
            "THURSDAY" or "THURS" or "THUR" or "TH" => 5,
            "FRIDAY" or "FRI" or "FR" => 6,
            "SATURDAY" or "SAT" or "SA" => 7,
            _ => 0,
        };

    private static byte GetMonth(string value) =>
        value.Trim().TrimEnd(':').ToUpperInvariant() switch
        {
            "JANUARY" or "JAN" => 1,
            "FEBRUARY" or "FEB" => 2,
            "MARCH" or "MAR" => 3,
            "APRIL" or "APR" => 4,
            "MAY" => 5,
            "JUNE" or "JUN" => 6,
            "JULY" or "JUL" => 7,
            "AUGUST" or "AUG" => 8,
            "SEPTEMBER" or "SEP" or "SEPT" => 9,
            "OCTOBER" or "OCT" => 10,
            "NOVEMBER" or "NOV" => 11,
            "DECEMBER" or "DEC" => 12,
            _ => 0,
        };
}
