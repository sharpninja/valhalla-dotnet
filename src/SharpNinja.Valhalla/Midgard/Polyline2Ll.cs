// Faithful C# port of the PointLL specialization of Valhalla midgard Polyline2.
// Sources: valhalla/midgard/polyline2.h and src/midgard/polyline2.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// This is the spherical (GeoPoint<double> / PointLL) counterpart to Polyline2<TPrecision>.
// It supports the same surface (Add/Length/ClosestPoint/Generalize/Clip/GetSelfIntersections/
// HausdorffDistance) plus the self-intersection-avoiding Douglas-Peucker variant, which - as in
// the C++ - relies on PointTileIndex and therefore only applies to PointLL.

using System;
using System.Collections.Generic;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// 2D polyline over spherical <see cref="PointLL"/> coordinates (longitude/latitude). Mirrors the
/// C++ <c>Polyline2&lt;GeoPoint&lt;double&gt;&gt;</c> (i.e. <c>Polyline2&lt;PointLL&gt;</c>).
/// </summary>
public sealed class PointLlPolyline2
{
    private readonly List<PointLL> _pts;

    /// <summary>Default constructor. Creates an empty polyline.</summary>
    public PointLlPolyline2() => _pts = new List<PointLL>();

    /// <summary>Constructor given a list of points.</summary>
    /// <param name="pts">List of points.</param>
    public PointLlPolyline2(IEnumerable<PointLL> pts) => _pts = new List<PointLL>(pts);

    /// <summary>Gets the (mutable) list of points.</summary>
    public List<PointLL> Pts => _pts;

    /// <summary>
    /// Add a point to the polyline. Does not add the point if it is equal to the current endpoint.
    /// </summary>
    public void Add(PointLL p)
    {
        int n = _pts.Count;
        if (n == 0 || !p.Equals(_pts[n - 1]))
        {
            _pts.Add(p);
        }
    }

    /// <summary>Finds the length of the polyline by accumulating the length of all segments.</summary>
    public double Length() => Length(_pts);

    /// <summary>Computes the length of the specified polyline (spherical distance).</summary>
    public static double Length(IReadOnlyList<PointLL> pts)
    {
        double length = 0;
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
    /// segments in the polyline and returns the intersection points found. The intersection math
    /// is performed in planar lng/lat space (matching the C++ <c>LineSegment2&lt;PointLL&gt;</c>).
    /// </summary>
    public List<PointLL> GetSelfIntersections()
    {
        var intersections = new List<PointLL>();
        List<PointLL> points = _pts;
        for (int i = 1; i + 2 < points.Count; i++)
        {
            PointLL ia = points[i - 1];
            PointLL ib = points[i];
            for (int j = i + 2; j + 1 < points.Count; j++)
            {
                PointLL ja = points[j - 1];
                PointLL jb = points[j];
                var segmenti = new LineSegment2d(ToXy(ia), ToXy(ib));
                var segmentj = new LineSegment2d(ToXy(ja), ToXy(jb));
                if (segmenti.Intersect(segmentj, out PointXY<double> intersectionPoint))
                {
                    intersections.Add(new PointLL(intersectionPoint.X, intersectionPoint.Y));
                }
            }
        }

        return intersections;
    }

    /// <summary>
    /// Finds the closest point to the supplied point as well as the distance to that point and the
    /// index of the segment where the closest point lies.
    /// </summary>
    public (PointLL Closest, double Distance, int Index) ClosestPoint(PointLL pt) => pt.ClosestPoint(_pts);

    /// <summary>
    /// Generalize this polyline in place.
    /// </summary>
    /// <param name="t">Generalization tolerance (meters).</param>
    /// <param name="indices">Indices of points not to generalize.</param>
    /// <param name="avoidSelfIntersection">Avoid simplifications that cause self-intersection.</param>
    /// <returns>The number of points in the generalized polyline.</returns>
    public uint Generalize(double t, ISet<int>? indices = null, bool avoidSelfIntersection = false)
    {
        Generalize(_pts, t, indices, avoidSelfIntersection);
        return (uint)_pts.Count;
    }

    /// <summary>
    /// Get a generalized polyline from this polyline. This polyline remains unchanged.
    /// </summary>
    public PointLlPolyline2 GeneralizedPolyline(
        double t,
        ISet<int>? indices = null,
        bool avoidSelfIntersection = false)
    {
        var generalized = new PointLlPolyline2(_pts);
        generalized.Generalize(t, indices, avoidSelfIntersection);
        return generalized;
    }

    /// <summary>
    /// Generalize the given list of points.
    /// </summary>
    /// <param name="polyline">The list of points (modified in place).</param>
    /// <param name="epsilonM">The tolerance in meters used in removing points.</param>
    /// <param name="exclusions">Indices of points not to generalize.</param>
    /// <param name="avoidSelfIntersection">Avoid simplifications that cause self-intersection.</param>
    public static void Generalize(
        List<PointLL> polyline,
        double epsilonM,
        ISet<int>? exclusions = null,
        bool avoidSelfIntersection = false)
    {
        // any epsilon this low will have no effect on the input nor will any super short input
        if (epsilonM <= 0 || polyline.Count < 3)
        {
            return;
        }

        ISet<int> ex = exclusions ?? EmptyIndices;
        if (avoidSelfIntersection)
        {
            DouglasPeuckerAvoidSelfIntersection(polyline, epsilonM, ex);
        }
        else
        {
            DouglasPeucker(polyline, epsilonM, ex);
        }
    }

    /// <summary>
    /// Clips this polyline to the specified bounding box in place (planar lng/lat clipping).
    /// </summary>
    public uint Clip(Aabb2T<double> box)
    {
        var xy = new List<PointXY<double>>(_pts.Count);
        foreach (PointLL p in _pts)
        {
            xy.Add(ToXy(p));
        }

        uint n = box.Clip(xy, false);
        _pts.Clear();
        foreach (PointXY<double> p in xy)
        {
            _pts.Add(new PointLL(p.X, p.Y));
        }

        return n;
    }

    /// <summary>Checks if the polylines are equal (same size and element-wise equal).</summary>
    public bool Equals(PointLlPolyline2 other)
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
    public override bool Equals(object? obj) => obj is PointLlPolyline2 p && Equals(p);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (PointLL p in _pts)
        {
            hash.Add(p);
        }

        return hash.ToHashCode();
    }

