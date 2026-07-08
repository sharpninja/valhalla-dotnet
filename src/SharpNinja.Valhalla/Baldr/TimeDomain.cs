// Faithful C# port of Valhalla baldr TimeDomain (timedomain.h + src/baldr/timedomain.cc) @ 3.7.0.
// Source: valhalla/baldr/timedomain.h, src/baldr/timedomain.cc
//
// TimeDomain is a C++ union of a single uint64 value and a bit-packed DateRange
// struct. It is read directly from / written directly to tile blobs (inside a
// ConditionalSpeedLimit). The exact bit layout MUST match the C++ so that an
// 8-byte tile word parses identically.
//
// EXACT BIT LAYOUT of the DateRange (LSB first, packed into a uint64):
//   bit   0       ( 1 bit)  : type           (0 = day of month, 1 = nth dow)
//   bits  1..7    ( 7 bits) : dow            (day-of-week mask, week starts Su)
//   bits  8..12   ( 5 bits) : begin_hrs
//   bits 13..18   ( 6 bits) : begin_mins
//   bits 19..22   ( 4 bits) : begin_month
//   bits 23..27   ( 5 bits) : begin_day_dow
//   bits 28..30   ( 3 bits) : begin_week
//   bits 31..35   ( 5 bits) : end_hrs
//   bits 36..41   ( 6 bits) : end_mins
//   bits 42..45   ( 4 bits) : end_month
//   bits 46..50   ( 5 bits) : end_day_dow
//   bits 51..53   ( 3 bits) : end_week
//   bits 54..63   (10 bits) : spare
// Total size: 8 bytes (single uint64).

using System.Globalization;
using System.Text;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Represents a single date/time range from the OSM opening_hours specification,
/// packed into a single 64-bit value. Faithful port of C++ <c>union TimeDomain</c>.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: backed by a single <see cref="ulong"/> (8 bytes), with the
/// DateRange bitfields reproduced exactly. See file header for the full bit map.
/// </remarks>
public struct TimeDomain
{
    /// <summary>kYMD type: <see cref="Type"/> uses day-of-month for begin/end day_dow.</summary>
    public const uint Ymd = 0;

    /// <summary>kNthDow type: <see cref="Type"/> uses nth-day-of-week for begin/end day_dow.</summary>
    public const uint NthDow = 1;

    public const uint MaxDateRangeWeek = 5;
    public const uint MaxDateRangeDowMask = 127;
    public const uint MaxDateRangeHrs = 23;
    public const uint MaxDateRangeMins = 59;
    public const uint MaxDateRangeMonth = 12;
    public const uint MaxDateRangeDay = 31;
    public const uint MaxDateRangeDow = 7;

    // Single 64-bit value representing the date range (the union's `value` member).
    private ulong _value;

    /// <summary>Default constructor. All bits zero. Mirrors C++ <c>TimeDomain()</c>.</summary>
    public TimeDomain()
    {
        _value = 0;
    }

    /// <summary>Constructor with all the datetime bits as a single value. Mirrors C++ <c>TimeDomain(uint64)</c>.</summary>
    public TimeDomain(ulong value)
    {
        _value = value;
    }

    // -- bit field accessors (shift/mask helpers) --
    private const int TypeShift = 0;
    private const int DowShift = 1;
    private const int BeginHrsShift = 8;
    private const int BeginMinsShift = 13;
    private const int BeginMonthShift = 19;
    private const int BeginDayDowShift = 23;
    private const int BeginWeekShift = 28;
    private const int EndHrsShift = 31;
    private const int EndMinsShift = 36;
    private const int EndMonthShift = 42;
    private const int EndDayDowShift = 46;
    private const int EndWeekShift = 51;

    private readonly uint Get(int shift, ulong mask) => (uint)((_value >> shift) & mask);

    private void Set(int shift, ulong mask, ulong v)
        => _value = (_value & ~(mask << shift)) | ((v & mask) << shift);

    /// <summary>Gets the value (all date range bits). Mirrors C++ <c>td_value()</c>.</summary>
    public readonly ulong TdValue => _value;

    /// <summary>Gets the type (0 = day of month [1,31], 1 = nth day of week [1,7]).</summary>
    public readonly byte Type => (byte)Get(TypeShift, 0x1);

    /// <summary>Sets the type.</summary>
    public void SetType(bool type) => Set(TypeShift, 0x1, type ? 1u : 0u);

    /// <summary>Gets the days-of-week mask (e.g. Mo-Fr = 62).</summary>
    public readonly byte Dow => (byte)Get(DowShift, 0x7F);

    /// <summary>Sets the days of week this time domain is active.</summary>
    public void SetDow(byte dow)
    {
        if (dow > MaxDateRangeDowMask)
        {
            throw new InvalidOperationException("Exceeding max dow value. Skipping");
        }

        Set(DowShift, 0x7F, dow);
    }

    /// <summary>Gets the begin hours.</summary>
    public readonly byte BeginHrs => (byte)Get(BeginHrsShift, 0x1F);

