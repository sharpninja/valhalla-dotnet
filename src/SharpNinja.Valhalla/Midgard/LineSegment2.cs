// Faithful C# port of Valhalla midgard LineSegment2 (2D line segment).
// Sources: valhalla/midgard/linesegment2.h and src/midgard/linesegment2.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
// Generic over the precision type (float or double) via PointXY<TPrecision>, mirroring the
// C++ template class LineSegment2<coord_t>. LineSegment2 = LineSegment2T<float>.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Line segment in 2D. Generic over the precision type (float or double), mirroring the C++
/// <c>LineSegment2&lt;coord_t&gt;</c> which works with <c>Point2</c> (Euclidean x,y) or
/// <c>PointLL</c> (latitude/longitude). Endpoints are <see cref="PointXY{TPrecision}"/>.
/// </summary>
/// <typeparam name="TPrecision">Numeric precision type (float or double).</typeparam>
public sealed class LineSegment2T<TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    private PointXY<TPrecision> _a;
    private PointXY<TPrecision> _b;

    /// <summary>Default constructor. Both endpoints initialized to (0, 0).</summary>
    public LineSegment2T()
    {
        _a = new PointXY<TPrecision>(TPrecision.Zero, TPrecision.Zero);
        _b = new PointXY<TPrecision>(TPrecision.Zero, TPrecision.Zero);
    }

    /// <summary>Constructor given 2 points.</summary>
    /// <param name="p1">First point of the segment.</param>
    /// <param name="p2">Second point of the segment.</param>
    public LineSegment2T(PointXY<TPrecision> p1, PointXY<TPrecision> p2)
    {
        _a = p1;
        _b = p2;
    }

    /// <summary>Gets the first point of the segment.</summary>
    public PointXY<TPrecision> A => _a;

    /// <summary>Gets the second point of the segment.</summary>
    public PointXY<TPrecision> B => _b;

    /// <summary>
    /// Finds the distance squared of a specified point from the line segment and the closest point
    /// on the segment to the specified point.
    /// </summary>
    /// <param name="p">Test point.</param>
    /// <param name="closest">(Return) Closest point on the segment to p.</param>
    /// <returns>Returns the distance squared from p to the closest point on the segment.</returns>
    public TPrecision DistanceSquared(PointXY<TPrecision> p, out PointXY<TPrecision> closest)
    {
        // Construct vector v (ab) and w (ap)
        var v = new VectorXY<TPrecision>(_a, _b);
        var w = new VectorXY<TPrecision>(_a, p);

        // Numerator of the component of w onto v. If <= 0 then a is the closest point. By
        // separating into the numerator and denominator of the component we avoid a division
        // unless it is necessary.
        TPrecision n = w.Dot(v);
        if (n <= TPrecision.Zero)
        {
            closest = _a;
        }
        else
        {
            // Get the denominator of the component. If the component >= 1 (d <= n) then point b is
            // the closest point.
            TPrecision d = v.Dot(v);
            if (d <= n)
            {
                closest = _b;
            }
            else
            {
                // Closest point is along the segment - the projection of w onto v.
                closest = _a + (v * (n / d));
            }
        }

        return closest.DistanceSquared(p);
    }

    /// <summary>
    /// Finds the distance of a specified point from the line segment and the closest point on the
    /// segment to the specified point.
    /// </summary>
    /// <param name="p">Test point.</param>
    /// <param name="closest">(Return) Closest point on the segment to p.</param>
    /// <returns>Returns the distance from p to the closest point on the segment.</returns>
    public TPrecision Distance(PointXY<TPrecision> p, out PointXY<TPrecision> closest)
        => TPrecision.Sqrt(DistanceSquared(p, out closest));

    /// <summary>
    /// Determines if the current segment intersects the specified segment. If an intersect occurs
    /// the intersection is computed. Note: the case where the lines overlap is not considered.
    /// </summary>
    /// <param name="segment">Segment to determine intersection with.</param>
    /// <param name="intersect">(OUT) Intersection point.</param>
    /// <returns>Returns true if an intersection exists, false if not.</returns>
    public bool Intersect(LineSegment2T<TPrecision> segment, out PointXY<TPrecision> intersect)
    {
        // Construct vectors
        VectorXY<TPrecision> b = _b - _a;
        VectorXY<TPrecision> d = segment.B - segment.A;

        // Set 2D perpendicular vector to d
        VectorXY<TPrecision> dp = d.GetPerpendicular();

        // Check if denominator will be 0 (lines are parallel)
        TPrecision dtb = dp.Dot(b);
        if (dtb == TPrecision.Zero)
        {
            intersect = new PointXY<TPrecision>(TPrecision.Zero, TPrecision.Zero);
            return false;
        }

        // Solve for the parameter t
        VectorXY<TPrecision> c = segment.A - _a;
        TPrecision t = dp.Dot(c) / dtb;
        if (t < TPrecision.Zero || t > TPrecision.One)
        {
            intersect = new PointXY<TPrecision>(TPrecision.Zero, TPrecision.Zero);
            return false;
        }

        // Solve for the parameter u
        VectorXY<TPrecision> bp = b.GetPerpendicular();
        TPrecision u = bp.Dot(c) / dtb;
        if (u < TPrecision.Zero || u > TPrecision.One)
        {
            intersect = new PointXY<TPrecision>(TPrecision.Zero, TPrecision.Zero);
            return false;
        }

        // An intersect occurs. Set the intersect point and return true.
        intersect = _a + (b * t);
        return true;
    }

    /// <summary>
    /// Determines if the line segment intersects the specified convex polygon. Based on the
    /// Cyrus-Beck clipping method.
    /// </summary>
    /// <param name="poly">A counter-clockwise oriented polygon.</param>
    /// <returns>
    /// Returns true if any part of the segment intersects the polygon, false if no intersection.
    /// </returns>
    public bool Intersect(IReadOnlyList<PointXY<TPrecision>> poly)
    {
        // Initialize the candidate interval
        TPrecision tOut = TPrecision.One;
        TPrecision tIn = TPrecision.Zero;
        TPrecision epsilon = TPrecision.CreateChecked(Constants.Epsilon);

        // Iterate through each edge of the polygon
        VectorXY<TPrecision> c = _b - _a;
        int pt1 = poly.Count - 1;
        for (int pt2 = 0; pt2 < poly.Count; pt1 = pt2, pt2++)
        {
            // Set an outward facing normal (polygon is assumed to be CCW)
            var n = new VectorXY<TPrecision>(
                poly[pt2].Y - poly[pt1].Y,
                poly[pt1].X - poly[pt2].X);

            // Dot product of the normal to this polygon edge with the ray
            TPrecision nDotC = n.Dot(c);
            TPrecision num = n.Dot(poly[pt1] - _a);

            // Check for parallel line
            if (TPrecision.Abs(nDotC) < epsilon)
            {
                // No intersection if segment origin is outside this edge
                if (num < TPrecision.Zero)
                {
                    return false;
                }

                // Skip this edge
                continue;
            }

            // Get intersection and update candidate interval
            TPrecision t = num / nDotC;
            if (nDotC > TPrecision.Zero)
            {
                // Ray is exiting polygon
                if (t < tOut)
                {
                    tOut = t;
                }
            }
            else
            {
                // Ray is entering polygon
                if (t > tIn)
                {
                    tIn = t;
                }
            }

            // Early out
            if (tIn > tOut)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Clips the line segment to a specified convex polygon. Based on the Cyrus-Beck clipping
    /// method.
    /// </summary>
    /// <param name="poly">A counter-clockwise oriented polygon.</param>
    /// <param name="clipSegment">Returns the clipped segment.</param>
    /// <returns>
    /// Returns true if any part of the segment intersects the polygon, false if no intersection.
    /// </returns>
    public bool ClipToPolygon(
        IReadOnlyList<PointXY<TPrecision>> poly,
        out LineSegment2T<TPrecision> clipSegment)
    {
        // Initialize the candidate interval
        TPrecision tOut = TPrecision.One;
        TPrecision tIn = TPrecision.Zero;
        TPrecision epsilon = TPrecision.CreateChecked(Constants.Epsilon);

        // Iterate through each edge of the polygon
        VectorXY<TPrecision> c = _b - _a;
        int pt1 = poly.Count - 1;
        for (int pt2 = 0; pt2 < poly.Count; pt1 = pt2, pt2++)
        {
            // Set an outward facing normal (polygon is assumed to be CCW)
            var n = new VectorXY<TPrecision>(
                poly[pt2].Y - poly[pt1].Y,
                poly[pt1].X - poly[pt2].X);

            // Dot product of the normal to this polygon edge with the ray
            TPrecision nDotC = n.Dot(c);
            TPrecision num = n.Dot(poly[pt1] - _a);

            // Check for parallel line
            if (TPrecision.Abs(nDotC) < epsilon)
            {
                // No intersection if segment origin is outside this edge
                if (num < TPrecision.Zero)
                {
                    clipSegment = new LineSegment2T<TPrecision>();
                    return false;
                }

                // Skip this edge
                continue;
            }

            // Get intersection and update candidate interval
            TPrecision t = num / nDotC;
            if (nDotC > TPrecision.Zero)
            {
                // Ray is exiting polygon
                if (t < tOut)
                {
                    tOut = t;
                }
            }
            else
            {
                // Ray is entering polygon
                if (t > tIn)
                {
                    tIn = t;
                }
            }

            // Early out
            if (tIn > tOut)
            {
                clipSegment = new LineSegment2T<TPrecision>();
                return false;
            }
        }

        // If candidate interval is not empty then set the clip segment
        clipSegment = new LineSegment2T<TPrecision>(_a + (c * tIn), _a + (c * tOut));
        return true;
    }

    /// <summary>
    /// Tests if a point is to left, right, or on the line segment.
    /// </summary>
    /// <param name="p">Point to test.</param>
    /// <returns>
    /// Returns &gt;0 for a point to the left, &lt;0 for a point to the right, and 0 for a point on
    /// the line.
    /// </returns>
    public TPrecision IsLeft(PointXY<TPrecision> p)
        => ((_b.X - _a.X) * (p.Y - _a.Y)) - ((p.X - _a.X) * (_b.Y - _a.Y));

    /// <summary>
    /// Equality approximation. Returns true if two line segments are approximately equal.
    /// </summary>
    /// <param name="other">Line segment to compare to the current line segment.</param>
    public bool ApproximatelyEqual(LineSegment2T<TPrecision> other)
        => _a.ApproximatelyEqual(other.A) && _b.ApproximatelyEqual(other.B);
}