    /// <summary>Computes the Hausdorff distance between the two linestring features.</summary>
    public static double HausdorffDistance(IReadOnlyList<PointLL> l1, IReadOnlyList<PointLL> l2)
    {
        double hausdorff = 0;
        foreach (PointLL p in l1)
        {
            var closest = p.ClosestPoint(l2);
            double minDistance = p.Distance(closest.Closest);
            if (minDistance > hausdorff)
            {
                hausdorff = minDistance;
            }
        }

        foreach (PointLL p in l2)
        {
            var closest = p.ClosestPoint(l1);
            double minDistance = p.Distance(closest.Closest);
            if (minDistance > hausdorff)
            {
                hausdorff = minDistance;
            }
        }

        return hausdorff;
    }

    private static readonly HashSet<int> EmptyIndices = new();

    private static PointXY<double> ToXy(PointLL p) => new(p.X, p.Y);

    // Mirrors LineSegment2<PointLL>::DistanceSquared: the closest point on segment (a,b) to p is
    // found via planar lng/lat vector projection, but the returned squared distance is in meters
    // (PointLL::DistanceSquared dispatches through the DistanceApproximator). This unit detail is
    // essential: the Douglas-Peucker tolerance (gen_factor) is in meters.
    private static double SegmentDistanceSquared(PointLL a, PointLL b, PointLL p)
    {
        // Construct vector v (ab) and w (ap) in planar lng/lat space.
        double vx = b.X - a.X;
        double vy = b.Y - a.Y;
        double wx = p.X - a.X;
        double wy = p.Y - a.Y;

        // Numerator of the component of w onto v.
        double n = (wx * vx) + (wy * vy);
        PointLL closest;
        if (n <= 0.0)
        {
            closest = a;
        }
        else
        {
            double d = (vx * vx) + (vy * vy);
            if (d <= n)
            {
                closest = b;
            }
            else
            {
                double t = n / d;
                closest = new PointLL(a.X + (vx * t), a.Y + (vy * t));
            }
        }

        return closest.DistanceSquared(p);
    }

    // Standard Douglas-Peucker over PointLL (planar lng/lat distance via LineSegment2<PointLL>).
    private static void DouglasPeucker(List<PointLL> polyline, double epsilon, ISet<int> exclusions)
    {
        double eps = epsilon * epsilon;

        void Peucker(int s, int e)
        {
            double dmax = double.MinValue;
            int itr = s;
            PointLL ls = polyline[s];
            PointLL le = polyline[e];
            int k = 0;

            int j = e - 1;
            for (int i = e - 1; i > s; --i, --j)
            {
                if (exclusions.Contains(j))
                {
                    itr = i;
                    dmax = eps;
                    k = j;
                    break;
                }

                double d = SegmentDistanceSquared(ls, le, polyline[i]);
                if (d > dmax)
                {
                    itr = i;
                    dmax = d;
                    k = j;
                }
            }

            if (dmax >= eps)
            {
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
                polyline.RemoveRange(s + 1, e - s - 1);
            }
        }

        Peucker(0, polyline.Count - 1);
    }

