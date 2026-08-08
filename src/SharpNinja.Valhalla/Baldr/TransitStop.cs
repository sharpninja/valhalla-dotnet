// Faithful packed C# port of Valhalla 3.8.3 baldr TransitStop.
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Non-routing transit stop information stored beside the corresponding transit node.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TransitStop
{
    private const ulong NameOffsetMask = (1ul << 24) - 1;
    private readonly ulong word0_;

    /// <summary>Constructs a transit stop record.</summary>
    public TransitStop(
        uint oneStopOffset,
        uint nameOffset,
        bool generated,
        Traversability traversability)
    {
        if (oneStopOffset > GraphConstants.MaxNameOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(oneStopOffset));
        }

        if (nameOffset > GraphConstants.MaxNameOffset)
        {
            throw new ArgumentOutOfRangeException(nameof(nameOffset));
        }

        if ((uint)traversability > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(traversability));
        }

        word0_ = oneStopOffset |
            ((ulong)nameOffset << 24) |
            (generated ? 1ul << 48 : 0) |
            ((ulong)traversability << 49);
    }

    /// <summary>Gets the OneStop identifier text-list offset.</summary>
    public uint OneStopOffset => (uint)(word0_ & NameOffsetMask);

    /// <summary>Gets the stop-name text-list offset.</summary>
    public uint NameOffset => (uint)((word0_ >> 24) & NameOffsetMask);

    /// <summary>Gets whether Valhalla generated the stop.</summary>
    public bool Generated => ((word0_ >> 48) & 1) != 0;

    /// <summary>Gets the real-world egress traversability.</summary>
    public Traversability Traversability => (Traversability)((word0_ >> 49) & 0x3);
}
