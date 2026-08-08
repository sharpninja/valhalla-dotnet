using System.Buffers;
using System.Buffers.Binary;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.IO;

namespace SharpNinja.Valhalla.Generation.Extracts;

public sealed class ManagedTileExtractBuilder : ITileExtractBuilder
{
    private const int TarBlockSize = 512;
    private const int IndexEntrySize = 16;
    private const int CopyBufferSize = 128 * 1024;
    private static readonly byte[] EmptyTarBlock = new byte[TarBlockSize];

    public async ValueTask<TileExtractBuildResult> BuildAsync(
        TileExtractBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidatedRequest validated = Validate(request);
        cancellationToken.ThrowIfCancellationRequested();

        string temporaryPath = validated.OutputPath +
            $".extract-{Guid.NewGuid():N}.tmp";
        var tileReceipts = new List<TileExtractTileReceipt>(validated.TilePaths.Length);
        byte[] copyBuffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        byte[] tarHeader = ArrayPool<byte>.Shared.Rent(TarBlockSize);
        string manifestSha256;

        try
        {
            await using (FileStream output = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CopyBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var headerReader = new GenerationGraphTileReader(
                    new GenerationGraphTileReaderOptions(GraphTileHeader.HeaderSize));
                byte[] indexBytes = CreateIndex(validated.TilePaths);
                await using (var indexStream = new MemoryStream(indexBytes, writable: false))
                {
                    await WriteEntryAsync(
                        output,
                        "index.bin",
                        indexStream,
                        indexBytes.LongLength,
                        copyBuffer,
                        tarHeader,
                        cancellationToken);
                }

                foreach (TilePath tile in validated.TilePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RejectReparsePoint(tile.FullPath, nameof(request.GraphTileDirectory));
                    GenerationGraphTileHeaderReadResult header =
                        await headerReader.ReadHeaderAsync(
                            tile.FullPath,
                            cancellationToken);
                    if (header.Header.Graphid().TileBase() != tile.GraphId.TileBase())
                    {
                        throw new TileExtractBuildException(
                            TileExtractFailureCode.InvalidGraphTile,
                            $"Graph tile '{tile.RelativePath}' does not match its header identity");
                    }

                    await using FileStream input = new(
                        tile.FullPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        CopyBufferSize,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);
                    if (input.Length != tile.ByteLength)
                    {
                        throw new TileExtractBuildException(
                            TileExtractFailureCode.InvalidGraphTile,
                            $"Graph tile '{tile.RelativePath}' changed while the extract was built");
                    }

                    string sha256 = await WriteEntryAsync(
                        output,
                        tile.RelativePath,
                        input,
                        tile.ByteLength,
                        copyBuffer,
                        tarHeader,
                        cancellationToken);
                    tileReceipts.Add(new TileExtractTileReceipt(
                        tile.RelativePath,
                        tile.ByteLength,
                        sha256));
                }

                byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(
                    new
                    {
                        schemaVersion = 1,
                        regionId = validated.RegionId,
                        datasetId = request.DatasetId,
                        buildId = request.BuildId,
                        upstreamCompatibilityVersion =
                            ValhallaGenerationBuilder.UpstreamCompatibilityVersion,
                        deterministicOutput = request.DeterministicOutput,
                        tiles = tileReceipts,
                    });
                await using var manifestStream = new MemoryStream(
                    manifestBytes,
                    writable: false);
                manifestSha256 = await WriteEntryAsync(
                    output,
                    "manifest.json",
                    manifestStream,
                    manifestBytes.LongLength,
                    copyBuffer,
                    tarHeader,
                    cancellationToken);
                await output.WriteAsync(EmptyTarBlock, cancellationToken);
                await output.WriteAsync(EmptyTarBlock, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);

            }

            File.Move(temporaryPath, validated.OutputPath, overwrite: false);
            long byteLength = new FileInfo(validated.OutputPath).Length;
            await using FileStream completed = new(
                validated.OutputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            string archiveSha256 = Convert.ToHexString(
                await SHA256.HashDataAsync(completed, cancellationToken));
            return new TileExtractBuildResult(
                validated.OutputPath,
                validated.RegionId,
                tileReceipts.Count,
                byteLength,
                archiveSha256,
                manifestSha256);
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryFile(temporaryPath);
            throw;
        }
        catch (TileExtractBuildException)
        {
            DeleteTemporaryFile(temporaryPath);
            throw;
        }
        catch (InvalidDataException exception)
        {
            DeleteTemporaryFile(temporaryPath);
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidGraphTile,
                "A graph tile is truncated or invalid",
                exception);
        }
        catch (IOException exception)
        {
            DeleteTemporaryFile(temporaryPath);
            TileExtractFailureCode code = File.Exists(validated.OutputPath)
                ? TileExtractFailureCode.OutputAlreadyExists
                : TileExtractFailureCode.WriteFailed;
            throw new TileExtractBuildException(
                code,
                code == TileExtractFailureCode.OutputAlreadyExists
                    ? "The tile extract output already exists"
                    : "The tile extract could not be written",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            DeleteTemporaryFile(temporaryPath);
            throw new TileExtractBuildException(
                TileExtractFailureCode.WriteFailed,
                "The tile extract could not be written",
                exception);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(copyBuffer);
            ArrayPool<byte>.Shared.Return(tarHeader);
        }
    }

    private static ValidatedRequest Validate(TileExtractBuildRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.GraphTileDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RegionId);

        string tileDirectory = Path.GetFullPath(request.GraphTileDirectory);
        if (!Directory.Exists(tileDirectory))
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidConfiguration,
                "GraphTileDirectory must identify an existing directory");
        }

        RejectReparsePoint(tileDirectory, nameof(request.GraphTileDirectory));
        if (!IsSafeRegionId(request.RegionId))
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidConfiguration,
                "RegionId must contain only lower-case ASCII letters, digits, and hyphens");
        }

        string outputPath = Path.GetFullPath(request.OutputPath);
        if (File.Exists(outputPath))
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.OutputAlreadyExists,
                "The tile extract output already exists");
        }

        string tilePrefix = tileDirectory.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (outputPath.StartsWith(tilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.UnsafePath,
                "The tile extract output cannot be inside GraphTileDirectory");
        }

        string? parent = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(parent))
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidConfiguration,
                "OutputPath must have a parent directory");
        }

        Directory.CreateDirectory(parent);
        RejectReparsePoint(parent, nameof(request.OutputPath));

        var reader = new GraphReader(new GraphReader.Config { TileDir = tileDirectory });
        TilePath[] tiles = reader.GetTileSet()
            .OrderBy(id => id.Level())
            .ThenBy(id => id.Tileid())
            .Select(id =>
            {
                string relativePath = GraphTile.FileSuffix(id.TileBase())
                    .Replace(Path.DirectorySeparatorChar, '/');
                return new TilePath(
                    id,
                    relativePath,
                    Path.Combine(
                        tileDirectory,
                        relativePath.Replace('/', Path.DirectorySeparatorChar)));
            })
            .ToArray();
        if (tiles.Length == 0)
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidConfiguration,
                "GraphTileDirectory does not contain any graph tiles");
        }

        if (tiles.Any(tile => !File.Exists(tile.FullPath)))
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidGraphTile,
                "GraphTileDirectory contains an incomplete graph tile set");
        }

        tiles = tiles
            .Select(tile => tile with
            {
                ByteLength = new FileInfo(tile.FullPath).Length,
            })
            .ToArray();
        if (tiles.Any(tile => tile.ByteLength > uint.MaxValue))
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidGraphTile,
                "A graph tile exceeds the Valhalla extract index size limit");
        }

        return new ValidatedRequest(
            tileDirectory,
            outputPath,
            request.RegionId,
            tiles);
    }

    private static byte[] CreateIndex(IReadOnlyList<TilePath> tiles)
    {
        byte[] index = GC.AllocateUninitializedArray<byte>(
            checked(tiles.Count * IndexEntrySize));
        long dataOffset = checked(
            TarBlockSize + RoundUpToTarBlock(index.LongLength) + TarBlockSize);
        for (int indexNumber = 0; indexNumber < tiles.Count; indexNumber++)
        {
            TilePath tile = tiles[indexNumber];
            Span<byte> entry = index.AsSpan(
                indexNumber * IndexEntrySize,
                IndexEntrySize);
            BinaryPrimitives.WriteUInt64LittleEndian(entry, checked((ulong)dataOffset));
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry[sizeof(ulong)..],
                (tile.GraphId.Tileid() << 3) | tile.GraphId.Level());
            BinaryPrimitives.WriteUInt32LittleEndian(
                entry[(sizeof(ulong) + sizeof(uint))..],
                checked((uint)tile.ByteLength));
            dataOffset = checked(
                dataOffset + RoundUpToTarBlock(tile.ByteLength) + TarBlockSize);
        }

        return index;
    }

    private static long RoundUpToTarBlock(long byteLength) =>
        checked(((byteLength + TarBlockSize - 1) / TarBlockSize) * TarBlockSize);

    private static async ValueTask<string> WriteEntryAsync(
        FileStream output,
        string entryName,
        Stream input,
        long byteLength,
        byte[] copyBuffer,
        byte[] headerBuffer,
        CancellationToken cancellationToken)
    {
        Span<byte> header = headerBuffer.AsSpan(0, TarBlockSize);
        CreateHeader(header, entryName, byteLength);
        await output.WriteAsync(
            headerBuffer.AsMemory(0, TarBlockSize),
            cancellationToken);

        using IncrementalHash sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long remaining = byteLength;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int requested = (int)Math.Min(copyBuffer.Length, remaining);
            int read = await input.ReadAsync(
                copyBuffer.AsMemory(0, requested),
                cancellationToken);
            if (read == 0)
            {
                throw new TileExtractBuildException(
                    TileExtractFailureCode.WriteFailed,
                    $"Entry '{entryName}' ended before its declared length");
            }

            sha256.AppendData(copyBuffer.AsSpan(0, read));
            await output.WriteAsync(
                copyBuffer.AsMemory(0, read),
                cancellationToken);
            remaining -= read;
        }

        int padding = checked((int)((TarBlockSize - (byteLength % TarBlockSize)) %
            TarBlockSize));
        if (padding > 0)
        {
            await output.WriteAsync(
                EmptyTarBlock.AsMemory(0, padding),
                cancellationToken);
        }

        return Convert.ToHexString(sha256.GetHashAndReset());
    }

    private static void CreateHeader(
        Span<byte> destination,
        string entryName,
        long byteLength)
    {
        destination.Clear();
        (string name, string prefix) = SplitTarPath(entryName);
        WriteAscii(destination.Slice(0, 100), name);
        WriteOctal(destination.Slice(100, 8), 0x1A4);
        WriteOctal(destination.Slice(108, 8), 0);
        WriteOctal(destination.Slice(116, 8), 0);
        WriteOctal(destination.Slice(124, 12), byteLength);
        WriteOctal(destination.Slice(136, 12), 0);
        destination.Slice(148, 8).Fill((byte)' ');
        destination[156] = (byte)'0';
        WriteAscii(destination.Slice(257, 6), "ustar");
        destination[262] = 0;
        WriteAscii(destination.Slice(263, 2), "00");
        WriteOctal(destination.Slice(329, 8), 0);
        WriteOctal(destination.Slice(337, 8), 0);
        WriteAscii(destination.Slice(345, 155), prefix);

        int checksum = 0;
        foreach (byte value in destination)
        {
            checksum += value;
        }

        string checksumText = Convert.ToString(checksum, 8)
            .PadLeft(6, '0');
        Encoding.ASCII.GetBytes(
            checksumText,
            destination.Slice(148, 6));
        destination[154] = 0;
        destination[155] = (byte)' ';
    }

    private static (string Name, string Prefix) SplitTarPath(string path)
    {
        if (Encoding.UTF8.GetByteCount(path) <= 100)
        {
            return (path, string.Empty);
        }

        for (int index = path.LastIndexOf('/'); index > 0; index = path.LastIndexOf('/', index - 1))
        {
            string prefix = path[..index];
            string name = path[(index + 1)..];
            if (Encoding.UTF8.GetByteCount(prefix) <= 155 &&
                Encoding.UTF8.GetByteCount(name) <= 100)
            {
                return (name, prefix);
            }
        }

        throw new TileExtractBuildException(
            TileExtractFailureCode.UnsafePath,
            $"Tar entry path '{path}' exceeds the USTAR path limits");
    }

    private static void WriteAscii(Span<byte> destination, string value)
    {
        int count = Encoding.UTF8.GetByteCount(value);
        if (count > destination.Length)
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.UnsafePath,
                "A tar header value exceeds its fixed field");
        }

        Encoding.UTF8.GetBytes(value, destination);
    }

    private static void WriteOctal(Span<byte> destination, long value)
    {
        if (value < 0)
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidConfiguration,
                "Tar numeric values cannot be negative");
        }

        string octal = Convert.ToString(value, 8);
        if (octal.Length >= destination.Length)
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.InvalidConfiguration,
                "A tar numeric value exceeds its fixed field");
        }

        Span<byte> digits = destination[..^1];
        digits.Fill((byte)'0');
        Encoding.ASCII.GetBytes(
            octal,
            digits[(digits.Length - octal.Length)..]);
        destination[^1] = 0;
    }

    private static bool IsSafeRegionId(string regionId)
    {
        if (regionId.Length is < 1 or > 63 ||
            regionId[0] == '-' ||
            regionId[^1] == '-')
        {
            return false;
        }

        foreach (char character in regionId)
        {
            if ((character < 'a' || character > 'z') &&
                (character < '0' || character > '9') &&
                character != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static void RejectReparsePoint(string path, string parameterName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TileExtractBuildException(
                TileExtractFailureCode.UnsafePath,
                $"{parameterName} cannot be a symbolic link or reparse point");
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record ValidatedRequest(
        string TileDirectory,
        string OutputPath,
        string RegionId,
        TilePath[] TilePaths);

    private sealed record TilePath(
        GraphId GraphId,
        string RelativePath,
        string FullPath,
        long ByteLength = 0);
}
