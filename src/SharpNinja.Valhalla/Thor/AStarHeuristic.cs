// Faithful C# port of Valhalla thor AStarHeuristic (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/thor/astarheuristic.h
//
// Class to calculate A* cost heuristics based on distances of nodes from a destination within the
// shortest path computation. The heuristic estimate is the great-circle distance (approximated via
// midgard::DistanceApproximator) to the destination multiplied by a costing factor that MUST
// underestimate the true cost so that A* remains admissible.
//
// PORT-NOTE: the C++ class is header-only and uses the templated
// midgard::DistanceApproximator<PointLL>. The C# DistanceApproximator is
// DistanceApproximator<TPoint,TPrecision>; PointLL implements IGeoPoint<double>, so we instantiate
// DistanceApproximator<PointLL,double>. All arithmetic (sqrtf in float precision) is preserved.

using System;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Thor;

/// <summary>
/// Class to calculate A* cost heuristics based on distances of nodes from a destination within the
/// shortest path computation. Faithful port of <c>valhalla::thor::AStarHeuristic</c>.
/// </summary>
public sealed class AStarHeuristic
{
    // Distance approximation (lazily set via Init/SetTestPoint).
    private DistanceApproximator<PointLL, double>? _distapprox;

    // Cost factor - ensures the cost estimate underestimates the true cost.
    private float _costfactor;

    /// <summary>Constructor. Mirrors the C++ default ctor (<c>distapprox_({}), costfactor_(1.0f)</c>).</summary>
    public AStarHeuristic()
    {
        _distapprox = null;
        _costfactor = 1.0f;
    }

    /// <summary>
    /// Sets the destination latitude and longitude positions in the distance approximator. Faithful
    /// port of <c>Init(const PointLL&amp; ll, const float factor)</c>.
    /// </summary>
    /// <param name="ll">Latitude, longitude (in degrees) of the destination.</param>
    /// <param name="factor">
    /// Costing factor to multiply distance by. This factor needs to be tied to the costing model to
    /// multiply distance that will underestimate the cost to the destination, but keep close to a
    /// reasonable true cost so that performance is kept high.
    /// </param>
    public void Init(PointLL ll, float factor)
    {
        if (_distapprox is null)
        {
            _distapprox = new DistanceApproximator<PointLL, double>(ll);
        }
        else
        {
            _distapprox.SetTestPoint(ll);
        }

        _costfactor = factor;
    }

    /// <summary>
    /// Get the distance to the destination given the lat,lng. Faithful port of
    /// <c>GetDistance(const PointLL&amp; ll)</c>.
    /// </summary>
    /// <param name="ll">Current latitude, longitude.</param>
    /// <returns>Returns the distance to the destination.</returns>
    public float GetDistance(PointLL ll) => MathF.Sqrt((float)Distapprox.DistanceSquared(ll));

    /// <summary>
    /// Get the A* heuristic given the distance to the destination. Faithful port of
    /// <c>Get(const float distance)</c>.
    /// </summary>
    /// <param name="distance">Distance (meters) to the destination.</param>
    /// <returns>An estimate of the cost to the destination (MUST underestimate the true cost).</returns>
    public float Get(float distance) => distance * _costfactor;

    /// <summary>
    /// Get the A* heuristic given the current lat,lng. Faithful port of <c>Get(const PointLL&amp; ll)</c>.
    /// </summary>
    /// <param name="ll">Lat,lng.</param>
    /// <returns>An estimate of the cost to the destination (MUST underestimate the true cost).</returns>
    public float Get(PointLL ll) => MathF.Sqrt((float)Distapprox.DistanceSquared(ll)) * _costfactor;

    /// <summary>
    /// Get the A* heuristic given the lat,lng. Also returns distance via <paramref name="dist"/>.
    /// Faithful port of <c>Get(const PointLL&amp; ll, float&amp; dist)</c>.
    /// </summary>
    /// <param name="ll">Lat,lng.</param>
    /// <param name="dist">Distance (meters) to the destination (output).</param>
    /// <returns>An estimate of the cost to the destination (MUST underestimate the true cost).</returns>
    public float Get(PointLL ll, out float dist)
    {
        dist = MathF.Sqrt((float)Distapprox.DistanceSquared(ll));
        return dist * _costfactor;
    }

    private DistanceApproximator<PointLL, double> Distapprox =>
        _distapprox ?? throw new InvalidOperationException("AStarHeuristic.Init must be called before use");
}
