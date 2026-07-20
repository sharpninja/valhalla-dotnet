using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Traffic.Routing;

/// <summary>Creates timezone-independent UTC routing time for native live-traffic lookup.</summary>
public static class InvariantTrafficTime
{
    public static TimeInfo Create(DateTimeOffset departureTimeUtc)
    {
        DateTimeOffset utc = departureTimeUtc.ToUniversalTime();
        int mondayBasedDay = ((int)utc.DayOfWeek + 6) % 7;
        ulong secondOfWeek = checked((ulong)(
            (mondayBasedDay * 86_400)
            + (utc.Hour * 3_600)
            + (utc.Minute * 60)
            + utc.Second));
        return new TimeInfo
        {
            Valid = true,
            TimezoneIndex = 0,
            LocalTime = checked((ulong)utc.ToUnixTimeSeconds()),
            SecondOfWeek = secondOfWeek,
            SecondsFromNow = 0,
            NegativeSecondsFromNow = false,
        };
    }
}
