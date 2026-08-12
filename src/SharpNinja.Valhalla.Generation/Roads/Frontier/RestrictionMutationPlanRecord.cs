using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal enum PlannedRestrictionDirection : byte
{
    Forward = 0,
    Reverse = 1,
}

[InlineArray(RestrictionPayloadBuffer.Length)]
internal struct RestrictionPayloadBuffer
{
    internal const int Length =
        ComplexRestriction.SizeOfStruct +
        (ComplexRestriction.MaxViasPerRestriction *
         ComplexRestriction.SizeOfGraphId);

    private byte element0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PlannedRestrictionRecord
{
    internal ulong TileValue;
    internal ulong CanonicalOrdinal;
    internal ushort PayloadLength;
    internal PlannedRestrictionDirection Direction;
    internal byte CrossTile;
    internal RestrictionPayloadBuffer Payload;

    internal readonly GraphId Tile => new(TileValue);

    internal void SetPayload(
        GraphId from,
        GraphId to,
        ReadOnlySpan<GraphId> vias,
        RestrictionType type,
        ushort modes,
        byte probability,
        ulong timeDomain)
    {
        PayloadLength = checked((ushort)ComplexRestrictionBuilder.Serialize(
            Payload,
            from,
            to,
            vias,
            type,
            modes,
            probability,
            timeDomain));
    }

    internal readonly ReadOnlySpan<byte> PayloadSpan()
    {
        ref byte first = ref Unsafe.AsRef(in Payload[0]);
        return MemoryMarshal.CreateReadOnlySpan(
            ref first,
            PayloadLength);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct PlannedEdgePatchRecord(
    ulong TileValue,
    uint EdgeIndex,
    uint StartMaskOr,
    uint EndMaskOr,
    byte SetComplexRestriction,
    byte CrossTile,
    ulong CanonicalOrdinal)
{
    internal GraphId Tile => new(TileValue);
}
