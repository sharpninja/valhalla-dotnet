using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class RestrictionBuilderPlanTests
{
    [Fact]
    public void BuildPlan_EmptyInputEmitsNothingAndDoesNotWriteTiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            var tileId = new GraphId(0, 2, 0);
            string tilePath = WriteMinimalTile(directory, tileId);
            byte[] before = File.ReadAllBytes(tilePath);
            int writes = 0;
            var sink = new CapturingPlanSink();
            var options = new RestrictionBuilder.ExecutionOptions(
                MaxTilesPerLevel: 1,
                MaxDeferredRestrictions: 1,
                MaxPartOfRestrictionEdges: 1,
                TileWrittenObserver: _ => writes++)
            {
                TraversalDepthCapacity = 32,
                VisitedNodeCapacity = 64,
                TraversedEdgeCapacity = 64,
                TileCatalogProvider = level =>
                    level == tileId.Level()
                        ? new[] { tileId }
                        : Array.Empty<GraphId>(),
            };
            var reader = new GraphReader(
                new GraphReader.Config { TileDir = directory });

            RestrictionMutationPlanReceipt receipt =
                RestrictionBuilder.BuildPlan(
                    reader,
                    Array.Empty<OSMRestriction>(),
                    Array.Empty<OSMRestriction>(),
                    sink,
                    options,
                    TestContext.Current.CancellationToken);

            Assert.Equal(0u, receipt.ForwardRestrictionCount);
            Assert.Equal(0u, receipt.ReverseRestrictionCount);
            Assert.Equal(0u, receipt.EdgePatchCount);
            Assert.Equal(32, receipt.TraversalDepthCapacity);
            Assert.Equal(64, receipt.VisitedNodeCapacity);
            Assert.Equal(64, receipt.TraversedEdgeCapacity);
            Assert.Equal(0, receipt.PeakTraversalDepth);
            Assert.Equal(0, receipt.PeakVisitedNodes);
            Assert.Equal(0, receipt.PeakTraversedEdges);
            Assert.Equal(
                RestrictionBuilder.GetPlanTraversalWorkspaceReservationBytes(
                    options),
                receipt.TraversalWorkspaceReservedBytes);
            Assert.True(receipt.TraversalWorkspaceReservedBytes > 0);
            Assert.Equal(0, sink.RestrictionCount);
            Assert.Equal(0, sink.EdgePatchCount);
            Assert.Equal(0, writes);
            Assert.Equal(before, File.ReadAllBytes(tilePath));
        }
        finally
        {
            DeleteDirectory(directory);
        }
    }

    [Fact]
    public void BuildPlan_PreCanceledTokenDoesNotEnumerateTilesOrEmit()
    {
        bool catalogEnumerated = false;
        var sink = new CapturingPlanSink();
        var options = new RestrictionBuilder.ExecutionOptions(
            MaxTilesPerLevel: 1,
            MaxDeferredRestrictions: 1,
            MaxPartOfRestrictionEdges: 1)
        {
            TileCatalogProvider = _ =>
            {
                catalogEnumerated = true;
                return Array.Empty<GraphId>();
            },
        };
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var reader = new GraphReader(
            new GraphReader.Config { TileDir = CreateMissingTilePath() });

        Assert.Throws<OperationCanceledException>(
            () => RestrictionBuilder.BuildPlan(
                reader,
                Array.Empty<OSMRestriction>(),
                Array.Empty<OSMRestriction>(),
                sink,
                options,
                cancellation.Token));

        Assert.False(catalogEnumerated);
        Assert.Equal(0, sink.RestrictionCount);
        Assert.Equal(0, sink.EdgePatchCount);
    }




    [Fact]
    public void BuildPlan_InvalidTraversalCapacityFailsBeforeCatalogOrEmission()
    {
        bool catalogEnumerated = false;
        var sink = new CapturingPlanSink();
        var options = new RestrictionBuilder.ExecutionOptions(
            MaxTilesPerLevel: 1,
            MaxDeferredRestrictions: 1,
            MaxPartOfRestrictionEdges: 1)
        {
            TraversalDepthCapacity = 0,
            VisitedNodeCapacity = 8,
            TraversedEdgeCapacity = 8,
            TileCatalogProvider = _ =>
            {
                catalogEnumerated = true;
                return Array.Empty<GraphId>();
            },
        };
        var reader = new GraphReader(
            new GraphReader.Config { TileDir = CreateMissingTilePath() });

        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(
                () => RestrictionBuilder.BuildPlan(
                    reader,
                    Array.Empty<OSMRestriction>(),
                    Array.Empty<OSMRestriction>(),
                    sink,
                    options,
                    TestContext.Current.CancellationToken));

        Assert.Contains("traversal-depth", exception.Message);
        Assert.False(catalogEnumerated);
        Assert.Equal(0, sink.RestrictionCount);
        Assert.Equal(0, sink.EdgePatchCount);
    }



    private static string WriteMinimalTile(
        string directory,
        GraphId tileId)
    {
        var builder = new GraphTileBuilder(tileId);
        Tiles<PointLL, double> tiling =
            TileHierarchy.GetTiling((byte)tileId.Level());
        PointLL baseLocation = tiling.Base((int)tileId.Tileid());
        var nodeLocation = new PointLL(
            baseLocation.Lng + 0.01,
            baseLocation.Lat + 0.01);
        var shape = new List<PointLL>
        {
            nodeLocation,
            new(nodeLocation.Lng + 0.001, nodeLocation.Lat + 0.001),
        };
        var source = new GraphId(
            tileId.Tileid(),
            tileId.Level(),
            0);
        var target = new GraphId(
            tileId.Tileid(),
            tileId.Level(),
            1);
        builder.AddEdgeInfo(
            0,
            source,
            target,
            100,
            0f,
            0,
            0,
            shape,
            [],
            [],
            [],
            0,
            out _);

        var node = new NodeInfo(
            baseLocation,
            nodeLocation,
            GraphConstants.AutoAccess,
            NodeType.StreetIntersection,
            false,
            false,
            false,
            false);
        node.SetEdgeIndex(0);
        node.SetEdgeCount(1);
        builder.Nodes.Add(node);

        DirectedEdge edge = DirectedEdge.Create();
        edge.SetEndNode(target);
        edge.SetForward(true);
        edge.SetLength(10);
        edge.SetUse(Use.Road);
        edge.SetClassification(RoadClass.Residential);
        edge.SetForwardAccess(GraphConstants.AutoAccess);
        edge.SetReverseAccess(GraphConstants.AutoAccess);
        builder.DirectedEdges.Add(edge);
        builder.HasEdgeInfo(0, source, target, out uint offset);
        edge = builder.DirectedEdgeBuilder(0);
        edge.SetEdgeInfoOffset(offset);
        builder.SetDirectedEdgeBuilder(0, edge);

        string path = Path.Combine(
            directory,
            GraphTile.FileSuffix(tileId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, builder.StoreTileData());
        return path;
    }


    private static string CreateTempDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "valhalla-restriction-plan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string CreateMissingTilePath() =>
        Path.Combine(
            Path.GetTempPath(),
            "valhalla-restriction-plan-missing-" +
            Guid.NewGuid().ToString("N"));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class CapturingPlanSink :
        IRestrictionMutationPlanSink
    {
        internal int RestrictionCount { get; private set; }

        internal int EdgePatchCount { get; private set; }

        public void EmitRestriction(
            RestrictionMutationDirection direction,
            GraphId tileId,
            GraphId from,
            GraphId to,
            ReadOnlySpan<GraphId> vias,
            RestrictionType type,
            uint modes,
            byte probability,
            ulong timeDomain,
            bool crossTile,
            ulong canonicalOrdinal)
        {
            RestrictionCount++;
        }

        public void EmitEdgePatch(
            GraphId tileId,
            uint directedEdgeIndex,
            uint startRestrictionMask,
            uint endRestrictionMask,
            bool setComplexRestriction,
            bool crossTile,
            ulong canonicalOrdinal)
        {
            EdgePatchCount++;
        }
    }
}