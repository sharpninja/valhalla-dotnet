// Faithful C# port of Valhalla midgard Polyline2<coord_t>.
// Sources: valhalla/midgard/polyline2.h and src/midgard/polyline2.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// The C++ class is templated on coord_t and is explicitly instantiated for
// PointXY<float>, PointXY<double>, GeoPoint<float>, GeoPoint<double>. C# generic
// math constraints cannot unify the planar PointXY API with the spherical PointLL
// API (they have different Distance/ClosestPoint signatures), so this port mirrors
// the C++ explicit instantiations with two concrete types:
//   - Polyline2<TPrecision> : planar polyline over PointXY<TPrecision> (Point2/Point2d).
//   - PointLlPolyline2       : spherical polyline over PointLL (GeoPoint<double>).
// The Douglas-Peucker and self-intersection logic is shared at the algorithm level;
// only the self-intersection-avoiding variant (which relies on PointTileIndex, and is
// PointLL-only in the C++ as well) lives on PointLlPolyline2.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// 2D polyline over planar <see cref="PointXY{TPrecision}"/> coordinates (Euclidean x,y).
/// Mirrors the C++ <c>Polyline2&lt;PointXY&lt;PrecisionT&gt;&gt;</c> (i.e. Point2 / Point2d).
/// </summary>
/// <typeparam name="TPrecision">Numeric precision type (float or double).</typeparam>
public sealed class Polyline2<TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    private readonly List<PointXY<TPrecision>> _pts;

    /// <summary>Default constructor. Creates an empty polyline.</summary>
    public Polyline2() => _pts = new List<PointXY<TPrecision>>();

    /// <summary>Constructor given a list of points.</summary>
    /// <param name="pts">List of points.</param>
    public Polyline2(IEnumerable<PointXY<TPrecision>> pts) => _pts = new List<PointXY<TPrecision>>(pts);

    /// <summary>Gets the (mutable) list of points.</summary>
    public List<PointXY<TPrecision>> Pts => _pts;

    /// <summary>
    /// Add a point to the polyline. Does not add the point if it is equal to the current endpoint.
    /// </summary>
    /// <param name="p">Point to add to the polyline.</param>
    public void Add(PointXY<TPrecision> p)
    {
        int n = _pts.Count;
        if (n == 0 || !p.Equals(_pts[n - 1]))
        {
            _pts.Add(p);
        }
    }

    /// <summary>Finds the length of the polyline by accumulating the length of all segments.</summary>
    public TPrecision Length() => Length(_pts);

    /// <summary>Computes the length of the specified polyline.</summary>
    /// <param name="pts">Polyline vertices.</param>
    public static TPrecision Length(IReadOnlyList<PointXY<TPrecision>> pts)
    {
        TPrecision length = TPrecision.Zero;
        if (pts.Count < 2)
        {
            return length;
        }

        for (int i = 1; i < pts.Count; ++i)
        {
            length += pts[i - 1].Distance(pts[i]);
        }

        return length;
    }

    /// <summary>
    /// In an O(n^2) manner (only useful for debugging/testing), checks for any intersecting
    /// segments in the polyline and returns the intersection points found.
    /// </summary>
    public List<PointXY<TPrecision>> GetSelfIntersections()
    {
        var intersections = new List<PointXY<TPrecision>>();
        List<PointXY<TPrecision>> points = _pts;
        for (int i = 1; i + 2 < points.Count; i++)
        {
            PointXY<TPrecision> ia = points[i - 1];
            PointXY<TPrecision> ib = points[i];
            for (int j = i + 2; j + 1 < points.Count; j++)
            {
                PointXY<TPrecision> ja = points[j - 1];
                PointXY<TPrecision> jb = points[j];
                var segmenti = new LineSegment2T<TPrecision>(ia, ib);
                var segmentj = new LineSegment2T<TPrecision>(ja, jb);
                if (segmenti.Intersect(segmentj, out PointXY<TPrecision> intersectionPoint))
                {
                    intersections.Add(intersectionPoint);
                }
            }
        }

        return intersections;
    }

    /// <summary>
    /// Finds the closest point to the supplied point as well as the distance to that point and the
    /// index of the segment where the closest point lies.
    /// </summary>
    public (PointXY<TPrecision> Closest, TPrecision Distance, int Index) ClosestPoint(PointXY<TPrecision> pt)
        => pt.ClosestPoint(_pts);

    /// <summary>
    /// Generalize this polyline in place.
    /// </summary>
    /// <param name="t">Generalization tolerance.</param>
    /// <param name="indices">Indices of points not to generalize.</param>
    /// <returns>The number of points in the generalized polyline.</returns>
    public uint Generalize(TPrecision t, ISet<int>? indices = null)
    {
        Generalize(_pts, t, indices);
        return (uint)_pts.Count;
    }

    /// <summary>
    /// Get a generalized polyline from this polyline. This polyline remains unchanged.
    /// </summary>
    public Polyline2<TPrecision> GeneralizedPolyline(TPrecision t, ISet<int>? indices = null)
    {
        var generalized = new Polyline2<TPrecision>(_pts);
        generalized.Generalize(t, indices);
        return generalized;
    }

    /// <summary>
    /// Generalize the given list of points using the Douglas-Peucker algorithm.
    /// </summary>
    /// <param name="polyline">The list of points (modified in place).</param>
    /// <param name="epsilon">The tolerance used in removing points.</param>
    /// <param name="exclusions">Indices of points not to generalize.</param>
    public static void Generalize(
        List<PointXY<TPrecision>> polyline,
        TPrecision epsilon,
        ISet<int>? exclusions = null)
    {
        // any epsilon this low will have no effect on the input nor will any super short input
        if (epsilon <= TPrecision.Zero || polyline.Count < 3)
        {
            return;
        }

        DouglasPeuckerCore.DouglasPeucker(polyline, epsilon, exclusions ?? EmptyIndices);
    }

    /// <summary>
    /// Clips this polyline to the specified bounding box in place.
    /// </summary>
    /// <param name="box">Bounding box to clip this polyline to.</param>
    /// <returns>The number of vertices in the clipped polyline.</returns>
    public uint Clip(Aabb2T<TPrecision> box) => box.Clip(_pts, false);

    /// <summary>
    /// Gets a polyline clipped to the supplied bounding box. This polyline remains unchanged.
    /// </summary>
    public Polyline2<TPrecision> ClippedPolyline(Aabb2T<TPrecision> box)
    {
        var pts = new List<PointXY<TPrecision>>(_pts);
        box.Clip(pts, false);
        return new Polyline2<TPrecision>(pts);
    }

    /// <summary>Checks if the polylines are equal (same size and element-wise equal).</summary>
    public bool Equals(Polyline2<TPrecision> other)
    {
        if (other is null || _pts.Count != other._pts.Count)
        {
            return false;
        }

        for (int i = 0; i < _pts.Count; i++)
        {
            if (!_pts[i].Equals(other._pts[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Polyline2<TPrecision> p && Equals(p);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (PointXY<TPrecision> p in _pts)
        {
            hash.Add(p);
        }

        return hash.ToHashCode();
    }

    /// <summary>
    /// Computes the Hausdorff distance between the two linestring features.
    /// </summary>
    public static TPrecision HausdorffDistance(
        IReadOnlyList<PointXY<TPrecision>> l1,
        IReadOnlyList<PointXY<TPrecision>> l2)
    {
        TPrecision hausdorff = TPrecision.Zero;

        // which point of l1 is furthest away from l2
        foreach (PointXY<TPrecision> p in l1)
        {
            var closest = p.ClosestPoint(l2);
            TPrecision minDistance = p.Distance(closest.Closest);
            if (minDistance > hausdorff)
            {
                hausdorff = minDistance;
            }
        }

        // which point of l2 is furthest away from l1
        foreach (PointXY<TPrecision> p in l2)
        {
            var closest = p.ClosestPoint(l1);
            TPrecision minDistance = p.Distance(closest.Closest);
            if (minDistance > hausdorff)
            {
                hausdorff = minDistance;
            }
        }

        return hausdorff;
    }

    private static readonly HashSet<int> EmptyIndices = new();

    // Shared planar Douglas-Peucker over PointXY<TPrecision>.
    private static class DouglasPeuckerCore
    {
        public static void DouglasPeucker(
            List<PointXY<TPrecision>> polyline,
            TPrecision epsilon,
            ISet<int> exclusions)
        {
            // the recursive bit (square the error tolerance to avoid sqrts)
            TPrecision eps = epsilon * epsilon;

            void Peucker(int s, int e)
            {
                // find the point furthest from the line
                TPrecision dmax = TPrecision.MinValue;
                int itr = s;
                var l = new LineSegment2T<TPrecision>(polyline[s], polyline[e]);
                int k = 0;

                // for (auto i = prev(end); i != start; --i, --j) with j starting at e-1
                int j = e - 1;
                for (int i = e - 1; i > s; --i, --j)
                {
                    // special points we dont want to generalize no matter what take precedence
                    if (exclusions.Contains(j))
                    {
                        itr = i;
                        dmax = eps;
                        k = j;
                        break;
                    }

                    // if this is the highest frequency detail so far
                    TPrecision d = l.DistanceSquared(polyline[i], out _);
                    if (d > dmax)
                    {
                        itr = i;
                        dmax = d;
                        k = j;
                    }
                }

                // there are some high frequency details between start and end so we need to look
                // for flatter sections between them
                if (dmax >= eps)
                {
                    // recurse from right to left to preserve index/iterator validity
                    if (e - k > 1)
                    {
                        Peucker(itr, e);
                    }

                    if (k - s > 1)
                    {
                        Peucker(s, itr);
                    }
                }
                else
                {
                    // nothing sticks out between start and end so simplify everything between away
                    // erase (start, end) -> remove indices s+1 .. e-1 inclusive
                    polyline.RemoveRange(s + 1, e - s - 1);
                }
            }

            Peucker(0, polyline.Count - 1);
        }
    }
}
