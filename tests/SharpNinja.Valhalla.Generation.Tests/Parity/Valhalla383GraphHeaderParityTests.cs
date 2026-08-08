using System.Buffers.Binary;
using SharpNinja.Valhalla.Baldr;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Parity;

public sealed class Valhalla383GraphHeaderParityTests
{
    [Fact]
    public void ManagedWriter_MatchesUpstreamHeaderSemantics()
    {
        const ulong datasetId = 0x0123456789ABCDEF;
        const ushort buildId = 0xBEEF;
        const ulong tileHash = 0x123456789ABC;
        const uint tileSize = 4096;
        const uint boundingCircleOffset = 3072;

        var header = new GraphTileHeader();
        header.SetDatasetId(datasetId);
        header.SetEndOffset(tileSize);
        header.SetBoundingCircleOffset(boundingCircleOffset);
        header.SetRawChecksum(((ulong)buildId << GraphTileHeader.TileHashBits) | tileHash);

        ReadOnlySpan<byte> bytes = header.AsSpan();

        Assert.Equal(272, GraphTileHeader.HeaderSize);
        Assert.Equal(10, GraphTileHeader.EmptySlots);
        Assert.Equal(datasetId, BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(32, 8)));
        Assert.Equal(
            ((ulong)buildId << GraphTileHeader.TileHashBits) | tileHash,
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(88, 8)));
        Assert.Equal(tileSize, BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(224, 4)));
        Assert.Equal(boundingCircleOffset, BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(228, 4)));
        Assert.True(header.HasBoundingCircles());
        Assert.Equal(boundingCircleOffset, header.BoundingCircleOffset());
        Assert.Equal(tileHash, header.TileChecksum());
        Assert.Equal(buildId, header.BuildId());
        Assert.Equal(header.RawChecksum(), header.Checksum());
    }
}

public sealed class GraphFormatCompatibilityTests
{
    [Fact]
    public void Reader_OpensValhalla370And383Fixtures()
    {
        const uint tileSize = 4096;
        byte[] valhalla370Fixture = new byte[GraphTileHeader.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(valhalla370Fixture.AsSpan(224, 4), tileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(valhalla370Fixture.AsSpan(228, 4), tileSize);

        GraphTileHeader legacyHeader = GraphTileHeader.FromBytes(valhalla370Fixture);

        Assert.Equal(tileSize, legacyHeader.EndOffset());
        Assert.False(legacyHeader.HasBoundingCircles());
        Assert.Equal(0u, legacyHeader.BoundingCircleOffset());

        byte[] valhalla383Fixture = new byte[GraphTileHeader.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(valhalla383Fixture.AsSpan(224, 4), tileSize);
        BinaryPrimitives.WriteUInt32LittleEndian(valhalla383Fixture.AsSpan(228, 4), 3072);

        GraphTileHeader currentHeader = GraphTileHeader.FromBytes(valhalla383Fixture);

        Assert.Equal(tileSize, currentHeader.EndOffset());
        Assert.True(currentHeader.HasBoundingCircles());
        Assert.Equal(3072u, currentHeader.BoundingCircleOffset());
    }

    [Fact]
    public void Reader_TreatsZeroBoundingCircleOffsetAsAbsent()
    {
        byte[] fixture = new byte[GraphTileHeader.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.AsSpan(224, 4), 4096);

        GraphTileHeader header = GraphTileHeader.FromBytes(fixture);

        Assert.False(header.HasBoundingCircles());
        Assert.Equal(0u, header.BoundingCircleOffset());
    }
}
