// Faithful C# port of Valhalla midgard PointLL (GeoPoint<double>).
// Sources: valhalla/midgard/pointll.h and src/midgard/pointll.cc
// Self-contained engine port: does NOT reuse other TruckMate types.
//
// The C++ GeoPoint<PrecisionT> derives from PointXY<PrecisionT> and adds lng/lat
// naming plus spherical geometry (heading, curvature, great-circle distance,
// spherical midpoint, longitude-scaled projection). C# PointXY is sealed, so this
// port reproduces the needed planar storage/members directly and adds the spherical
// extensions. Storage mirrors std::pair: First == x == lng, Second == y == lat.
// PointLL is the double-precision instantiation (GeoPoint<double>).

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Longitude, Latitude point (double precision). Mirrors the C++
/// <c>GeoPoint&lt;double&gt;</c> (aliased <c>PointLL</c>) which derives from
/// <c>PointXY&lt;double&gt;</c> and exposes lng/lat naming. Extends functionality to add
/// heading, curvature, and distance based on spherical geometry. The order in the pair is
/// LONGITUDE first (<see cref="First"/>/<see cref="Lng"/>), LATITUDE second
/// (<see cref="Second"/>/<see cref="Lat"/>).
/// </summary>
public sealed class PointLL : IGeoPoint<double>, IMidgardCoord<PointLL, double>
{
    /// <summary>
    /// The invalid sentinel value used for uninitialized coordinates. Mirrors the C++
    /// <c>INVALID_LL = (double)0xBADBADBAD</c>.
    /// </summary>
    public const double InvalidLl = unchecked((double)0xBADBADBAD);

    /// <summary>
    /// The C++ default approximate-equality epsilon (anonymous-namespace <c>LL_EPSILON</c>).
    /// </summary>
    public const double LlEpsilon = .00002;

    /// <summary>
    /// Default constructor. Sets longitude and latitude to <see cref="InvalidLl"/> (matches
    /// the C++ <c>GeoPoint()</c> default constructor).
    /// </summary>
    public PointLL()
    {
        First = InvalidLl;
        Second = InvalidLl;
    }

    /// <summary>Constructs a point with the given longitude (first/x) and latitude (second/y).</summary>
    public PointLL(double lng, double lat)
    {
        First = lng;
        Second = lat;
    }

    /// <summary>First component of the pair (the x coordinate = longitude).</summary>
    public double First { get; private set; }

    /// <summary>Second component of the pair (the y coordinate = latitude).</summary>
    public double Second { get; private set; }

    /// <summary>Gets the x component of the point (= longitude).</summary>
    public double X => First;

    /// <summary>Gets the y component of the point (= latitude).</summary>
    public double Y => Second;

    /// <summary>Gets the longitude in degrees.</summary>
    public double Lng => First;

    /// <summary>Gets the latitude in degrees.</summary>
    public double Lat => Second;

    /// <summary>Sets the x component (= longitude).</summary>
    public void SetX(double x) => First = x;

    /// <summary>Sets the y component (= latitude).</summary>
    public void SetY(double y) => Second = y;

    /// <summary>Sets the coordinate components to the specified values (lng, lat).</summary>
    public void Set(double lng, double lat)
    {
        First = lng;
        Second = lat;
    }

    /// <summary>
    /// Checks for validity of the coordinates. Returns false if lat or lon coordinates are set
    /// to <see cref="InvalidLl"/>.
    /// </summary>
    public bool IsValid() => First != InvalidLl && Second != InvalidLl;

    /// <summary>
    /// Checks whether the lon and lat coordinates fall within -180/180 and -90/90 respectively.
    /// </summary>
    public bool InRange() => First is >= -180 and <= 180 && Second is >= -90 and <= 90;

    /// <summary>Sets the coordinates to an invalid state.</summary>
    public void Invalidate()
    {
        First = InvalidLl;
        Second = InvalidLl;
    }

