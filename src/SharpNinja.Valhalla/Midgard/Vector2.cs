// Faithful C# port of Valhalla midgard Vector2 (VectorXY<PrecisionT>).
// Source: valhalla/midgard/vector2.h
// Self-contained engine port: does NOT reuse other TruckMate types.
// PrecisionT is generic; Vector2 = VectorXY<float>, Vector2d = VectorXY<double>.
// Float instantiations use float-precision sqrt/acos to match C++ sqrtf/acosf.

using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// 2D vector class. Generic over the precision type (float or double), mirroring
/// the C++ <c>VectorXY&lt;PrecisionT&gt;</c>.
/// </summary>
/// <typeparam name="TPrecision">Numeric precision type (float or double).</typeparam>
public sealed class VectorXY<TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    private TPrecision _x;
    private TPrecision _y;

    /// <summary>Default constructor. Initializes the vector to (0, 0).</summary>
    public VectorXY()
    {
        _x = TPrecision.Zero;
        _y = TPrecision.Zero;
    }

    /// <summary>
    /// Constructor given a point. Essentially a vector from the origin to the point.
    /// </summary>
    public VectorXY(PointXY<TPrecision> p)
    {
        _x = p.X;
        _y = p.Y;
    }

    /// <summary>Constructor given components of the vector.</summary>
    public VectorXY(TPrecision x, TPrecision y)
    {
        _x = x;
        _y = y;
    }

    /// <summary>Constructor from one point to another (to - from).</summary>
    public VectorXY(PointXY<TPrecision> from, PointXY<TPrecision> to)
    {
        _x = to.X - from.X;
        _y = to.Y - from.Y;
    }

    /// <summary>Copy constructor.</summary>
    public VectorXY(VectorXY<TPrecision> w)
    {
        _x = w.X;
        _y = w.Y;
    }

    /// <summary>Gets the x component of the vector.</summary>
    public TPrecision X => _x;

    /// <summary>Gets the y component of the vector.</summary>
    public TPrecision Y => _y;

    /// <summary>Sets the x component.</summary>
    public void SetX(TPrecision x) => _x = x;

    /// <summary>Sets the y component.</summary>
    public void SetY(TPrecision y) => _y = y;

    /// <summary>Sets the current vector to the specified components.</summary>
    public void Set(TPrecision x, TPrecision y)
    {
        _x = x;
        _y = y;
    }

    /// <summary>
    /// Sets the vector components to those of a point (vector from origin to the point).
    /// </summary>
    public void Set(PointXY<TPrecision> p)
    {
        _x = p.X;
        _y = p.Y;
    }

    /// <summary>Sets the current vector to be from one point to another (to - from).</summary>
    public void Set(PointXY<TPrecision> from, PointXY<TPrecision> to)
    {
        _x = to.X - from.X;
        _y = to.Y - from.Y;
    }

    /// <summary>Creates a new vector that is the current vector plus the specified vector.</summary>
    public static VectorXY<TPrecision> operator +(VectorXY<TPrecision> v, VectorXY<TPrecision> w)
        => new(v._x + w.X, v._y + w.Y);

    /// <summary>Creates a new vector that is the current vector minus the specified vector.</summary>
    public static VectorXY<TPrecision> operator -(VectorXY<TPrecision> v, VectorXY<TPrecision> w)
        => new(v._x - w.X, v._y - w.Y);

    /// <summary>Creates a new vector that is the current vector multiplied with the scalar.</summary>
    public static VectorXY<TPrecision> operator *(VectorXY<TPrecision> v, TPrecision scalar)
        => new(v._x * scalar, v._y * scalar);

    /// <summary>Scalar-first multiplication (mirrors the free <c>operator*(s, v)</c>).</summary>
    public static VectorXY<TPrecision> operator *(TPrecision scalar, VectorXY<TPrecision> v)
        => new(v._x * scalar, v._y * scalar);

    /// <summary>
    /// Adds vector w to the current vector in place. Mirrors C++ <c>operator+=</c> which
    /// returns a reference to the current vector.
    /// </summary>
    public VectorXY<TPrecision> AddAssign(VectorXY<TPrecision> w)
    {
        _x += w.X;
        _y += w.Y;
        return this;
    }

    /// <summary>
    /// Subtracts vector w from the current vector in place. Mirrors C++ <c>operator-=</c>.
    /// </summary>
    public VectorXY<TPrecision> SubtractAssign(VectorXY<TPrecision> w)
    {
        _x -= w.X;
        _y -= w.Y;
        return this;
    }

    /// <summary>Multiplies the current vector by a scalar in place. Mirrors C++ <c>operator*=</c>.</summary>
    public VectorXY<TPrecision> MultiplyAssign(TPrecision scalar)
    {
        _x *= scalar;
        _y *= scalar;
        return this;
    }

    /// <summary>Equality operator. True if both components are exactly equal.</summary>
    public static bool operator ==(VectorXY<TPrecision>? v, VectorXY<TPrecision>? w)
    {
        if (v is null)
        {
            return w is null;
        }

        if (w is null)
        {
            return false;
        }

        return v._x == w._x && v._y == w._y;
    }

    /// <summary>Inequality operator.</summary>
    public static bool operator !=(VectorXY<TPrecision>? v, VectorXY<TPrecision>? w) => !(v == w);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is VectorXY<TPrecision> w && this == w;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_x, _y);

    /// <summary>Computes the dot product of the current vector with the specified vector.</summary>
    public TPrecision Dot(VectorXY<TPrecision> w) => (_x * w.X) + (_y * w.Y);

    /// <summary>
    /// Computes the 2D cross product (current X w). Returns the magnitude of the resulting
    /// vector (which is along the z axis).
    /// </summary>
    public TPrecision Cross(VectorXY<TPrecision> w) => (_x * w.Y) - (_y * w.X);

    /// <summary>
    /// Gets a perpendicular vector to this vector.
    /// </summary>
    /// <param name="clockwise">
    /// If true, get the clockwise oriented perpendicular; otherwise counter-clockwise.
    /// </param>
    public VectorXY<TPrecision> GetPerpendicular(bool clockwise = false)
        => clockwise ? new VectorXY<TPrecision>(_y, -_x) : new VectorXY<TPrecision>(-_y, _x);

    /// <summary>Computes the norm (length) of the current vector.</summary>
    public TPrecision Norm() => TPrecision.Sqrt(Dot(this));

    /// <summary>
    /// Computes the squared norm of the vector (useful when absolute distance is not required).
    /// </summary>
    public TPrecision NormSquared() => Dot(this);

    /// <summary>
    /// Normalizes the vector in place. Mirrors C++ <c>Normalize</c>: only divides when the
    /// norm is greater than <see cref="Constants.Epsilon"/> and not equal to 1.
    /// </summary>
    public VectorXY<TPrecision> Normalize()
    {
        TPrecision n = Norm();
        TPrecision epsilon = TPrecision.CreateChecked(Constants.Epsilon);
        if (n > epsilon && n != TPrecision.One)
        {
            _x /= n;
            _y /= n;
        }

        return this;
    }

    /// <summary>
    /// Calculates the component of the current vector along the specified vector.
    /// Returns 0 if w has zero length.
    /// </summary>
    public TPrecision Component(VectorXY<TPrecision> w)
    {
        TPrecision n = w.Dot(w);
        return n != TPrecision.Zero ? Dot(w) / n : TPrecision.Zero;
    }

    /// <summary>
    /// Creates a new vector that is the projection of the current vector along the
    /// specified vector.
    /// </summary>
    public VectorXY<TPrecision> Projection(VectorXY<TPrecision> w) => w * Component(w);

    /// <summary>
    /// Calculates the angle (radians) between the current vector and the specified vector.
    /// </summary>
    public TPrecision AngleBetween(VectorXY<TPrecision> w)
        => TPrecision.Acos(Dot(w) / (Norm() * w.Norm()));

    /// <summary>
    /// Reflects the current vector given a unit-length normal to the reflecting surface.
    /// </summary>
    public VectorXY<TPrecision> Reflect(VectorXY<TPrecision> normal)
    {
        VectorXY<TPrecision> d = this;
        TPrecision two = TPrecision.CreateChecked(2.0);
        return d - (normal * (two * d.Dot(normal)));
    }
}
