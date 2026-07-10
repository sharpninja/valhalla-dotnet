// Faithful C# port of Valhalla thor alternates (valhalla @ 3.7.0).
// Sources:
//   F:/github/valhalla/valhalla/thor/alternates.h   (declarations)
//   F:/github/valhalla/src/thor/alternates.cc        (definitions, ~200 LOC)
//
// Viability tests for alternate paths based on M. Kobitzsch's Alternative Route Techniques (2015).
// Tests verify limited sharing between segments, bounded stretch, and local optimality. Any candidate
// path that meets all the criteria may be considered a valid alternate to the shortest path.
//
// PORT-NOTES:
//   - get_max_sharing takes the loki-correlated origin/destination. Upstream reads the projected point
//     of the first candidate edge via origin.correlation().edges(0).ll(); the ported PathLocation
//     exposes that projected point as Edges[0].Projected (a midgard PointLL). The public overload
//     therefore takes PathLocation (what BidirectionalAStar.FormPath passes) and delegates to an
//     internal PointLL seam; the internal MaxSharingForDistance seam pins the pure threshold math so it
//     can be unit-tested at the exact 10km/100km boundaries without synthesizing lat/lng distances.
//   - filter_alternates_by_stretch sorts the CandidateConnection list and culls with std::lower_bound
//     against a float cost. CandidateConnection (in BidirectionalAStar.cs) carries the IComparable
//     ordering + the LowerBoundByCost helper that reproduce those std::sort / std::lower_bound
//     semantics (first element whose cost >= max_cost).
//   - validate_alternate_by_local_optimality is a `return true` stub upstream; it is ported as-is.
//   - LOG_DEBUG diagnostics are dropped (logging is not part of this slice).

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Viability tests for alternate paths. Faithful port of the free functions in
/// <c>valhalla::thor</c> (alternates.h / alternates.cc).
/// </summary>
public static class Alternates
{
    // Default thresholds (anonymous namespace in alternates.cc).

    // Stretch threshold.
    private const float KAtMostLonger = 1.25f;

    // Alternative route shouldn't contain unreasonable detours. We skip an alternative if it has a
    // detour longer than 2 x cost of the corresponding path in the optimal route.
    private const float KAtMostLongerDetour = 2.0f;

    // Sharing threshold.
    private const float KAtMostShared = 0.75f;

    /// <summary>
    /// Returns the maximum sharing tolerance based on the straight-line origin-&gt;destination distance
    /// of the correlated locations. Faithful port of <c>get_max_sharing</c>.
    /// </summary>
    /// <param name="origin">Correlated origin (its first candidate edge's projected point is used).</param>
    /// <param name="destination">Correlated destination (its first candidate edge's projected point is used).</param>
    public static float GetMaxSharing(PathLocation origin, PathLocation destination)
    {
        // PORT-NOTE: upstream builds PointLL from origin.correlation().edges(0).ll().lng()/.lat(); the
        // ported PathLocation carries that projected point directly as Edges[0].Projected.
        PointLL from = origin.Edges[0].Projected;
        PointLL to = destination.Edges[0].Projected;
        return GetMaxSharing(from, to);
    }

    /// <summary>
    /// PointLL seam for <see cref="GetMaxSharing(PathLocation, PathLocation)"/>: computes the great-circle
    /// distance between the two projected points and maps it to a sharing tolerance.
    /// </summary>
    internal static float GetMaxSharing(PointLL from, PointLL to)
        => MaxSharingForDistance((float)from.Distance(to));

    /// <summary>
    /// Pure threshold math for <see cref="GetMaxSharing(PathLocation, PathLocation)"/>: maps a distance
    /// (meters) to a sharing tolerance, interpolating from 0.6 to 0.75 between 10km and 100km.
    /// </summary>
    internal static float MaxSharingForDistance(float distance)
    {
        // 10km
        if (distance < 10000.0f)
        {
            return 0.6f;
        }

        // 100km
        if (distance < 100000.0f)
        {
            // Uniformly increase 'at_most_shared' from 0.6 to 0.75 for routes from 10km to 100km.
            return 0.6f + ((KAtMostShared - 0.6f) * (distance - 10000.0f) / (100000.0f - 10000.0f));
        }

        // > 100km
        return KAtMostShared;
    }