    /// <summary>
    /// Equality approximation. Returns true if the two points are approximately equal within
    /// the default epsilon (<see cref="LlEpsilon"/>).
    /// </summary>
    public bool ApproximatelyEqual(PointLL p) => ApproximatelyEqual(p, LlEpsilon);

    /// <summary>Equality approximation with an explicit epsilon. Mirrors C++ <c>ApproximatelyEqual</c>.</summary>
    public bool ApproximatelyEqual(PointLL p, double e)
        => MidgardMath.Equal(First, p.First, e) && MidgardMath.Equal(Second, p.Second, e);

    /// <summary>
    /// Approximates the distance squared between two lng,lat points - uses the
    /// <see cref="DistanceApproximator{TPoint,TPrecision}"/>. Returns squared distance in meters.
    /// </summary>
    public double DistanceSquared(PointLL ll2)
    {
        var approx = new DistanceApproximator<PointLL, double>(this);
        return approx.DistanceSquared(ll2);
    }

    /// <summary>
    /// Returns the point along the segment between this point and the provided point using the
    /// provided distance along (0..1). Default 0.5 yields the (spherical) midpoint. Faithful port
    /// of the spherical interpolation in <c>src/midgard/pointll.cc</c>.
    /// </summary>
    public PointLL PointAlongSegment(PointLL p) => PointAlongSegment(p, 0.5);

    /// <summary>
    /// Returns the point along the segment between this point and p at the given fractional
    /// distance using spherical geometry. Mirrors C++ <c>GeoPoint::PointAlongSegment</c>.
    /// </summary>
    public PointLL PointAlongSegment(PointLL p, double distance)
    {
        if (distance == 0)
        {
            return this;
        }

        if (distance == 1)
        {
            return p;
        }

        // radians
        double lon1 = First * -Constants.RadPerDegD;
        double lat1 = Second * Constants.RadPerDegD;
        double lon2 = p.First * -Constants.RadPerDegD;
        double lat2 = p.Second * Constants.RadPerDegD;

        // useful throughout
        double sl1 = Math.Sin(lat1);
        double sl2 = Math.Sin(lat2);
        double cl1 = Math.Cos(lat1);
        double cl2 = Math.Cos(lat2);

        // fairly accurate distance between points
        double d = Math.Acos((sl1 * sl2) + (cl1 * cl2 * Math.Cos(lon1 - lon2)));

        // interpolation parameters
        double sd = Math.Sin(d);
        double a = Math.Sin(d * (1 - distance)) / sd;
        double b = Math.Sin(d * distance) / sd;
        double acs1 = a * cl1;
        double bcs2 = b * cl2;

        // find the interpolated point along the arc
        double x = (acs1 * Math.Cos(lon1)) + (bcs2 * Math.Cos(lon2));
        double y = (acs1 * Math.Sin(lon1)) + (bcs2 * Math.Sin(lon2));
        double z = (a * sl1) + (b * sl2);
        return new PointLL(
            Math.Atan2(y, x) * -Constants.DegPerRadD,
            Math.Atan2(z, Math.Sqrt((x * x) + (y * y))) * Constants.DegPerRadD);
    }

    /// <summary>
    /// Calculates the distance between two lng,lat's in meters. Uses spherical geometry
    /// (haversine formula). Faithful port of <c>GeoPoint::Distance</c>.
    /// </summary>
    public double Distance(PointLL other)
    {
        // Equal points short-circuit
        if (Equals(other))
        {
            return 0.0;
        }

        // Convert the coordinates to radians.
        double phi1 = Lat * Constants.RadPerDegD;
        double phi2 = other.Lat * Constants.RadPerDegD;
        double dphi = phi2 - phi1;

        double lambda1 = Lng * Constants.RadPerDegD;
        double lambda2 = other.Lng * Constants.RadPerDegD;
        double dlambda = lambda2 - lambda1;

        double c1 = Math.Cos(phi1);
        double c2 = Math.Cos(phi2);

        // Haversine (numerically stable for small angles)
        double sdphi2 = Math.Sin(dphi / 2.0);
        double sdlmb2 = Math.Sin(dlambda / 2.0);

        // h = sin²(Δφ/2) + c1 * c2 * sin²(Δλ/2)
        double h = (sdphi2 * sdphi2) + (c1 * c2 * sdlmb2 * sdlmb2);

        // Clamp protects against cases where the two points are on opposite sides of the Earth.
        h = Math.Min(1.0, Math.Max(0.0, h));

        // d = 2r * arcsin(sqrt(h))
        double d = 2.0 * Math.Asin(Math.Sqrt(h));
        double meters = Constants.RadEarthMeters * d;
        return meters;
    }