    /// <summary>Sets the begin hours.</summary>
    public void SetBeginHrs(byte beginHrs)
    {
        if (beginHrs == 24)
        {
            Set(BeginHrsShift, 0x1F, 0);
        }
        else if (beginHrs > MaxDateRangeHrs)
        {
            throw new InvalidOperationException("Exceeding max begin hrs value. Skipping");
        }
        else
        {
            Set(BeginHrsShift, 0x1F, beginHrs);
        }
    }

    /// <summary>Gets the begin minutes.</summary>
    public readonly byte BeginMins => (byte)Get(BeginMinsShift, 0x3F);

    /// <summary>Sets the begin minutes.</summary>
    public void SetBeginMins(byte beginMins)
    {
        if (beginMins == 60)
        {
            Set(BeginMinsShift, 0x3F, 0);
        }
        else if (beginMins > MaxDateRangeMins)
        {
            throw new InvalidOperationException("Exceeding max begin mins value. Skipping");
        }
        else
        {
            Set(BeginMinsShift, 0x3F, beginMins);
        }
    }

    /// <summary>Gets the begin month (1=Jan..12=Dec, 0 if not set).</summary>
    public readonly byte BeginMonth => (byte)Get(BeginMonthShift, 0xF);

    /// <summary>Sets the begin month.</summary>
    public void SetBeginMonth(byte beginMonth)
    {
        if (beginMonth > MaxDateRangeMonth)
        {
            throw new InvalidOperationException("Exceeding max begin month value. Skipping");
        }

        Set(BeginMonthShift, 0xF, beginMonth);
    }

    /// <summary>Gets the begin day of month or nth dow.</summary>
    public readonly byte BeginDayDow => (byte)Get(BeginDayDowShift, 0x1F);

    /// <summary>Sets the begin day of month or nth dow.</summary>
    public void SetBeginDayDow(byte beginDayDow)
    {
        if (Type == Ymd && beginDayDow > MaxDateRangeDay)
        {
            throw new InvalidOperationException("Exceeding max begin day value. Skipping");
        }
        else if (Type == NthDow && beginDayDow > MaxDateRangeDow)
        {
            throw new InvalidOperationException("Exceeding max begin dow value. Skipping");
        }
        else
        {
            Set(BeginDayDowShift, 0x1F, beginDayDow);
        }
    }

    /// <summary>Gets the begin week (1-5).</summary>
    public readonly byte BeginWeek => (byte)Get(BeginWeekShift, 0x7);

    /// <summary>Sets the begin week.</summary>
    public void SetBeginWeek(byte beginWeek)
    {
        if (beginWeek > MaxDateRangeWeek)
        {
            throw new InvalidOperationException("Exceeding max begin week value. Skipping");
        }

        Set(BeginWeekShift, 0x7, beginWeek);
    }

    /// <summary>Gets the end hours.</summary>
    public readonly byte EndHrs => (byte)Get(EndHrsShift, 0x1F);

    /// <summary>Sets the end hours.</summary>
    public void SetEndHrs(byte endHrs)
    {
        if (endHrs == 24)
        {
            Set(EndHrsShift, 0x1F, 0);
        }
        else if (endHrs > MaxDateRangeHrs)
        {
            throw new InvalidOperationException("Exceeding max end hrs value. Skipping");
        }
        else
        {
            Set(EndHrsShift, 0x1F, endHrs);
        }
    }

    /// <summary>Gets the end minutes.</summary>
    public readonly byte EndMins => (byte)Get(EndMinsShift, 0x3F);

    /// <summary>Sets the end minutes.</summary>
    public void SetEndMins(byte endMins)
    {
        if (endMins == 60)
        {
            Set(EndMinsShift, 0x3F, 0);
        }
        else if (endMins > MaxDateRangeMins)
        {
            throw new InvalidOperationException("Exceeding max end mins value. Skipping");
        }
        else
        {
            Set(EndMinsShift, 0x3F, endMins);
        }
    }

    /// <summary>Gets the end month.</summary>
    public readonly byte EndMonth => (byte)Get(EndMonthShift, 0xF);

    /// <summary>Sets the end month.</summary>
    public void SetEndMonth(byte endMonth)
    {
        if (endMonth > MaxDateRangeMonth)
        {
            throw new InvalidOperationException("Exceeding max end month value. Skipping");
        }

        Set(EndMonthShift, 0xF, endMonth);
    }

    /// <summary>Gets the end day of month or nth dow.</summary>
    public readonly byte EndDayDow => (byte)Get(EndDayDowShift, 0x1F);

    /// <summary>Sets the end day of month or nth dow.</summary>
    public void SetEndDayDow(byte endDayDow)
    {
        if (Type == Ymd && endDayDow > MaxDateRangeDay)
        {
            throw new InvalidOperationException("Exceeding max end day value. Skipping");
        }
        else if (Type == NthDow && endDayDow > MaxDateRangeDow)
        {
            throw new InvalidOperationException("Exceeding max end dow value. Skipping");
        }
        else
        {
            Set(EndDayDowShift, 0x1F, endDayDow);
        }
    }

    /// <summary>Gets the end week (1-5).</summary>
    public readonly byte EndWeek => (byte)Get(EndWeekShift, 0x7);

