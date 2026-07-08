// Faithful C# port of Valhalla's gtest suite test/polyline2.cc.
// Each [Fact] mirrors a TEST(Polyline2, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal / Assert.True(a.Equals(b)); EXPECT_NEAR -> Assert.Equal(exp, act, tol).
//
// Planar cases (Point2/Point2d) use Polyline2<TPrecision> over PointXY<TPrecision>.
// Spherical / self-intersection cases (PointLL, GeoPoint<double>) use PointLlPolyline2.

using System.Collections.Generic;

using SharpNinja.Valhalla.Midgard;

using Point2 = SharpNinja.Valhalla.Midgard.PointXY<float>;
using Point2d = SharpNinja.Valhalla.Midgard.PointXY<double>;
using Aabb2 = SharpNinja.Valhalla.Midgard.Aabb2T<float>;

namespace SharpNinja.Valhalla.Tests.Midgard;

public class Polyline2Tests
{
    // ----- Generalize + Length (planar) -----

    private static void TryGeneralizeAndLengthFloat(Polyline2<float> pl, float gen, float res)
    {
        pl.Generalize(gen);

        List<Point2> pts = pl.Pts;
        Assert.Equal(2, pl.Pts.Count);
        Assert.Equal(new Point2(25.0f, 25.0f), pts[0]);
        Assert.Equal(new Point2(50.0f, 100.0f), pts[1]);

        Polyline2<float> pl2 = pl.GeneralizedPolyline(gen);
        Assert.Equal(2, pl2.Pts.Count);
        Assert.Equal(new Point2(25.0f, 25.0f), pl2.Pts[0]);
        Assert.Equal(new Point2(50.0f, 100.0f), pl2.Pts[1]);

        Assert.Equal((double)res, pl2.Length(), 1e-4);
    }

    private static void TryGeneralizeAndLengthDouble(Polyline2<double> pl, double gen, double res)
    {
        pl.Generalize(gen);

        List<Point2d> pts = pl.Pts;
        Assert.Equal(2, pl.Pts.Count);
        Assert.Equal(new Point2d(25.0, 25.0), pts[0]);
        Assert.Equal(new Point2d(50.0, 100.0), pts[1]);

        Polyline2<double> pl2 = pl.GeneralizedPolyline(gen);
        Assert.Equal(2, pl2.Pts.Count);
        Assert.Equal(new Point2d(25.0, 25.0), pl2.Pts[0]);
        Assert.Equal(new Point2d(50.0, 100.0), pl2.Pts[1]);

        Assert.Equal(res, pl2.Length(), 1e-4);
    }

    [Fact]
    public void TestGeneralizeAndLength()
    {
        var pts = new List<Point2>
        {
            new(25.0f, 25.0f), new(50.0f, 50.0f), new(25.0f, 75.0f), new(50.0f, 100.0f),
        };
        var pl = new Polyline2<float>(pts);
        TryGeneralizeAndLengthFloat(pl, 100.0f, 79.0569f);
    }

    [Fact]
    public void TestGeneralizeAndLengthWithDoubles()
    {
        var pts = new List<Point2d>
        {
            new(25.0, 25.0), new(50.0, 50.0), new(25.0, 75.0), new(50.0, 100.0),
        };
        var pl = new Polyline2<double>(pts);
        TryGeneralizeAndLengthDouble(pl, 100.0, 79.0569);
    }

    // ----- Generalize with exclusions / self-intersection -----

