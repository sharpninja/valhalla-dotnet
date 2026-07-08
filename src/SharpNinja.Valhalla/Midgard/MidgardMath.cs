// Faithful C# port of small midgard numeric helpers needed by the ported set.
// Sources:
//   valhalla/midgard/util_core.h  (equal)
//   valhalla/midgard/util.h       (hash_combine)
// Kept minimal to what the PointXY / Vector2 port needs to compile and behave.

using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Numeric helpers ported from midgard. <see cref="Equal{T}"/> mirrors
/// <c>valhalla::midgard::equal</c> and <see cref="HashCombine{T}"/> mirrors
/// <c>valhalla::midgard::hash_combine</c>.
/// </summary>
public static class MidgardMath
{
    /// <summary>
    /// Equality with an epsilon for approximation. Faithful port of
    /// <c>template &lt;class T&gt; bool equal(const T a, const T b, const T epsilon)</c>.
    /// </summary>
    /// <param name="a">First operand.</param>
    /// <param name="b">Second operand.</param>
    /// <param name="epsilon">Epsilon to help with approximate equality.</param>
    /// <returns>True if the two values are approximately equal.</returns>
    /// <exception cref="InvalidOperationException">Thrown if epsilon is negative.</exception>
    public static bool Equal<T>(T a, T b, T epsilon)
        where T : INumber<T>
    {
        if (epsilon < T.Zero)
        {
            // C++ throws std::logic_error; closest BCL analogue.
            throw new InvalidOperationException("Using a negative epsilon is not supported");
        }

        T diff = a - b;
        // if its non-negative it better be less than epsilon, if its negative then it better be
        // bigger than epsilon
        bool negative = diff < T.Zero;
        return (!negative && diff <= epsilon) || (negative && diff >= -epsilon);
    }

    /// <summary>
    /// Default-epsilon overload of <see cref="Equal{T}(T,T,T)"/>. The C++ default epsilon
    /// is <c>static_cast&lt;T&gt;(.00001)</c>.
    /// </summary>
    public static bool Equal<T>(T a, T b)
        where T : INumber<T>
    {
        return Equal(a, b, T.CreateChecked(0.00001));
    }

    /// <summary>
    /// Combines a value into a running hash seed. Faithful port of boost-style
    /// <c>hash_combine</c> used by midgard (the 0x9e3779b9 golden-ratio mix).
    /// </summary>
    public static void HashCombine<T>(ref int seed, T value)
    {
        unchecked
        {
            int h = value is null ? 0 : value.GetHashCode();
            seed ^= h + unchecked((int)0x9e3779b9) + (seed << 6) + (seed >> 2);
        }
    }
}