    /// <summary>
    /// Calculates the curvature using this position and 2 others. Found by computing the radius
    /// of the circle that circumscribes the 3 positions. Returns max value if the points are
    /// collinear.
    /// </summary>
    public double Curvature(PointLL ll1, PointLL ll2)
    {
        double a = Distance(ll1);
        double b = ll1.Distance(ll2);
        double c = Distance(ll2);
        double s = (a + b + c) * 0.5;
        double k = Math.Sqrt(s * (s - a) * (s - b) * (s - c));
        return double.IsNaN(k) || k == 0.0
            ? double.MaxValue
            : (a * b * c) / (4.0 * k);
    }

    /// <summary>
    /// Calculates the heading or azimuth from the current lng,lat to the specified lng,lat.
    /// This uses Haversine method (spherical geometry). Returns heading in degrees [0,360]
    /// where 0 is due north, 90 east, 180 south, 270 west.
    /// </summary>
    public double Heading(PointLL ll2)
    {
        // If points are the same, return 0
        if (Equals(ll2))
        {
            return 0.0;
        }

        double lat1 = Lat * Constants.RadPerDegD;
        double lat2 = ll2.Lat * Constants.RadPerDegD;
        double dlng = (ll2.Lng - Lng) * Constants.RadPerDegD;
        double y = Math.Sin(dlng) * Math.Cos(lat2);
        double x = (Math.Cos(lat1) * Math.Sin(lat2)) - (Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dlng));
        double bearing = Math.Atan2(y, x) * Constants.DegPerRadD;
        return bearing < 0.0 ? bearing + 360.0 : bearing;
    }

    /// <summary>
    /// Finds the closest point to the supplied polyline as well as the distance to that point
    /// and the (floor) index of the segment where the closest point lies. In the case of a tie
    /// where the closest point is a point in the linestring, the most extreme index (closest to
    /// the end of the linestring in the direction of the search) will win. Faithful port of
    /// <c>GeoPoint::ClosestPoint</c>.
    /// </summary>
    /// <param name="pts">List of points on the polyline.</param>
    /// <param name="pivotIndex">Index where the processing of closest point should start.</param>
    /// <param name="forwardDistCutoff">
    /// Minimum linear distance along pts that should be considered before giving up (forward).
    /// </param>
    /// <param name="reverseDistCutoff">
    /// Minimum linear distance along pts that should be considered before giving up (reverse).
    /// </param>
    /// <returns>
    /// A tuple of (closest point along the polyline, distance in meters of the closest point,
    /// index of the segment of the polyline which contains the closest point).
    /// </returns>
    public (PointLL Closest, double Distance, int Index) ClosestPoint(
        IReadOnlyList<PointLL> pts,
        int pivotIndex = 0,
        double forwardDistCutoff = double.PositiveInfinity,
        double reverseDistCutoff = 0)
    {
        // setup
        if (pts.Count == 0 || pivotIndex < 0 || pivotIndex > pts.Count - 1)
        {
            return (new PointLL(), double.MaxValue, -1);
        }

        int closestSegment = pivotIndex;
        PointLL closest = pts[pivotIndex];
        var approx = new DistanceApproximator<PointLL, double>(this);
        double mindistsqr = approx.DistanceSquared(closest);

        // start going backwards, then go forwards
        foreach (bool reverse in new[] { true, false })
        {
            // get the range and distance for this direction
            double distCutoff = reverse ? reverseDistCutoff : forwardDistCutoff;
            int increment = reverse ? -1 : 1;
            int indices = reverse ? pivotIndex : (pts.Count - 1) - pivotIndex;

            for (int index = pivotIndex - (reverse ? 1 : 0);
                 indices > 0 && distCutoff > 0.0;
                 index += increment, --indices)
            {
                // Get the current segment
                PointLL u = pts[index];
                PointLL v = pts[index + 1];

                // Project a onto b where b is the origin vector representing this segment
                // and a is the origin vector to the point we are projecting, (a.b/b.b)*b
                double bx = v.Lng - u.Lng;
                double by = v.Lat - u.Lat;

                // Scale longitude when finding the projection. Avoid divided-by-zero
                // which gives a NaN scale, otherwise comparisons below will fail
                double bx2 = bx * approx.GetLngScale();
                double sq = (bx2 * bx2) + (by * by);
                double scale = sq > 0
                    ? ((((Lng - u.Lng) * approx.GetLngScale() * bx2) + ((Lat - u.Lat) * by)) / sq)
                    : 0.0;

                // Projects along the ray before u
                bool rightMost = false;
                PointLL point;
                if (scale <= 0.0)
                {
                    point = new PointLL(u.Lng, u.Lat);
                }
                else if (scale >= 1.0)
                {
                    // Projects along the ray after v
                    point = new PointLL(v.Lng, v.Lat);
                    rightMost = true;
                }
                else
                {
                    // Projects along the ray between u and v
                    point = new PointLL(u.Lng + (bx * scale), u.Lat + (by * scale));
                }

                // Check if this point is better
                double sqDistance = approx.DistanceSquared(point);
                if (sqDistance < mindistsqr)
                {
                    closestSegment = index + (rightMost ? 1 : 0);
                    mindistsqr = sqDistance;
                    closest = point;
                }

                // Check if we should bail early because of looking at too much shape
                if (!double.IsPositiveInfinity(distCutoff))
                {
                    distCutoff -= u.Distance(v);
                }
            }
        }

        // give back what we found
        return (closest, Math.Sqrt(mindistsqr), closestSegment);
    }

    /// <summary>
    /// Calculate the heading from the start index within a polyline of lng,lat points to a point
    /// at the specified distance from the start. Faithful port of
    /// <c>GeoPoint::HeadingAlongPolyline(pts, dist, idx0, idx1)</c>.
    /// </summary>
    public static double HeadingAlongPolyline(
        IReadOnlyList<PointLL> pts,
        double dist,
        uint idx0,
        uint idx1)
    {
        // Check that at least 2 points exist
        int n = (int)idx1 - (int)idx0;
        if (n < 1)
        {
            // LOG_ERROR("PointLL::HeadingAlongPolyline has < 2 vertices");
            return 0.0;
        }

        // If more than 2 points, walk edges of the polyline until the length is exceeded.
        if (n > 1)
        {
            double d = 0.0;
            int pt0 = (int)idx0;
            int pt1 = pt0 + 1;
            while (d < dist && pt1 <= (int)idx1)
            {
                double seglength = pts[pt0].Distance(pts[pt1]);
                if (d + seglength > dist)
                {
                    // Set the extrapolated point along the line.
                    double pct = (dist - d) / seglength;
                    var ll = new PointLL(
                        pts[pt0].Lng + ((pts[pt1].Lng - pts[pt0].Lng) * pct),
                        pts[pt0].Lat + ((pts[pt1].Lat - pts[pt0].Lat) * pct));
                    return pts[(int)idx0].Heading(ll);
                }

                d += seglength;
                pt0++;
                pt1++;
            }
        }

        // Only 2 points or the length of polyline is less than the specified distance.
        // Return heading from first to last point.
        return pts[(int)idx0].Heading(pts[(int)idx1]);
    }

    /// <summary>
    /// Calculate the heading from the start of a polyline of lng,lat points to a point at the
    /// specified distance from the start.
    /// </summary>
    public static double HeadingAlongPolyline(IReadOnlyList<PointLL> pts, double dist)
        => HeadingAlongPolyline(pts, dist, 0, (uint)(pts.Count - 1));

    /// <summary>
    /// Calculate the heading from a point at a specified distance from the end of a polyline of
    /// lng,lat points to the end point of the polyline. Faithful port of
    /// <c>GeoPoint::HeadingAtEndOfPolyline(pts, dist, idx0, idx1)</c>.
    /// </summary>
    public static double HeadingAtEndOfPolyline(
        IReadOnlyList<PointLL> pts,
        double dist,
        uint idx0,
        uint idx1)
    {
        // Check that at least 2 points exist
        int n = (int)idx1 - (int)idx0;
        if (n < 1)
        {
            // LOG_ERROR("PointLL::HeadingAtEndOfPolyline has < 2 vertices");
            return 0.0;
        }

        // If more than 2 points, walk edges of the polyline until the length is exceeded.
        if (n > 1)
        {
            double d = 0.0;
            int pt1 = (int)idx1;
            int pt0 = pt1 - 1;
            while (d < dist && pt0 >= (int)idx0)
            {
                double seglength = pts[pt0].Distance(pts[pt1]);
                if (d + seglength > dist)
                {
                    // Set the extrapolated point along the line.
                    double pct = (dist - d) / seglength;
                    var ll = new PointLL(
                        pts[pt1].Lng + ((pts[pt0].Lng - pts[pt1].Lng) * pct),
                        pts[pt1].Lat + ((pts[pt0].Lat - pts[pt1].Lat) * pct));
                    return ll.Heading(pts[(int)idx1]);
                }

                if (pt0 == 0)
                {
                    break;
                }

                d += seglength;
                pt1--;
                pt0--;
            }
        }

        // Only 2 points or the length of polyline is less than the specified distance.
        // Return heading from first to last point.
        return pts[(int)idx0].Heading(pts[(int)idx1]);
    }

    /// <summary>
    /// Calculate the heading from a point at a specified distance from the end of a polyline of
    /// lng,lat points to the end point of the polyline.
    /// </summary>
    public static double HeadingAtEndOfPolyline(IReadOnlyList<PointLL> pts, double dist)
        => HeadingAtEndOfPolyline(pts, dist, 0, (uint)(pts.Count - 1));

    /// <summary>
    /// Tests whether this point is to the left of a segment from p1 to p2. Positive when left.
    /// </summary>
    public double IsLeft(PointLL p1, PointLL p2)
        => ((p2.Lng - p1.Lng) * (Lat - p1.Lat)) - ((Lng - p1.Lng) * (p2.Lat - p1.Lat));

    /// <summary>
    /// Tests whether this point is within a polygon. Assumes only the first and last vertices may
    /// be duplicated. Faithful port of the winding-number algorithm in <c>src/midgard/pointll.cc</c>.
    /// </summary>
    /// <param name="poly">List of vertices that form a polygon.</param>
    /// <returns>True if the point is within the polygon.</returns>
    public bool WithinPolygon(IReadOnlyList<PointLL> poly)
    {
        int count = poly.Count;
        if (count == 0)
        {
            return false;
        }

        bool closedRing = poly[0].Equals(poly[count - 1]);

        // Mirror the C++ iterator setup:
        //   p1 = closedRing ? begin            : prev(end)
        //   p2 = closedRing ? next(p1) (=begin+1) : begin
        int p1Index = closedRing ? 0 : count - 1;
        int p2Index = closedRing ? 1 : 0;

        long windingNumber = 0;
        for (; p2Index < count; p1Index = p2Index, ++p2Index)
        {
            PointLL p1 = poly[p1Index];
            PointLL p2 = poly[p2Index];

            // going upward
            if (p1.Second <= Second)
            {
                // crosses if its in between on the y and to the left
                if (p2.Second > Second && IsLeft(p1, p2) > 0)
                {
                    windingNumber += 1;
                }
            }
            else
            {
                // going downward: crosses if its in between or on and to the right
                if (p2.Second <= Second && IsLeft(p1, p2) < 0)
                {
                    windingNumber -= 1;
                }
            }
        }

        return windingNumber != 0;
    }

    /// <summary>
    /// Handy for templated functions that use both Point2 or PointLL to know whether the
    /// coordinate system is spherical or planar. Always true for <see cref="PointLL"/>.
    /// </summary>
    public static bool IsSpherical() => true;

    /// <summary>
    /// Factory used by generic midgard containers (e.g. <see cref="Tiles{TCoord,TPrecision}"/>) to
    /// construct a PointLL from scalar components. Mirrors C++ <c>coord_t(lng, lat)</c>.
    /// </summary>
    public static PointLL Create(double x, double y) => new(x, y);

    /// <summary>
    /// Project this point onto the line from u to v. Computes the longitude scale at this point's
    /// latitude. Faithful port of <c>GeoPoint::Project(u, v)</c>.
    /// </summary>
    public PointLL Project(PointLL u, PointLL v)
    {
        double lonScale = Math.Cos(Second * Constants.RadPerDeg);
        return Project(u, v, lonScale);
    }

    /// <summary>
    /// Project this point onto the line from u to v with a precomputed longitude scale. Faithful
    /// port of <c>GeoPoint::Project(u, v, lon_scale)</c>.
    /// </summary>
    public PointLL Project(PointLL u, PointLL v, double lonScale)
    {
        // we're done if this is a zero length segment
        if (u.Equals(v))
        {
            return u;
        }

        // project a onto b where b is the origin vector representing this segment
        // and a is the origin vector to the point we are projecting, (a.b/b.b)*b
        double bx = v.First - u.First;
        double by = v.Second - u.Second;

        // Scale longitude when finding the projection
        double bx2 = bx * lonScale;
        double sq = (bx2 * bx2) + (by * by);
        double scale = ((First - u.First) * lonScale * bx2)
                       + ((Second - u.Second) * by); // only need the numerator at first

        // projects along the ray before u
        if (scale <= 0.0)
        {
            return u;
        }

        // projects along the ray after v
        if (scale >= sq)
        {
            return v;
        }

        // projects along the ray between u and v
        scale /= sq;
        return new PointLL(u.First + (bx * scale), u.Second + (by * scale));
    }

    /// <summary>
    /// Project this point to the supplied polyline as well as the distance to that point and the
    /// (floor) index of the segment where the projected location lies. Faithful port of
    /// <c>GeoPoint::Project(pts)</c>.
    /// </summary>
    public (PointLL Best, double Distance, int Index) Project(IReadOnlyList<PointLL> pts)
    {
        double minDistance = double.MaxValue;
        var best = new PointLL();
        int bestIndex = 0;
        for (int u = 0; u + 1 < pts.Count; ++u)
        {
            int v = u + 1;
            PointLL candidate = Project(pts[u], pts[v]);
            double distance = Distance(candidate);
            if (distance < minDistance)
            {
                minDistance = distance;
                best = candidate;
                bestIndex = u;
            }
        }

        return (best, minDistance, bestIndex);
    }

    /// <summary>Returns a string in the format "lng,lat" (first,second).</summary>
    public override string ToString() => $"{First},{Second}";

    /// <summary>Value equality on both components. Mirrors std::pair equality.</summary>
    public override bool Equals(object? obj)
        => obj is PointLL p && First == p.First && Second == p.Second;

    /// <summary>
    /// Hash code combining both components, mirroring the C++ <c>std::hash&lt;PointLL&gt;</c>
    /// specialization (hash_combine of first then second).
    /// </summary>
    public override int GetHashCode()
    {
        int seed = 0;
        MidgardMath.HashCombine(ref seed, First);
        MidgardMath.HashCombine(ref seed, Second);
        return seed;
    }
}