    [Fact]
    public void TestGeneralizeSimplification()
    {
        var line = new Polyline2<float>(new List<Point2>
        {
            new(17, 0), new(17, 1), new(17, 2), new(17, 3), new(17, 4), new(17, 5),
        });

        line.Generalize(1, new HashSet<int> { 2, 4 });
        Assert.True(
            line.Equals(new Polyline2<float>(new List<Point2>
            {
                new(17, 0), new(17, 2), new(17, 4), new(17, 5),
            })),
            "Should have removed all but the first, last and marked points");

        line.Generalize(1, new HashSet<int> { 2 });
        Assert.True(
            line.Equals(new Polyline2<float>(new List<Point2>
            {
                new(17, 0), new(17, 4), new(17, 5),
            })),
            "Should have removed all but the first, last and marked points");

        {
            var llLine = new PointLlPolyline2(new List<PointLL>
            {
                new(-76.58489, 40.31402), new(-76.58496, 40.31411), new(-76.58506, 40.31416), new(-76.58521, 40.31414),
                new(-76.58586, 40.31383), new(-76.58596, 40.31379), new(-76.58658, 40.31349), new(-76.58723, 40.31319),
                new(-76.58787, 40.31286), new(-76.58842, 40.31362), new(-76.58865, 40.31427), new(-76.58895, 40.31514),
                new(-76.58921, 40.31579), new(-76.58923, 40.31582), new(-76.58924, 40.31586), new(-76.58924, 40.31589),
                new(-76.58994, 40.31779), new(-76.59043, 40.31924), new(-76.59077, 40.32019), new(-76.5922, 40.32265),
                new(-76.5927, 40.32239), new(-76.59319, 40.32216), new(-76.59346, 40.32202), new(-76.59371, 40.32189),
                new(-76.59594, 40.32079), new(-76.59701, 40.32033), new(-76.59809, 40.31994), new(-76.59971, 40.31932),
                new(-76.60005, 40.31922), new(-76.60037, 40.31917), new(-76.6011, 40.31905),
            });
            llLine.Generalize(10, new HashSet<int> { 3, 7, 11, 15, 19, 23, 27 });
            var remaining = new List<PointLL>
            {
                new(-76.58489, 40.31402), new(-76.58521, 40.31414),
                new(-76.58723, 40.31319), new(-76.58895, 40.31514),
                new(-76.58924, 40.31589), new(-76.5922, 40.32265),
                new(-76.59371, 40.32189), new(-76.59971, 40.31932),
                new(-76.6011, 40.31905),
            };
            foreach (PointLL p in remaining)
            {
                Assert.Contains(p, llLine.Pts);
            }
        }

        {
            // GeoPoint<double> behaves like PointLL here.
            var llLine = new PointLlPolyline2(new List<PointLL>
            {
                new(-76.58489, 40.31402), new(-76.58496, 40.31411), new(-76.58506, 40.31416), new(-76.58521, 40.31414),
                new(-76.58586, 40.31383), new(-76.58596, 40.31379), new(-76.58658, 40.31349), new(-76.58723, 40.31319),
                new(-76.58787, 40.31286), new(-76.58842, 40.31362), new(-76.58865, 40.31427), new(-76.58895, 40.31514),
                new(-76.58921, 40.31579), new(-76.58923, 40.31582), new(-76.58924, 40.31586), new(-76.58924, 40.31589),
                new(-76.58994, 40.31779), new(-76.59043, 40.31924), new(-76.59077, 40.32019), new(-76.5922, 40.32265),
                new(-76.5927, 40.32239), new(-76.59319, 40.32216), new(-76.59346, 40.32202), new(-76.59371, 40.32189),
                new(-76.59594, 40.32079), new(-76.59701, 40.32033), new(-76.59809, 40.31994), new(-76.59971, 40.31932),
                new(-76.60005, 40.31922), new(-76.60037, 40.31917), new(-76.6011, 40.31905),
            });
            llLine.Generalize(10, new HashSet<int> { 3, 7, 11, 15, 19, 23, 27 });
            var remaining = new List<PointLL>
            {
                new(-76.58489, 40.31402), new(-76.58521, 40.31414),
                new(-76.58723, 40.31319), new(-76.58895, 40.31514),
                new(-76.58924, 40.31589), new(-76.5922, 40.32265),
                new(-76.59371, 40.32189), new(-76.59971, 40.31932),
                new(-76.6011, 40.31905),
            };
            foreach (PointLL p in remaining)
            {
                Assert.Contains(p, llLine.Pts);
            }
        }

        {
            var line2 = new Polyline2<float>(new List<Point2>
            {
                new(-79.3837f, 43.6481f),
                new(-79.3839f, 43.6485f),
                new(-79.3839f, 43.6485f),
                new(-79.3839f, 43.6486f),
                new(-79.3842f, 43.6491f),
                new(-79.3842f, 43.6492f),
                new(-79.3842f, 43.6492f),
                new(-79.3841f, 43.6493f),
                new(-79.3841f, 43.6493f),
                new(-79.384f, 43.6493f),
                new(-79.3841f, 43.6496f),
                new(-79.384f, 43.6496f),
                new(-79.384f, 43.6496f),
                new(-79.3839f, 43.6496f),
                new(-79.3839f, 43.6496f),
                new(-79.3838f, 43.6496f),
            });
            line2.Generalize(2.6f, new HashSet<int> { 15, 14, 13, 0, 10, 6, 9 });

            Assert.True(
                line2.Equals(new Polyline2<float>(new List<Point2>
                {
                    new(-79.3837f, 43.6481f),
                    new(-79.3842f, 43.6492f),
                    new(-79.384f, 43.6493f),
                    new(-79.3841f, 43.6496f),
                    new(-79.3839f, 43.6496f),
                    new(-79.3839f, 43.6496f),
                    new(-79.3838f, 43.6496f),
                })),
                "Wrong points removed.");
        }

        {
            var line2 = new Polyline2<float>(new List<Point2>
            {
                new(-79.3837f, 43.6481f),
                new(-79.3839f, 43.6485f),
                new(-79.3839f, 43.6485f),
                new(-79.3839f, 43.6486f),
                new(-79.3842f, 43.6491f),
                new(-79.3842f, 43.6492f),
                new(-79.3842f, 43.6492f),
                new(-79.3841f, 43.6493f),
                new(-79.3841f, 43.6493f),
                new(-79.384f, 43.6493f),
                new(-79.3841f, 43.6496f),
                new(-79.384f, 43.6496f),
                new(-79.384f, 43.6496f),
                new(-79.3839f, 43.6496f),
                new(-79.3839f, 43.6496f),
                new(-79.3838f, 43.6496f),
            });
            line2.Generalize(0f);

            Assert.Equal(16, line2.Pts.Count);
        }
    }

