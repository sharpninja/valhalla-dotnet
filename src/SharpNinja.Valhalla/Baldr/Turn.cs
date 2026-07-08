// Faithful C# port of Valhalla baldr Turn (valhalla/baldr/turn.h + src/baldr/turn.cc) @ 3.7.0.
// Defines the turn type based on turn degrees, plus the heading -> Turn::Type lookup math.
// Public members are PascalCase; algorithm and lookup-table boundaries mirror the C++ exactly.

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Defines the turn type based on turn degrees. Faithful port of <c>valhalla::baldr::Turn</c>.
/// </summary>
public static class Turn
{
    /// <summary>
    /// Turn type based on turn degree. Underlying type is <see cref="byte"/> to match the
    /// C++ <c>enum class Type : uint8_t</c> exactly (1 byte).
    /// </summary>
    public enum Type : byte
    {
        Straight = 0,
        SlightRight = 1,
        Right = 2,
        SharpRight = 3,
        Reverse = 4,
        SharpLeft = 5,
        Left = 6,
        SlightLeft = 7,
    }

    // This function is on the hot path in C++, so it uses a 360-entry lookup table to avoid
    // branch prediction misses. We reproduce the exact same table (make_turn_type_lut()).
    private static readonly Type[] TurnTypeLut = MakeTurnTypeLut();

    private static readonly Dictionary<int, string> TurnTypeToString = new()
    {
        [(int)Type.Straight] = "straight",
        [(int)Type.SlightRight] = "slight right",
        [(int)Type.Right] = "right",
        [(int)Type.SharpRight] = "sharp right",
        [(int)Type.Reverse] = "reverse",
        [(int)Type.SharpLeft] = "sharp left",
        [(int)Type.Left] = "left",
        [(int)Type.SlightLeft] = "slight left",
    };

    /// <summary>
    /// Returns the turn type based on the specified turn degree. For example, if 90 is supplied
    /// for the turn degree, then <see cref="Type.Right"/> is returned.
    /// </summary>
    /// <param name="turnDegree">
    /// The specified turn degree used to determine the returned type. Expected range is 0 to 359,
    /// but any value is accepted and wrapped via modulo 360 (matching the C++).
    /// </param>
    /// <returns>The turn type based on the specified turn degree.</returns>
    public static Type GetType(uint turnDegree)
    {
        // Mirror C++: kTurnTypeLUT[turn_degree % 360].
        return TurnTypeLut[turnDegree % 360u];
    }

    /// <summary>
    /// Returns the turn type string for the specified turn type.
    /// </summary>
    /// <param name="turnType">The specified turn type.</param>
    /// <returns>The turn type string, or "undefined" if not found.</returns>
    public static string GetTypeString(Type turnType)
    {
        return TurnTypeToString.TryGetValue((int)turnType, out string? str) ? str : "undefined";
    }

    private static Type[] MakeTurnTypeLut()
    {
        var t = new Type[360]; // default-initialized to Type.Straight (0), matching C++ `t{}`.

        void Fill(int fromAngle, int toAngle, Type type)
        {
            for (int angle = fromAngle; angle <= toAngle; ++angle)
            {
                t[angle] = type;
            }
        }

        Fill(0, 10, Type.Straight);
        Fill(11, 44, Type.SlightRight);
        Fill(45, 135, Type.Right);
        Fill(136, 159, Type.SharpRight);
        Fill(160, 200, Type.Reverse);
        Fill(201, 224, Type.SharpLeft);
        Fill(225, 315, Type.Left);
        Fill(316, 349, Type.SlightLeft);
        Fill(350, 359, Type.Straight);

        return t;
    }
}
