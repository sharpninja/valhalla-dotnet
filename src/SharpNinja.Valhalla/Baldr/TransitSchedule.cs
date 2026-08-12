// Faithful packed C# port of Valhalla 3.8.3 baldr TransitSchedule.
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>Validity mask for one transit schedule entry.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TransitSchedule : IComparable<TransitSchedule>
{
    private readonly ulong days_;
    private readonly ulong word1_;

    /// <summary>Constructs a transit schedule validity record.</summary>
    public TransitSchedule(ulong days, uint daysOfWeek, uint endDay)
    {
        if (daysOfWeek > GraphConstants.AllDaysOfWeek)
        {
            throw new ArgumentOutOfRangeException(nameof(daysOfWeek));
        }

        days_ = days;
        word1_ = daysOfWeek | ((ulong)Math.Min(endDay, GraphConstants.MaxEndDay) << 7);
    }

    /// <summary>Gets the tile-creation-relative 64-day validity mask.</summary>
    public ulong Days => days_;

    /// <summary>Gets the recurring days-of-week mask.</summary>
    public uint DaysOfWeek => (uint)(word1_ & 0x7F);

    /// <summary>Gets the last tile-relative day represented by <see cref="Days"/>.</summary>
    public uint EndDay => (uint)((word1_ >> 7) & 0x3F);

    /// <summary>Returns whether this schedule applies to the requested relative day.</summary>
    public bool IsValid(uint day, uint dayOfWeek, bool dateBeforeTile)
    {
        if (!dateBeforeTile && day <= EndDay)
        {
            return day < 64 && (Days & (1ul << (int)day)) != 0;
        }

        return (DaysOfWeek & dayOfWeek) != 0;
    }

    /// <inheritdoc />
    public int CompareTo(TransitSchedule other)
    {
        int comparison = Days.CompareTo(other.Days);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = DaysOfWeek.CompareTo(other.DaysOfWeek);
        return comparison != 0 ? comparison : EndDay.CompareTo(other.EndDay);
    }
}
