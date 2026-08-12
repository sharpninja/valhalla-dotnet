// Differential fixture for Valhalla 3.8.3 predicted and historical speed profile generation.
// Official oracle:
//   test/graphtilebuilder.cc GraphTileBuilder.TestDuplicatePredictedSpeeds
//   src/mjolnir/graphtilebuilder.cc AddPredictedSpeed/UpdatePredictedSpeeds
// @ commit a60c7cbfc83e073f50887cd27e0109d02e6b64e5

using System.Buffers.Binary;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class PredictedSpeedGenerationParityTests
{
    [Fact]
    public void ManagedProfiles_MatchOfficialFixture()
    {
        var tileId = new GraphId(0, 2, 0);
        var builder = new GraphTileBuilder(tileId);
        builder.DirectedEdges.Add(new DirectedEdge());
        builder.DirectedEdges.Add(new DirectedEdge());
        builder.DirectedEdges.Add(new DirectedEdge());

        short[] twentyKph = CompressConstantSpeed(20.0f);
        short[] thirtyKph = CompressConstantSpeed(30.0f);

        builder.AddPredictedSpeed(0, twentyKph, predictedCountHint: 3);
        builder.AddPredictedSpeed(1, thirtyKph, predictedCountHint: 3);
        builder.AddPredictedSpeed(2, thirtyKph, predictedCountHint: 3);

        for (int index = 0; index < builder.DirectedEdges.Count; index++)
        {
            DirectedEdge edge = builder.DirectedEdges[index];
            edge.SetHasPredictedSpeed(true);
            edge.SetFreeFlowSpeed((byte)(40 + index));
            edge.SetConstrainedFlowSpeed((byte)(25 + index));
            builder.DirectedEdges[index] = edge;
        }

        byte[] blob = builder.StoreTileData();
        GraphTile tile = GraphTile.Create(tileId, blob);

        Assert.Equal(2u, tile.Header().PredictedspeedsCount());
        Assert.True(tile.Header().PredictedspeedsOffset() >= tile.Header().LaneConnectivityOffset());
        Assert.Equal((uint)blob.Length, tile.Header().EndOffset());

        int offsetsStart = checked((int)tile.Header().PredictedspeedsOffset());
        uint firstOffset = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offsetsStart));
        uint secondOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            blob.AsSpan(offsetsStart + sizeof(uint)));
        uint thirdOffset = BinaryPrimitives.ReadUInt32LittleEndian(
            blob.AsSpan(offsetsStart + (2 * sizeof(uint))));

        Assert.Equal(0u, firstOffset);
        Assert.Equal(PredictedSpeedConstants.CoefficientCount, secondOffset);
        Assert.Equal(secondOffset, thirdOffset);

        Assert.Equal(20.0f, tile.PredictedSpeed(0, 0), 0.1f);
        Assert.Equal(30.0f, tile.PredictedSpeed(1, 0), 0.1f);
        Assert.Equal(30.0f, tile.PredictedSpeed(2, 0), 0.1f);

        Assert.Equal(40u, tile.DirectedEdge(0).FreeFlowSpeed);
        Assert.Equal(26u, tile.DirectedEdge(1).ConstrainedFlowSpeed);
        Assert.All(
            Enumerable.Range(0, 3),
            index => Assert.True(tile.DirectedEdge(index).HasPredictedSpeed));
    }

    [Fact]
    public void DuplicateProfiles_RemainDeduplicatedWhenCapacityHintIsSmall()
    {
        const int edgeCount = 100;
        const int uniqueProfileCount = 50;
        var tileId = new GraphId(0, 2, 0);
        var builder = new GraphTileBuilder(tileId);
        for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
        {
            builder.DirectedEdges.Add(new DirectedEdge());
        }

        short[][] profiles = Enumerable.Range(0, uniqueProfileCount)
            .Select(index => CompressConstantSpeed(10.0f + index))
            .ToArray();
        for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
        {
            builder.AddPredictedSpeed(
                (uint)edgeIndex,
                profiles[edgeIndex % uniqueProfileCount],
                predictedCountHint: 1);
            DirectedEdge edge = builder.DirectedEdges[edgeIndex];
            edge.SetHasPredictedSpeed(true);
            builder.DirectedEdges[edgeIndex] = edge;
        }

        byte[] blob = builder.StoreTileData();
        GraphTile tile = GraphTile.Create(tileId, blob);
        Assert.Equal((uint)uniqueProfileCount, tile.Header().PredictedspeedsCount());

        int offsetsStart = checked((int)tile.Header().PredictedspeedsOffset());
        for (int edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
        {
            Assert.Equal(
                10.0f + (edgeIndex % uniqueProfileCount),
                tile.PredictedSpeed((uint)edgeIndex, 0),
                0.5f);
            if (edgeIndex >= uniqueProfileCount)
            {
                uint offset = ReadProfileOffset(blob, offsetsStart, edgeIndex);
                uint sharedOffset = ReadProfileOffset(
                    blob,
                    offsetsStart,
                    edgeIndex % uniqueProfileCount);
                Assert.Equal(sharedOffset, offset);
            }
        }
    }

    [Fact]
    public void ExistingProfiles_ArePreservedWhenTileIsRewritten()
    {
        var tileId = new GraphId(0, 2, 0);
        var originalBuilder = new GraphTileBuilder(tileId);
        originalBuilder.DirectedEdges.Add(new DirectedEdge());
        originalBuilder.AddPredictedSpeed(0, CompressConstantSpeed(37.0f));
        DirectedEdge edge = originalBuilder.DirectedEdges[0];
        edge.SetHasPredictedSpeed(true);
        originalBuilder.DirectedEdges[0] = edge;

        GraphTile original = GraphTile.Create(tileId, originalBuilder.StoreTileData());
        var rewriteBuilder = new GraphTileBuilder(original);
        GraphTile rewritten = GraphTile.Create(tileId, rewriteBuilder.StoreTileData());

        Assert.Equal(1u, rewritten.Header().PredictedspeedsCount());
        Assert.Equal(37.0f, rewritten.PredictedSpeed(0, 0), 0.5f);
    }

    [Fact]
    public void InvalidProfileOrEdge_FailsBeforeTileMutation()
    {
        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));
        builder.DirectedEdges.Add(new DirectedEdge());

        Assert.Throws<ArgumentOutOfRangeException>(
            () => builder.AddPredictedSpeed(1, new short[PredictedSpeedConstants.CoefficientCount]));
        Assert.Throws<ArgumentException>(
            () => builder.AddPredictedSpeed(
                0,
                new short[PredictedSpeedConstants.CoefficientCount - 1]));
        Assert.Equal(0u, builder.Header().PredictedspeedsCount());
        Assert.Equal(0u, builder.Header().PredictedspeedsOffset());
    }

    private static uint ReadProfileOffset(byte[] blob, int offsetsStart, int edgeIndex)
        => BinaryPrimitives.ReadUInt32LittleEndian(
            blob.AsSpan(offsetsStart + (edgeIndex * sizeof(uint))));

    private static short[] CompressConstantSpeed(float speed)
    {
        var buckets = new float[PredictedSpeedConstants.BucketsPerWeek];
        Array.Fill(buckets, speed);
        return PredictedSpeedCompression.CompressSpeedBuckets(buckets);
    }
}
