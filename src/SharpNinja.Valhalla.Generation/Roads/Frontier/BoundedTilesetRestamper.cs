using Microsoft.Win32.SafeHandles;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal static class BoundedTilesetRestamper
{
    private const int HeaderBufferBytes = GraphTileHeader.HeaderSize;
    private const int ManagedArrayOverheadBytes = 32;

    internal static long GetHashReservationBytes(int tileCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tileCount);
        return checked(
            ManagedArrayOverheadBytes +
            ((long)sizeof(ulong) * tileCount));
    }

    internal static ushort Restamp(
        string tileDirectory,
        BoundedRestrictionTileCatalog catalog,
        CancellationToken cancellationToken,
        Action<int, GraphId>? passObserver = null,
        long hashMemoryBudgetBytes = long.MaxValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileDirectory);
        ArgumentNullException.ThrowIfNull(catalog);
        cancellationToken.ThrowIfCancellationRequested();

        long requiredHashMemoryBytes =
            GetHashReservationBytes(catalog.TileCount);
        if (requiredHashMemoryBytes > hashMemoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The tileset restamp hash reservation exceeds its " +
                "configured memory budget.");
        }

        byte[] headerBytes =
            GC.AllocateUninitializedArray<byte>(HeaderBufferBytes);
        ulong accumulator = 0;
        ulong? datasetId = null;
        int firstPassCount = 0;
        ulong[] validatedTileHashes =
            GC.AllocateUninitializedArray<ulong>(catalog.TileCount);

        foreach (GraphId tileId in catalog.EnumerateAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            passObserver?.Invoke(1, tileId);
            string tilePath = ResolveTilePath(tileDirectory, tileId);
            GraphTileHeader header = ReadHeader(tilePath, headerBytes);
            ValidateHeaderIdentityAndLength(tilePath, tileId, header);

            if (datasetId is null)
            {
                datasetId = header.DatasetId();
            }
            else if (datasetId.Value != header.DatasetId())
            {
                throw new InvalidDataException(
                    $"Tile {tileId} belongs to dataset {header.DatasetId()}, " +
                    $"not the expected dataset {datasetId.Value}.");
            }

            ulong tileHash =
                header.TileChecksum() & GraphTileHeader.TileHashMask;
            validatedTileHashes[firstPassCount] = tileHash;
            accumulator = unchecked(accumulator + tileHash);
            firstPassCount = checked(firstPassCount + 1);
        }

        ushort buildId = ComputeBuildId(accumulator);
        ulong buildBits =
            (ulong)buildId << GraphTileHeader.TileHashBits;
        int validationPassCount = 0;
        foreach (GraphId tileId in catalog.EnumerateAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            passObserver?.Invoke(2, tileId);
            string tilePath = ResolveTilePath(tileDirectory, tileId);
            GraphTileHeader header = ReadHeader(tilePath, headerBytes);
            ValidateHeaderIdentityAndLength(tilePath, tileId, header);
            if (header.DatasetId() != datasetId)
            {
                throw new InvalidDataException(
                    $"Tile {tileId} changed dataset identity during restamp.");
            }

            ulong currentHash =
                header.TileChecksum() & GraphTileHeader.TileHashMask;
            if (currentHash != validatedTileHashes[validationPassCount])
            {
                throw new InvalidDataException(
                    $"Tile {tileId} changed checksum between restamp passes.");
            }

            validationPassCount = checked(validationPassCount + 1);
        }

        if (firstPassCount != catalog.TileCount ||
            validationPassCount != catalog.TileCount)
        {
            throw new InvalidDataException(
                "The tile catalog changed between restamp passes.");
        }

        int writePassCount = 0;
        foreach (GraphId tileId in catalog.EnumerateAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            passObserver?.Invoke(3, tileId);
            string tilePath = ResolveTilePath(tileDirectory, tileId);
            using SafeFileHandle handle = File.OpenHandle(
                tilePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                FileOptions.RandomAccess | FileOptions.WriteThrough);
            ReadExactly(handle, headerBytes);
            GraphTileHeader header =
                GraphTileHeader.FromBytes(headerBytes);
            ValidateHeaderIdentityAndLength(
                handle,
                tileId,
                header);
            if (header.DatasetId() != datasetId)
            {
                throw new InvalidDataException(
                    $"Tile {tileId} changed dataset identity during restamp.");
            }

            ulong currentHash =
                header.TileChecksum() & GraphTileHeader.TileHashMask;
            if (currentHash != validatedTileHashes[writePassCount])
            {
                throw new InvalidDataException(
                    $"Tile {tileId} changed checksum before restamp write.");
            }

            passObserver?.Invoke(4, tileId);
            cancellationToken.ThrowIfCancellationRequested();
            header.SetRawChecksum(buildBits | currentHash);
            RandomAccess.Write(handle, header.AsSpan(), 0);
            RandomAccess.FlushToDisk(handle);
            cancellationToken.ThrowIfCancellationRequested();
            writePassCount = checked(writePassCount + 1);
        }

        if (writePassCount != catalog.TileCount)
        {
            throw new InvalidDataException(
                "The tile catalog changed while the tileset was restamped.");
        }

        return buildId;
    }

    private static string ResolveTilePath(
        string tileDirectory,
        GraphId tileId) =>
        Path.Combine(
            tileDirectory,
            GraphTile.FileSuffix(tileId.TileBase()));

    private static GraphTileHeader ReadHeader(
        string tilePath,
        byte[] headerBytes)
    {
        using SafeFileHandle handle = File.OpenHandle(
            tilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            FileOptions.RandomAccess);
        ReadExactly(handle, headerBytes);
        return GraphTileHeader.FromBytes(headerBytes);
    }

    private static void ValidateHeaderIdentityAndLength(
        string tilePath,
        GraphId tileId,
        GraphTileHeader header)
    {
        long fileLength = new FileInfo(tilePath).Length;
        ValidateHeaderIdentityAndLength(
            fileLength,
            tileId,
            header);
    }

    private static void ValidateHeaderIdentityAndLength(
        SafeFileHandle handle,
        GraphId tileId,
        GraphTileHeader header)
    {
        ValidateHeaderIdentityAndLength(
            RandomAccess.GetLength(handle),
            tileId,
            header);
    }

    private static void ValidateHeaderIdentityAndLength(
        long fileLength,
        GraphId tileId,
        GraphTileHeader header)
    {
        if (header.Graphid().TileBase() != tileId.TileBase() ||
            header.EndOffset() != fileLength)
        {
            throw new InvalidDataException(
                $"Tile {tileId} changed identity or length during restamp.");
        }
    }

    private static ushort ComputeBuildId(ulong accumulator) =>
        unchecked(
            (ushort)(
                accumulator ^
                (accumulator >> 16) ^
                (accumulator >> 32) ^
                (accumulator >> 48)));

    private static void ReadExactly(
        SafeFileHandle handle,
        Span<byte> destination)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = RandomAccess.Read(
                handle,
                destination[totalRead..],
                totalRead);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    "A graph tile ended before its complete header.");
            }

            totalRead = checked(totalRead + read);
        }
    }
}
