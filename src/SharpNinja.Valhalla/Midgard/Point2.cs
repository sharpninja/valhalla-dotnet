// Faithful C# port of Valhalla midgard Point2 (PointXY<PrecisionT>).
// Sources: valhalla/midgard/point2.h and src/midgard/point2.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
// PrecisionT is generic; Point2 = PointXY<float>, Point2d = PointXY<double>.
// The underlying storage mirrors std::pair: First == X, Second == Y.

using System.Collections.Generic;
using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// 2D Point (cartesian). Generic over the precision type (float or double), mirroring the
/// C++ <c>PointXY&lt;PrecisionT&gt;</c> which derives from <c>std::pair&lt;PrecisionT, PrecisionT&gt;</c>.
/// <c>First</c> is the x component, <c>Second</c> is the y component (lng=first/x, lat=second/y
/// in the PointLL convention).
/// </summary>
/// <typeparam name="TPrecision">Numeric precision type (float or double).</typeparam>
public sealed class PointXY<TPrecision>
    : IMidgardCoord<PointXY<TPrecision>, TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    /// <summary>
    /// The C++ default approximate-equality epsilon (anonymous-namespace <c>LL_EPSILON</c>).
    /// </summary>
    public const float LlEpsilon = .00002f;

    /// <summary>Default constructor. Initializes both components to zero (matches std::pair).</summary>
    public PointXY()
    {
        First = TPrecision.Zero;
        Second = TPrecision.Zero;
    }

    /// <summary>Constructs a point with the given x (first) and y (second) components.</summary>
    public PointXY(TPrecision x, TPrecision y)
    {
        First = x;
        Second = y;
    }

    /// <summary>First component of the pair (the x coordinate).</summary>
    public TPrecision First { get; private set; }

    /// <summary>Second component of the pair (the y coordinate).</summary>
    public TPrecision Second { get; private set; }

    /// <summary>Gets the x component of the point.</summary>
    public TPrecision X => First;

    /// <summary>Gets the y component of the point.</summary>
    public TPrecision Y => Second;

    /// <summary>Sets the x component.</summary>
    public void SetX(TPrecision x) => First = x;

    /// <summary>Sets the y component.</summary>
    public void SetY(TPrecision y) => Second = y;

    /// <summary>Sets the coordinate components to the specified values.</summary>
    public void Set(TPrecision x, TPrecision y)
    {
        First = x;
        Second = y;
    }

    /// <summary>
    /// Equality approximation. Returns true if the two points are approximately equal within
    /// the given epsilon (default <see cref="LlEpsilon"/>).
    /// </summary>
    public bool ApproximatelyEqual(PointXY<TPrecision> p)
        => ApproximatelyEqual(p, TPrecision.CreateChecked(LlEpsilon));

    /// <summary>
    /// Equality approximation with an explicit epsilon. Mirrors C++ <c>ApproximatelyEqual</c>.
    /// </summary>
    public bool ApproximatelyEqual(PointXY<TPrecision> p, TPrecision e)
        => MidgardMath.Equal(First, p.First, e) && MidgardMath.Equal(Second, p.Second, e);

    /// <summary>Gets the squared distance from this point to point p.</summary>
    public TPrecision DistanceSquared(PointXY<TPrecision> p)
    {
        TPrecision a = First - p.First;
        TPrecision b = Second - p.Second;
        return (a * a) + (b * b);
    }

    /// <summary>
    /// Gets the distance from this point to point p. Uses precision-typed sqrt (float-precision
    /// for <c>PointXY&lt;float&gt;</c>, matching C++ <c>sqrtf</c>).
    /// </summary>
    public TPrecision Distance(PointXY<TPrecision> p) => TPrecision.Sqrt(DistanceSquared(p));

    /// <summary>
    /// Returns the point along the segment between this point and the provided point using the
    /// provided distance along (0..1). Default 0.5 yields the midpoint.
    /// </summary>
    public PointXY<TPrecision> PointAlongSegment(PointXY<TPrecision> p1)
        => PointAlongSegment(p1, TPrecision.CreateChecked(0.5));

    /// <summary>
    /// Returns the point along the segment between this point and p1 at the given fractional
    /// distance. Mirrors C++ <c>PointAlongSegment</c>.
    /// </summary>
    public PointXY<TPrecision> PointAlongSegment(PointXY<TPrecision> p1, TPrecision distance)
        => new(
            First + (distance * (p1.First - First)),
            Second + (distance * (p1.Second - Second)));

    /// <summary>Add a vector to the current point, returning a new point.</summary>
    public static PointXY<TPrecision> operator +(PointXY<TPrecision> p, VectorXY<TPrecision> v)
        => new(p.First + v.X, p.Second + v.Y);

    /// <summary>Subtract a vector from the current point, returning a new point.</summary>
    public static PointXY<TPrecision> operator -(PointXY<TPrecision> p, VectorXY<TPrecision> v)
        => new(p.First - v.X, p.Second - v.Y);

    /// <summary>
    /// Subtraction of a point from the current point, returning a vector (this - p).
    /// </summary>
    public static VectorXY<TPrecision> operator -(PointXY<TPrecision> p, PointXY<TPrecision> q)
        => new(p.First - q.First, p.Second - q.Second);

    /// <summary>
    /// Finds the closest point to the supplied polyline as well as the distance to that point
    /// and the index of the segment where the closest point lies. Faithful port of the
    /// algorithm in <c>src/midgard/point2.cc</c>.
    /// </summary>
    /// <param name="pts">List of points on the polyline.</param>
    /// <returns>
    /// A tuple of (closest point along the polyline, distance of the closest point, index of the
    /// segment of the polyline which contains the closest point).
    /// </returns>
    public (PointXY<TPrecision> Closest, TPrecision Distance, int Index) ClosestPoint(
        IReadOnlyList<PointXY<TPrecision>> pts)
    {
        var closest = new PointXY<TPrecision>();
        TPrecision mindist = TPrecision.MaxValue;

        // If there are no points we are done
        if (pts.Count == 0)
        {
            return (closest, mindist, 0);
        }

        // If there is one point we are done
        if (pts.Count == 1)
        {
            return (pts[0], TPrecision.Sqrt(DistanceSquared(pts[0])), 0);
        }

        bool beyondEnd = true;            // Need to test past the end point?
        int idx = 0;                      // Index of closest segment so far
        var v1 = new VectorXY<TPrecision>(); // Segment vector (v1)
        var v2 = new VectorXY<TPrecision>(); // Vector from origin to target (v2)
        PointXY<TPrecision> projpt;       // Projected point along v1
        TPrecision dot;                   // Dot product of v1 and v2
        TPrecision comp;                  // Component of v2 along v1
        TPrecision dist;                  // Squared distance from target to closest point on line

        for (int index = 0; index < pts.Count - 1; ++index)
        {
            // Get the current segment
            PointXY<TPrecision> p0 = pts[index];
            PointXY<TPrecision> p1 = pts[index + 1];

            // Construct vector v1 - represents the segment. Skip 0 length segments that are not
            // at the end of the line.
            v1.Set(p0, p1);
            if (v1.X == TPrecision.Zero && v1.Y == TPrecision.Zero && index < pts.Count - 2)
            {
                continue;
            }

            // Vector v2 from the segment origin to the target point
            v2.Set(p0, this);

            // Find the dot product of v1 and v2. If less than 0 the segment origin is the closest
            // point. Find the distance and continue to the next segment.
            dot = v1.Dot(v2);
            if (dot <= TPrecision.Zero)
            {
                beyondEnd = false;
                dist = DistanceSquared(p0);
                if (dist < mindist)
                {
                    mindist = dist;
                    closest = p0;
                    idx = index;
                }

                continue;
            }

            // Closest point is either beyond the end of the segment or at a point along the
            // segment. Find the component of v2 along v1.
            comp = dot / v1.Dot(v1);

            // If component >= 1.0 the segment end is the closest point. A future polyline segment
            // will be closer. If last segment we need to check distance to the endpoint. Set flag
            // so this happens.
            if (comp >= TPrecision.One)
            {
                beyondEnd = true;
            }
            else
            {
                // Closest point is along the segment. The closest point is found by adding the
                // projection of v2 onto v1 to the origin point. The squared distance from this
                // point to the target is then found.
                beyondEnd = false;
                projpt = p0 + (v1 * comp);
                dist = DistanceSquared(projpt);
                if (dist < mindist)
                {
                    mindist = dist;
                    closest = projpt;
                    idx = index;
                }
            }
        }

        // Test the end point if flag is set - it may be the closest point
        if (beyondEnd)
        {
            dist = DistanceSquared(pts[pts.Count - 1]);
            if (dist < mindist)
            {
                mindist = dist;
                closest = pts[pts.Count - 1];
                idx = pts.Count - 2;
            }
        }

        return (closest, TPrecision.Sqrt(mindist), idx);
    }

    /// <summary>
    /// Tests whether this point is to the left of a segment from p1 to p2. Positive when left.
    /// </summary>
    public TPrecision IsLeft(PointXY<TPrecision> p1, PointXY<TPrecision> p2)
        => ((p2.X - p1.X) * (Y - p1.Y)) - ((X - p1.X) * (p2.Y - p1.Y));

    /// <summary>
    /// Tests whether this point is within a polygon. Assumes only the first and last vertices may
    /// be duplicated. Faithful port of the winding-number algorithm in <c>src/midgard/point2.cc</c>.
    /// </summary>
    /// <param name="poly">List of vertices that form a polygon.</param>
    /// <returns>True if the point is within the polygon.</returns>
    public bool WithinPolygon(IReadOnlyList<PointXY<TPrecision>> poly)
    {
        int count = poly.Count;
        if (count == 0)
        {
            return false;
        }

        bool closedRing = PairsEqual(poly[0], poly[count - 1]);

        // Mirror the C++ iterator setup:
        //   p1 = closedRing ? begin            : prev(end)
        //   p2 = closedRing ? next(p1) (=begin+1) : begin
        int p1Index = closedRing ? 0 : count - 1;
        int p2Index = closedRing ? 1 : 0;

        long windingNumber = 0;
        for (; p2Index < count; p1Index = p2Index, ++p2Index)
        {
            PointXY<TPrecision> p1 = poly[p1Index];
            PointXY<TPrecision> p2 = poly[p2Index];

            // going upward
            if (p1.Second <= Second)
            {
                // crosses if its in between on the y and to the left
                if (p2.Second > Second && IsLeft(p1, p2) > TPrecision.Zero)
                {
                    windingNumber += 1;
                }
            }
            else
            {
                // going downward: crosses if its in between or on and to the right
                if (p2.Second <= Second && IsLeft(p1, p2) < TPrecision.Zero)
                {
                    windingNumber -= 1;
                }
            }
        }

        return windingNumber != 0;
    }

    /// <summary>
    /// Handy for templated functions that use both Point2 or PointLL to know whether the
    /// coordinate system is spherical or planar. Always false for <see cref="PointXY{TPrecision}"/>.
    /// </summary>
    public static bool IsSpherical() => false;

    /// <summary>
    /// Factory used by generic midgard containers to construct a point of this type from scalar
    /// components. Mirrors the C++ <c>coord_t(x, y)</c> construction.
    /// </summary>
    public static PointXY<TPrecision> Create(TPrecision x, TPrecision y) => new(x, y);

    /// <summary>Returns a string in the format "lon,lat" (first,second).</summary>
    public override string ToString() => $"{First},{Second}";

    /// <summary>
    /// Value equality on both components. Mirrors std::pair equality used by the C++ container
    /// operations (e.g. ring-closure check and unordered_map lookup combined with the hash).
    /// </summary>
    public override bool Equals(object? obj) => obj is PointXY<TPrecision> p && PairsEqual(this, p);

    /// <summary>
    /// Hash code combining both components, mirroring the C++
    /// <c>std::hash&lt;PointXY&gt;</c> specialization (hash_combine of first then second).
    /// </summary>
    public override int GetHashCode()
    {
        int seed = 0;
        MidgardMath.HashCombine(ref seed, First);
        MidgardMath.HashCombine(ref seed, Second);
        return seed;
    }

    private static bool PairsEqual(PointXY<TPrecision> a, PointXY<TPrecision> b)
        => a.First == b.First && a.Second == b.Second;
}