    /// <summary>
    /// Calculates the stretch threshold based on the optimal route cost. Faithful port of
    /// <c>get_at_most_longer</c>.
    /// </summary>
    /// <param name="optimalCost">The cost of the optimal route.</param>
    public static double GetAtMostLonger(double optimalCost)
    {
        // < 10min
        if (optimalCost < 10.0 * 60.0)
        {
            return 2.0;
        }

        // > 10min and < 5hours
        if (optimalCost < 5.0 * 3600.0)
        {
            // Coefficients of the quadratic-hyperbolic function that approximates the following values:
            // t = [10 * 60, 20 * 60, 30 * 60, 60 * 60, 2 * 3600, 5 * 3600]
            // y = [2.0,     1.75,    1.5,     1.4,     1.3,      1.25]
            const double a = 1.21067994e+00;
            const double b = 7.22941576e+02;
            const double c = -1.45726221e+05;

            return a + (b / optimalCost) + (c / (optimalCost * optimalCost));
        }

        // > 5hours
        return KAtMostLonger;
    }

    /// <summary>
    /// Bounded stretch. Uses cost as an approximation for stretch, filtering out candidate connections
    /// that are much more costly than the optimal cost. Culls the list of connections to only those
    /// within the stretch tolerance. Faithful port of <c>filter_alternates_by_stretch</c>.
    /// </summary>
    /// <param name="connections">The candidate connections; sorted in place and culled.</param>
    public static void FilterAlternatesByStretch(List<CandidateConnection> connections)
    {
        connections.Sort();
        float atMostLonger = (float)GetAtMostLonger(connections[0].Cost);
        float maxCost = connections[0].Cost * atMostLonger;
        int newEnd = CandidateConnection.LowerBoundByCost(connections, maxCost);
        connections.RemoveRange(newEnd, connections.Count - newEnd);
    }

    /// <summary>
    /// Returns the cost of a path segment between indexes <paramref name="first"/> and
    /// <paramref name="last"/>. Faithful port of <c>get_segment_cost</c>.
    /// </summary>
    internal static Cost GetSegmentCost(IReadOnlyList<PathInfo> path, int first, int last)
    {
        Cost cost = path[last].ElapsedCost - path[first].TransitionCost;
        if (first > 0)
        {
            cost -= path[first - 1].ElapsedCost;
        }

        return cost;
    }

    /// <summary>
    /// Finds the single differing segment for two routes. By design bidirectional A* returns routes that
    /// have only one different segment, with the same first and last edges. Returns the
    /// <c>((first,last), (first,last))</c> index pairs of the differing segments in
    /// <paramref name="path1"/> and <paramref name="path2"/>. Faithful port of <c>find_diff_segment</c>.
    /// </summary>
    internal static ((int First, int Last) Seg1, (int First, int Last) Seg2) FindDiffSegment(
        IReadOnlyList<PathInfo> path1, IReadOnlyList<PathInfo> path2)
    {
        int idx1First = 0;
        int idx2First = 0;

        // find first different edge
        while (idx1First < path1.Count && idx2First < path2.Count &&
               path1[idx1First].Edgeid == path2[idx2First].Edgeid)
        {
            ++idx1First;
            ++idx2First;
        }

        // check corner cases: stop if we didn't find a different edge
        if (idx1First == path1.Count)
        {
            return ((idx1First, idx1First), (idx2First, Math.Max(idx2First, path2.Count - 1)));
        }
        else if (idx2First == path2.Count)
        {
            return ((idx1First, Math.Max(idx1First, path1.Count - 1)), (idx2First, idx2First));
        }

        int idx1Last = path1.Count - 1;
        int idx2Last = path2.Count - 1;

        // find last different edge
        while (idx1Last > idx1First && idx2Last > idx2First &&
               path1[idx1Last].Edgeid == path2[idx2Last].Edgeid)
        {
            --idx1Last;
            --idx2Last;
        }

        return ((idx1First, idx1Last), (idx2First, idx2Last));
    }

