// Faithful packed C# port of Valhalla 3.8.3 baldr TransitDeparture.
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Information held for one departure from a transit stop. The on-disk representation is exactly
/// three little-endian 64-bit words (24 bytes), matching Valhalla 3.8.3.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TransitDeparture : IComparable<TransitDeparture>
{
    /// <summary>Fixed-schedule departure discriminator.</summary>
    public const uint FixedSchedule = 0;

    /// <summary>Frequency-schedule departure discriminator.</summary>
    public const uint FrequencySchedule = 1;

    private const ulong LineIdMask = (1ul << 20) - 1;
    private const ulong RouteIndexMask = (1ul << 12) - 1;
    private const ulong BlockIdMask = (1ul << 20) - 1;
    private const ulong ScheduleIndexMask = (1ul << 12) - 1;
    private const ulong NameOffsetMask = (1ul << 24) - 1;
    private const ulong TimeMask = (1ul << 17) - 1;
    private const ulong FrequencyMask = (1ul << 13) - 1;

    private readonly ulong word0_;
    private readonly ulong word1_;
    private readonly ulong word2_;

    /// <summary>Constructs a fixed-schedule departure.</summary>
    public TransitDeparture(
        uint lineId,
        uint tripId,
        uint routeIndex,
        uint blockId,
        uint headsignOffset,
        uint departureTime,
        uint elapsedTime,
        uint scheduleIndex,
        bool wheelchairAccessible,
        bool bicycleAccessible)
    {
        ValidateCommon(lineId, tripId, routeIndex, blockId, headsignOffset, scheduleIndex);
        if (departureTime > GraphConstants.MaxTransitDepartureTime)
        {
            throw new ArgumentOutOfRangeException(nameof(departureTime));
        }

        uint boundedElapsedTime = Math.Min(elapsedTime, GraphConstants.MaxTransitElapsedTime);
        word0_ = lineId | ((ulong)routeIndex << 20) | ((ulong)tripId << 32);
        word1_ = blockId |
            ((ulong)scheduleIndex << 20) |
            ((ulong)headsignOffset << 32) |
            (wheelchairAccessible ? 1ul << 58 : 0) |
            (bicycleAccessible ? 1ul << 59 : 0);
        word2_ = departureTime | ((ulong)boundedElapsedTime << 17);
    }

    /// <summary>Constructs a frequency-schedule departure.</summary>
    public TransitDeparture(
        uint lineId,
        uint tripId,
        uint routeIndex,
        uint blockId,
        uint headsignOffset,
        uint departureTime,
        uint endTime,
        uint frequency,
        uint elapsedTime,
        uint scheduleIndex,
        bool wheelchairAccessible,
        bool bicycleAccessible)
    {
        ValidateCommon(lineId, tripId, routeIndex, blockId, headsignOffset, scheduleIndex);
        if (departureTime > GraphConstants.MaxTransitDepartureTime)
        {
            throw new ArgumentOutOfRangeException(nameof(departureTime));
        }

        if (endTime > GraphConstants.MaxEndTime)
        {
            throw new ArgumentOutOfRangeException(nameof(endTime));
        }

        if (frequency > GraphConstants.MaxFrequency)
        {
            throw new ArgumentOutOfRangeException(nameof(frequency));
        }

        uint boundedElapsedTime = Math.Min(elapsedTime, GraphConstants.MaxTransitElapsedTime);
        word0_ = lineId | ((ulong)routeIndex << 20) | ((ulong)tripId << 32);
        word1_ = blockId |
            ((ulong)scheduleIndex << 20) |
            ((ulong)headsignOffset << 32) |
            ((ulong)FrequencySchedule << 56) |
            (wheelchairAccessible ? 1ul << 58 : 0) |
            (bicycleAccessible ? 1ul << 59 : 0);
        word2_ = departureTime |
            ((ulong)endTime << 17) |
            ((ulong)frequency << 34) |
            ((ulong)boundedElapsedTime << 47);
    }

    /// <summary>Gets the departure schedule type.</summary>
    public uint Type => (uint)((word1_ >> 56) & 0x3);

    /// <summary>Gets the tile-local line identifier.</summary>
    public uint LineId => (uint)(word0_ & LineIdMask);

    /// <summary>Gets the global internal trip identifier.</summary>
    public uint TripId => (uint)(word0_ >> 32);

    /// <summary>Gets the tile-local route index.</summary>
    public uint RouteIndex => (uint)((word0_ >> 20) & RouteIndexMask);

    /// <summary>Gets the block identifier.</summary>
    public uint BlockId => (uint)(word1_ & BlockIdMask);

    /// <summary>Gets the headsign text-list offset.</summary>
    public uint HeadsignOffset => (uint)((word1_ >> 32) & NameOffsetMask);

    /// <summary>Gets the schedule validity-table index.</summary>
    public uint ScheduleIndex => (uint)((word1_ >> 20) & ScheduleIndexMask);

    /// <summary>Gets the departure time in seconds from midnight.</summary>
    public uint DepartureTime => (uint)(word2_ & TimeMask);

    /// <summary>Gets the elapsed time in seconds to the next stop.</summary>
    public uint ElapsedTime => Type == FixedSchedule
        ? (uint)((word2_ >> 17) & TimeMask)
        : (uint)((word2_ >> 47) & TimeMask);

    /// <summary>Gets the frequency window end time.</summary>
    public uint EndTime => (uint)((word2_ >> 17) & TimeMask);

    /// <summary>Gets the frequency interval in seconds.</summary>
    public uint Frequency => (uint)((word2_ >> 34) & FrequencyMask);

    /// <summary>Gets whether the departure is wheelchair accessible.</summary>
    public bool WheelchairAccessible => ((word1_ >> 58) & 1) != 0;

    /// <summary>Gets whether bicycles are allowed on the departure.</summary>
    public bool BicycleAccessible => ((word1_ >> 59) & 1) != 0;

    /// <inheritdoc />
    public int CompareTo(TransitDeparture other)
    {
        int comparison = LineId.CompareTo(other.LineId);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Type.CompareTo(other.Type);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = DepartureTime.CompareTo(other.DepartureTime);
        return comparison != 0 ? comparison : TripId.CompareTo(other.TripId);
    }

    private static void ValidateCommon(
        uint lineId,
        uint tripId,
        uint routeIndex,
        uint blockId,
        uint headsignOffset,
        uint scheduleIndex)
    {
        if (lineId > GraphConstants.MaxTransitLineId)
        {
            throw new ArgumentOutOfRangeException(nameof(lineId));
        }

        if (tripId > GraphConstants.MaxTripId)
        {
            throw new ArgumentOutOfRangeException(nameof(tripId));
        }

        if (routeIndex > GraphConstants.MaxTransitRoutes)
        {
            throw new ArgumentOutOfRangeException(nameof(routeIndex));
        }

        if (blockId > GraphConstants.MaxTransitBlockId)
        {
            throw new ArgumentOutOfRangeException(nameof(blockId));
        }

        if (headsignOffset > GraphConstants.MaxNameOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(headsignOffset));
        }

        if (scheduleIndex > GraphConstants.MaxTransitSchedules)
        {
            throw new ArgumentOutOfRangeException(nameof(scheduleIndex));
        }
    }
}
