using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Traffic.Routing;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class LaneTransitionDeriverTests
{
    private readonly EvidenceBackedLaneTransitionDeriver _sut = new();

    [Fact]
    public void Derive_StraightSameWaySharedNodeWithKnownStableLaneCount_ProvesIdentityContinuity()
    {
        LaneTopologySegment from = Segment(
            "from",
            lanes: 3,
            wayId: 100,
            startNode: 1,
            endNode: 2,
            startHeading: 90,
            endHeading: 90);
        LaneTopologySegment to = Segment(
            "to",
            lanes: 3,
            wayId: 100,
            startNode: 2,
            endNode: 3,
            startHeading: 90,
            endHeading: 90);

        LaneTransitionDerivation actual = _sut.Derive(
            from,
            to,
            new LaneTransitionTopologyContext([to], [from]));

        Assert.Equal(LaneTransitionProvenance.SameWayTopology, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.High, actual.Confidence);
        Assert.Equal(LaneTopologyChangeKind.Continuation, actual.ChangeKind);
        Assert.Equal(
            [(1, 1), (2, 2), (3, 3)],
            actual.Options.Select(static option => (option.FromLane, option.ToLane)));
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.SharedCanonicalNode);
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.KnownStableLaneCount);
    }

    [Fact]
    public void Derive_SameWayLaneDropWithUniqueTurnLaneBranch_ExcludesDroppingLane()
    {
        LaneTopologySegment from = Segment(
            "from",
            lanes: 4,
            wayId: 200,
            startNode: 10,
            endNode: 11,
            startHeading: 90,
            endHeading: 90,
            intents:
            [
                LaneTurnIntent.Through,
                LaneTurnIntent.Through,
                LaneTurnIntent.Through,
                LaneTurnIntent.Right,
            ]);
        LaneTopologySegment continuing = Segment(
            "continuing",
            lanes: 3,
            wayId: 200,
            startNode: 11,
            endNode: 12,
            startHeading: 90,
            endHeading: 90);
        LaneTopologySegment branch = Segment(
            "branch",
            lanes: 1,
            wayId: 201,
            startNode: 11,
            endNode: 13,
            startHeading: 145,
            endHeading: 145,
            use: Use.Ramp);

        LaneTransitionDerivation actual = _sut.Derive(
            from,
            continuing,
            new LaneTransitionTopologyContext(
                [continuing, branch],
                [from]));

        Assert.Equal(LaneTransitionProvenance.TurnLaneInferred, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.High, actual.Confidence);
        Assert.Equal(LaneTopologyChangeKind.LaneDrop, actual.ChangeKind);
        Assert.Equal(
            [(1, 1), (2, 2), (3, 3)],
            actual.Options.Select(static option => (option.FromLane, option.ToLane)));
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.UniqueTurnIntentBranch);
    }

    [Fact]
    public void Derive_RampSplitWithUniqueRightTurnLane_MapsOnlyBranchLane()
    {
        LaneTopologySegment from = Segment(
            "from",
            lanes: 4,
            wayId: 300,
            startNode: 20,
            endNode: 21,
            startHeading: 90,
            endHeading: 90,
            intents:
            [
                LaneTurnIntent.Through,
                LaneTurnIntent.Through,
                LaneTurnIntent.Through,
                LaneTurnIntent.Right,
            ],
            references: ["I 40"]);
        LaneTopologySegment continuing = Segment(
            "continuing",
            lanes: 3,
            wayId: 301,
            startNode: 21,
            endNode: 22,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment ramp = Segment(
            "ramp",
            lanes: 1,
            wayId: 302,
            startNode: 21,
            endNode: 23,
            startHeading: 145,
            endHeading: 145,
            use: Use.Ramp,
            references: ["SR 155"]);

        LaneTransitionDerivation actual = _sut.Derive(
            from,
            ramp,
            new LaneTransitionTopologyContext(
                [continuing, ramp],
                [from]));

        Assert.Equal(LaneTransitionProvenance.TurnLaneInferred, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.High, actual.Confidence);
        Assert.Equal(LaneTopologyChangeKind.RampExit, actual.ChangeKind);
        LaneTransitionOption option = Assert.Single(actual.Options);
        Assert.Equal(4, option.FromLane);
        Assert.Equal(1, option.ToLane);
    }

    [Fact]
    public void Derive_MergeWithOneDistinctInboundSource_ProvesContinuingMainlineLanes()
    {
        LaneTopologySegment mainline = Segment(
            "mainline",
            lanes: 3,
            wayId: 400,
            startNode: 30,
            endNode: 31,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment rightMerge = Segment(
            "right-merge",
            lanes: 1,
            wayId: 401,
            startNode: 32,
            endNode: 31,
            startHeading: 45,
            endHeading: 55,
            use: Use.Ramp);
        LaneTopologySegment merged = Segment(
            "merged",
            lanes: 4,
            wayId: 400,
            startNode: 31,
            endNode: 33,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            mainline,
            merged,
            new LaneTransitionTopologyContext(
                [merged],
                [mainline, rightMerge]));

        Assert.Equal(LaneTransitionProvenance.SameWayTopology, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.High, actual.Confidence);
        Assert.Equal(LaneTopologyChangeKind.Merge, actual.ChangeKind);
        Assert.Equal(
            [(1, 1), (2, 2), (3, 3)],
            actual.Options.Select(static option => (option.FromLane, option.ToLane)));
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.UniqueAdditionalInboundSource);
    }

    [Fact]
    public void Derive_LeftSideMerge_ShiftsContinuingMainlineLaneIndices()
    {
        LaneTopologySegment mainline = Segment(
            "mainline",
            lanes: 3,
            wayId: 410,
            startNode: 34,
            endNode: 35,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment leftMerge = Segment(
            "left-merge",
            lanes: 1,
            wayId: 411,
            startNode: 36,
            endNode: 35,
            startHeading: 135,
            endHeading: 125,
            use: Use.Ramp);
        LaneTopologySegment merged = Segment(
            "merged",
            lanes: 4,
            wayId: 410,
            startNode: 35,
            endNode: 37,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            mainline,
            merged,
            new LaneTransitionTopologyContext(
                [merged],
                [mainline, leftMerge]));

        Assert.Equal(LaneTransitionProvenance.SameWayTopology, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.High, actual.Confidence);
        Assert.Equal(LaneTopologyChangeKind.Merge, actual.ChangeKind);
        Assert.Equal(
            [(1, 2), (2, 3), (3, 4)],
            actual.Options.Select(static option => (option.FromLane, option.ToLane)));
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.MergeFromLeft);
    }

    [Fact]
    public void Derive_RightSideMerge_PreservesContinuingMainlineLaneIndices()
    {
        LaneTopologySegment mainline = Segment(
            "mainline",
            lanes: 3,
            wayId: 420,
            startNode: 38,
            endNode: 39,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment rightMerge = Segment(
            "right-merge",
            lanes: 1,
            wayId: 421,
            startNode: 40,
            endNode: 39,
            startHeading: 45,
            endHeading: 55,
            use: Use.Ramp);
        LaneTopologySegment merged = Segment(
            "merged",
            lanes: 4,
            wayId: 420,
            startNode: 39,
            endNode: 41,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            mainline,
            merged,
            new LaneTransitionTopologyContext(
                [merged],
                [mainline, rightMerge]));

        Assert.Equal(
            [(1, 1), (2, 2), (3, 3)],
            actual.Options.Select(static option => (option.FromLane, option.ToLane)));
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.MergeFromRight);
    }

    [Fact]
    public void Derive_MergeWithParallelInboundGeometry_RemainsUnavailable()
    {
        LaneTopologySegment mainline = Segment(
            "mainline",
            lanes: 3,
            wayId: 430,
            startNode: 42,
            endNode: 43,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment parallelRamp = Segment(
            "parallel-ramp",
            lanes: 1,
            wayId: 431,
            startNode: 44,
            endNode: 43,
            startHeading: 90,
            endHeading: 90,
            use: Use.Ramp);
        LaneTopologySegment merged = Segment(
            "merged",
            lanes: 4,
            wayId: 430,
            startNode: 43,
            endNode: 45,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            mainline,
            merged,
            new LaneTransitionTopologyContext(
                [merged],
                [mainline, parallelRamp]));

        Assert.Equal(LaneTransitionProvenance.Unavailable, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.Unavailable, actual.Confidence);
        Assert.Empty(actual.Options);
    }

    [Fact]
    public void Derive_ParallelRightExitsWithoutDistinctReferenceOrDestination_RemainsUnavailable()
    {
        LaneTopologySegment from = Segment(
            "from",
            lanes: 4,
            wayId: 500,
            startNode: 40,
            endNode: 41,
            startHeading: 90,
            endHeading: 90,
            intents:
            [
                LaneTurnIntent.Through,
                LaneTurnIntent.Through,
                LaneTurnIntent.Right,
                LaneTurnIntent.Right,
            ]);
        LaneTopologySegment continuing = Segment(
            "continuing",
            lanes: 2,
            wayId: 500,
            startNode: 41,
            endNode: 42,
            startHeading: 90,
            endHeading: 90);
        LaneTopologySegment exitA = Segment(
            "exit-a",
            lanes: 1,
            wayId: 501,
            startNode: 41,
            endNode: 43,
            startHeading: 135,
            endHeading: 135,
            use: Use.Ramp);
        LaneTopologySegment exitB = Segment(
            "exit-b",
            lanes: 1,
            wayId: 502,
            startNode: 41,
            endNode: 44,
            startHeading: 150,
            endHeading: 150,
            use: Use.Ramp);

        LaneTransitionDerivation actual = _sut.Derive(
            from,
            exitA,
            new LaneTransitionTopologyContext(
                [continuing, exitA, exitB],
                [from]));

        Assert.Equal(LaneTransitionProvenance.Unavailable, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.Unavailable, actual.Confidence);
        Assert.Equal(LaneTopologyChangeKind.Ambiguous, actual.ChangeKind);
        Assert.Empty(actual.Options);
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.CompetingTopologyMatch);
    }

    [Fact]
    public void Derive_ExplicitConnectivityForNonAdjacentEdges_RemainsUnavailable()
    {
        LaneTopologySegment from = Segment(
            "from",
            lanes: 2,
            wayId: 600,
            startNode: 50,
            endNode: 51,
            startHeading: 90,
            endHeading: 90);
        LaneTopologySegment staleTarget = Segment(
            "stale-target",
            lanes: 2,
            wayId: 601,
            startNode: 99,
            endNode: 100,
            startHeading: 90,
            endHeading: 90) with
        {
            IncomingConnections =
            [
                new LaneTopologyConnection("600", [1, 2], [1, 2]),
            ],
        };

        LaneTransitionDerivation actual = _sut.Derive(
            from,
            staleTarget,
            new LaneTransitionTopologyContext([staleTarget], [from]));

        Assert.Equal(LaneTransitionProvenance.Unavailable, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.Unavailable, actual.Confidence);
        Assert.Empty(actual.Options);
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.MissingSharedCanonicalNode);
    }

    [Fact]
    public void Derive_SameWayWithDivergentOrCompetingContinuations_RemainsUnavailable()
    {
        LaneTopologySegment from = Segment(
            "from",
            lanes: 3,
            wayId: 700,
            startNode: 60,
            endNode: 61,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment routeTarget = Segment(
            "route-target",
            lanes: 3,
            wayId: 700,
            startNode: 61,
            endNode: 62,
            startHeading: 118,
            endHeading: 118,
            references: ["I 40"]);
        LaneTopologySegment competing = Segment(
            "competing",
            lanes: 3,
            wayId: 701,
            startNode: 61,
            endNode: 63,
            startHeading: 91,
            endHeading: 91,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            from,
            routeTarget,
            new LaneTransitionTopologyContext(
                [routeTarget, competing],
                [from]));

        Assert.Equal(LaneTransitionProvenance.Unavailable, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.Unavailable, actual.Confidence);
        Assert.Empty(actual.Options);
    }

    [Fact]
    public void Derive_OneLaneCrossStreetDoesNotProveMainlineMerge()
    {
        LaneTopologySegment mainline = Segment(
            "mainline",
            lanes: 3,
            wayId: 800,
            startNode: 70,
            endNode: 71,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment crossStreet = Segment(
            "cross-street",
            lanes: 1,
            wayId: 801,
            startNode: 72,
            endNode: 71,
            startHeading: 0,
            endHeading: 0,
            use: Use.Road);
        LaneTopologySegment widerMainline = Segment(
            "wider-mainline",
            lanes: 4,
            wayId: 800,
            startNode: 71,
            endNode: 73,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            mainline,
            widerMainline,
            new LaneTransitionTopologyContext(
                [widerMainline],
                [mainline, crossStreet]));

        Assert.Equal(LaneTransitionProvenance.Unavailable, actual.Provenance);
        Assert.Equal(LaneTransitionConfidence.Unavailable, actual.Confidence);
        Assert.Empty(actual.Options);
        Assert.DoesNotContain(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.UniqueAdditionalInboundSource);
    }

    [Fact]
    public void Derive_IncompleteOutboundContextCannotProveUniqueContinuation()
    {
        LaneTopologySegment from = Segment(
            "from",
            lanes: 3,
            wayId: 900,
            startNode: 80,
            endNode: 81,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment to = Segment(
            "to",
            lanes: 3,
            wayId: 900,
            startNode: 81,
            endNode: 82,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            from,
            to,
            new LaneTransitionTopologyContext(
                [to],
                [from],
                outboundEdgesComplete: false,
                inboundEdgesComplete: true,
                source: LaneTransitionTopologyContextSource.IncompleteGraphTile));

        Assert.Equal(LaneTransitionProvenance.Unavailable, actual.Provenance);
        Assert.Empty(actual.Options);
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.IncompleteTopologyContext);
    }

    [Fact]
    public void Derive_IncompleteInboundContextCannotProveUniqueMerge()
    {
        LaneTopologySegment mainline = Segment(
            "mainline",
            lanes: 3,
            wayId: 910,
            startNode: 90,
            endNode: 91,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);
        LaneTopologySegment ramp = Segment(
            "ramp",
            lanes: 1,
            wayId: 911,
            startNode: 92,
            endNode: 91,
            startHeading: 45,
            endHeading: 55,
            use: Use.Ramp);
        LaneTopologySegment merged = Segment(
            "merged",
            lanes: 4,
            wayId: 910,
            startNode: 91,
            endNode: 93,
            startHeading: 90,
            endHeading: 90,
            references: ["I 40"]);

        LaneTransitionDerivation actual = _sut.Derive(
            mainline,
            merged,
            new LaneTransitionTopologyContext(
                [merged],
                [mainline, ramp],
                outboundEdgesComplete: true,
                inboundEdgesComplete: false,
                source: LaneTransitionTopologyContextSource.IncompleteGraphTile));

        Assert.Equal(LaneTransitionProvenance.Unavailable, actual.Provenance);
        Assert.Empty(actual.Options);
        Assert.Contains(
            actual.Evidence,
            static item => item.Kind == LaneTransitionEvidenceKind.IncompleteTopologyContext);
    }

    [Fact]
    public void ExistingPositionalLaneTopologySegmentConstruction_RemainsSourceCompatible()
    {
        var segment = new LaneTopologySegment(
            "legacy",
            2,
            100d,
            [LaneTurnIntent.Through, LaneTurnIntent.Through],
            []);

        Assert.Null(segment.GraphEvidence);
    }

    private static LaneTopologySegment Segment(
        string id,
        int lanes,
        ulong wayId,
        ulong startNode,
        ulong endNode,
        double startHeading,
        double endHeading,
        IReadOnlyList<LaneTurnIntent>? intents = null,
        Use use = Use.Road,
        IReadOnlyList<string>? references = null,
        IReadOnlyList<string>? destinations = null)
        => new(
            id,
            lanes,
            500d,
            intents ?? Enumerable.Repeat(LaneTurnIntent.None, lanes).ToArray(),
            [])
        {
            CanonicalDirectedEdgeId = (ulong)id.GetHashCode(StringComparison.Ordinal),
            OsmWayId = wayId,
            GraphEvidence = new LaneTopologyGraphEvidence(
                startNode,
                endNode,
                localEdgeIndex: 0,
                startHeading,
                endHeading,
                use,
                laneCountKnown: true,
                references ?? [],
                destinations ?? []),
        };
}