    /// <summary>
    /// Checks whether the candidate path contains an unreasonably long detour compared to the optimal
    /// path. Faithful port of <c>validate_alternate_by_stretch</c>.
    /// </summary>
    public static bool ValidateAlternateByStretch(
        IReadOnlyList<PathInfo> optimalPath, IReadOnlyList<PathInfo> candidatePath)
    {
        ((int First, int Last) uniqueOptimalSegment, (int First, int Last) uniqueCandidateSegment) =
            FindDiffSegment(optimalPath, candidatePath);

        if (uniqueOptimalSegment.First == optimalPath.Count)
        {
            // return true if the paths are equal, otherwise the optimal path is a subpath of the alternative
            if (uniqueCandidateSegment.First < candidatePath.Count)
            {
                return false;
            }

            return true;
        }

        Cost optimalSegmentCost =
            GetSegmentCost(optimalPath, uniqueOptimalSegment.First, uniqueOptimalSegment.Last);
        Cost candidateSegmentCost =
            GetSegmentCost(candidatePath, uniqueCandidateSegment.First, uniqueCandidateSegment.Last);

        // check if the detour is reasonable
        if (KAtMostLongerDetour * optimalSegmentCost.CostValue < candidateSegmentCost.CostValue)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Limited sharing. Compares the length of edge segments shared between each accepted path and the
    /// candidate path. If they share more than <paramref name="atMostShared"/> the alternate is thrown
    /// out. All shortcuts should be recovered before calling this. Faithful port of
    /// <c>validate_alternate_by_sharing</c>.
    /// </summary>
    /// <param name="sharedEdgeIds">
    /// Per-accepted-path caches of edge ids; carried across the FormPath loop and grown/populated lazily
    /// (mutated in place, mirroring the C++ <c>std::vector&lt;std::unordered_set&lt;GraphId&gt;&gt;&amp;</c>).
    /// </param>
    /// <param name="paths">The accepted paths (fastest path plus any alternates already chosen).</param>
    /// <param name="candidatePath">The candidate alternate under evaluation.</param>
    /// <param name="atMostShared">The sharing tolerance (from <see cref="GetMaxSharing(PathLocation, PathLocation)"/>).</param>
    public static bool ValidateAlternateBySharing(
        List<HashSet<GraphId>> sharedEdgeIds,
        IReadOnlyList<IReadOnlyList<PathInfo>> paths,
        IReadOnlyList<PathInfo> candidatePath,
        float atMostShared)
    {
        // We calculate the overlap in edge duration between the candidate_path and paths (paths is the
        // fastest path + any alternates already chosen).
        if (paths.Count > sharedEdgeIds.Count)
        {
            while (sharedEdgeIds.Count < paths.Count)
            {
                sharedEdgeIds.Add(new HashSet<GraphId>());
            }
        }

        // Check each accepted path against the candidate.
        for (int i = 0; i < paths.Count; ++i)
        {
            // Cache edge ids encountered on the current best path. Shortcuts have already been recovered.
            HashSet<GraphId> shared = sharedEdgeIds[i];
            if (shared.Count == 0)
            {
                foreach (PathInfo pi in paths[i])
                {
                    shared.Add(pi.Edgeid);
                }
            }

            // If an edge on the candidate_path also lies on one of the existing paths, count it as shared.
            float sharedLength = 0.0f;
            for (int c = 0; c < candidatePath.Count; ++c)
            {
                PathInfo cpi = candidatePath[c];
                float length = c == 0
                    ? cpi.PathDistance
                    : cpi.PathDistance - candidatePath[c - 1].PathDistance;
                if (shared.Contains(cpi.Edgeid))
                {
                    sharedLength += length;
                }
            }

            // Throw this alternate away if any chosen path shares more than at_most_shared with it.
            if (sharedLength > atMostShared * paths[i][paths[i].Count - 1].PathDistance)
            {
                return false;
            }
        }

        // this is a viable alternate
        return true;
    }

    /// <summary>
    /// Local optimality check. Faithful port of <c>validate_alternate_by_local_optimality</c>, which is a
    /// <c>return true</c> stub upstream ([TODO] NOT IMPLEMENTED).
    /// </summary>
    public static bool ValidateAlternateByLocalOptimality(IReadOnlyList<PathInfo> candidatePath)
    {
        // [TODO] NOT IMPLEMENTED
        _ = candidatePath;
        return true;
    }
}
