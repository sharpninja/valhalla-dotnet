// Unit tests for the C# port of thor alternates (valhalla @ 3.7.0).
// Source oracle: src/thor/alternates.cc + valhalla/thor/alternates.h.
//
// Valhalla exercises the alternate-route viability filters through the gurka alternates suite
// (test/gurka/test_alternates.cc) against tiled graphs. The faithful analogue here is a set of pure
// unit tests that construct synthetic PathInfo lists + CandidateConnection lists (no tiles) and pin
// each viability function to its exact upstream math: the sharing threshold interpolation, the
// stretch quadratic, the lower-bound stretch cull, the diff-segment detection, the bounded-detour
// stretch test, and the limited-sharing test (per-edge path_distance deltas).

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla.Tests.Thor;

public sealed class AlternatesTests
{
    // ===================== builders =====================

    private static GraphId E(uint id) => new GraphId(1, 0, id);

    private static PathInfo Pi(GraphId edge, float pathDistance, float elapsed = 0f, float trans = 0f)
        => new PathInfo(TravelMode.Drive, new Cost(elapsed, elapsed), edge, 0, pathDistance,
            tc: new Cost(trans, trans));

    private static PathLocation Loc(double lng, double lat, PointLL projected)
    {
        var loc = new PathLocation(new Location(new PointLL(lng, lat)));
        loc.Edges.Add(new PathLocation.PathEdge(E(1), 0.5, projected, 0));
        return loc;
    }

    private static void AssertClose(double expected, double actual, double tol = 1e-4)
        => Assert.True(Math.Abs(expected - actual) < tol,
            $"expected {expected} but got {actual} (tol {tol})");

    // ===================== get_max_sharing =====================

    [Fact]
    public void MaxSharingForDistance_Below10km_Returns_0_6()
    {
        Assert.Equal(0.6f, Alternates.MaxSharingForDistance(5000f));
    }

    [Fact]
    public void MaxSharingForDistance_At10kmBoundary_Returns_0_6()
    {
        // At exactly 10km the interpolation begins at 0.6 (continuous with the < 10km branch).
        Assert.Equal(0.6f, Alternates.MaxSharingForDistance(10000f));
    }

    [Fact]
    public void MaxSharingForDistance_At55km_Interpolated_Between_0_6_And_0_75()
    {
        // 0.6 + (0.75 - 0.6) * (55000 - 10000) / (100000 - 10000) = 0.6 + 0.15 * 0.5 = 0.675
        AssertClose(0.675, Alternates.MaxSharingForDistance(55000f));
    }

    [Fact]
    public void MaxSharingForDistance_At100kmBoundary_Returns_0_75()
    {
        // distance < 100000 is false at exactly 100km, so returns kAtMostShared.
        Assert.Equal(0.75f, Alternates.MaxSharingForDistance(100000f));
    }

    [Fact]
    public void MaxSharingForDistance_Above100km_Returns_0_75()
    {
        Assert.Equal(0.75f, Alternates.MaxSharingForDistance(150000f));
    }

    [Fact]
    public void GetMaxSharing_PointLL_Delegates_To_Distance_Formula()
    {
        var from = new PointLL(0, 0);
        var to = new PointLL(0.5, 0); // ~55.7km along the equator -> interpolated region
        float expected = Alternates.MaxSharingForDistance((float)from.Distance(to));
        Assert.Equal(expected, Alternates.GetMaxSharing(from, to));
    }

    [Fact]
    public void GetMaxSharing_PathLocation_Reads_Projected_Points()
    {
        // Projected points ~5.5km apart along the equator -> < 10km -> 0.6.
        PathLocation origin = Loc(1.0, 1.0, new PointLL(0, 0));
        PathLocation dest = Loc(2.0, 2.0, new PointLL(0.05, 0));
        Assert.Equal(0.6f, Alternates.GetMaxSharing(origin, dest));
    }

    // ===================== get_at_most_longer =====================

    [Fact]
    public void GetAtMostLonger_Below10min_Returns_2()
    {
        Assert.Equal(2.0, Alternates.GetAtMostLonger(300.0));
    }

