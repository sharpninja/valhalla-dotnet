// Coordinate abstraction shared by the midgard port's coordinate types
// (PointXY / Point2 and PointLL). The C++ engine uses a template parameter
// `coord_t` (instantiated as Point2 or PointLL) together with the associated
// `coord_t::value_type` / `first_type` / `second_type` scalar precision. In C#
// we model that with a static-abstract interface so generic containers such as
// Aabb2<TCoord,TPrecision> and Tiles<TCoord,TPrecision> can both read the x/y
// components and construct new coordinates of the concrete type.
//
// This mirrors what the C++ code relies upon for `coord_t`:
//   - x() / y() accessors          -> X / Y
//   - construction from (x, y)     -> Create(x, y)
//   - static IsSpherical()         -> IsSpherical()

using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Abstraction over a midgard coordinate type (<see cref="PointXY{TPrecision}"/> or
/// <see cref="PointLL"/>). Plays the role of the C++ template parameter <c>coord_t</c>,
/// exposing the x/y components, a factory for constructing the concrete coordinate type
/// from scalar components, and the spherical/planar flag.
/// </summary>
/// <typeparam name="TSelf">The concrete coordinate type implementing the interface.</typeparam>
/// <typeparam name="TPrecision">The scalar precision type (float or double).</typeparam>
public interface IMidgardCoord<TSelf, TPrecision>
    where TSelf : IMidgardCoord<TSelf, TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    /// <summary>Gets the x component (first / longitude) of the coordinate.</summary>
    TPrecision X { get; }

    /// <summary>Gets the y component (second / latitude) of the coordinate.</summary>
    TPrecision Y { get; }

    /// <summary>
    /// Constructs a coordinate of the concrete type from the given x (first) and y (second).
    /// Mirrors the C++ <c>coord_t(x, y)</c> construction used throughout <c>tiles.cc</c>.
    /// </summary>
    static abstract TSelf Create(TPrecision x, TPrecision y);

    /// <summary>
    /// Indicates whether the coordinate system is spherical (lat/lng) or planar (cartesian).
    /// Mirrors the static C++ <c>coord_t::IsSpherical()</c>.
    /// </summary>
    static abstract bool IsSpherical();
}
