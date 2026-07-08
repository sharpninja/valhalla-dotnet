// Faithful C# port of Valhalla's gtest suite test/double_bucket_queue.cc.
// Each [Fact] mirrors a TEST(DoubleBucketQueue, ...) case with the same inputs and behavior.
//
// PORT-NOTE: The C++ file also contains TEST(EdgeLabel, test_sizeof) asserting the
// byte sizes of sif::EdgeLabel/PathEdgeLabel/BDEdgeLabel/MMEdgeLabel. Those types
// live in sif/edgelabel.h which is outside the baldr port slice, so that test is
// intentionally omitted here and belongs with a future sif port.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class DoubleBucketQueueTests
{
    // struct simple_label { float c; float sortcost() const { return c; } };
    private sealed class SimpleLabel : ISortCost
    {
        public float C { get; init; }

        public float SortCost() => C;
    }

    private static void TryAddRemove(IReadOnlyList<uint> costs, IReadOnlyList<uint> expectedorder)
    {
        var edgelabels = new List<SimpleLabel>();

        uint i = 0;
        var adjlist = new DoubleBucketQueue<SimpleLabel>(0, 10000, 1, edgelabels);
        foreach (uint cost in costs)
        {
            edgelabels.Add(new SimpleLabel { C = cost });
            adjlist.Add(i);
            ++i;
        }

        foreach (uint expected in expectedorder)
        {
            uint labelindex = adjlist.Pop();
            SimpleLabel edgelabel = new();
            if (labelindex != GraphConstants.InvalidLabel)
            {
                edgelabel = edgelabels[(int)labelindex];
            }

            // Do the same transform that's done in `edgecost()`.
            Assert.Equal((float)expected, edgelabel.SortCost());
        }
    }

    [Fact]
    public void TestInvalidConstruction()
    {
        var edgelabels = new List<SimpleLabel>();

        // Invalid bucket size not caught.
        Assert.Throws<InvalidOperationException>(
            () => new DoubleBucketQueue<SimpleLabel>(0, 10000, 0, edgelabels));

        // Invalid cost range not caught.
        Assert.Throws<InvalidOperationException>(
            () => new DoubleBucketQueue<SimpleLabel>(0, 0.0f, 1, edgelabels));
    }

    [Fact]
    public void TestAddRemove()
    {
        var costs = new uint[]
        {
            67, 325, 25, 466, 1000, 100005,
            758, 167, 258, 16442, 278, 111111000,
        };
        var expectedorder = (uint[])costs.Clone();
        Array.Sort(expectedorder);
        TryAddRemove(costs, expectedorder);
    }

    private static void TryClear(IReadOnlyList<uint> costs)
    {
        uint i = 0;
        var edgelabels = new List<SimpleLabel>();
        var adjlist = new DoubleBucketQueue<SimpleLabel>(0, 10000, 50, edgelabels);
        foreach (uint cost in costs)
        {
            edgelabels.Add(new SimpleLabel { C = cost });
            adjlist.Add(i);
            i++;
        }

        adjlist.Clear();
        uint idx = adjlist.Pop();
        Assert.Equal(GraphConstants.InvalidLabel, idx);
    }

    [Fact]
    public void TestClear()
    {
        var costs = new uint[]
        {
            67, 325, 25, 466, 1000, 100005,
            758, 167, 258, 16442, 278, 111111000,
        };
        TryClear(costs);
    }

    [Fact]
    public void Rc4FloatPrecisionErrors()
    {
        // Tests what happens when the internal floats in DoubleBucketQueue lose precision.
        var costs = new uint[] { 1320209856 };
        var expectedorder = (uint[])costs.Clone();
        Array.Sort(expectedorder);
        TryAddRemove(costs, expectedorder);
    }

    private static void TryRemove(
        DoubleBucketQueue<SimpleLabel> dbqueue,
        int numToRemove,
        IReadOnlyList<SimpleLabel> costs)
    {
        float previousCost = float.NegativeInfinity;
        for (int i = 0; i < numToRemove; ++i)
        {
            uint top = dbqueue.Pop();
            Assert.NotEqual(GraphConstants.InvalidLabel, top);
            float cost = costs[(int)top].SortCost();
            Assert.True(previousCost <= cost, "expected order test failed");
            previousCost = cost;
        }

        {
            uint top = dbqueue.Pop();
            Assert.Equal(GraphConstants.InvalidLabel, top);
        }
    }

    private static void TrySimulation(
        DoubleBucketQueue<SimpleLabel> dbqueue,
        List<SimpleLabel> costs,
        int loopCount,
        int expansionSize,
        int maxIncrementCost,
        Random gen)
    {
        // Track all label indexes in the dbqueue.
        var addedLabels = new HashSet<uint>();

        uint seedIdx = (uint)costs.Count;
        costs.Add(new SimpleLabel { C = 10.0f });
        dbqueue.Add(seedIdx);

        for (int loop = 0; loop < loopCount; loop++)
        {
            uint key = dbqueue.Pop();
            if (key == GraphConstants.InvalidLabel)
            {
                break;
            }

            float minCost = costs[(int)key].SortCost();

            // Must be the minimal one among the tracked labels.
            foreach (uint k in addedLabels)
            {
                Assert.True(minCost <= costs[(int)k].SortCost(), "Simulation: minimal cost expected");
            }

            addedLabels.Remove(key);

            for (int i = 0; i < expansionSize; i++)
            {
                float newcost = (float)Math.Floor(minCost + 1 + (Rand01(gen) * maxIncrementCost));
                if (i % 2 == 0 && addedLabels.Count > 0)
                {
                    // Decrease cost.
                    int pick = (int)(Rand01(gen) * addedLabels.Count);
                    if (pick >= addedLabels.Count)
                    {
                        pick = addedLabels.Count - 1;
                    }

                    uint idx = ElementAt(addedLabels, pick);
                    if (newcost < costs[(int)idx].SortCost())
                    {
                        dbqueue.Decrease(idx, newcost);
                        costs[(int)idx] = new SimpleLabel { C = newcost };
                    }
                }
                else
                {
                    // Add new label.
                    uint idx = (uint)costs.Count;
                    costs.Add(new SimpleLabel { C = newcost });
                    dbqueue.Add(idx);
                    addedLabels.Add(idx);
                }
            }
        }

        TryRemove(dbqueue, addedLabels.Count, costs);
    }

    [Fact]
    public void TestSimulation()
    {
        // Deterministic seed for reproducibility (the C++ uses std::random_device/mt19937).
        var gen = new Random(12345);

        {
            var costs = new List<SimpleLabel>();
            var dbqueue1 = new DoubleBucketQueue<SimpleLabel>(0, 1, 100000, costs);
            TrySimulation(dbqueue1, costs, 1000, 10, 1000, gen);
        }

        {
            var costs = new List<SimpleLabel>();
            var dbqueue2 = new DoubleBucketQueue<SimpleLabel>(0, 1, 100000, costs);
            TrySimulation(dbqueue2, costs, 222, 40, 100, gen);
        }

        {
            var costs = new List<SimpleLabel>();
            var dbqueue3 = new DoubleBucketQueue<SimpleLabel>(0, 1, 100000, costs);
            TrySimulation(dbqueue3, costs, 333, 60, 100, gen);
        }

        {
            var costs = new List<SimpleLabel>();
            var dbqueue4 = new DoubleBucketQueue<SimpleLabel>(0, 1, 1000, costs);
            TrySimulation(dbqueue4, costs, 333, 60, 100, gen);
        }
    }

    // Mirrors valhalla test::rand01(gen): a uniform value in [0, 1).
    private static double Rand01(Random gen) => gen.NextDouble();

    private static uint ElementAt(HashSet<uint> set, int index)
    {
        int i = 0;
        foreach (uint v in set)
        {
            if (i == index)
            {
                return v;
            }

            i++;
        }

        throw new ArgumentOutOfRangeException(nameof(index));
    }
}
