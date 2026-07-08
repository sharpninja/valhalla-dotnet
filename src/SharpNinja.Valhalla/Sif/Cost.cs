// Faithful C# port of Valhalla sif Cost (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/sif/costconstants.h (struct Cost)
//
// Simple structure for returning costs. Includes cost and true elapsed time in seconds.
// Operator semantics (+, -, +=, -=, *=, *, <, >) mirror the C++ struct exactly. Members are
// kept as float to match the C++ representation precisely.

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// Simple structure for returning costs. Includes cost and true elapsed time in seconds.
/// Faithful port of <c>valhalla::sif::Cost</c>.
/// </summary>
/// <remarks>
/// PORT-NOTE: C++ declares the fields in the order <c>{ float cost; float secs; }</c> but its
/// two-arg constructor is <c>Cost(const float c, const float s)</c> (cost first, then secs).
/// The order is preserved here.
/// </remarks>
public struct Cost
{
    /// <summary>Cost (units defined by the costing model).</summary>
    public float CostValue;

    /// <summary>True elapsed time in seconds.</summary>
    public float Secs;

    /// <summary>Constructor given cost and seconds.</summary>
    /// <param name="c">Cost (units defined by the costing model).</param>
    /// <param name="s">Time in seconds.</param>
    public Cost(float c, float s)
    {
        CostValue = c;
        Secs = s;
    }

    /// <summary>Add 2 costs.</summary>
    public static Cost operator +(Cost a, Cost other)
        => new Cost(a.CostValue + other.CostValue, a.Secs + other.Secs);

    /// <summary>Subtract cost from another.</summary>
    public static Cost operator -(Cost a, Cost other)
        => new Cost(a.CostValue - other.CostValue, a.Secs - other.Secs);

    /// <summary>Scale the cost by a factor (for partial costs along edges).</summary>
    public static Cost operator *(Cost a, float f)
        => new Cost(a.CostValue * f, a.Secs * f);

    /// <summary>Less than operator - compares cost.</summary>
    public static bool operator <(Cost a, Cost other) => a.CostValue < other.CostValue;

    /// <summary>Greater than operator - compares cost.</summary>
    public static bool operator >(Cost a, Cost other) => a.CostValue > other.CostValue;
}
