// Faithful managed implementation of Valhalla 3.8.3 tile checksum and tileset build-ID
// semantics from src/mjolnir/graphtilebuilder.cc and src/mjolnir/util.cc at commit
// a60c7cbfc83e073f50887cd27e0109d02e6b64e5.

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Computes and stamps the Valhalla 3.8.3 checksum field: a reproducible 48-bit tile-body hash
/// in the low bits and one order-independent 16-bit tileset build ID in the high bits.
/// </summary>
public static class GraphTileChecksum
{
    private const ulong FoldConstant = 0x9e3779b97f4a7c15UL;

    /// <summary>Computes the official 48-bit folded-MD5 hash for a tile body.</summary>
    public static ulong ComputeTileHash(ReadOnlySpan<byte> tileBody)
    {
        Span<byte> digest = stackalloc byte[MD5.HashSizeInBytes];
        MD5.HashData(tileBody, digest);

        ulong lo = BinaryPrimitives.ReadUInt64BigEndian(digest[..8]);
        ulong hi = BinaryPrimitives.ReadUInt64BigEndian(digest[8..]);

        ulong folded = unchecked(
            lo ^ (hi + FoldConstant + (lo << 12) + (lo >> 4)));
        return folded & GraphTileHeader.TileHashMask;
    }

    /// <summary>
    /// Computes the order-independent 16-bit tileset build ID from per-tile 48-bit hashes.
    /// </summary>
    public static ushort ComputeTilesetBuildId(IEnumerable<ulong> tileChecksums)
    {
        ArgumentNullException.ThrowIfNull(tileChecksums);

        ulong accumulator = 0;
        foreach (ulong checksum in tileChecksums)
        {
            accumulator = unchecked(
                accumulator + (checksum & GraphTileHeader.TileHashMask));
        }

        return FoldTilesetHashAccumulator(accumulator);
    }

    private static ushort FoldTilesetHashAccumulator(ulong accumulator)
        => unchecked(
            (ushort)(
                accumulator ^
                (accumulator >> 16) ^
                (accumulator >> 32) ^
                (accumulator >> 48)));

    /// <summary>
    /// Recomputes and stamps the low 48-bit hash for a mutated tile while preserving its current
    /// tileset build ID until the complete tileset can be restamped.
    /// </summary>
    public static ulong RefreshTileHash(byte[] tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (tile.Length < GraphTileHeader.HeaderSize)
        {
            throw new ArgumentException(
                $"Tile must contain at least {GraphTileHeader.HeaderSize} bytes.",
                nameof(tile));
        }

        GraphTileHeader header = GraphTileHeader.FromBytes(tile);
        if (header.EndOffset() != tile.Length)
        {
            throw new InvalidDataException(
                $"Tile end offset {header.EndOffset()} does not match byte length {tile.Length}.");
        }

        ulong tileHash = ComputeTileHash(tile.AsSpan(GraphTileHeader.HeaderSize));
        ulong buildIdBits = (ulong)header.BuildId() << GraphTileHeader.TileHashBits;
        header.SetRawChecksum(buildIdBits | tileHash);
        header.AsSpan().CopyTo(tile);
        return tileHash;
    }

    /// <summary>
    /// Recomputes every tile hash and stamps one shared build ID using bounded two-pass I/O.
    /// Each tile is validated and atomically replaced without retaining the complete tileset.
    /// </summary>
    public static ushort RefreshTilesetFiles(string tileDirectory) =>
        RefreshTilesetFiles(tileDirectory, CancellationToken.None);

    /// <summary>
    /// Recomputes and stamps one tileset while honoring cancellation between every bounded tile
    /// read, hash, and atomic replacement operation.
    /// </summary>
    public static ushort RefreshTilesetFiles(
        string tileDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(tileDirectory);
        cancellationToken.ThrowIfCancellationRequested();

        string[] tilePaths =
            Directory.GetFiles(tileDirectory, "*.gph", SearchOption.AllDirectories);
        Array.Sort(tilePaths, StringComparer.Ordinal);

        ulong accumulator = 0;
        foreach (string tilePath in tilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] tile = File.ReadAllBytes(tilePath);
            cancellationToken.ThrowIfCancellationRequested();
            ulong tileHash = RefreshTileHash(tile);
            accumulator = unchecked(accumulator + tileHash);
            WriteTileAtomically(tilePath, tile);
        }

        ushort buildId = FoldTilesetHashAccumulator(accumulator);
        ulong buildIdBits = (ulong)buildId << GraphTileHeader.TileHashBits;
        foreach (string tilePath in tilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] tile = File.ReadAllBytes(tilePath);
            cancellationToken.ThrowIfCancellationRequested();
            GraphTileHeader header = GraphTileHeader.FromBytes(tile);
            header.SetRawChecksum(buildIdBits | header.TileChecksum());
            header.AsSpan().CopyTo(tile);
            WriteTileAtomically(tilePath, tile);
        }

        return buildId;
    }

    /// <summary>
    /// Validates all supplied tile blobs, computes one build ID, and stamps it into every header
    /// while preserving each tile's low 48-bit data hash.
    /// </summary>
    public static ushort StampTilesetBuildId(IReadOnlyList<byte[]> tiles)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        var headers = new GraphTileHeader[tiles.Count];
        var checksums = new ulong[tiles.Count];

        for (int index = 0; index < tiles.Count; index++)
        {
            byte[] tile = tiles[index] ??
                throw new ArgumentException($"Tile at index {index} is null.", nameof(tiles));
            if (tile.Length < GraphTileHeader.HeaderSize)
            {
                throw new ArgumentException(
                    $"Tile at index {index} must contain at least {GraphTileHeader.HeaderSize} bytes.",
                    nameof(tiles));
            }

            GraphTileHeader header = GraphTileHeader.FromBytes(tile);
            headers[index] = header;
            checksums[index] = header.TileChecksum();
        }

        ushort buildId = ComputeTilesetBuildId(checksums);
        ulong buildIdBits = (ulong)buildId << GraphTileHeader.TileHashBits;

        for (int index = 0; index < tiles.Count; index++)
        {
            GraphTileHeader header = headers[index];
            header.SetRawChecksum(buildIdBits | header.TileChecksum());
            header.AsSpan().CopyTo(tiles[index]);
        }

        return buildId;
    }

    internal static void WriteTileAtomically(string path, byte[] tile)
    {
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, tile);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
