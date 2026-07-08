// Faithful C# port of Valhalla baldr TimeInfo (time_info.h) @ 3.7.0.
// Source: valhalla/baldr/time_info.h
//
// A structure for tracking time information as a route progresses. In C++ this is a struct of
// bit-packed fields plus a non-owning timezone-cache pointer. It is a RUNTIME tracking object,
// NOT an on-disk tile struct, so its in-memory bit packing is not part of tile-blob fidelity;
// the field widths are reproduced as documentation and the values/semantics are preserved exactly.
//
// FIELD LAYOUT (mirrors the C++ bitfields, for reference):
//   valid                     : 1
//   timezone_index            : 9
//   local_time                : 54   (seconds from epoch, adjusted for tz at the location)
//   second_of_week            : 20   (ordinal second from beginning of week, Monday 00:00)
//   seconds_from_now          : 43
//   negative_seconds_from_now : 1
//   tz_cache                  : pointer (non-owning timezone offset cache)
//
// PORT-NOTE: the two static make(...) factory overloads are NOT ported here. They depend on the
//            valhalla protobuf Location type, GraphReader tile access, and the DateTime timezone
//            database (date::tz / get_tz_db / get_formatted_date), all of which are outside the
//            routing-struct subset being ported (protobuf + curler/HTTP tile fetch + datetime tz
//            db are excluded modules). date_time() is likewise omitted (it calls
//            DateTime::seconds_to_date against the tz database). The routing-critical forward()/
//            reverse() offset arithmetic, invalid(), day_seconds(), and equality ARE ported
//            faithfully. The cross-timezone correction inside forward()/reverse() (C++
//            DateTime::timezone_diff) is exposed via an optional delegate so callers that have a
//            timezone database can supply it; when null the behavior matches a route that never
//            changes timezone (tz_diff == 0), which is the common single-timezone case.

using System;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// A structure for tracking time information as a route progresses. Faithful port of C++
/// <c>struct TimeInfo</c> (the routing-relevant subset; see file PORT-NOTE for omissions).
/// </summary>
public struct TimeInfo : IEquatable<TimeInfo>
{
    /// <summary>
    /// Optional cross-timezone correction. Mirrors C++ <c>DateTime::timezone_diff(local_time,
    /// from_tz, to_tz, tz_cache)</c>: returns the signed second offset to apply to the
    /// second-of-week when the route crosses from <paramref name="fromTzIndex"/> into
    /// <paramref name="toTzIndex"/> at the given <paramref name="localTime"/>.
    /// </summary>
    /// <param name="localTime">Local time (seconds from epoch) at which the change occurs.</param>
    /// <param name="fromTzIndex">Timezone index being left.</param>
    /// <param name="toTzIndex">Timezone index being entered.</param>
    /// <returns>Signed second offset for the timezone change.</returns>
    public delegate int TimezoneDiff(ulong localTime, int fromTzIndex, int toTzIndex);

    /// <summary>Whether the provided location had valid time information.</summary>
    public bool Valid;

    /// <summary>Index into the timezone database of the location (used for tz offset along the route).</summary>
    public ulong TimezoneIndex;

    /// <summary>Seconds from epoch adjusted for timezone at the location (local time offset along the route).</summary>
    public ulong LocalTime;

    /// <summary>
    /// The ordinal second from the beginning of the week (Monday 00:00); used to look up historical
    /// traffic as the route progresses.
    /// </summary>
    public ulong SecondOfWeek;

    /// <summary>The distance in seconds from now (magnitude; sign held by <see cref="NegativeSecondsFromNow"/>).</summary>
    public ulong SecondsFromNow;

    /// <summary>The sign bit for <see cref="SecondsFromNow"/> (true means negative).</summary>
    public bool NegativeSecondsFromNow;

    /// <summary>
    /// Creates a <see cref="TimeInfo"/> with default (invalid) parameters. Mirrors C++
    /// <c>TimeInfo::invalid()</c>: { false, 0, 0, kInvalidSecondsOfWeek, 0, false, nullptr }.
    /// </summary>
    public static TimeInfo Invalid() => new TimeInfo
    {
        Valid = false,
        TimezoneIndex = 0,
        LocalTime = 0,
        SecondOfWeek = GraphConstants.InvalidSecondsOfWeek,
        SecondsFromNow = 0,
        NegativeSecondsFromNow = false,
    };

