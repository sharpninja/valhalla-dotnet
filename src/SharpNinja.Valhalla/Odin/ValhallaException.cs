// Minimal C# stand-in for Valhalla's valhalla_exception_t (valhalla/exceptions.h) @ 3.7.0.
//
// odin throws valhalla_exception_t{code} for invalid trip paths (e.g. 210 = no nodes, 211 = single
// node, 220 = invalid heading for cardinal direction). The full exception registry (HTTP status
// codes, OSRM/OSM error bodies) belongs to the EXCLUDED request/serialization layer; only the
// numeric error code the maneuver/directions builders raise is ported here.

using System;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Exception thrown by the odin builders for invalid input. Faithful stand-in for the
/// <c>valhalla::valhalla_exception_t</c> the builders construct with a numeric error code.
/// </summary>
public sealed class ValhallaException : Exception
{
    /// <summary>Constructs an exception carrying the Valhalla error code.</summary>
    /// <param name="code">The Valhalla error code (e.g. 210, 211, 220).</param>
    public ValhallaException(int code)
        : base($"valhalla_exception_t{{{code}}}")
    {
        Code = code;
    }

    /// <summary>The Valhalla error code.</summary>
    public int Code { get; }
}