    [Fact]
    public void GetAtMostLonger_At10minBoundary_Uses_Quadratic()
    {
        // 600s is NOT < 600, so the quadratic branch runs: a + b/600 + c/600^2 ~= 2.0107875.
        AssertClose(2.0107875, Alternates.GetAtMostLonger(600.0), 1e-4);
    }

    [Fact]
    public void GetAtMostLonger_MidQuadratic_At1Hour()
    {
        // a + b/3600 + c/3600^2 ~= 1.4002527 (approximates the 1.4 anchor at 60 minutes).
        AssertClose(1.4002527, Alternates.GetAtMostLonger(3600.0), 1e-4);
    }

    [Fact]
    public void GetAtMostLonger_At5hoursBoundary_Returns_1_25()
    {
        // 18000s is NOT < 18000, so returns kAtMostLonger.
        Assert.Equal(1.25, Alternates.GetAtMostLonger(18000.0));
    }

    [Fact]
    public void GetAtMostLonger_Above5hours_Returns_1_25()
    {
        Assert.Equal(1.25, Alternates.GetAtMostLonger(20000.0));
    }

    // ===================== filter_alternates_by_stretch =====================

    [Fact]
    public void FilterAlternatesByStretch_Sorts_Ascending_With_Deterministic_TieBreak()
    {
        // Equal cost: deterministic ordering by Edgeid (all costs 100 -> nothing culled).
        var connections = new List<CandidateConnection>
        {
            new CandidateConnection(E(5), E(105), 100f),
            new CandidateConnection(E(2), E(102), 100f),
            new CandidateConnection(E(8), E(108), 100f),
        };

        Alternates.FilterAlternatesByStretch(connections);

        Assert.Equal(3, connections.Count);
        Assert.Equal(E(2), connections[0].Edgeid);
        Assert.Equal(E(5), connections[1].Edgeid);
        Assert.Equal(E(8), connections[2].Edgeid);
    }

    [Fact]
    public void FilterAlternatesByStretch_Culls_Element_Exactly_At_MaxCost()
    {
        // Unsorted input; front cost after sort is 100. get_at_most_longer(100) = 2.0 -> max_cost = 200.
        // lower_bound removes the first element whose cost >= 200 (the 200 element itself) and beyond.
        var connections = new List<CandidateConnection>
        {
            new CandidateConnection(E(4), E(104), 250f),
            new CandidateConnection(E(1), E(101), 100f),
            new CandidateConnection(E(3), E(103), 200f),
            new CandidateConnection(E(2), E(102), 120f),
        };

        Alternates.FilterAlternatesByStretch(connections);

        Assert.Equal(2, connections.Count);
        Assert.Equal(100f, connections[0].Cost);
        Assert.Equal(120f, connections[1].Cost);
    }

    // ===================== CandidateConnection ordering =====================

    [Fact]
    public void CandidateConnection_CompareTo_Orders_By_Cost()
    {
        var a = new CandidateConnection(E(9), E(9), 100f);
        var b = new CandidateConnection(E(1), E(1), 200f);
        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
    }

    [Fact]
    public void CandidateConnection_CompareTo_TieBreaks_On_Edgeid_Then_OppEdgeid()
    {
        var lowEdge = new CandidateConnection(E(2), E(50), 100f);
        var highEdge = new CandidateConnection(E(7), E(10), 100f);
        Assert.True(lowEdge.CompareTo(highEdge) < 0);

        // Same Edgeid, differ on OppEdgeid.
        var lowOpp = new CandidateConnection(E(3), E(4), 100f);
        var highOpp = new CandidateConnection(E(3), E(9), 100f);
        Assert.True(lowOpp.CompareTo(highOpp) < 0);
        Assert.Equal(0, lowOpp.CompareTo(lowOpp));
    }

    [Fact]
    public void LowerBoundByCost_Returns_First_Index_At_Or_Above_Cost()
    {
        var connections = new List<CandidateConnection>
        {
            new CandidateConnection(E(1), E(1), 100f),
            new CandidateConnection(E(2), E(2), 120f),
            new CandidateConnection(E(3), E(3), 200f),
            new CandidateConnection(E(4), E(4), 250f),
        };

        Assert.Equal(2, CandidateConnection.LowerBoundByCost(connections, 200f));
        Assert.Equal(0, CandidateConnection.LowerBoundByCost(connections, 50f));
        Assert.Equal(4, CandidateConnection.LowerBoundByCost(connections, 300f));
    }

