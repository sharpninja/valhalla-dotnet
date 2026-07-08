// Faithful C# port of Valhalla's gtest suite test/graphtileheader.cc (part of the
// "ids-constants" group).
// Source: F:/github/valhalla/test/graphtileheader.cc
//
// The single TEST(GraphtileHeader, TestWriteRead) exercises every getter/setter round-trip,
// the quality-metric clamping, the transit-count limit throws, the version truncation, the
// base lat/lon round-trip, and the edge-bin offset begin/end + out-of-bounds throw.
// EXPECT_EQ -> Assert.Equal; EXPECT_THROW(..., std::runtime_error) ->
// Assert.Throws<InvalidOperationException> (the C# port maps std::runtime_error to
// InvalidOperationException).

using System;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class GraphTileHeaderTests
{
    [Fact]
    public void TestWriteRead()
    {
        // Test building a header and reading back values
        var hdr = new GraphTileHeader();

        var tileId = new GraphId(2555, 2, 0);
        hdr.SetGraphid(tileId);
        Assert.Equal(tileId, hdr.Graphid());

        hdr.SetDateCreated(12345);
        Assert.Equal(12345u, hdr.DateCreated());

        var baseLl = new PointLL(
            ((tileId.Tileid() % 1440) * .25) - 180,
            ((tileId.Tileid() / 1440) * .25) - 90);
        hdr.SetBaseLl(baseLl);
        Assert.Equal(baseLl, hdr.BaseLl());

        const string version = "3.99.99-3a4fe6b";
        hdr.SetVersion(version + "more_characters"); // should be truncated
        Assert.Equal(version, hdr.Version());

        hdr.SetDatasetId(5678);
        Assert.Equal(5678u, hdr.DatasetId());

        hdr.SetDensity(5);
        Assert.Equal(5u, hdr.Density());

        hdr.SetDensity(GraphConstants.MaxDensity + 10);
        Assert.Equal(GraphConstants.MaxDensity, hdr.Density());

        hdr.SetNameQuality(5);
        Assert.Equal(5u, hdr.NameQuality());

        hdr.SetNameQuality(GraphTileHeader.MaxQualityMeasure + 10);
        Assert.Equal(GraphTileHeader.MaxQualityMeasure, hdr.NameQuality());

        hdr.SetSpeedQuality(5);
        Assert.Equal(5u, hdr.SpeedQuality());

        hdr.SetSpeedQuality(GraphTileHeader.MaxQualityMeasure + 10);
        Assert.Equal(GraphTileHeader.MaxQualityMeasure, hdr.SpeedQuality());

        hdr.SetExitQuality(5);
        Assert.Equal(5u, hdr.ExitQuality());

        hdr.SetExitQuality(GraphTileHeader.MaxQualityMeasure + 10);
        Assert.Equal(GraphTileHeader.MaxQualityMeasure, hdr.ExitQuality());

        hdr.SetNodecount(55511);
        Assert.Equal(55511u, hdr.Nodecount());

        hdr.SetTransitioncount(555);
        Assert.Equal(555u, hdr.Transitioncount());

        hdr.SetDirectededgecount(55511);
        Assert.Equal(55511u, hdr.Directededgecount());

        hdr.SetSigncount(55511);
        Assert.Equal(55511u, hdr.Signcount());

        hdr.SetAccessRestrictionCount(55511);
        Assert.Equal(55511u, hdr.AccessRestrictionCount());

        hdr.SetAdmincount(55511);
        Assert.Equal(55511u, hdr.Admincount());

        hdr.SetDeparturecount(555);
        Assert.Equal(555u, hdr.Departurecount());

        Assert.Throws<InvalidOperationException>(
            () => hdr.SetDeparturecount(GraphConstants.MaxTransitDepartures + 1));

        hdr.SetStopcount(555);
        Assert.Equal(555u, hdr.Stopcount());

        Assert.Throws<InvalidOperationException>(
            () => hdr.SetStopcount(GraphConstants.MaxTransitStops + 1));

        hdr.SetRoutecount(555);
        Assert.Equal(555u, hdr.Routecount()); // Header transit route count test failed

        Assert.Throws<InvalidOperationException>(
            () => hdr.SetRoutecount(GraphConstants.MaxTransitRoutes + 1));

        hdr.SetSchedulecount(555);
        Assert.Equal(555u, hdr.Schedulecount()); // Header transit schedule count test failed

        Assert.Throws<InvalidOperationException>(
            () => hdr.SetSchedulecount(GraphConstants.MaxTransitSchedules + 1));

        hdr.SetTransfercount(555);
        Assert.Equal(555u, hdr.Transfercount()); // Header transit transfer count test failed

        Assert.Throws<InvalidOperationException>(
            () => hdr.SetTransfercount(GraphConstants.MaxTransfers + 1));

        hdr.SetComplexRestrictionForwardOffset(55511);
        Assert.Equal(55511u, hdr.ComplexRestrictionForwardOffset());

        hdr.SetComplexRestrictionReverseOffset(55511);
        Assert.Equal(55511u, hdr.ComplexRestrictionReverseOffset());

        hdr.SetEdgeinfoOffset(55511);
        Assert.Equal(55511u, hdr.EdgeinfoOffset());

        hdr.SetTextlistOffset(55511);
        Assert.Equal(55511u, hdr.TextlistOffset());

        // TODO - add tests for edge bin offsets
        var offsets = new uint[GraphTileHeader.BinCount];
        offsets[10] = 66666;
        hdr.SetEdgeBinOffsets(offsets);
        var offset = hdr.BinOffset(10);
        Assert.Equal(66666u, offset.End); // Header edge bin offset test failed

        // Test for trying to access outside the bin index list
        Assert.Throws<InvalidOperationException>(() => hdr.BinOffset(GraphTileHeader.BinCount + 1));

        const ulong checksum = 24189014;
        hdr.SetChecksum(checksum);
        Assert.Equal(checksum, hdr.Checksum());
    }

    /// <summary>
    /// Extra fidelity guard (not in the C++ test): the on-disk image MUST be exactly 272 bytes,
    /// matching the C++ <c>static_assert(sizeof(GraphTileHeader) == 272)</c>.
    /// </summary>
    [Fact]
    public void HeaderImageIsExactly272Bytes()
    {
        var hdr = new GraphTileHeader();
        Assert.Equal(272, GraphTileHeader.HeaderSize);
        Assert.Equal(272, hdr.ToBytes().Length);
    }

    /// <summary>
    /// Extra fidelity guard: independent bit-packed count fields must not alias one another.
    /// Writing distinct values to every count/offset field and reading them all back verifies the
    /// exact bit positions/masks reproduce the C++ struct layout (no field overwrites a neighbour).
    /// </summary>
    [Fact]
    public void IndependentFieldsDoNotAlias()
    {
        var hdr = new GraphTileHeader();

        hdr.SetGraphid(new GraphId(2555, 2, 0));
        hdr.SetDensity(15);
        hdr.SetNameQuality(14);
        hdr.SetSpeedQuality(13);
        hdr.SetExitQuality(12);
        hdr.SetHasElevation(true);
        hdr.SetHasExtDirectededge(true);

        hdr.SetNodecount(2097151);          // 21-bit max
        hdr.SetDirectededgecount(2097150);
        hdr.SetPredictedspeedsCount(2097149);

        hdr.SetTransitioncount(4194303);    // 22-bit max
        hdr.SetTurnlaneCount(2097151);      // 21-bit max

        hdr.SetTransfercount(65535);        // 16-bit max
        hdr.SetDeparturecount(16777215);    // 24-bit max
        hdr.SetStopcount(65534);            // 16-bit

        hdr.SetRoutecount(4095);            // 12-bit max
        hdr.SetSchedulecount(4094);         // 12-bit
        hdr.SetSigncount(16777214);         // 24-bit

        hdr.SetAccessRestrictionCount(16777213); // 24-bit
        hdr.SetAdmincount(65533);                // 16-bit

        Assert.Equal(new GraphId(2555, 2, 0), hdr.Graphid());
        Assert.Equal(15u, hdr.Density());
        Assert.Equal(14u, hdr.NameQuality());
        Assert.Equal(13u, hdr.SpeedQuality());
        Assert.Equal(12u, hdr.ExitQuality());
        Assert.True(hdr.HasElevation());
        Assert.True(hdr.HasExtDirectededge());

        Assert.Equal(2097151u, hdr.Nodecount());
        Assert.Equal(2097150u, hdr.Directededgecount());
        Assert.Equal(2097149u, hdr.PredictedspeedsCount());

        Assert.Equal(4194303u, hdr.Transitioncount());
        Assert.Equal(2097151u, hdr.TurnlaneCount());

        Assert.Equal(65535u, hdr.Transfercount());
        Assert.Equal(16777215u, hdr.Departurecount());
        Assert.Equal(65534u, hdr.Stopcount());

        Assert.Equal(4095u, hdr.Routecount());
        Assert.Equal(4094u, hdr.Schedulecount());
        Assert.Equal(16777214u, hdr.Signcount());

        Assert.Equal(16777213u, hdr.AccessRestrictionCount());
        Assert.Equal(65533u, hdr.Admincount());
    }
}
