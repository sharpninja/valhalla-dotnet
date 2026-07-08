// Faithful C# port of Valhalla midgard AABB2 (Axis Aligned Bounding Box, 2D).
// Sources: valhalla/midgard/aabb2.h and src/midgard/aabb2.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
// Generic over the precision type (float or double) via PointXY<TPrecision>, mirroring the
// C++ template class AABB2<coord_t>. Aabb2 = Aabb2T<float>, Aabb2d = Aabb2T<double>.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Axis Aligned Bounding Box (2 dimensional). Generic over the precision type (float or double),
/// mirroring the C++ <c>AABB2&lt;coord_t&gt;</c> which works with <c>Point2</c> (Euclidean x,y) or
/// <c>PointLL</c> (latitude/longitude). Coordinates are <see cref="PointXY{TPrecision}"/>.
/// </summary>
/// <typeparam name="TPrecision">Numeric precision type (float or double).</typeparam>
public sealed class Aabb2T<TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    // Edge to clip against.
    private enum ClipEdge
    {
        Left,
        Right,
        Bottom,
        Top,
    }

    // Minimum and maximum x,y values (lower left and upper right corners) of the bounding box.
    private TPrecision _minx;
    private TPrecision _miny;
    private TPrecision _maxx;
    private TPrecision _maxy;

    /// <summary>Default constructor. Sets all min,max values to 0.</summary>
    public Aabb2T()
    {
        _minx = TPrecision.Zero;
        _miny = TPrecision.Zero;
        _maxx = TPrecision.Zero;
        _maxy = TPrecision.Zero;
    }

    /// <summary>Construct an AABB given a minimum and maximum point.</summary>
    /// <param name="minpt">Minimum point (x,y).</param>
    /// <param name="maxpt">Maximum point (x,y).</param>
    public Aabb2T(PointXY<TPrecision> minpt, PointXY<TPrecision> maxpt)
    {
        _minx = minpt.X;
        _miny = minpt.Y;
        _maxx = maxpt.X;
        _maxy = maxpt.Y;
    }

    /// <summary>Constructor with specified bounds.</summary>
    /// <param name="minx">Minimum x of the bounding box.</param>
    /// <param name="miny">Minimum y of the bounding box.</param>
    /// <param name="maxx">Maximum x of the bounding box.</param>
    /// <param name="maxy">Maximum y of the bounding box.</param>
    public Aabb2T(TPrecision minx, TPrecision miny, TPrecision maxx, TPrecision maxy)
    {
        _minx = minx;
        _miny = miny;
        _maxx = maxx;
        _maxy = maxy;
    }

    /// <summary>Construct an AABB given a list of points.</summary>
    /// <param name="pts">Vertex list.</param>
    public Aabb2T(IReadOnlyList<PointXY<TPrecision>> pts)
    {
        _minx = TPrecision.Zero;
        _miny = TPrecision.Zero;
        _maxx = TPrecision.Zero;
        _maxy = TPrecision.Zero;
        Create(pts);
    }

    /// <summary>Gets the minimum x.</summary>
    public TPrecision Minx => _minx;

    /// <summary>Gets the maximum x.</summary>
    public TPrecision Maxx => _maxx;

    /// <summary>Gets the minimum y.</summary>
    public TPrecision Miny => _miny;

    /// <summary>Gets the maximum y.</summary>
    public TPrecision Maxy => _maxy;

    /// <summary>Gets the point at the minimum x,y.</summary>
    public PointXY<TPrecision> Minpt => new(_minx, _miny);

    /// <summary>Gets the point at the maximum x,y.</summary>
    public PointXY<TPrecision> Maxpt => new(_maxx, _maxy);

    /// <summary>Equality operator. Returns true if the 2 bounding boxes are equal.</summary>
    public static bool operator ==(Aabb2T<TPrecision>? r1, Aabb2T<TPrecision>? r2)
    {
        if (r1 is null)
        {
            return r2 is null;
        }

        if (r2 is null)
        {
            return false;
        }

        return r1._minx == r2._minx && r1._maxx == r2._maxx
            && r1._miny == r2._miny && r1._maxy == r2._maxy;
    }

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(Aabb2T<TPrecision>? r1, Aabb2T<TPrecision>? r2) => !(r1 == r2);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Aabb2T<TPrecision> r && this == r;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_minx, _miny, _maxx, _maxy);

    /// <summary>Creates an AABB given a list of points.</summary>
    /// <param name="pts">Vertex list.</param>
    public void Create(IReadOnlyList<PointXY<TPrecision>> pts)
    {
        _minx = pts[0].X;
        _maxx = _minx;
        _miny = pts[0].Y;
        _maxy = _miny;
        for (int i = 1; i < pts.Count; ++i)
        {
            TPrecision x = pts[i].X;
            if (x < _minx)
            {
                _minx = x;
            }
            else if (x > _maxx)
            {
                _maxx = x;
            }

            TPrecision y = pts[i].Y;
            if (y < _miny)
            {
                _miny = y;
            }
            else if (y > _maxy)
            {
                _maxy = y;
            }
        }
    }

    /// <summary>Gets the width of the bounding box.</summary>
    public TPrecision Width() => _maxx - _minx;

    /// <summary>Gets the height of the bounding box.</summary>
    public TPrecision Height() => _maxy - _miny;

    /// <summary>Gets the center of the bounding box.</summary>
    public PointXY<TPrecision> Center()
    {
        TPrecision half = TPrecision.CreateChecked(0.5);
        return new PointXY<TPrecision>((_minx + _maxx) * half, (_miny + _maxy) * half);
    }

    /// <summary>
    /// Tests if a specified point is within the bounding box. Points that lie along the minimum
    /// x or y edge are considered inside; points that lie on the maximum x or y edge are outside.
    /// </summary>
    public bool Contains(PointXY<TPrecision> pt)
        => pt.X >= _minx && pt.Y >= _miny && pt.X < _maxx && pt.Y < _maxy;

    /// <summary>
    /// Checks to determine if another bounding box is completely inside this bounding box.
    /// </summary>
    public bool Contains(Aabb2T<TPrecision> r2) => Contains(r2.Minpt) && Contains(r2.Maxpt);

    /// <summary>
    /// Computes the intersection of this bounding box with another. If the bounding boxes do not
    /// intersect a bounding box with no area is returned (all min,max values are 0).
    /// </summary>
    public Aabb2T<TPrecision> Intersection(Aabb2T<TPrecision> bbox)
    {
        if (!Intersects(bbox))
        {
            return new Aabb2T<TPrecision>(
                TPrecision.Zero, TPrecision.Zero, TPrecision.Zero, TPrecision.Zero);
        }

        return new Aabb2T<TPrecision>(
            TPrecision.Max(_minx, bbox._minx),
            TPrecision.Max(_miny, bbox._miny),
            TPrecision.Min(_maxx, bbox._maxx),
            TPrecision.Min(_maxy, bbox._maxy));
    }

    /// <summary>
    /// Test if this bounding box intersects another bounding box. The bounding boxes do NOT
    /// intersect if the other bounding box (r2) is entirely LEFT, BELOW, RIGHT, or ABOVE this
    /// bounding box. Otherwise they intersect.
    /// </summary>
    public bool Intersects(Aabb2T<TPrecision> r2)
        => !((r2.Minx < _minx && r2.Maxx < _minx)
            || (r2.Miny < _miny && r2.Maxy < _miny)
            || (r2.Minx > _maxx && r2.Maxx > _maxx)
            || (r2.Miny > _maxy && r2.Maxy > _maxy));

    /// <summary>
    /// Tests whether the segment intersects (or lies completely within) the bounding box.
    /// </summary>
    public bool Intersects(LineSegment2T<TPrecision> seg) => Intersects(seg.A, seg.B);

    /// <summary>
    /// Tests whether the segment (a..b) intersects (or lies completely within) the bounding box.
    /// </summary>
    public bool Intersects(PointXY<TPrecision> a, PointXY<TPrecision> b)
    {
        // Trivial case - either point within the bounding box
        if (Contains(a) || Contains(b))
        {
            return true;
        }

        // Trivial rejection - both points outside any one bounding edge
        if ((a.X < _minx && b.X < _minx) || // Both left
            (a.Y < _miny && b.Y < _miny) || // Both below
            (a.X > _maxx && b.X > _maxx) || // Both right
            (a.Y > _maxy && b.Y > _maxy))   // Both above
        {
            return false;
        }

        // For LineSegment from a to b check which half plane each corner lies in. If there is a
        // change (different sign returned from the IsLeft 2-D cross product for any one corner)
        // then the AABB intersects the segment. Any corner point on the segment half plane will
        // have IsLeft == 0 and we count as an intersection.
        var s = new LineSegment2T<TPrecision>(a, b);
        TPrecision s1 = s.IsLeft(new PointXY<TPrecision>(_minx, _miny));
        TPrecision zero = TPrecision.Zero;
        return (s1 * s.IsLeft(new PointXY<TPrecision>(_minx, _maxy)) <= zero)
            || (s1 * s.IsLeft(new PointXY<TPrecision>(_maxx, _maxy)) <= zero)
            || (s1 * s.IsLeft(new PointXY<TPrecision>(_maxx, _miny)) <= zero);
    }

    /// <summary>
    /// Tests whether the circle (center, radius) intersects (or lies completely within) the
    /// bounding box.
    /// </summary>
    public bool Intersects(PointXY<TPrecision> center, float radius)
    {
        // Trivial case - center of circle is within the bounding box
        if (Contains(center))
        {
            return true;
        }

        TPrecision r = TPrecision.CreateChecked(radius);

        // Trivial rejection - if the center is more than radius away from any box edge (in the
        // direction perpendicular to the edge and away from the box center) it cannot intersect
        if (center.First < _minx - r || center.Second < _miny - r
            || center.First > _maxx + r || center.Second > _maxy + r)
        {
            return false;
        }

        // If closest point on the box to the center is within radius of the center then we
        // intersected. Project the center onto each edge and check the distance.
        r *= r;
        TPrecision horizontal = Clamp(center.Second, _miny, _maxy);
        TPrecision vertical = Clamp(center.First, _minx, _maxx);
        return center.DistanceSquared(new PointXY<TPrecision>(_minx, horizontal)) <= r  // left side
            || center.DistanceSquared(new PointXY<TPrecision>(_maxx, horizontal)) <= r  // right side
            || center.DistanceSquared(new PointXY<TPrecision>(vertical, _miny)) <= r    // bottom side
            || center.DistanceSquared(new PointXY<TPrecision>(vertical, _maxy)) <= r;   // top side
    }

    /// <summary>
    /// Clips the input set of vertices to the boundary. Clips against each edge in succession.
    /// </summary>
    /// <param name="pts">
    /// In/Out. List of points in the polyline/polygon. After clipping this list is clipped to the
    /// boundary.
    /// </param>
    /// <param name="closed">Is the shape closed?</param>
    /// <returns>
    /// Returns the number of vertices in the clipped shape. May have 0 vertices if none of the
    /// input polyline intersects or lies within the boundary.
    /// </returns>
    public uint Clip(List<PointXY<TPrecision>> pts, bool closed)
    {
        // Temporary vertex list
        var tmpPts = new List<PointXY<TPrecision>>();

        // Clip against each edge in succession. At each step we swap the roles of the 2 vertex
        // lists. If at any time there are no points remaining we return 0 (everything outside).
        if (ClipAgainstEdge(ClipEdge.Left, closed, pts, tmpPts) == 0)
        {
            pts.Clear();
            return 0;
        }

        if (ClipAgainstEdge(ClipEdge.Right, closed, tmpPts, pts) == 0)
        {
            pts.Clear();
            return 0;
        }

        if (ClipAgainstEdge(ClipEdge.Bottom, closed, pts, tmpPts) == 0)
        {
            pts.Clear();
            return 0;
        }

        if (ClipAgainstEdge(ClipEdge.Top, closed, tmpPts, pts) == 0)
        {
            pts.Clear();
            return 0;
        }

        // Return number of vertices in the clipped shape
        return (uint)pts.Count;
    }

    /// <summary>
    /// Expands (if necessary) the bounding box to include the specified bounding box.
    /// </summary>
    public void Expand(Aabb2T<TPrecision> r2)
    {
        if (r2.Minx < _minx)
        {
            _minx = r2.Minx;
        }

        if (r2.Miny < _miny)
        {
            _miny = r2.Miny;
        }

        if (r2.Maxx > _maxx)
        {
            _maxx = r2.Maxx;
        }

        if (r2.Maxy > _maxy)
        {
            _maxy = r2.Maxy;
        }
    }

    /// <summary>
    /// Expands (if necessary) the bounding box to include the specified point.
    /// </summary>
    /// <returns>Returns true if the bbox was expanded.</returns>
    public bool Expand(PointXY<TPrecision> point)
    {
        bool expanded = false;
        if (point.X < _minx)
        {
            _minx = point.X;
            expanded = true;
        }

        if (point.Y < _miny)
        {
            _miny = point.Y;
            expanded = true;
        }

        if (point.X > _maxx)
        {
            _maxx = point.X;
            expanded = true;
        }

        if (point.Y > _maxy)
        {
            _maxy = point.Y;
            expanded = true;
        }

        return expanded;
    }

    private static TPrecision Clamp(TPrecision value, TPrecision lo, TPrecision hi)
        => TPrecision.Min(TPrecision.Max(value, lo), hi);

    /// <summary>
    /// Clips the polyline/polygon against a single edge.
    /// </summary>
    /// <param name="bdry">Edge to clip against.</param>
    /// <param name="closed">True if the vertices form a polygon.</param>
    /// <param name="vin">List of input vertices.</param>
    /// <param name="vout">Output vertices.</param>
    /// <returns>Returns the number of vertices after clipping (in vout).</returns>
    private uint ClipAgainstEdge(
        ClipEdge bdry,
        bool closed,
        IReadOnlyList<PointXY<TPrecision>> vin,
        List<PointXY<TPrecision>> vout)
    {
        // Clear the output vector
        vout.Clear();

        // Special case for the 1st vertex. For polygons (closed) connect last vertex to first
        // vertex. For polylines repeat the first vertex.
        int n = vin.Count;
        int v1 = closed ? n - 1 : 0;

        // Loop through all vertices (edges are created from v1 to v2).
        for (int v2 = 0; v2 < n; v1 = v2, v2++)
        {
            // Relation of v1 and v2 with the bdry
            bool v1in = Inside(bdry, vin[v1]);
            bool v2in = Inside(bdry, vin[v2]);

            // Add vertices to the output list based on the 4 cases
            if (v1in && v2in)
            {
                // Both vertices inside - output v2
                Add(vin[v2], vout);
            }
            else if (!v1in && v2in)
            {
                // v1 is outside and v2 is inside - clip and add intersection followed by v2
                Add(ClipIntersection(bdry, vin[v2], vin[v1]), vout);
                Add(vin[v2], vout);
            }
            else if (v1in && !v2in)
            {
                // v1 is inside and v2 is outside - clip and add the intersection
                Add(ClipIntersection(bdry, vin[v1], vin[v2]), vout);
            }

            // Both are outside - do nothing
        }

        return (uint)vout.Count;
    }

    /// <summary>
    /// Finds the intersection of the segment from insidept to outsidept with the specified
    /// boundary edge. Uses the parametric line equation.
    /// </summary>
    private PointXY<TPrecision> ClipIntersection(
        ClipEdge bdry,
        PointXY<TPrecision> insidept,
        PointXY<TPrecision> outsidept)
    {
        TPrecision t = TPrecision.Zero;
        TPrecision inx = insidept.X;
        TPrecision iny = insidept.Y;
        TPrecision dx = outsidept.X - inx;
        TPrecision dy = outsidept.Y - iny;
        switch (bdry)
        {
            case ClipEdge.Left:
                t = (_minx - inx) / dx;
                break;
            case ClipEdge.Right:
                t = (_maxx - inx) / dx;
                break;
            case ClipEdge.Bottom:
                t = (_miny - iny) / dy;
                break;
            case ClipEdge.Top:
                t = (_maxy - iny) / dy;
                break;
        }

        // Return the intersection point.
        return new PointXY<TPrecision>(inx + (t * dx), iny + (t * dy));
    }

    /// <summary>
    /// Tests if the vertex is inside the rectangular boundary with respect to the specified edge.
    /// </summary>
    private bool Inside(ClipEdge edge, PointXY<TPrecision> v) => edge switch
    {
        ClipEdge.Left => v.X > _minx,
        ClipEdge.Right => v.X < _maxx,
        ClipEdge.Bottom => v.Y > _miny,
        _ => v.Y < _maxy, // kTop (and default)
    };

    /// <summary>Adds a vertex to the output vector if not equal to the prior.</summary>
    private static void Add(PointXY<TPrecision> pt, List<PointXY<TPrecision>> vout)
    {
        if (vout.Count == 0 || !vout[^1].Equals(pt))
        {
            vout.Add(pt);
        }
    }
}
