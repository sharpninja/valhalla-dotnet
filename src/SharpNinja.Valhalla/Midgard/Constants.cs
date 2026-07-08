// Faithful C# port of Valhalla midgard constants.
// Source: valhalla/midgard/constants.h
// This is a self-contained engine port: it intentionally does NOT reuse other
// TruckMate types. Values, signedness, and precision (float vs double) mirror the
// original C++ constexpr declarations exactly.

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Midgard numeric constants ported verbatim from <c>valhalla/midgard/constants.h</c>.
/// Float constants are kept as <see cref="float"/> and double constants as <see cref="double"/>
/// to preserve the exact numeric behavior of the C++ engine.
/// </summary>
public static class Constants
{
    // Time constants
    public const float SecPerMinute = 60.0f;
    public const float MinPerSec = 1.0f / 60.0f;
    public const float SecPerHour = 3600.0f;
    public const float HourPerSec = 1.0f / 3600.0f;
    public const double SecPerMillisecond = 0.001;
    public const double MillisecondPerSec = 1000;
    public const uint SecondsPerMinute = 60;
    public const uint SecondsPerHour = 3600;
    public const uint SecondsPerDay = 86400;
    public const uint SecondsPerWeek = 604800;

    // Distance constants
    public const float FeetPerMile = 5280.0f;
    public const float MilePerFoot = 1.0f / FeetPerMile;
    public const float FeetPerMeter = 3.2808399f;
    public const float MetersPerKm = 1000.0f;
    public const float KmPerMeter = 0.001f;
    public const float MilePerKm = 0.621371f;
    public const float MilePerMeter = MilePerKm / 1000;
    public const float KmPerMile = 1.609344f;
    public const float RadEarthMeters = 6378160.187f;
    public const double MetersPerDegreeLat = 110567.0f;
    public const double KmPerDecimeter = 0.0001;
    public const double MeterPerDecimeter = 0.1;
    public const double DecimeterPerMeter = 10;

    // Speed conversion constants
    public const float MphToMetersPerSec = 0.44704f;
    public const double DecimeterPerSecToKph = 0.36; // dm/s to km/h
    public const double KphToMetersPerSec = 1000.0 / 3600.0;
    public const double MetersPerSecToKph = 3600.0 / 1000.0;

    // Angular measures
    public const float Pi = 3.14159265f;
    public const double PiD = 3.14159265358979323846264338327950288;
    public const float PiOver2 = Pi * 0.5f;
    public const float PiOver4 = Pi * 0.25f;
    public const float DegPerRad = 180.0f / Pi;   // Radians to degrees conversion
    public const double DegPerRadD = 180.0 / PiD; // Radians to degrees conversion in double precision
    public const float RadPerDeg = Pi / 180.0f;   // Degrees to radians conversion
    public const double RadPerDegD = PiD / 180.0; // Degrees to radians conversion in double precision
    public const float Epsilon = 0.000001f;

    // To avoid using M_PI
    public const double PiDouble = 3.14159265358979323846;

    // Weight measures
    public const float TonsShortToMetric = 0.907f; // Short tons to metric
}