    [Fact]
    public void PeuckerSelfIntersectionTest1()
    {
        var points = new List<PointLL>
        {
            new(-117.20467966, 33.77518033), new(-117.20394301, 33.77518757), new(-117.20303785, 33.77482215),
            new(-117.20251280, 33.77391699), new(-117.20232287, 33.77353715), new(-117.20194304, 33.77334723),
            new(-117.20100573, 33.77297971), new(-117.20086145, 33.77299859), new(-117.20082284, 33.77279683),
            new(-117.20026941, 33.77191702), new(-117.20016060, 33.77169937), new(-117.19994294, 33.77159056),
            new(-117.19962097, 33.77159503), new(-117.19822555, 33.77163442), new(-117.19794297, 33.77163847),
            new(-117.19749885, 33.77147289), new(-117.19604794, 33.77181206), new(-117.19596388, 33.76993787),
            new(-117.19595160, 33.76991698), new(-117.19594873, 33.76991125), new(-117.19594299, 33.76990838),
            new(-117.19593012, 33.76990411), new(-117.19587159, 33.76991698), new(-117.19593906, 33.76992091),
            new(-117.19592176, 33.77189578), new(-117.19593198, 33.77191702), new(-117.19592806, 33.77193195),
            new(-117.19594299, 33.77196341), new(-117.19599274, 33.77196676), new(-117.19768004, 33.77217994),
            new(-117.19794297, 33.77219556), new(-117.19859647, 33.77257053), new(-117.19924840, 33.77261156),
            new(-117.19916533, 33.77313940), new(-117.19948144, 33.77391699), new(-117.19963527, 33.77422466),
            new(-117.19994294, 33.77437851), new(-117.20090806, 33.77495196), new(-117.20132555, 33.77591702),
            new(-117.20153140, 33.77632866), new(-117.20194304, 33.77653447), new(-117.20258894, 33.77656294),
        };

        const double genFactor = 5.0;

        {
            // Allow self-intersections, see them occur.
            var polyline = new PointLlPolyline2(points);
            polyline.Generalize(genFactor, null, false);
            List<PointLL> intersections = polyline.GetSelfIntersections();
            Assert.Single(intersections);
        }

        {
            // Avoid self-intersections, see none.
            var polyline = new PointLlPolyline2(points);
            polyline.Generalize(genFactor, null, true);
            List<PointLL> intersections = polyline.GetSelfIntersections();
            Assert.Empty(intersections);
        }
    }

    [Fact]
    public void PeuckerSelfIntersectionTest2()
    {
        var points = new List<PointLL>
        {
            new(-118.17329133, 33.78885961), new(-118.17410177, 33.78965699), new(-118.17407460, 33.79007635),
            new(-118.17379829, 33.79096132), new(-118.17377209, 33.79165699), new(-118.17404231, 33.79210863),
            new(-118.17449396, 33.79235270), new(-118.17518239, 33.79234540), new(-118.17601166, 33.79213932),
            new(-118.17649399, 33.79213106), new(-118.17725704, 33.79242005), new(-118.17841540, 33.79173556),
            new(-118.17774024, 33.79290326), new(-118.17803809, 33.79365699), new(-118.17807143, 33.79407953),
            new(-118.17849397, 33.79548576), new(-118.17936408, 33.79452708), new(-118.18010898, 33.79365699),
            new(-118.17945666, 33.79269433), new(-118.17891198, 33.79207499), new(-118.17855126, 33.79165699),
            new(-118.17852006, 33.79163090), new(-118.17849397, 33.79161013), new(-118.17825732, 33.79142034),
            new(-118.17741529, 33.79073567), new(-118.17649399, 33.79007552), new(-118.17627859, 33.78987239),
            new(-118.17597193, 33.78965699), new(-118.17626466, 33.78942765), new(-118.17649399, 33.78910775),
            new(-118.17736539, 33.78852840), new(-118.17778081, 33.78837014), new(-118.17849397, 33.78803414),
            new(-118.17871682, 33.78787982), new(-118.17900272, 33.78765698), new(-118.17996996, 33.78713294),
            new(-118.18049400, 33.78653054), new(-118.18094331, 33.78720766), new(-118.18082302, 33.78765698),
            new(-118.18159441, 33.78855655), new(-118.18249397, 33.78881215), new(-118.18324743, 33.78841046),
        };

        const double genFactor = 50.0;

        {
            // Allow self-intersections, see them occur.
            var polyline = new PointLlPolyline2(points);
            polyline.Generalize(genFactor, null, false);
            List<PointLL> intersections = polyline.GetSelfIntersections();
            Assert.Equal(2, intersections.Count);
        }

        {
            // Avoid self-intersections, see none.
            var polyline = new PointLlPolyline2(points);
            polyline.Generalize(genFactor, null, true);
            List<PointLL> intersections = polyline.GetSelfIntersections();
            Assert.Empty(intersections);
        }
    }