    /// <summary>Sets the end week.</summary>
    public void SetEndWeek(byte endWeek)
    {
        if (endWeek > MaxDateRangeWeek)
        {
            throw new InvalidOperationException("Exceeding max end week value. Skipping");
        }

        Set(EndWeekShift, 0x7, endWeek);
    }

    /// <summary>Implicit conversion to the underlying 64-bit value. Mirrors C++ cast operator.</summary>
    public static implicit operator ulong(TimeDomain td) => td._value;

    /// <summary>Operator equality. Mirrors C++ <c>operator==</c>.</summary>
    public readonly bool Equals(TimeDomain td) => _value == td._value;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is TimeDomain td && Equals(td);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => _value.GetHashCode();

    /// <summary>Operator equality.</summary>
    public static bool operator ==(TimeDomain a, TimeDomain b) => a._value == b._value;

    /// <summary>Operator inequality.</summary>
    public static bool operator !=(TimeDomain a, TimeDomain b) => a._value != b._value;

    private static readonly string[] MonthNames =
    {
        "", "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec",
    };

    private static readonly string[] DayNames = { "", "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" };

    private static string MonthName(int month) => MonthNames[month];

    private static string DowName(int dow) => DayNames[dow];

    // Format the dow mask into human-readable format. Week starts from Sunday in the mask.
    private static void FormatDow(uint dowMask, StringBuilder ss)
    {
        bool prev = false;
        bool preprev = false;
        bool empty = true;

        for (int i = 0; i < 7; ++i)
        {
            bool curr = (dowMask & (1u << i)) != 0;

            if (curr && !prev)
            {
                if (!empty)
                {
                    ss.Append(',');
                }

                ss.Append(DowName(i + 1));
                empty = false;
            }

            if (!curr && prev && preprev)
            {
                ss.Append('-').Append(DowName(i));
            }

            preprev = prev;
            prev = curr;
        }

        if (prev && preprev)
        {
            ss.Append('-').Append("Sa");
        }
    }

    /// <summary>
    /// Provides a string representation of a condition in a format close to the OSM
    /// opening_hours specification. Faithful port of C++ <c>TimeDomain::to_string()</c>.
    /// </summary>
    public override readonly string ToString()
    {
        var ss = new StringBuilder();

        bool needSpace = false;
        if (Type == Ymd)
        {
            if (BeginMonth != 0)
            {
                ss.Append(MonthName(BeginMonth));
                if (BeginDayDow != 0)
                {
                    ss.Append(' ').Append((int)BeginDayDow);
                }

                needSpace = true;
            }

            if (EndMonth != 0 && (EndMonth != BeginMonth || EndDayDow != BeginDayDow))
            {
                if (EndMonth != BeginMonth || EndDayDow != 0)
                {
                    ss.Append('-');
                }

                if (EndMonth != 0)
                {
                    ss.Append(MonthName(EndMonth));
                    if (EndDayDow != 0)
                    {
                        ss.Append(' ').Append((int)EndDayDow);
                    }

                    needSpace = true;
                }
            }
        }
        else
        {
            if (BeginMonth != 0)
            {
                ss.Append(MonthName(BeginMonth)).Append(' ');
            }

            if (BeginWeek != 0)
            {
                int beginNthWeek = BeginWeek != 5 ? BeginWeek : -1;
                ss.Append(DowName(BeginDayDow)).Append('[').Append(beginNthWeek).Append(']');
            }
            else
            {
                ss.Append((int)BeginDayDow);
            }

            if (EndDayDow != 0 &&
                (EndDayDow != BeginDayDow || EndWeek != BeginWeek || EndMonth != BeginMonth))
            {
                ss.Append('-');
                if (EndMonth != BeginMonth)
                {
                    ss.Append(MonthName(EndMonth)).Append(' ');
                }

                if (EndWeek != 0)
                {
                    int endNthWeek = EndWeek != 5 ? EndWeek : -1;
                    ss.Append(DowName(EndDayDow)).Append('[').Append(endNthWeek).Append(']');
                }
                else
                {
                    ss.Append((int)EndDayDow);
                }
            }

            needSpace = true;
        }

        if (Dow != 0 && Dow != 0b1111111)
        {
            if (needSpace)
            {
                ss.Append(' ');
            }

            FormatDow(Dow, ss);
            needSpace = true;
        }

        if (BeginHrs != 0 || EndHrs != 0 || BeginMins != 0 || EndMins != 0)
        {
            if (needSpace)
            {
                ss.Append(' ');
            }

            ss.Append(((int)BeginHrs).ToString("D2", CultureInfo.InvariantCulture))
              .Append(':')
              .Append(((int)BeginMins).ToString("D2", CultureInfo.InvariantCulture));
            if (EndHrs != 0 || EndMins != 0)
            {
                ss.Append('-')
                  .Append(((int)EndHrs).ToString("D2", CultureInfo.InvariantCulture))
                  .Append(':')
                  .Append(((int)EndMins).ToString("D2", CultureInfo.InvariantCulture));
            }
        }

        return ss.ToString();
    }
}
