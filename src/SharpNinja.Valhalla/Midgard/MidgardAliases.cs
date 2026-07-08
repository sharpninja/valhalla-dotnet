// Type aliases mirroring the C++ midgard `using` declarations:
//   using Point2  = PointXY<float>;
//   using Point2d = PointXY<double>;
//   using Vector2  = VectorXY<float>;
//   using Vector2d = VectorXY<double>;
// These are global (assembly-wide) so dependent ports can reference Point2 / Vector2
// directly, exactly as the Valhalla C++ code does.

global using Point2 = SharpNinja.Valhalla.Midgard.PointXY<float>;
global using Point2d = SharpNinja.Valhalla.Midgard.PointXY<double>;
global using Vector2 = SharpNinja.Valhalla.Midgard.VectorXY<float>;
global using Vector2d = SharpNinja.Valhalla.Midgard.VectorXY<double>;

// Aabb2/LineSegment2 specialized over the float/double coordinate types, mirroring the
// C++ AABB2<Point2> / LineSegment2<Point2> usage in the engine.
global using Aabb2 = SharpNinja.Valhalla.Midgard.Aabb2T<float>;
global using Aabb2d = SharpNinja.Valhalla.Midgard.Aabb2T<double>;
global using LineSegment2 = SharpNinja.Valhalla.Midgard.LineSegment2T<float>;
global using LineSegment2d = SharpNinja.Valhalla.Midgard.LineSegment2T<double>;