    // ===================== get_segment_cost =====================

    [Fact]
    public void GetSegmentCost_First_Zero_Is_Last_Elapsed_Minus_First_Transition()
    {
        var path = new List<PathInfo>
        {
            Pi(E(1), 100f, elapsed: 10f, trans: 2f),
            Pi(E(2), 200f, elapsed: 30f, trans: 3f),
        };

        // last.elapsed(30) - first.transition(2) = 28  (first == 0, no predecessor term)
        Cost c = Alternates.GetSegmentCost(path, 0, 1);
        Assert.Equal(28f, c.CostValue);
    }

    [Fact]
    public void GetSegmentCost_First_NonZero_Subtracts_Previous_Elapsed()
    {
        var path = new List<PathInfo>
        {
            Pi(E(1), 100f, elapsed: 10f, trans: 0f),
            Pi(E(2), 200f, elapsed: 20f, trans: 1f),
            Pi(E(3), 300f, elapsed: 45f, trans: 4f),
        };

        // last(2).elapsed(45) - first(1).transition(1) - prev(0).elapsed(10) = 34
        Cost c = Alternates.GetSegmentCost(path, 1, 2);
        Assert.Equal(34f, c.CostValue);
    }

    // ===================== find_diff_segment =====================

    [Fact]
    public void FindDiffSegment_Isolates_The_Single_Differing_Middle_Edge()
    {
        var optimal = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(3), 300f) };
        var candidate = new List<PathInfo> { Pi(E(1), 100f), Pi(E(9), 250f), Pi(E(3), 350f) };

        var ((o1, o2), (c1, c2)) = Alternates.FindDiffSegment(optimal, candidate);

        Assert.Equal((1, 1), (o1, o2));
        Assert.Equal((1, 1), (c1, c2));
    }

    // ===================== validate_alternate_by_stretch =====================

    [Fact]
    public void ValidateAlternateByStretch_Identical_Paths_Accepted()
    {
        var optimal = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(2), 200f, 20f), Pi(E(3), 300f, 30f) };
        var candidate = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(2), 200f, 20f), Pi(E(3), 300f, 30f) };

        Assert.True(Alternates.ValidateAlternateByStretch(optimal, candidate));
    }

    [Fact]
    public void ValidateAlternateByStretch_Optimal_Strict_Subpath_Of_Candidate_Rejected()
    {
        var optimal = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(2), 200f, 20f), Pi(E(3), 300f, 30f) };
        var candidate = new List<PathInfo>
        {
            Pi(E(1), 100f, 10f), Pi(E(2), 200f, 20f), Pi(E(3), 300f, 30f), Pi(E(4), 400f, 40f),
        };

        Assert.False(Alternates.ValidateAlternateByStretch(optimal, candidate));
    }

    [Fact]
    public void ValidateAlternateByStretch_Detour_Exactly_Twice_Accepted()
    {
        // shared first (E1) + last (E3); differing middle. optimal segment cost = 10, candidate = 20.
        // 2.0 * 10 < 20 is false -> accepted (== boundary is accept per upstream strict <).
        var optimal = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(2), 200f, 20f), Pi(E(3), 300f, 30f) };
        var candidate = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(9), 200f, 30f), Pi(E(3), 300f, 40f) };

        Assert.True(Alternates.ValidateAlternateByStretch(optimal, candidate));
    }

    [Fact]
    public void ValidateAlternateByStretch_Detour_Just_Over_Twice_Rejected()
    {
        // optimal segment cost = 10, candidate segment cost = 20.5. 2.0 * 10 < 20.5 -> rejected.
        var optimal = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(2), 200f, 20f), Pi(E(3), 300f, 30f) };
        var candidate = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(9), 200f, 30.5f), Pi(E(3), 300f, 40f) };

        Assert.False(Alternates.ValidateAlternateByStretch(optimal, candidate));
    }

    [Fact]
    public void ValidateAlternateByStretch_Detour_Sub_Twice_Accepted()
    {
        // optimal segment cost = 10, candidate segment cost = 15. 2.0 * 10 < 15 is false -> accepted.
        var optimal = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(2), 200f, 20f), Pi(E(3), 300f, 30f) };
        var candidate = new List<PathInfo> { Pi(E(1), 100f, 10f), Pi(E(9), 200f, 25f), Pi(E(3), 300f, 35f) };

        Assert.True(Alternates.ValidateAlternateByStretch(optimal, candidate));
    }

    // ===================== validate_alternate_by_sharing =====================

    [Fact]
    public void ValidateAlternateBySharing_Shares_All_Edges_Rejected()
    {
        var accepted = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(3), 300f) };
        var candidate = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(3), 300f) };

        var shared = new List<HashSet<GraphId>>();
        var paths = new List<IReadOnlyList<PathInfo>> { accepted };

        // shared_length 300 > 0.75 * 300 (225) -> rejected.
        Assert.False(Alternates.ValidateAlternateBySharing(shared, paths, candidate, 0.75f));
    }

    [Fact]
    public void ValidateAlternateBySharing_Disjoint_Middle_Under_Threshold_Accepted()
    {
        var accepted = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(3), 300f) };
        // Shares only the endpoints (E1, E3); the middle (E9) is disjoint.
        var candidate = new List<PathInfo> { Pi(E(1), 100f), Pi(E(9), 200f), Pi(E(3), 300f) };

        var shared = new List<HashSet<GraphId>>();
        var paths = new List<IReadOnlyList<PathInfo>> { accepted };

        // shared_length = 100 (E1) + 100 (E3) = 200; 200 > 0.75 * 300 (225) is false -> accepted.
        Assert.True(Alternates.ValidateAlternateBySharing(shared, paths, candidate, 0.75f));
    }

    [Fact]
    public void ValidateAlternateBySharing_Boundary_Equal_Is_Accepted()
    {
        var accepted = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(3), 300f), Pi(E(4), 400f) };
        // Shares E1 + E2 -> shared_length exactly 200; threshold 0.5 * 400 = 200 -> equal -> accepted.
        var candidate = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(8), 300f), Pi(E(9), 400f) };

        var shared = new List<HashSet<GraphId>>();
        var paths = new List<IReadOnlyList<PathInfo>> { accepted };

        Assert.True(Alternates.ValidateAlternateBySharing(shared, paths, candidate, 0.5f));
    }

    [Fact]
    public void ValidateAlternateBySharing_Boundary_Just_Over_Is_Rejected()
    {
        var accepted = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(3), 300f), Pi(E(4), 400f) };
        // Shares E1 (100) + E2 (delta 201-100=101) -> shared_length 201 > 200 -> rejected.
        var candidate = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 201f), Pi(E(8), 300f), Pi(E(9), 400f) };

        var shared = new List<HashSet<GraphId>>();
        var paths = new List<IReadOnlyList<PathInfo>> { accepted };

        Assert.False(Alternates.ValidateAlternateBySharing(shared, paths, candidate, 0.5f));
    }

    [Fact]
    public void ValidateAlternateBySharing_Populates_Shared_Edge_Cache()
    {
        var accepted = new List<PathInfo> { Pi(E(1), 100f), Pi(E(2), 200f), Pi(E(3), 300f) };
        var candidate = new List<PathInfo> { Pi(E(7), 100f), Pi(E(8), 200f) };

        var shared = new List<HashSet<GraphId>>();
        var paths = new List<IReadOnlyList<PathInfo>> { accepted };

        // Disjoint candidate -> accepted, and the shared cache is lazily resized + populated.
        Assert.True(Alternates.ValidateAlternateBySharing(shared, paths, candidate, 0.75f));
        Assert.Single(shared);
        Assert.Equal(new HashSet<GraphId> { E(1), E(2), E(3) }, shared[0]);
    }

    // ===================== validate_alternate_by_local_optimality (stub) =====================

    [Fact]
    public void ValidateAlternateByLocalOptimality_Always_True()
    {
        var path = new List<PathInfo> { Pi(E(1), 100f) };
        Assert.True(Alternates.ValidateAlternateByLocalOptimality(path));
    }
}
