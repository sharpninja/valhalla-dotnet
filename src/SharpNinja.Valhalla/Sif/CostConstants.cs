// Faithful C# port of Valhalla sif costconstants.h (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/sif/costconstants.h
//
// Holds the cost-model enumerations and small value types shared across the sif
// costing layer. Values mirror the C++ `enum class : uint8_t` declarations exactly
// (underlying type byte), and float/double precision is preserved. Public members
// are PascalCase per project convention.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Sif;

/// <summary>
/// Shared sif costing constants and enumerations. Faithful port of
/// <c>valhalla::sif</c> declarations in <c>costconstants.h</c>.
/// </summary>
public static class CostConstants
{
    /// <summary>
    /// Transition factor to use when traffic data is available. This multiplies the
    /// turn cost * stop impact (rather than using the density factor). When traffic
    /// is available, the edge speeds account for some of the intersection costing
    /// due to deceleration and acceleration into and out of an intersection.
    /// </summary>
    public const float TrafficTransitionFactor = 0.25f;

    /// <summary>
    /// This is the edge length, in meters, that we consider short for internal edges.
    /// </summary>
    public const float ShortInternalLength = 8.0f;
}

/// <summary>Travel modes. C++ <c>enum class TravelMode : uint8_t</c>.</summary>
public enum TravelMode : byte
{
    Drive = 0,
    Pedestrian = 1,
    Bicycle = 2,
    PublicTransit = 3,
    MaxTravelMode = 4,
}

/// <summary>Vehicle travel type. C++ <c>enum class VehicleType : uint8_t</c>.</summary>
public enum VehicleType : byte
{
    Car = 0,
    Motorcycle = 1,
    Bus = 2,
    Truck = 3,
    MotorScooter = 4,
    FourWheelDrive = 5,
}

/// <summary>Pedestrian travel type. C++ <c>enum class PedestrianType : uint8_t</c>.</summary>
public enum PedestrianType : byte
{
    Foot = 0,
    Wheelchair = 1,
    Blind = 2,
}

/// <summary>Bicycle travel type. C++ <c>enum class BicycleType : uint8_t</c>.</summary>
public enum BicycleType : byte
{
    Road = 0,
    Cross = 1,    // Cyclocross bike - road bike setup with wider tires
    Hybrid = 2,   // Hybrid or city bike
    Mountain = 3,
}

/// <summary>Did we make a turn on a short internal edge. C++ <c>enum class InternalTurn : uint8_t</c>.</summary>
public enum InternalTurn : byte
{
    NoTurn = 0,
    LeftTurn = 1,
    RightTurn = 2,
}

/// <summary>
/// Simple structure to denote edge locations to avoid. Includes the edge Id and percent
/// along the edge. The percent along is used when checking origin and destination locations
/// to see if the avoided location can be traveled along the "partial" edge.
/// Faithful port of <c>struct AvoidEdge</c>.
/// </summary>
public struct AvoidEdge
{
    public GraphId Id;
    public double PercentAlong;
}