    // ----- ClosestPoint -----

    private static void TryClosestPoint(Polyline2<float> pl, Point2 a, Point2 b)
    {
        var result = pl.ClosestPoint(a);
        Assert.Equal(b, result.Closest);
    }

    [Fact]
    public void TestClosestPoint()
    {
        var a = new Point2(25.0f, 25.0f);
        var b = new Point2(50.0f, 50.0f);
        var c = new Point2(25.0f, 75.0f);
        var d = new Point2(50.0f, 100.0f);

        var pl = new Polyline2<float>();
        pl.Add(a);
        pl.Add(b);
        pl.Add(c);
        pl.Add(d);

        TryClosestPoint(pl, new Point2(0.0f, 0.0f), a);
        TryClosestPoint(pl, new Point2(60.0f, 50.0f), b);
        TryClosestPoint(pl, new Point2(50.0f, 125.0f), d);
    }

    // ----- Clip -----

    private static void TryClip(Polyline2<float> pl, Aabb2 a, uint exp)
    {
        uint x = pl.Clip(a);
        Assert.Equal(exp, x);

        Assert.Equal(new Point2(25.0f, 25.0f), pl.Pts[0]);
        Assert.Equal(new Point2(50.0f, 50.0f), pl.Pts[1]);
    }

    private static void TryClipOutside(Polyline2<float> pl, Aabb2 a)
    {
        uint x = pl.Clip(a);
        Assert.Equal(0u, x);
    }

    [Fact]
    public void TestClip()
    {
        var pts = new List<Point2>
        {
            new(25.0f, 25.0f), new(50.0f, 50.0f), new(25.0f, 75.0f), new(50.0f, 100.0f),
        };
        var pl = new Polyline2<float>(pts);
        TryClip(pl, new Aabb2(new Point2(0.0f, 0.0f), new Point2(75.0f, 50.0f)), 2);

        // Test with vertices on edges.
        var pl2 = new Polyline2<float>(pts);
        TryClip(pl2, new Aabb2(new Point2(25.0f, 25.0f), new Point2(50.0f, 100.0f)), 4);

        // All vertices above top of AABB.
        var pl3 = new Polyline2<float>(pts);
        TryClipOutside(pl3, new Aabb2(new Point2(0.0f, 0.0f), new Point2(50.0f, 20.0f)));

        // All vertices left of AABB.
        var pl4 = new Polyline2<float>(pts);
        TryClipOutside(pl4, new Aabb2(new Point2(50.0f, 25.0f), new Point2(100.0f, 100.0f)));

        // All vertices right of AABB.
        var pl5 = new Polyline2<float>(pts);
        TryClipOutside(pl5, new Aabb2(new Point2(0.0f, 25.0f), new Point2(10.0f, 100.0f)));

        // All vertices below bottom of AABB.
        var pl6 = new Polyline2<float>(pts);
        TryClipOutside(pl6, new Aabb2(new Point2(25.0f, 100.0f), new Point2(50.0f, 200.0f)));
    }

    private static void TryClippedPolyline(Polyline2<float> pl, Aabb2 a)
    {
        Polyline2<float> pl2 = pl.ClippedPolyline(a);
        Assert.Equal(2, pl2.Pts.Count);
        Assert.Equal(new Point2(25.0f, 25.0f), pl2.Pts[0]);
        Assert.Equal(new Point2(50.0f, 50.0f), pl2.Pts[1]);
    }

    [Fact]
    public void TestClippedPolyline()
    {
        var pts = new List<Point2>
        {
            new(25.0f, 25.0f), new(50.0f, 50.0f), new(25.0f, 75.0f), new(50.0f, 100.0f),
        };
        var pl = new Polyline2<float>(pts);
        TryClippedPolyline(pl, new Aabb2(new Point2(0.0f, 0.0f), new Point2(75.0f, 50.0f)));
    }
}
