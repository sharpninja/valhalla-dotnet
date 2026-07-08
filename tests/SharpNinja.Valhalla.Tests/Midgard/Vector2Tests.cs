// Faithful C# port of Valhalla's gtest suite test/vector2.cc.
// Each [Fact] mirrors a TEST(Vector2, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal (exact); EXPECT_FLOAT_EQ -> Assert.Equal with float tolerance.

using SharpNinja.Valhalla.Midgard;

using Point2 = SharpNinja.Valhalla.Midgard.PointXY<float>;
using Vector2 = SharpNinja.Valhalla.Midgard.VectorXY<float>;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class Vector2Tests
{
    // EXPECT_FLOAT_EQ in gtest compares within ~4 ULPs. A small relative-ish tolerance
    // is sufficient for the magnitudes used in these tests.
    private const float FloatEq = 1e-5f;

    [Fact]
    public void TestCtorDefault()
    {
        var target = new Vector2();
        var expected = new Vector2(0.0f, 0.0f);
        Assert.True(expected == target);
    }

    private static void TryCtorPoint2(Point2 pt, Vector2 expected)
    {
        var result = new Vector2(pt);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestCtorPoint2()
    {
        TryCtorPoint2(new Point2(3.0f, 0.0f), new Vector2(3.0f, 0.0f));
        TryCtorPoint2(new Point2(-8.0f, 6.0f), new Vector2(-8.0f, 6.0f));
    }

    private static void TryCtorFloatFloat(float x, float y, Vector2 expected)
    {
        var result = new Vector2(x, y);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestCtorFloatFloat()
    {
        TryCtorFloatFloat(3.0f, 0.0f, new Vector2(3.0f, 0.0f));
        TryCtorFloatFloat(-8.0f, 6.0f, new Vector2(-8.0f, 6.0f));
    }

    private static void TryCtorPoint2Point2(Point2 from, Point2 to, Vector2 expected)
    {
        var result = new Vector2(from, to);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestCtorPoint2Point2()
    {
        TryCtorPoint2Point2(new Point2(4.0f, 0.0f), new Point2(3.0f, 3.0f), new Vector2(-1.0f, 3.0f));
        TryCtorPoint2Point2(new Point2(4.0f, 2.0f), new Point2(4.0f, -2.0f), new Vector2(0.0f, -4.0f));
    }

    private static void TryCtorVector2(Vector2 v, Vector2 expected)
    {
        var result = new Vector2(v);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestCtorVector2()
    {
        TryCtorVector2(new Vector2(3.0f, 0.0f), new Vector2(3.0f, 0.0f));
        TryCtorVector2(new Vector2(-8.0f, 6.0f), new Vector2(-8.0f, 6.0f));
    }

    private static void TryOpAssignment(Vector2 v, Vector2 expected)
    {
        // C# has no copy-assignment operator; the copy constructor is the faithful equivalent.
        var result = new Vector2(v);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestOpAssignment()
    {
        TryOpAssignment(new Vector2(3.0f, 0.0f), new Vector2(3.0f, 0.0f));
        TryOpAssignment(new Vector2(-8.0f, 6.0f), new Vector2(-8.0f, 6.0f));
    }

    private static void TryGetX(Vector2 v, float expected)
    {
        Assert.Equal(expected, v.X, FloatEq);
    }

    [Fact]
    public void TestGetX()
    {
        TryGetX(new Vector2(3.0f, 0.0f), 3.0f);
        TryGetX(new Vector2(-8.0f, 6.0f), -8.0f);
    }

    private static void TryGetY(Vector2 v, float expected)
    {
        Assert.Equal(expected, v.Y, FloatEq);
    }

    [Fact]
    public void TestGetY()
    {
        TryGetY(new Vector2(3.0f, 2.0f), 2.0f);
        TryGetY(new Vector2(-8.0f, 6.0f), 6.0f);
    }

    private static void TrySetX(Vector2 v, float expected)
    {
        v.SetX(expected);
        Assert.Equal(expected, v.X, FloatEq);
    }

    [Fact]
    public void TestSetX()
    {
        var v = new Vector2(1.0f, 1.0f);
        TrySetX(v, 3.0f);
        TrySetX(v, -8.0f);
    }

    private static void TrySetY(Vector2 v, float expected)
    {
        v.SetY(expected);
        Assert.Equal(expected, v.Y, FloatEq);
    }

    [Fact]
    public void TestSetY()
    {
        var v = new Vector2(1.0f, 1.0f);
        TrySetY(v, 3.0f);
        TrySetY(v, -8.0f);
    }

    private static void TrySetFloatFloat(float x, float y, Vector2 expected)
    {
        var result = new Vector2();
        result.Set(x, y);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestSetFloatFloat()
    {
        TrySetFloatFloat(3.0f, 0.0f, new Vector2(3.0f, 0.0f));
        TrySetFloatFloat(-8.0f, 6.0f, new Vector2(-8.0f, 6.0f));
    }

    private static void TrySetPoint2(Point2 pt, Vector2 expected)
    {
        var result = new Vector2();
        result.Set(pt);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestSetPoint2()
    {
        TrySetPoint2(new Point2(3.0f, 0.0f), new Vector2(3.0f, 0.0f));
        TrySetPoint2(new Point2(-8.0f, 6.0f), new Vector2(-8.0f, 6.0f));
    }

    private static void TrySetPoint2Point2(Point2 from, Point2 to, Vector2 expected)
    {
        var result = new Vector2();
        result.Set(from, to);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestSetPoint2Point2()
    {
        TrySetPoint2Point2(new Point2(4.0f, 0.0f), new Point2(3.0f, 3.0f), new Vector2(-1.0f, 3.0f));
        TrySetPoint2Point2(new Point2(4.0f, 2.0f), new Point2(4.0f, -2.0f), new Vector2(0.0f, -4.0f));
    }

    private static void TryOpAddition(Vector2 v, Vector2 w, Vector2 expected)
    {
        Vector2 result = v + w;
        Assert.True(expected == result);
    }

    [Fact]
    public void TestOpAddition()
    {
        TryOpAddition(new Vector2(4.0f, -2.0f), new Vector2(3.0f, 3.0f), new Vector2(7.0f, 1.0f));
        TryOpAddition(new Vector2(4.0f, 2.0f), new Vector2(-2.0f, -2.0f), new Vector2(2.0f, 0.0f));
    }

    private static void TryOpAdditionAssignment(Vector2 v, Vector2 w, Vector2 expected)
    {
        v.AddAssign(w);
        Assert.True(expected == v);
    }

    [Fact]
    public void TestOpAdditionAssignment()
    {
        var v1 = new Vector2(4.0f, -2.0f);
        TryOpAdditionAssignment(v1, new Vector2(3.0f, 3.0f), new Vector2(7.0f, 1.0f));
        var v2 = new Vector2(4.0f, 2.0f);
        TryOpAdditionAssignment(v2, new Vector2(-2.0f, -2.0f), new Vector2(2.0f, 0.0f));
    }

    private static void TryOpSubtraction(Vector2 v, Vector2 w, Vector2 expected)
    {
        Vector2 result = v - w;
        Assert.True(expected == result);
    }

    [Fact]
    public void TestOpSubtraction()
    {
        TryOpSubtraction(new Vector2(4.0f, -2.0f), new Vector2(3.0f, 3.0f), new Vector2(1.0f, -5.0f));
        TryOpSubtraction(new Vector2(4.0f, 2.0f), new Vector2(-2.0f, -2.0f), new Vector2(6.0f, 4.0f));
    }

    private static void TryOpSubtractionAssignment(Vector2 v, Vector2 w, Vector2 expected)
    {
        v.SubtractAssign(w);
        Assert.True(expected == v);
    }

    [Fact]
    public void TestOpSubtractionAssignment()
    {
        var v1 = new Vector2(4.0f, -2.0f);
        TryOpSubtractionAssignment(v1, new Vector2(3.0f, 3.0f), new Vector2(1.0f, -5.0f));
        var v2 = new Vector2(4.0f, 2.0f);
        TryOpSubtractionAssignment(v2, new Vector2(-2.0f, -2.0f), new Vector2(6.0f, 4.0f));
    }

    private static void TryOpMultiplication(Vector2 v, float scalar, Vector2 expected)
    {
        Vector2 result = v * scalar;
        Assert.True(expected == result); // scalar pre

        Vector2 result2 = scalar * v;
        Assert.True(expected == result2); // scalar post
    }

    [Fact]
    public void TestOpMultiplication()
    {
        TryOpMultiplication(new Vector2(4.0f, -2.0f), 3.0f, new Vector2(12.0f, -6.0f));
        TryOpMultiplication(new Vector2(-4.0f, 2.0f), -2.0f, new Vector2(8.0f, -4.0f));
    }

    private static void TryOpMultiplicationAssignment(Vector2 v, float scalar, Vector2 expected)
    {
        v.MultiplyAssign(scalar);
        Assert.True(expected == v);
    }

    [Fact]
    public void TestOpMultiplicationAssignment()
    {
        var v1 = new Vector2(4.0f, -2.0f);
        TryOpMultiplicationAssignment(v1, 3.0f, new Vector2(12.0f, -6.0f));
        var v2 = new Vector2(-4.0f, 2.0f);
        TryOpMultiplicationAssignment(v2, -2.0f, new Vector2(8.0f, -4.0f));
    }

    private static void TryOpEqualTo(Vector2 v, Vector2 expected)
    {
        Assert.True(expected == v);
        Assert.True(v == expected);
    }

    [Fact]
    public void TestOpEqualTo()
    {
        TryOpEqualTo(new Vector2(1.0f, 3.0f), new Vector2(1.0f, 3.0f));
        TryOpEqualTo(new Vector2(4.0f, -2.0f), new Vector2(4.0f, -2.0f));
        TryOpEqualTo(new Vector2(-4.0f, 2.0f), new Vector2(-4.0f, 2.0f));
    }

    private static void TryDotProduct(Vector2 a, Vector2 b, float expected)
    {
        float result = a.Dot(b);
        Assert.Equal(expected, result, FloatEq);
    }

    [Fact]
    public void TestDotProduct()
    {
        TryDotProduct(new Vector2(3.0f, 0.0f), new Vector2(5.0f, 5.0f), 15.0f);
        TryDotProduct(new Vector2(3.0f, 4.0f), new Vector2(-8.0f, 6.0f), 0.0f);
    }

    private static void TryCrossProduct(Vector2 a, Vector2 b, float expected)
    {
        float result = a.Cross(b);
        Assert.Equal(expected, result, FloatEq);
    }

    [Fact]
    public void TestCrossProduct()
    {
        TryCrossProduct(new Vector2(3.0f, 0.0f), new Vector2(5.0f, 5.0f), 15.0f);
        TryCrossProduct(new Vector2(3.0f, 4.0f), new Vector2(-8.0f, 6.0f), 50.0f);
    }

    private static void TryPerpendicular(Vector2 a, Vector2 expected)
    {
        Vector2 result = a.GetPerpendicular();
        Assert.True(expected == result);
    }

    [Fact]
    public void TestPerpendicular()
    {
        TryPerpendicular(new Vector2(3.0f, 4.0f), new Vector2(-4.0f, 3.0f));
    }

    private static void TryNorm(Vector2 a, float expected)
    {
        float result = a.Norm();
        Assert.Equal(expected, result, FloatEq);
    }

    [Fact]
    public void TestNorm()
    {
        TryNorm(new Vector2(3.0f, 4.0f), 5.0f);
        TryNorm(new Vector2(6.0f, 8.0f), 10.0f);
    }

    private static void TryNormSquared(Vector2 a, float expected)
    {
        float result = a.NormSquared();
        Assert.Equal(expected, result, FloatEq);
    }

    [Fact]
    public void TestNormSquared()
    {
        TryNormSquared(new Vector2(3.0f, 4.0f), 25.0f);
        TryNormSquared(new Vector2(6.0f, 8.0f), 100.0f);
    }

    private static void TryNormalize(Vector2 a, Vector2 expected)
    {
        a.Normalize();
        Assert.True(expected == a);
    }

    [Fact]
    public void TestNormalize()
    {
        var v = new Vector2(3.0f, 4.0f);
        TryNormalize(v, new Vector2(3.0f / 5.0f, 4.0f / 5.0f));
        var w = new Vector2(6.0f, 8.0f);
        TryNormalize(w, new Vector2(6.0f / 10.0f, 8.0f / 10.0f));
    }

    private static void TryComponent(Vector2 a, Vector2 b, float expected)
    {
        float result = a.Component(b);
        Assert.Equal(expected, result, FloatEq);
    }

    [Fact]
    public void TestComponent()
    {
        TryComponent(new Vector2(3.0f, 4.0f), new Vector2(6.0f, 8.0f), 0.5f);
        TryComponent(new Vector2(6.0f, 8.0f), new Vector2(3.0f, 4.0f), 2.0f);
    }

    private static void TryProjection(Vector2 a, Vector2 b, Vector2 expected)
    {
        Vector2 result = a.Projection(b);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestProjection()
    {
        TryProjection(new Vector2(3.0f, 4.0f), new Vector2(6.0f, 8.0f), new Vector2(3.0f, 4.0f));
        TryProjection(new Vector2(6.0f, 8.0f), new Vector2(3.0f, 4.0f), new Vector2(6.0f, 8.0f));
        TryProjection(
            new Vector2(2.0f, 1.0f),
            new Vector2(-3.0f, 4.0f),
            new Vector2(6.0f / 25.0f, -8.0f / 25.0f));
    }

    private static void TryAngleBetween(Vector2 a, Vector2 b, float expected)
    {
        float result = a.AngleBetween(b) * Constants.DegPerRad;
        Assert.Equal(expected, result, FloatEq);
    }

    [Fact]
    public void TestAngleBetween()
    {
        TryAngleBetween(new Vector2(3.0f, 0.0f), new Vector2(5.0f, 5.0f), 45.0f);
        TryAngleBetween(new Vector2(3.0f, 4.0f), new Vector2(-8.0f, 6.0f), 90.0f);
    }

    private static void TryReflect(Vector2 a, Vector2 b, Vector2 expected)
    {
        Vector2 result = a.Reflect(b);
        Assert.True(expected == result);
    }

    [Fact]
    public void TestReflect()
    {
        var n1 = new Vector2(0.0f, 2.0f);
        n1.Normalize();
        TryReflect(new Vector2(4.0f, -2.0f), n1, new Vector2(4.0f, 2.0f));
        var n2 = new Vector2(-3.0f, 0.0f);
        n2.Normalize();
        TryReflect(new Vector2(3.0f, -4.0f), n2, new Vector2(-3.0f, -4.0f));
    }
}
