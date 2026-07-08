// Faithful C# port of Valhalla midgard closest_first_generator_t + Tiles::ClosestFirst
// (valhalla @ 3.7.0). Source: F:/github/valhalla/src/midgard/tiles.cc.
//
// Generates, in nearest-first order, the (tile, subdivision/bin, distance) tuples around a seed
// point. Loki's search uses this as its "binner" to walk outward bin-by-bin from each input
// location. The generator does a Dijkstra-like expansion over the global subdivision grid using a
// min-priority-queue keyed by the closest possible distance of each subdivision to the seed.
//
// PORT-NOTE: in C++ this is a free functor created via Tiles::ClosestFirst(seed) and bound to its
// next() method (returning std::function). Here it is a stateful generator class with a Next()
// method, instantiated over the concrete PointLL/double Tiles used by the tile hierarchy (the only
// instantiation loki needs). The priority-queue tie-break (a.first == b.first ? b.second < a.second
// : b.first < a.first) is reproduced exactly so iteration order matches the engine.

using System;
using System.Collections.Generic;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Loki;

/// <summary>
/// Generates (tile, bin, distance) tuples around a seed point in nearest-first order. Faithful port
/// of <c>valhalla::midgard::closest_first_generator_t&lt;PointLL&gt;</c>.
/// </summary>
public sealed class ClosestFirstGenerator
{
    private readonly Tiles<PointLL, double> _tiles;
    private readonly PointLL _seed;
    private readonly HashSet<int> _queued = new();
    private readonly int _subcols;
    private readonly int _subrows;

    // Min-heap of (distance, subdivision). The C++ comparator makes the smallest distance pop first,
    // breaking ties by the SMALLER subdivision index (a.first == b.first ? b.second < a.second).
    private readonly PriorityQueue<int, Best> _queue;

    // Re-usable corner buffer used by Dist (matches the C++ pre-allocated corners vector).
    private readonly List<PointLL> _corners = new(8);

    private static readonly (int Dx, int Dy)[] NeighborOffsets = { (0, -1), (-1, 0), (1, 0), (0, 1) };

    /// <summary>
    /// Constructor. Faithful port of <c>closest_first_generator_t(tiles, seed)</c>.
    /// </summary>
    /// <param name="tiles">The tiling system to walk.</param>
    /// <param name="seed">The seed point to expand around.</param>
    public ClosestFirstGenerator(Tiles<PointLL, double> tiles, PointLL seed)
    {
        _tiles = tiles;
        _seed = seed;
        _queue = new PriorityQueue<int, Best>(new BestComparer());

        // what global subdivision are we starting in
        _subcols = tiles.Ncolumns() * tiles.Nsubdivisions();
        _subrows = tiles.Nrows() * tiles.Nsubdivisions();
        double x = (seed.First - tiles.TileBounds().Minx) / tiles.TileBounds().Width() * _subcols;
        double y = (seed.Second - tiles.TileBounds().Miny) / tiles.TileBounds().Height() * _subrows;
        int subdivision = ((int)y * _subcols) + (int)x;
        _queued.Add(subdivision);
        _queue.Enqueue(subdivision, new Best(0, subdivision));
        Neighbors(subdivision);
    }

    /// <summary>
    /// Get the next closest subdivision as (tileId, bin, distance). Faithful port of <c>next()</c>.
    /// Throws when subdivisions are exhausted.
    /// </summary>
    public (int TileId, ushort Bin, double Distance) Next()
    {
        if (_queue.Count == 0)
        {
            throw new InvalidOperationException("Subdivisions were exhausted");
        }

        _queue.TryDequeue(out int bestSub, out Best best);

        // add its neighbors
        Neighbors(bestSub);

        // return it
        int sx = bestSub % _subcols;
        int sy = bestSub / _subcols;
        int tileColumn = sx / _tiles.Nsubdivisions();
        int tileRow = sy / _tiles.Nsubdivisions();
        int tile = (tileRow * _tiles.Ncolumns()) + tileColumn;
        ushort subdivision = (ushort)(((sy - (tileRow * _tiles.Nsubdivisions())) * _tiles.Nsubdivisions()) +
                                      (sx - (tileColumn * _tiles.Nsubdivisions())));
        return (tile, subdivision, best.Distance);
    }

    // something to measure the closest possible point of a subdivision from the given seed point
    private double Dist(int sub)
    {
        int x = sub % _subcols;
        double x0 = _tiles.TileBounds().Minx + (x * _tiles.SubdivisionSize());
        double x1 = _tiles.TileBounds().Minx + ((x + 1) * _tiles.SubdivisionSize());
        int y = sub / _subcols;
        double y0 = _tiles.TileBounds().Miny + (y * _tiles.SubdivisionSize());
        double y1 = _tiles.TileBounds().Miny + ((y + 1) * _tiles.SubdivisionSize());
        double distance = double.MaxValue;
        _corners.Clear();
        _corners.Add(new PointLL(x0, y0));
        _corners.Add(new PointLL(x1, y0));
        _corners.Add(new PointLL(x0, y1));
        _corners.Add(new PointLL(x1, y1));
        if (x0 < _seed.First && x1 > _seed.First)
        {
            _corners.Add(new PointLL(_seed.First, y0));
            _corners.Add(new PointLL(_seed.First, y1));
        }

        if (y0 < _seed.Second && y1 > _seed.Second)
        {
            _corners.Add(new PointLL(x0, _seed.Second));
            _corners.Add(new PointLL(x1, _seed.Second));
        }

        foreach (PointLL c in _corners)
        {
            double d = _seed.Distance(c);
            if (d < distance)
            {
                distance = d;
            }
        }

        return distance;
    }

    // something to add the neighbors of a given subdivision
    private void Neighbors(int s)
    {
        // walk over all adjacent subdivisions in row major order
        int x = s % _subcols;
        int y = s / _subcols;
        foreach ((int dx, int dy) in NeighborOffsets)
        {
            // skip y out of bounds
            int ny = y + dy;
            if (ny == -1 || ny == _subrows)
            {
                continue;
            }

            // fix x
            int nx = x + dx;
            if (nx == -1 || nx == _subcols)
            {
                if (!PointLL.IsSpherical())
                {
                    continue;
                }

                nx = (nx + _subcols) % _subcols;
            }

            // actually add the thing
            int neighbor = (ny * _subcols) + nx;
            if (_queued.Add(neighbor))
            {
                _queue.Enqueue(neighbor, new Best(Dist(neighbor), neighbor));
            }
        }
    }

    // best_t: std::pair<double, int32_t> (distance, subdivision).
    private readonly struct Best
    {
        public Best(double distance, int subdivision)
        {
            Distance = distance;
            Subdivision = subdivision;
        }

        public double Distance { get; }

        public int Subdivision { get; }
    }

    // Reproduces the C++ priority_queue comparator. std::priority_queue is a max-heap whose top is
    // the element for which comp(top, other) is false for all others; the C++ comp returns true when
    // 'a' should be ordered BELOW 'b'. .NET PriorityQueue is a min-heap dequeuing the SMALLEST per
    // IComparer. To get the same top element we define Compare so that the desired top is "smallest":
    // smaller distance is smaller; on equal distance, smaller subdivision is smaller.
    private sealed class BestComparer : IComparer<Best>
    {
        public int Compare(Best a, Best b)
        {
            int byDist = a.Distance.CompareTo(b.Distance);
            return byDist != 0 ? byDist : a.Subdivision.CompareTo(b.Subdivision);
        }
    }
}
