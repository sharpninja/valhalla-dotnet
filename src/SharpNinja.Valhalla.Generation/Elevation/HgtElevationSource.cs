using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Elevation;

public interface IElevationSampleSource : IDisposable
{
    double Sample(PointLL coordinate);

    double[] SampleAll(IReadOnlyList<PointLL> coordinates);
}

/// <summary>
/// Memory-mapped SRTMGL1 HGT sampler matching Valhalla 3.8.3 Skadi bilinear semantics.
/// </summary>
public sealed class HgtElevationSource : IElevationSampleSource
{
    public const int Dimension = 3_601;
    public const int TileByteLength = Dimension * Dimension * sizeof(short);
    public const short NoDataValue = short.MinValue;

    private const short NoDataHigh = 16_384;
    private const short NoDataLow = -16_384;

    private readonly string _elevationDirectory;
    private readonly ConcurrentDictionary<int, HgtTile> _tiles = new();
    private bool _disposed;

    public HgtElevationSource(string elevationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(elevationDirectory);
        _elevationDirectory = Path.GetFullPath(elevationDirectory);
    }

    public double Sample(PointLL coordinate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        double longitude = coordinate.First;
        double latitude = coordinate.Second;
        if (!double.IsFinite(longitude) ||
            !double.IsFinite(latitude) ||
            longitude < -180.0 ||
            longitude >= 180.0 ||
            latitude < -90.0 ||
            latitude >= 90.0)
        {
            return NoDataValue;
        }

        int longitudeFloor = (int)Math.Floor(longitude);
        int latitudeFloor = (int)Math.Floor(latitude);
        int tileIndex = ((latitudeFloor + 90) * 360) + longitudeFloor + 180;
        HgtTile tile = _tiles.GetOrAdd(
            tileIndex,
            static (index, directory) => HgtTile.Open(directory, index),
            _elevationDirectory);
        if (!tile.IsAvailable)
        {
            return NoDataValue;
        }

        double u = (longitude - longitudeFloor) * (Dimension - 1);
        double v = (1.0 - (latitude - latitudeFloor)) * (Dimension - 1);
        return tile.Sample(u, v);
    }

    public double[] SampleAll(IReadOnlyList<PointLL> coordinates)
    {
        ArgumentNullException.ThrowIfNull(coordinates);
        double[] values = new double[coordinates.Count];
        for (int index = 0; index < coordinates.Count; index++)
        {
            values[index] = Sample(coordinates[index]);
        }

        return values;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (HgtTile tile in _tiles.Values)
        {
            tile.Dispose();
        }

        _tiles.Clear();
    }

    internal static string GetFileName(int tileIndex)
    {
        int longitude = (tileIndex % 360) - 180;
        int latitude = (tileIndex / 360) - 90;
        return string.Create(
            7,
            (latitude, longitude),
            static (destination, state) =>
            {
                destination[0] = state.latitude < 0 ? 'S' : 'N';
                int absoluteLatitude = Math.Abs(state.latitude);
                destination[1] = (char)('0' + (absoluteLatitude / 10));
                destination[2] = (char)('0' + (absoluteLatitude % 10));
                destination[3] = state.longitude < 0 ? 'W' : 'E';
                int absoluteLongitude = Math.Abs(state.longitude);
                destination[4] = (char)('0' + (absoluteLongitude / 100));
                destination[5] = (char)('0' + ((absoluteLongitude / 10) % 10));
                destination[6] = (char)('0' + (absoluteLongitude % 10));
            }) + ".hgt";
    }

    private sealed class HgtTile : IDisposable
    {
        private readonly MemoryMappedFile? _mapping;
        private readonly MemoryMappedViewAccessor? _view;

        private HgtTile()
        {
        }

        private HgtTile(
            MemoryMappedFile mapping,
            MemoryMappedViewAccessor view)
        {
            _mapping = mapping;
            _view = view;
        }

        public bool IsAvailable => _view is not null;

        public static HgtTile Open(string directory, int tileIndex)
        {
            string path = Path.Combine(directory, GetFileName(tileIndex));
            if (!File.Exists(path))
            {
                return new HgtTile();
            }

            var file = new FileInfo(path);
            if (file.Length != TileByteLength)
            {
                throw new ElevationDatasetBuildException(
                    ElevationDatasetFailureCode.InvalidElevationTile,
                    $"Elevation tile '{path}' has length {file.Length}; expected {TileByteLength}");
            }

            MemoryMappedFile mapping = MemoryMappedFile.CreateFromFile(
                path,
                FileMode.Open,
                mapName: null,
                capacity: 0,
                MemoryMappedFileAccess.Read);
            MemoryMappedViewAccessor view = mapping.CreateViewAccessor(
                0,
                TileByteLength,
                MemoryMappedFileAccess.Read);
            return new HgtTile(mapping, view);
        }

        public double Sample(double u, double v)
        {
            int x = (int)Math.Floor(u);
            int y = (int)Math.Floor(v);
            double uRatio = u - x;
            double vRatio = v - y;
            double uInverse = 1.0 - uRatio;
            double vInverse = 1.0 - vRatio;
            double aCoefficient = uInverse * vInverse;
            double bCoefficient = uRatio * vInverse;
            double cCoefficient = uInverse * vRatio;
            double dCoefficient = uRatio * vRatio;

            short a = ReadSample(x, y, ref aCoefficient);
            short b = ReadSample(x + 1, y, ref bCoefficient);
            double value = (a * aCoefficient) + (b * bCoefficient);
            double adjustment = aCoefficient + bCoefficient;
            if (y < Dimension - 1)
            {
                short c = ReadSample(x, y + 1, ref cCoefficient);
                short d = ReadSample(x + 1, y + 1, ref dCoefficient);
                value += (c * cCoefficient) + (d * dCoefficient);
                adjustment += cCoefficient + dCoefficient;
            }

            return adjustment > 0.0 ? value / adjustment : NoDataValue;
        }

        public void Dispose()
        {
            _view?.Dispose();
            _mapping?.Dispose();
        }

        private short ReadSample(
            int x,
            int y,
            ref double coefficient)
        {
            if (x < 0 || x >= Dimension || y < 0 || y >= Dimension)
            {
                coefficient = 0.0;
                return 0;
            }

            long offset = (((long)y * Dimension) + x) * sizeof(short);
            short sample = BinaryPrimitives.ReverseEndianness(_view!.ReadInt16(offset));
            if (sample > NoDataHigh || sample < NoDataLow)
            {
                coefficient = 0.0;
                return 0;
            }

            return sample;
        }
    }
}
