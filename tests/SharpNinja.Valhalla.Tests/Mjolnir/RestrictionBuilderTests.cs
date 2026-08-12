// Tests for the faithful C# port of the Valhalla mjolnir RestrictionBuilder + ComplexRestrictionBuilder
// + the GraphTileBuilder deserialize/complex-restriction-serialization path.
//
// Source gtests:
//   valhalla/test/complexrestriction.cc            (ComplexRestriction Sizeof / WriteRead)
//   valhalla/test/gurka/test_simple_restrictions.cc (multi-via complex restriction topology, ported
//                                                    here as a direct write+read of the complex
//                                                    restriction records since the full gurka build
//                                                    pipeline is out of scope)
//
// The end-to-end RestrictionBuilder.Build over the on-disk tile set is exercised by the
// Build_OverTileSet_* tests. The core deliverable - building complex (multi-via) restrictions into
// the tiles in a byte format the ported Baldr reader parses - is verified by writing forward/reverse
// complex restrictions through the GraphTileBuilder and reading them back via
// GraphTile.GetComplexRestrictions / ComplexRestrictionView.WalkVias.

using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public class RestrictionBuilderTests
{
    // ---- ComplexRestriction Sizeof (ported from complexrestriction.cc) ----

    [Fact]
    public void ComplexRestriction_Sizeof_Is24()
    {
        Assert.Equal(24, ComplexRestriction.SizeOfStruct);
    }

    // ---- ComplexRestrictionBuilder WriteRead (ported from complexrestriction.cc) ----

    [Fact]
    public void ComplexRestrictionBuilder_SettersRoundTripThroughTheRecord()
    {
        // Default ComplexRestriction has an invalid from id.
        ComplexRestriction r = ComplexRestriction.Create();
        Assert.False(r.FromGraphId().IsValid());

        var res = new ComplexRestrictionBuilder();
        res.SetFromId(new GraphId(1234, 1, 111));
        Assert.Equal(new GraphId(1234, 1, 111).Value, res.FromGraphId().Value);

        res.SetToId(new GraphId(2345, 1, 2222));
        Assert.Equal(new GraphId(2345, 1, 2222).Value, res.ToGraphId().Value);

        res.SetViaList(MakeVias(5));
        Assert.Equal(5, res.ViaCount());

        // via count is clamped to the max.
        res.SetViaList(MakeVias(ComplexRestriction.MaxViasPerRestriction + 7));
        Assert.Equal(5, res.ViaCount());

        res.SetType(RestrictionType.NoLeftTurn);
        res.SetModes(2224);
        res.SetDt(true);

        // Serialize and read the record back through the Baldr ComplexRestriction reader to confirm
        // the bit-packing matches.
        using var ms = new MemoryStream();
        res.Serialize(ms);
        byte[] bytes = ms.ToArray();

        ComplexRestriction read = ReadRestriction(bytes, 0);
        Assert.Equal(new GraphId(1234, 1, 111).Value, read.FromGraphId().Value);
        Assert.Equal(new GraphId(2345, 1, 2222).Value, read.ToGraphId().Value);
        Assert.Equal(5, read.ViaCount());
        Assert.Equal(RestrictionType.NoLeftTurn, read.Type());
        Assert.Equal((ushort)2224, read.Modes());
        Assert.True(read.HasDt());
    }

    [Fact]
    public void ComplexRestrictionBuilder_DateTimeFieldsRoundTrip()
    {
        var res = new ComplexRestrictionBuilder();
        res.SetFromId(new GraphId(10, 2, 3));
        res.SetToId(new GraphId(10, 2, 9));
        res.SetDt(true);
        res.SetBeginDayDow(3);
        res.SetBeginMonth(7);
        res.SetBeginWeek(4);
        res.SetBeginHrs(5);
        res.SetDtType(true);
        res.SetEndDayDow(2);
        res.SetEndMonth(4);
        res.SetEndWeek(5);
        res.SetEndHrs(15);
        res.SetDow(53);
        res.SetBeginMins(5);
        res.SetEndMins(55);

        using var ms = new MemoryStream();
        res.Serialize(ms);
        ComplexRestriction read = ReadRestriction(ms.ToArray(), 0);

        Assert.True(read.HasDt());
        Assert.Equal(3, read.BeginDayDow());
        Assert.Equal(7, read.BeginMonth());
        Assert.Equal(4, read.BeginWeek());
        Assert.Equal(5, read.BeginHrs());
        Assert.True(read.DtType());
        Assert.Equal(2, read.EndDayDow());
        Assert.Equal(4, read.EndMonth());
        Assert.Equal(5, read.EndWeek());
        Assert.Equal(15, read.EndHrs());
        Assert.Equal(53, read.Dow());
        Assert.Equal(5, read.BeginMins());
        Assert.Equal(55, read.EndMins());
    }

    [Fact]
    public void ComplexRestrictionBuilder_EqualityDetectsDuplicates()
    {
        ComplexRestrictionBuilder a = MakeMultiViaRestriction();
        ComplexRestrictionBuilder b = MakeMultiViaRestriction();
        Assert.True(a.Equals(b));

        var c = new ComplexRestrictionBuilder();
        c.SetFromId(new GraphId(10, 2, 99));
        c.SetToId(new GraphId(10, 2, 100));
        Assert.False(a.Equals(c));
    }

    [Fact]
    public void ComplexRestrictionValueSerializer_MatchesLegacyBuilderExactly()
    {
        var from = new GraphId(10, 2, 11);
        var to = new GraphId(10, 2, 12);
        GraphId[] vias =
        [
            new GraphId(10, 2, 13),
            new GraphId(10, 2, 14),
        ];
        var timeDomain = new TimeDomain();
        timeDomain.SetType(true);
        timeDomain.SetDow(62);
        timeDomain.SetBeginHrs(8);
        timeDomain.SetBeginMins(15);
        timeDomain.SetBeginMonth(3);
        timeDomain.SetBeginDayDow(2);
        timeDomain.SetBeginWeek(1);
        timeDomain.SetEndHrs(17);
        timeDomain.SetEndMins(45);
        timeDomain.SetEndMonth(10);
        timeDomain.SetEndDayDow(4);
        timeDomain.SetEndWeek(3);

        var legacy = new ComplexRestrictionBuilder();
        legacy.SetFromId(from);
        legacy.SetToId(to);
        legacy.SetViaList(vias);
        legacy.SetType(RestrictionType.NoProbable);
        legacy.SetModes(GraphConstants.TruckAccess);
        legacy.SetProbability(73);
        legacy.SetDt(true);
        legacy.SetDtType(timeDomain.Type != 0);
        legacy.SetDow(timeDomain.Dow);
        legacy.SetBeginHrs(timeDomain.BeginHrs);
        legacy.SetBeginMins(timeDomain.BeginMins);
        legacy.SetBeginMonth(timeDomain.BeginMonth);
        legacy.SetBeginDayDow(timeDomain.BeginDayDow);
        legacy.SetBeginWeek(timeDomain.BeginWeek);
        legacy.SetEndHrs(timeDomain.EndHrs);
        legacy.SetEndMins(timeDomain.EndMins);
        legacy.SetEndMonth(timeDomain.EndMonth);
        legacy.SetEndDayDow(timeDomain.EndDayDow);
        legacy.SetEndWeek(timeDomain.EndWeek);

        Span<byte> expected = stackalloc byte[
            ComplexRestriction.SizeOfStruct +
            (ComplexRestriction.MaxViasPerRestriction *
             ComplexRestriction.SizeOfGraphId)];
        Span<byte> actual = stackalloc byte[expected.Length];
        int expectedLength = legacy.Serialize(expected);
        int actualLength = ComplexRestrictionBuilder.Serialize(
            actual,
            from,
            to,
            vias,
            RestrictionType.NoProbable,
            GraphConstants.TruckAccess,
            73,
            timeDomain.TdValue);

        Assert.Equal(expectedLength, actualLength);
        Assert.True(expected[..expectedLength].SequenceEqual(
            actual[..actualLength]));
    }


    // ---- GraphTileBuilder deserialize round-trips a tile byte-identically ----

    [Fact]
    public void GraphTileBuilder_DeserializeReserialize_IsByteIdentical()
    {
        var tileId = new GraphId(0, 2, 0);
        byte[] original = BuildMinimalTile(tileId);

        GraphTile tile = GraphTile.Create(tileId, original);
        var builder = new GraphTileBuilder(tile);
        byte[] roundTripped = builder.StoreTileData();

        Assert.Equal(original.Length, roundTripped.Length);
        Assert.Equal(original, roundTripped);
    }

    // ---- Multi-via complex restriction WRITE + READ round-trip (the core deliverable) ----

    [Fact]
    public void ComplexRestriction_MultiVia_WriteThenReadThroughTile()
    {
        var tileId = new GraphId(0, 2, 0);
        byte[] baseBlob = BuildMinimalTile(tileId);
        GraphTile baseTile = GraphTile.Create(tileId, baseBlob);

        // Deserialize, add a forward + a reverse multi-via complex restriction, then re-serialize.
        var builder = new GraphTileBuilder(baseTile);

        // Forward restriction: from edge 0 -> via edges {1, 2} -> to edge 3, modes = auto.
        var from = new GraphId(0, 2, 0);
        var via1 = new GraphId(0, 2, 1);
        var via2 = new GraphId(0, 2, 2);
        var to = new GraphId(0, 2, 3);

        var fwd = new ComplexRestrictionBuilder();
        fwd.SetFromId(from);
        fwd.SetToId(to);
        fwd.SetViaList(new List<GraphId> { via1, via2 });
        fwd.SetType(RestrictionType.NoLeftTurn);
        fwd.SetModes(GraphConstants.AutoAccess);
        builder.AddForwardComplexRestriction(fwd);

        // Reverse restriction stored against the "to" id (from-id matches on the reverse view).
        var rev = new ComplexRestrictionBuilder();
        rev.SetFromId(to);
        rev.SetToId(from);
        rev.SetViaList(new List<GraphId> { via2, via1 });
        rev.SetType(RestrictionType.NoLeftTurn);
        rev.SetModes(GraphConstants.AutoAccess);
        builder.AddReverseComplexRestriction(rev);

        byte[] blob = builder.StoreTileData();


        // Read the tile back and verify both restrictions parse with the right ids + vias.
        GraphTile tile = GraphTile.Create(tileId, blob);

        ComplexRestrictionView fwdView = tile.GetComplexRestrictions(true, to, GraphConstants.AutoAccess);
        Assert.False(fwdView.Empty());
        (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias) fwdFirst = GetFirst(fwdView);
        Assert.Equal(from.Value, fwdFirst.Restriction.FromGraphId().Value);
        Assert.Equal(to.Value, fwdFirst.Restriction.ToGraphId().Value);
        Assert.Equal(RestrictionType.NoLeftTurn, fwdFirst.Restriction.Type());
        Assert.Equal(2, fwdFirst.Restriction.ViaCount());
        Assert.Equal(via1.Value, fwdFirst.Vias[0].Value);
        Assert.Equal(via2.Value, fwdFirst.Vias[1].Value);

        ComplexRestrictionView revView = tile.GetComplexRestrictions(false, to, GraphConstants.AutoAccess);
        Assert.False(revView.Empty());
        (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias) revFirst = GetFirst(revView);
        Assert.Equal(to.Value, revFirst.Restriction.FromGraphId().Value);
        Assert.Equal(from.Value, revFirst.Restriction.ToGraphId().Value);
        Assert.Equal(2, revFirst.Restriction.ViaCount());
        Assert.Equal(via2.Value, revFirst.Vias[0].Value);
        Assert.Equal(via1.Value, revFirst.Vias[1].Value);
    }

    [Fact]
    public void StreamingRestrictionTileMutation_PreservesEveryUnchangedSection()
    {
        string directory = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            byte[] source = BuildMinimalTile(tileId);
            WriteTileToDisk(directory, tileId, source);
            GraphTile tile = GraphTile.Create(directory, tileId)!;
            GraphTileHeader before = tile.Header();
            byte[] tail = source[(int)before.EdgeinfoOffset()..];

            var mutation = new StreamingRestrictionTileMutation(
                tile,
                64,
                4,
                4,
                4);
            DirectedEdge edge = mutation.DirectedEdgeBuilder(0);
            edge.SetEndRestriction(GraphConstants.AutoAccess);
            mutation.SetDirectedEdgeBuilder(0, edge);

            var forward = new ComplexRestrictionBuilder();
            forward.SetFromId(new GraphId(0, 2, 1));
            forward.SetToId(new GraphId(0, 2, 0));
            forward.SetViaList([new GraphId(0, 2, 2)]);
            forward.SetType(RestrictionType.NoTurn);
            forward.SetModes(GraphConstants.AutoAccess);
            mutation.AddForwardComplexRestriction(forward);

            var reverse = new ComplexRestrictionBuilder();
            reverse.SetFromId(new GraphId(0, 2, 0));
            reverse.SetToId(new GraphId(0, 2, 1));
            reverse.SetViaList([new GraphId(0, 2, 2)]);
            reverse.SetType(RestrictionType.NoTurn);
            reverse.SetModes(GraphConstants.AutoAccess);
            mutation.AddReverseComplexRestriction(reverse);
            mutation.StoreTileData(directory, CancellationToken.None);

            byte[] actual = File.ReadAllBytes(Path.Combine(
                directory,
                GraphTile.FileSuffix(tileId)));
            GraphTileHeader after = GraphTileHeader.FromBytes(actual);
            int addedForward = forward.SizeOf();
            int addedTotal = addedForward + reverse.SizeOf();

            Assert.Equal(
                before.ComplexRestrictionForwardOffset(),
                after.ComplexRestrictionForwardOffset());
            Assert.Equal(
                before.ComplexRestrictionReverseOffset() + addedForward,
                after.ComplexRestrictionReverseOffset());
            Assert.Equal(
                before.EdgeinfoOffset() + addedTotal,
                after.EdgeinfoOffset());
            Assert.Equal(
                before.BoundingCircleOffset(),
                after.BoundingCircleOffset());
            for (int bin = 0; bin < GraphTileHeader.BinCount; bin++)
            {
                Assert.Equal(before.BinOffset(bin), after.BinOffset(bin));
            }

            Assert.Equal(tail, actual[(int)after.EdgeinfoOffset()..]);
            Assert.Equal(
                GraphTileChecksum.ComputeTileHash(
                    actual.AsSpan(GraphTileHeader.HeaderSize)),
                after.TileChecksum());

            GraphTile reopened = GraphTile.Create(directory, tileId)!;
            Assert.Equal(
                GraphConstants.AutoAccess,
                reopened.DirectedEdge(0).EndRestriction);
            Assert.False(reopened.GetComplexRestrictions(
                true,
                forward.ToGraphId(),
                GraphConstants.AutoAccess).Empty());
            Assert.False(reopened.GetComplexRestrictions(
                false,
                reverse.FromGraphId(),
                GraphConstants.AutoAccess).Empty());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
    [Fact]
    public void StreamingRestrictionTileMutation_PreservesPreexistingRestrictions()
    {
        string directory = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            GraphTile initialTile = GraphTile.Create(
                tileId,
                BuildMinimalTile(tileId));
            var initialBuilder = new GraphTileBuilder(initialTile);

            var existingForward = new ComplexRestrictionBuilder();
            existingForward.SetFromId(new GraphId(0, 2, 0));
            existingForward.SetToId(new GraphId(0, 2, 1));
            existingForward.SetViaList([new GraphId(0, 2, 2)]);
            existingForward.SetType(RestrictionType.NoTurn);
            existingForward.SetModes(GraphConstants.AutoAccess);
            initialBuilder.AddForwardComplexRestriction(existingForward);

            var existingReverse = new ComplexRestrictionBuilder();
            existingReverse.SetFromId(new GraphId(0, 2, 1));
            existingReverse.SetToId(new GraphId(0, 2, 0));
            existingReverse.SetViaList([new GraphId(0, 2, 2)]);
            existingReverse.SetType(RestrictionType.NoTurn);
            existingReverse.SetModes(GraphConstants.AutoAccess);
            initialBuilder.AddReverseComplexRestriction(existingReverse);
            WriteTileToDisk(
                directory,
                tileId,
                initialBuilder.StoreTileData());

            GraphTile tile = GraphTile.Create(directory, tileId)!;
            var mutation = new StreamingRestrictionTileMutation(
                tile,
                64,
                4,
                4,
                4);

            var addedForward = new ComplexRestrictionBuilder();
            addedForward.SetFromId(new GraphId(0, 2, 2));
            addedForward.SetToId(new GraphId(0, 2, 3));
            addedForward.SetViaList([new GraphId(0, 2, 1)]);
            addedForward.SetType(RestrictionType.NoTurn);
            addedForward.SetModes(GraphConstants.AutoAccess);
            mutation.AddForwardComplexRestriction(addedForward);

            var addedReverse = new ComplexRestrictionBuilder();
            addedReverse.SetFromId(new GraphId(0, 2, 3));
            addedReverse.SetToId(new GraphId(0, 2, 2));
            addedReverse.SetViaList([new GraphId(0, 2, 1)]);
            addedReverse.SetType(RestrictionType.NoTurn);
            addedReverse.SetModes(GraphConstants.AutoAccess);
            mutation.AddReverseComplexRestriction(addedReverse);
            mutation.StoreTileData(directory, CancellationToken.None);

            GraphTile reopened = GraphTile.Create(directory, tileId)!;
            Assert.False(reopened.GetComplexRestrictions(
                true,
                existingForward.ToGraphId(),
                GraphConstants.AutoAccess).Empty());
            Assert.False(reopened.GetComplexRestrictions(
                false,
                existingReverse.FromGraphId(),
                GraphConstants.AutoAccess).Empty());
            Assert.False(reopened.GetComplexRestrictions(
                true,
                addedForward.ToGraphId(),
                GraphConstants.AutoAccess).Empty());
            Assert.False(reopened.GetComplexRestrictions(
                false,
                addedReverse.FromGraphId(),
                GraphConstants.AutoAccess).Empty());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StreamingRestrictionTileMutation_SourceChangedAfterPlanningFailsClosed()
    {
        string directory = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            WriteTileToDisk(
                directory,
                tileId,
                BuildMinimalTile(tileId));
            GraphTile captured = GraphTile.Create(directory, tileId)!;
            var mutation = new StreamingRestrictionTileMutation(
                captured,
                64,
                4,
                4,
                4);

            var replacementBuilder = new GraphTileBuilder(captured);
            var replacementRestriction = new ComplexRestrictionBuilder();
            replacementRestriction.SetFromId(new GraphId(0, 2, 0));
            replacementRestriction.SetToId(new GraphId(0, 2, 1));
            replacementRestriction.SetViaList([new GraphId(0, 2, 2)]);
            replacementRestriction.SetType(RestrictionType.NoTurn);
            replacementRestriction.SetModes(GraphConstants.AutoAccess);
            replacementBuilder.AddForwardComplexRestriction(
                replacementRestriction);
            byte[] replacement = replacementBuilder.StoreTileData();
            WriteTileToDisk(directory, tileId, replacement);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => mutation.StoreTileData(
                    directory,
                    CancellationToken.None));

            Assert.Contains(
                "changed after restriction mutation planning",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                replacement,
                File.ReadAllBytes(Path.Combine(
                    directory,
                    GraphTile.FileSuffix(tileId))));
            Assert.Empty(Directory.GetFiles(
                Path.GetDirectoryName(Path.Combine(
                    directory,
                    GraphTile.FileSuffix(tileId)))!,
                "*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StreamingRestrictionTileMutation_CurrentBodyChecksumMismatchFailsClosed()
    {
        string directory = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            byte[] source = BuildMinimalTile(tileId);
            WriteTileToDisk(directory, tileId, source);
            GraphTile captured = GraphTile.Create(directory, tileId)!;
            var mutation = new StreamingRestrictionTileMutation(
                captured,
                64,
                4,
                4,
                4);

            byte[] corruptReplacement = source.ToArray();
            int bodyIndex = GraphTileHeader.HeaderSize;
            corruptReplacement[bodyIndex] ^= 0x01;
            WriteTileToDisk(
                directory,
                tileId,
                corruptReplacement);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => mutation.StoreTileData(
                    directory,
                    CancellationToken.None));

            Assert.Contains(
                "does not match its checksum",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                corruptReplacement,
                File.ReadAllBytes(Path.Combine(
                    directory,
                    GraphTile.FileSuffix(tileId))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }


    [Fact]
    public void ResolveStoreOutcome_OperationFailureRemainsPrimaryWhenCleanupFails()
    {
        var operationFailure = new InvalidOperationException("operation");
        var cleanupFailure = new IOException("cleanup");

        Exception actual = Assert.Throws<InvalidOperationException>(
            () => StreamingRestrictionTileMutation.ResolveStoreOutcome(
                operationFailure,
                cleanupFailure));

        Assert.Same(operationFailure, actual);
        Assert.Same(
            cleanupFailure,
            actual.Data[
                "StreamingRestrictionTileMutation.CleanupFailure"]);
    }

    [Fact]
    public void ResolveStoreOutcome_CleanupFailureSurfacesAfterSuccessfulStore()
    {
        var cleanupFailure = new IOException("cleanup");

        Exception actual = Assert.Throws<IOException>(
            () => StreamingRestrictionTileMutation.ResolveStoreOutcome(
                null,
                cleanupFailure));

        Assert.Same(cleanupFailure, actual);
    }



    // ---- RestrictionBuilder.Build over the on-disk tile set (public entry point) ----

    [Fact]
    public void Build_OverTileSet_WithNoRestrictionFlags_LeavesTilesReadable()
    {
        string dir = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            WriteTileToDisk(dir, tileId, BuildMinimalTile(tileId));

            var reader = new GraphReader(new GraphReader.Config { TileDir = dir });
            IReadOnlyList<RestrictionBuilder.Result> results = RestrictionBuilder.Build(
                reader,
                new List<OSMRestriction>(),
                new List<OSMRestriction>(),
                TestContext.Current.CancellationToken);

            // No restrictions to add: every per-level result is empty.
            foreach (RestrictionBuilder.Result r in results)
            {
                Assert.Equal(0u, r.ForwardRestrictionsCount);
                Assert.Equal(0u, r.ReverseRestrictionsCount);
                Assert.Empty(r.Restrictions);
                Assert.Empty(r.PartOfRestriction);
            }

            // The tile is still readable after the build re-wrote it.
            var freshReader = new GraphReader(new GraphReader.Config { TileDir = dir });
            GraphTile? tile = freshReader.GetGraphTile(tileId);
            Assert.NotNull(tile);
            Assert.True(tile!.DirectedEdgeCount() > 0);
        }
        finally
        {
            CleanUp(dir);
        }
    }

    [Fact]
    public void Build_OverTileSet_ReturnsOneResultPerLevelWithTiles()
    {
        string dir = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            WriteTileToDisk(dir, tileId, BuildMinimalTile(tileId));

            var reader = new GraphReader(new GraphReader.Config { TileDir = dir });
            IReadOnlyList<RestrictionBuilder.Result> results = RestrictionBuilder.Build(
                reader,
                new List<OSMRestriction>(),
                new List<OSMRestriction>(),
                TestContext.Current.CancellationToken);

            // One Result per hierarchy level (Build iterates all levels).
            Assert.Equal(TileHierarchy.Levels().Count, results.Count);
        }
        finally
        {
            CleanUp(dir);
        }
    }

    [Fact]
    public void HandleOnlyRestrictionProperties_SecondPhasePreservesFreshFirstPhasePayload()
    {
        string dir = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            WriteTileToDisk(dir, tileId, BuildMinimalTile(tileId));

            var reader = new GraphReader(
                new GraphReader.Config
                {
                    TileDir = dir,
                    MaxCacheSize = 1024 * 1024,
                    UseLruMemCache = true,
                    LruMemCacheHardControl = true,
                });
            Assert.NotNull(reader.GetGraphTile(tileId));

            var from = new GraphId(
                tileId.Tileid(),
                tileId.Level(),
                0);
            var to = new GraphId(
                tileId.Tileid(),
                tileId.Level(),
                1);
            var partOf = new GraphId(
                tileId.Tileid(),
                tileId.Level(),
                2);
            var restriction = new ComplexRestrictionBuilder();
            restriction.SetFromId(from);
            restriction.SetToId(to);
            restriction.SetViaList([]);
            restriction.SetType(RestrictionType.OnlyStraightOn);
            restriction.SetModes(GraphConstants.AutoAccess);

            var result = new RestrictionBuilder.Result();
            result.AddDeferredRestriction(restriction);
            result.AddPartOfRestriction(partOf);

            RestrictionBuilder.DeferredWriteReceipt receipt =
                RestrictionBuilder.HandleOnlyRestrictionProperties(
                    [result],
                    reader,
                    TestContext.Current.CancellationToken);

            Assert.Equal(1U, receipt.SerializedCrossTileForwardCount);
            Assert.Equal(1U, receipt.MarkedCrossTileEdgeCount);
            Assert.Equal(0U, receipt.MissingDestinationTileCount);

            var freshReader = new GraphReader(
                new GraphReader.Config
                {
                    TileDir = dir,
                });
            GraphTile tile =
                freshReader.GetGraphTile(tileId) ??
                throw new InvalidDataException(
                    $"Tile {tileId} was not readable.");
            (
                ComplexRestriction Restriction,
                IReadOnlyList<GraphId> Vias) actual =
                GetFirst(
                    tile.GetComplexRestrictions(
                        forward: true,
                        to,
                        GraphConstants.AutoAccess));
            Assert.Equal(from, actual.Restriction.FromGraphId());
            Assert.Equal(to, actual.Restriction.ToGraphId());
            Assert.True(
                tile.DirectedEdge((int)partOf.Id())
                    .PartOfComplexRestriction);
        }
        finally
        {
            CleanUp(dir);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static List<GraphId> MakeVias(int count)
    {
        var vias = new List<GraphId>(count);
        for (int i = 0; i < count; i++)
        {
            vias.Add(GraphId.Invalid);
        }

        return vias;
    }

    private static ComplexRestrictionBuilder MakeMultiViaRestriction()
    {
        var res = new ComplexRestrictionBuilder();
        res.SetFromId(new GraphId(10, 2, 3));
        res.SetToId(new GraphId(10, 2, 9));
        res.SetViaList(new List<GraphId> { new GraphId(10, 2, 5), new GraphId(10, 2, 7) });
        res.SetType(RestrictionType.NoRightTurn);
        res.SetModes(GraphConstants.AutoAccess);
        return res;
    }

    private static ComplexRestriction ReadRestriction(byte[] bytes, int offset)
    {
        ulong w0 = ReadU64(bytes, offset);
        ulong w1 = ReadU64(bytes, offset + 8);
        ulong w2 = ReadU64(bytes, offset + 16);
        return ComplexRestriction.FromRawWords(w0, w1, w2);
    }

    private static ulong ReadU64(byte[] b, int o)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++)
        {
            v |= (ulong)b[o + i] << (8 * i);
        }

        return v;
    }

    private static (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias) GetFirst(ComplexRestrictionView view)
    {
        foreach ((ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias) entry in view.WithVias())
        {
            return entry;
        }

        throw new System.InvalidOperationException("view was empty");
    }

    [Fact]
    public void StreamingRestrictionPlanApplier_AppliesFixedPlanWithoutBuilderCollections()
    {
        string directory = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            byte[] source = BuildMinimalTile(tileId);
            WriteTileToDisk(directory, tileId, source);
            GraphTile captured = GraphTile.Create(directory, tileId)!;
            GraphTileHeader before = captured.Header();
            var from = new GraphId(0, 2, 1);
            var to = new GraphId(0, 2, 0);
            var via = new GraphId(0, 2, 2);
            byte[] payload = new byte[
                ComplexRestriction.SizeOfStruct +
                ComplexRestriction.SizeOfGraphId];
            int payloadLength = ComplexRestrictionBuilder.Serialize(
                payload,
                from,
                to,
                [via],
                RestrictionType.NoTurn,
                GraphConstants.AutoAccess,
                0,
                0);
            var plan = new TestRestrictionMutationPlanReader(
                [
                    new TestRestrictionPayload(
                        new RestrictionMutationPlanPayload(
                            tileId.Value,
                            RestrictionMutationDirection.Forward,
                            1,
                            checked((ushort)payloadLength)),
                        payload),
                ],
                [
                    new RestrictionMutationPlanEdgePatch(
                        tileId.Value,
                        0,
                        GraphConstants.AutoAccess,
                        0,
                        true,
                        1),
                ]);

            StreamingRestrictionPlanApplier.Apply(
                directory,
                captured,
                plan,
                64,
                CancellationToken.None);

            GraphTile reopened = GraphTile.Create(directory, tileId)!;
            Assert.Equal(
                GraphConstants.AutoAccess,
                reopened.DirectedEdge(0).StartRestriction);
            Assert.True(reopened.DirectedEdge(0).PartOfComplexRestriction);
            (ComplexRestriction Restriction, IReadOnlyList<GraphId> Vias)
                actual = GetFirst(reopened.GetComplexRestrictions(
                    true,
                    to,
                    GraphConstants.AutoAccess));
            Assert.Equal(from.Value, actual.Restriction.FromGraphId().Value);
            Assert.Equal(to.Value, actual.Restriction.ToGraphId().Value);
            Assert.Equal([via.Value], actual.Vias.Select(value => value.Value));

            byte[] rewritten = File.ReadAllBytes(Path.Combine(
                directory,
                GraphTile.FileSuffix(tileId)));
            GraphTileHeader after = GraphTileHeader.FromBytes(rewritten);
            Assert.Equal(
                before.EdgeinfoOffset() + payloadLength,
                after.EdgeinfoOffset());
            Assert.Equal(
                GraphTileChecksum.ComputeTileHash(
                    rewritten.AsSpan(GraphTileHeader.HeaderSize)),
                after.TileChecksum());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void StreamingRestrictionPlanApplier_CurrentBodyChangedWithSameHeaderFailsClosed()
    {
        string directory = NewTileDir();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            byte[] source = BuildMinimalTile(tileId);
            WriteTileToDisk(directory, tileId, source);
            GraphTile captured = GraphTile.Create(directory, tileId)!;
            byte[] replacement = source.ToArray();
            replacement[GraphTileHeader.HeaderSize] ^= 0x01;
            WriteTileToDisk(directory, tileId, replacement);
            var plan = new TestRestrictionMutationPlanReader(
                [],
                [
                    new RestrictionMutationPlanEdgePatch(
                        tileId.Value,
                        0,
                        GraphConstants.AutoAccess,
                        0,
                        false,
                        1),
                ]);

            InvalidDataException exception = Assert.Throws<InvalidDataException>(
                () => StreamingRestrictionPlanApplier.Apply(
                    directory,
                    captured,
                    plan,
                    64,
                    CancellationToken.None));

            Assert.Contains(
                "does not match its checksum",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Equal(
                replacement,
                File.ReadAllBytes(Path.Combine(
                    directory,
                    GraphTile.FileSuffix(tileId))));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private readonly record struct TestRestrictionPayload(
        RestrictionMutationPlanPayload Metadata,
        byte[] Bytes);

    private sealed class TestRestrictionMutationPlanReader(
        TestRestrictionPayload[] restrictions,
        RestrictionMutationPlanEdgePatch[] patches)
        : IRestrictionMutationPlanReader
    {
        public long RestrictionCount => restrictions.LongLength;

        public long EdgePatchCount => patches.LongLength;

        public RestrictionMutationPlanPayload ReadRestriction(long index)
            => restrictions[checked((int)index)].Metadata;

        public void CopyRestrictionPayload(
            long index,
            Span<byte> destination)
        {
            TestRestrictionPayload restriction =
                restrictions[checked((int)index)];
            restriction.Bytes.AsSpan(
                0,
                restriction.Metadata.PayloadLength).CopyTo(destination);
        }

        public RestrictionMutationPlanEdgePatch ReadEdgePatch(long index)
            => patches[checked((int)index)];
    }

    // Builds a minimal but fully valid single-tile blob: one node with 4 outbound directed edges
    // (each pointing back into the tile) and 4 shared edge infos, so the tile reads back through the
    // Baldr GraphTile reader. The edges are auto-accessible and short.
    private static byte[] BuildMinimalTile(GraphId tileId)
    {
        var builder = new GraphTileBuilder(tileId);

        // 5 nodes so each of the 4 edges has a distinct in-tile end node.
        const int edgeCount = 4;

        // Tile base corner + a node placed inside the tile.
        Tiles<PointLL, double> tiling = TileHierarchy.GetTiling((byte)tileId.Level());
        PointLL baseLl = tiling.Base((int)tileId.Tileid());
        var nodeLl = new PointLL(baseLl.Lng + 0.01, baseLl.Lat + 0.01);

        // Edge infos (one per edge, distinct way ids). Shape near the node.
        var shape = new List<PointLL>
        {
            nodeLl,
            new(nodeLl.Lng + 0.001, nodeLl.Lat + 0.001),
        };

        for (uint e = 0; e < edgeCount; e++)
        {
            var nodeA = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            var nodeB = new GraphId(tileId.Tileid(), tileId.Level(), e + 1);
            builder.AddEdgeInfo(
                e,
                nodeA,
                nodeB,
                100 + e, // way id
                0f,
                0,
                0,
                shape,
                new List<string>(),
                new List<string>(),
                new List<string>(),
                0,
                out _);
        }

        // One node owning all 4 edges.
        var node = new NodeInfo(baseLl, nodeLl, GraphConstants.AutoAccess, NodeType.StreetIntersection, false, false, false, false);
        node.SetEdgeIndex(0);
        node.SetEdgeCount(edgeCount);
        builder.Nodes.Add(node);

        // 4 directed edges leaving node 0.
        for (uint e = 0; e < edgeCount; e++)
        {
            DirectedEdge de = DirectedEdge.Create();
            de.SetEndNode(new GraphId(tileId.Tileid(), tileId.Level(), e + 1));
            de.SetForward(true);
            de.SetLength(10);
            de.SetUse(Use.Road);
            de.SetClassification(RoadClass.Residential);
            de.SetForwardAccess(GraphConstants.AutoAccess);
            de.SetReverseAccess(GraphConstants.AutoAccess);
            de.SetEdgeInfoOffset((uint)(e * 0)); // overwritten below from the offset map
            builder.DirectedEdges.Add(de);
        }

        // Wire each directed edge's edge-info offset by re-querying the builder (HasEdgeInfo).
        for (uint e = 0; e < edgeCount; e++)
        {
            var nodeA = new GraphId(tileId.Tileid(), tileId.Level(), 0);
            var nodeB = new GraphId(tileId.Tileid(), tileId.Level(), e + 1);
            builder.HasEdgeInfo(e, nodeA, nodeB, out uint offset);
            DirectedEdge de = builder.DirectedEdgeBuilder((int)e);
            de.SetEdgeInfoOffset(offset);
            builder.SetDirectedEdgeBuilder((int)e, de);
        }

        return builder.StoreTileData();
    }

    private static void WriteTileToDisk(string tileDir, GraphId tileId, byte[] blob)
    {
        string file = Path.Combine(tileDir, GraphTile.FileSuffix(tileId));
        string? dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllBytes(file, blob);
    }

    private static string NewTileDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tm_restr_" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void CleanUp(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
