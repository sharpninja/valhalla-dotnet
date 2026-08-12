using System.Buffers.Binary;
using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;
using SharpNinja.Valhalla.Mjolnir;

using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class RestrictionMutationPlanRecordTests
{
    [Fact]
    public void Records_AreUnmanagedFixedCapacityValues()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<
            PlannedRestrictionRecord>());
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<
            PlannedEdgePatchRecord>());
        Assert.Equal(
            ComplexRestriction.SizeOfStruct +
            (ComplexRestriction.MaxViasPerRestriction *
             ComplexRestriction.SizeOfGraphId),
            RestrictionPayloadBuffer.Length);
    }

    [Fact]
    public void RestrictionRecord_PreservesExactSerializedPayload()
    {
        var from = new GraphId(42, 2, 1);
        var to = new GraphId(42, 2, 2);
        GraphId[] vias =
        [
            new GraphId(42, 2, 3),
            new GraphId(42, 2, 4),
        ];
        var record = new PlannedRestrictionRecord
        {
            TileValue = to.TileBase().Value,
            CanonicalOrdinal = 17,
            Direction = PlannedRestrictionDirection.Forward,
        };
        record.SetPayload(
            from,
            to,
            vias,
            RestrictionType.NoTurn,
            GraphConstants.AutoAccess,
            0,
            0);

        Assert.Equal(to.TileBase(), record.Tile);
        Assert.Equal(
            ComplexRestriction.SizeOfStruct +
            (vias.Length * ComplexRestriction.SizeOfGraphId),
            record.PayloadSpan().Length);
        ReadOnlySpan<byte> payload = record.PayloadSpan();
        ComplexRestriction parsed = ComplexRestriction.FromRawWords(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[16..]));
        Assert.Equal(from, parsed.FromGraphId());
        Assert.Equal(to, parsed.ToGraphId());
        Assert.Equal(vias.Length, parsed.ViaCount());
    }
}
