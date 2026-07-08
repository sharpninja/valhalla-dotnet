// Faithful C# port of Valhalla midgard projector_t (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/midgard/util.h (struct projector_t)
//
// A place where we can share the projecting of a single point onto any number of geometries where
// the point is long lived and we survey many shape segments (used by both loki and meili). It
// precomputes the longitude scale at the point's latitude and a DistanceApproximator seeded at the
// point, then projects an arbitrary segment [u,v] onto the point in performance-critical inner
// loops.
//
// PORT-NOTE: projector_t lives in midgard in C++ but is only consumed by loki (and meili, excluded)
// in this slice, so it is ported into the loki namespace to avoid widening the shared midgard
// surface. The math is identical to PointLL.Project(u, v, lonScale); it is reproduced here verbatim
// (rather than delegating) to match the C++ inner-loop exactly and to expose the seeded
// DistanceApproximator (approx) that the search uses to score candidates.

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Loki;

/// <summary>
/// Shares the projection of a single (long-lived) point onto many shape segments. Faithful port of
/// <c>valhalla::midgard::projector_t</c>.
/// </summary>
public sealed class Projector
{
    /// <summary>Constructor. Faithful port of <c>projector_t(const PointLL&amp; ll)</c>.</summary>
    /// <param name="ll">The point to project onto segments.</param>
    public Projector(PointLL ll)
    {
        LonScale = System.Math.Cos(ll.Lat * Constants.RadPerDegD);
        Lat = ll.Lat;
        Lng = ll.Lng;
        Approx = new DistanceApproximator<PointLL, double>(ll);
    }

    /// <summary>Longitude scale at the point's latitude (cos(lat)).</summary>
    public double LonScale { get; }

    /// <summary>The point's latitude (degrees).</summary>
    public double Lat { get; }

    /// <summary>The point's longitude (degrees).</summary>
    public double Lng { get; }

    /// <summary>Distance approximator seeded at the point (used to score candidate projections).</summary>
    public DistanceApproximator<PointLL, double> Approx { get; }

    /// <summary>
    /// Project the long-lived point onto the segment [u, v]. Faithful port of
    /// <c>projector_t::operator()(const PointLL&amp; u, const PointLL&amp; v)</c>.
    /// </summary>
    /// <param name="u">Segment start.</param>
    /// <param name="v">Segment end.</param>
    /// <returns>The projected point on the segment nearest the long-lived point.</returns>
    public PointLL Project(PointLL u, PointLL v)
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
        double bx2 = bx * LonScale;
        double sq = (bx2 * bx2) + (by * by);
        double scale = ((Lng - u.Lng) * LonScale * bx2) + ((Lat - u.Lat) * by); // numerator first

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
}