    /// <summary>
    /// Offset all the initial time info to reflect the progress along the route to this point.
    /// Faithful port of C++ <c>TimeInfo::forward(float seconds_offset, int next_tz_index)</c>.
    /// </summary>
    /// <param name="secondsOffset">The number of seconds to offset the TimeInfo by.</param>
    /// <param name="nextTzIndex">The timezone index at the new location.</param>
    /// <param name="timezoneDiff">Optional cross-timezone correction (see <see cref="TimezoneDiff"/>); null = no tz change.</param>
    /// <returns>A new TimeInfo object reflecting the offset.</returns>
    public readonly TimeInfo Forward(float secondsOffset, int nextTzIndex, TimezoneDiff? timezoneDiff = null)
    {
        if (!Valid)
        {
            return this;
        }

        // offset the local time and second of week by the amount traveled to this label
        ulong lt = LocalTime + (ulong)secondsOffset;
        int sw = (int)(SecondOfWeek + (ulong)(long)secondsOffset);

        // if the timezone changed we need to account for that offset as well
        if (nextTzIndex != (int)TimezoneIndex)
        {
            int tzDiff = timezoneDiff?.Invoke(lt, (int)TimezoneIndex, nextTzIndex) ?? 0;
            sw += tzDiff;
        }

        // wrap the week second if it went past the beginning
        if (sw < 0)
        {
            sw += (int)Constants.SecondsPerWeek;
        }
        else if (sw > (int)Constants.SecondsPerWeek)
        {
            // wrap the week second if it went past the end
            sw -= (int)Constants.SecondsPerWeek;
        }

        // offset the distance to now handling the sign
        long sign = ((long)(NegativeSecondsFromNow ? 1 : 0) * -2) + 1;
        long sfn = ((long)SecondsFromNow * sign) + (long)secondsOffset;

        // return the shifted object; seconds-from-now is only useful for date_time type == current
        return new TimeInfo
        {
            Valid = Valid,
            TimezoneIndex = (ulong)nextTzIndex,
            LocalTime = lt,
            SecondOfWeek = (ulong)(uint)sw,
            SecondsFromNow = (ulong)Math.Abs(sfn),
            NegativeSecondsFromNow = sfn < 0,
        };
    }

    /// <summary>
    /// Offset all the initial time info to reflect the progress along the route to this point
    /// (reverse direction). Faithful port of C++ <c>TimeInfo::reverse(float, int)</c>.
    /// </summary>
    /// <param name="secondsOffset">The number of seconds to offset the TimeInfo by.</param>
    /// <param name="nextTzIndex">The timezone index at the new location.</param>
    /// <param name="timezoneDiff">Optional cross-timezone correction (see <see cref="TimezoneDiff"/>); null = no tz change.</param>
    /// <returns>A new TimeInfo object reflecting the offset.</returns>
    public readonly TimeInfo Reverse(float secondsOffset, int nextTzIndex, TimezoneDiff? timezoneDiff = null)
    {
        if (!Valid)
        {
            return this;
        }

        // offset the local time and second of week by the amount traveled to this label
        ulong lt = LocalTime - (ulong)secondsOffset; // dont route near the epoch
        int sw = (int)SecondOfWeek - (int)secondsOffset;

        // if the timezone changed we need to account for that offset as well
        if (nextTzIndex != (int)TimezoneIndex)
        {
            int tzDiff = timezoneDiff?.Invoke(lt, (int)TimezoneIndex, nextTzIndex) ?? 0;
            sw += tzDiff;
        }

        // wrap the week second if it went past the beginning
        if (sw < 0)
        {
            sw += (int)Constants.SecondsPerWeek;
        }
        else if (sw > (int)Constants.SecondsPerWeek)
        {
            // wrap the week second if it went past the end
            sw -= (int)Constants.SecondsPerWeek;
        }

        // offset the distance to now handling the sign
        long sign = ((long)(NegativeSecondsFromNow ? 1 : 0) * -2) + 1;
        long sfn = ((long)SecondsFromNow * sign) - (long)secondsOffset;

        // return the shifted object; seconds-from-now is negative (would be useful for arrive_by)
        return new TimeInfo
        {
            Valid = Valid,
            TimezoneIndex = (ulong)nextTzIndex,
            LocalTime = lt,
            SecondOfWeek = (ulong)(uint)sw,
            SecondsFromNow = (ulong)Math.Abs(sfn),
            NegativeSecondsFromNow = sfn < 0,
        };
    }

    /// <summary>
    /// Gets the second of the day (second_of_week modulo seconds-per-day). Mirrors C++
    /// <c>day_seconds()</c>.
    /// </summary>
    public readonly uint DaySeconds() => (uint)SecondOfWeek % Constants.SecondsPerDay;

    /// <inheritdoc/>
    public readonly bool Equals(TimeInfo other)
        => Valid == other.Valid &&
           TimezoneIndex == other.TimezoneIndex &&
           LocalTime == other.LocalTime &&
           SecondOfWeek == other.SecondOfWeek &&
           SecondsFromNow == other.SecondsFromNow &&
           NegativeSecondsFromNow == other.NegativeSecondsFromNow;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is TimeInfo ti && Equals(ti);

    /// <inheritdoc/>
    public override readonly int GetHashCode()
        => HashCode.Combine(Valid, TimezoneIndex, LocalTime, SecondOfWeek, SecondsFromNow, NegativeSecondsFromNow);

    /// <summary>Operator equality (mirrors C++ <c>operator==</c>).</summary>
    public static bool operator ==(TimeInfo lhs, TimeInfo rhs) => lhs.Equals(rhs);

    /// <summary>Operator inequality.</summary>
    public static bool operator !=(TimeInfo lhs, TimeInfo rhs) => !lhs.Equals(rhs);

    /// <summary>
    /// Diagnostic string representation. Mirrors the C++ <c>operator&lt;&lt;</c> stream output
    /// (minus the tz_cache pointer, which has no managed analogue).
    /// </summary>
    public override readonly string ToString()
        => $"{{valid: {Valid}, timezone_index: {TimezoneIndex}, local_time: {LocalTime}, " +
           $"second_of_week: {SecondOfWeek}, seconds_from_now: {SecondsFromNow}, " +
           $"negative_seconds_from_now: {NegativeSecondsFromNow}}}";
}