    // Self-intersection-avoiding Douglas-Peucker. Builds a PointTileIndex over the polyline, runs
    // the modified Peucker recursion, then collects the surviving (non-deleted) points.
    private static void DouglasPeuckerAvoidSelfIntersection(
        List<PointLL> polyline,
        double epsilonM,
        ISet<int> exclusions)
    {
        PointLL firstPoint = polyline[0];
        double metersPerDeg = DistanceApproximator<PointLL, double>.MetersPerLngDegree(firstPoint.Lat);
        double epsilonDeg = epsilonM / metersPerDeg;
        var index = new PointTileIndex(epsilonDeg, polyline);

        PeuckerAvoidSelfIntersections(index, epsilonM * epsilonM, exclusions, 0, polyline.Count - 1);

        // copy the simplified points into polyline
        polyline.Clear();
        foreach (PointLL pt in index.Points)
        {
            if (!pt.Equals(PointTileIndex.DeletedPoint))
            {
                polyline.Add(pt);
            }
        }
    }

    private static void PeuckerAvoidSelfIntersections(
        PointTileIndex pointTileIndex,
        double epsilonSq,
        ISet<int> exclusions,
        int sidx,
        int eidx)
    {
        while (exclusions.Contains(sidx) && sidx < eidx)
        {
            sidx++;
        }

        while (exclusions.Contains(eidx) && eidx > sidx)
        {
            eidx--;
        }

        if (sidx >= eidx)
        {
            return;
        }

        PointLL start = pointTileIndex.Points[sidx];
        PointLL end = pointTileIndex.Points[eidx];

        double dmax = double.MinValue;

        // hfidx is the index of the highest freq detail (the dividing point)
        int hfidx = sidx;

        // find the point furthest from the line-segment formed by {start, end}
        for (int idx = sidx + 1; idx < eidx; idx++)
        {
            // special points we dont want to generalize no matter what take precedence
            if (exclusions.Contains(idx))
            {
                dmax = epsilonSq;
                hfidx = idx;
                break;
            }

            PointLL c = pointTileIndex.Points[idx];
            double d = SegmentDistanceSquared(start, end, c);
            if (d > dmax)
            {
                dmax = d;
                hfidx = idx;
            }
        }

        // If (dmax < epsilon_sq) then we have a relatively straight line between (start,end).
        // Use the tiled-point-space to determine if decimating the line would cause a
        // self-intersection (triangle containment test of nearby points).
        if (dmax < epsilonSq)
        {
            HashSet<int> lineBufferPoints =
                pointTileIndex.GetPointsNearSegment(new LineSegment2d(ToXy(start), ToXy(end)));

            // remove the points along the polyline [sidx, eidx]
            for (int i = sidx; i <= eidx; i++)
            {
                lineBufferPoints.Remove(i);
            }

            bool canSimplify = true;
            for (int cidx = sidx + 1; cidx < eidx && canSimplify; cidx++)
            {
                PointLL c = pointTileIndex.Points[cidx];
                foreach (int pointIdx in lineBufferPoints)
                {
                    PointLL p = pointTileIndex.Points[pointIdx];
                    if (Util.TriangleContains(ToXy(start), ToXy(c), ToXy(end), ToXy(p)))
                    {
                        canSimplify = false;
                        break;
                    }
                }

                if (!canSimplify)
                {
                    break;
                }
            }

            if (canSimplify)
            {
                // remove all points between sidx and eidx (exclusive of the endpoints)
                pointTileIndex.RemovePoints(sidx + 1, eidx);
            }
            else
            {
                // simplifying would self-intersect; force recursion around hfidx
                dmax = epsilonSq;
            }
        }

        // if (dmax >= epsilon_sq) there are high frequency details between start and end
        if (dmax >= epsilonSq)
        {
            // recurse from right to left to preserve indices in the keep set
            if (eidx - hfidx > 1)
            {
                PeuckerAvoidSelfIntersections(pointTileIndex, epsilonSq, exclusions, hfidx, eidx);
            }

            if (hfidx - sidx > 1)
            {
                PeuckerAvoidSelfIntersections(pointTileIndex, epsilonSq, exclusions, sidx, hfidx);
            }
        }
    }
}
