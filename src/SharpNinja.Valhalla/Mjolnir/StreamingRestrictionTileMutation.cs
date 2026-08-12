using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

internal sealed class StreamingRestrictionTileMutation
    : IRestrictionTileMutation
{
    private const int NodeInfoSize = 32;
    private const int NodeTransitionSize = 8;
    private const int DirectedEdgeSize = DirectedEdge.SizeOf;
    private const int MaximumRestrictionSize =
        ComplexRestriction.SizeOfStruct +
        (ComplexRestriction.MaxViasPerRestriction *
         ComplexRestriction.SizeOfGraphId);

    private readonly GraphTile tile;
    private readonly DirectedEdgePatch[] edgePatches;
    private readonly ComplexRestrictionBuilder?[] forwardRestrictions;
    private readonly ComplexRestrictionBuilder?[] reverseRestrictions;
    private readonly int copyBufferBytes;
    private int edgePatchCount;
    private int forwardRestrictionCount;
    private int reverseRestrictionCount;
    private bool stored;

    internal StreamingRestrictionTileMutation(
        GraphTile tile,
        int copyBufferBytes,
        int maximumEdgePatches,
        int maximumForwardRestrictions,
        int maximumReverseRestrictions)
    {
        ArgumentNullException.ThrowIfNull(tile);
        if (copyBufferBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(copyBufferBytes));
        }

        this.tile = tile;
        this.copyBufferBytes = copyBufferBytes;
        edgePatches = new DirectedEdgePatch[
            ValidateCapacity(
                maximumEdgePatches,
                nameof(maximumEdgePatches))];
        forwardRestrictions = new ComplexRestrictionBuilder?[
            ValidateCapacity(
                maximumForwardRestrictions,
                nameof(maximumForwardRestrictions))];
        reverseRestrictions = new ComplexRestrictionBuilder?[
            ValidateCapacity(
                maximumReverseRestrictions,
                nameof(maximumReverseRestrictions))];
    }

    public GraphTileHeader Header() => tile.Header();

    public NodeInfo NodeBuilder(int index) => tile.Node(index);

    public DirectedEdge DirectedEdgeBuilder(int index)
    {
        for (int patchIndex = 0;
             patchIndex < edgePatchCount;
             patchIndex++)
        {
            if (edgePatches[patchIndex].Index == index)
            {
                return edgePatches[patchIndex].Edge;
            }
        }

        return tile.DirectedEdge(index);
    }

    public void SetDirectedEdgeBuilder(
        int index,
        DirectedEdge edge)
    {
        if ((uint)index >= tile.Header().Directededgecount())
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        for (int patchIndex = 0;
             patchIndex < edgePatchCount;
             patchIndex++)
        {
            if (edgePatches[patchIndex].Index == index)
            {
                edgePatches[patchIndex] =
                    new DirectedEdgePatch(index, edge);
                return;
            }
        }

        if (edgePatchCount == edgePatches.Length)
        {
            throw new InvalidOperationException(
                "The streaming restriction tile mutation exceeded its " +
                "bounded directed-edge patch capacity.");
        }

        edgePatches[edgePatchCount++] =
            new DirectedEdgePatch(index, edge);
    }

    public ulong EdgeInfoWayId(DirectedEdge edge) =>
        tile.EdgeInfoWayId(edge);

    public void AddForwardComplexRestriction(
        ComplexRestrictionBuilder restriction)
    {
        ArgumentNullException.ThrowIfNull(restriction);
        if (forwardRestrictionCount == forwardRestrictions.Length)
        {
            throw new InvalidOperationException(
                "The streaming restriction tile mutation exceeded its " +
                "bounded forward restriction capacity.");
        }

        forwardRestrictions[forwardRestrictionCount++] = restriction;
    }

    public void AddReverseComplexRestriction(
        ComplexRestrictionBuilder restriction)
    {
        ArgumentNullException.ThrowIfNull(restriction);
        if (reverseRestrictionCount == reverseRestrictions.Length)
        {
            throw new InvalidOperationException(
                "The streaming restriction tile mutation exceeded its " +
                "bounded reverse restriction capacity.");
        }

        reverseRestrictions[reverseRestrictionCount++] = restriction;
    }

    public void StoreTileData(
        string tileDirectory,
        CancellationToken cancellationToken)
    {
        if (stored)
        {
            throw new InvalidOperationException(
                "A streaming restriction mutation can be stored only once.");
        }

        string sourcePath = Path.Combine(
            tileDirectory,
            GraphTile.FileSuffix(tile.Id().TileBase()));
        string temporaryPath =
            sourcePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? operationFailure = null;
        try
        {
            Rewrite(
                sourcePath,
                temporaryPath,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(
                temporaryPath,
                sourcePath,
                overwrite: true);
            stored = true;
        }
        catch (Exception exception)
        {
            operationFailure = exception;
        }

        Exception? cleanupFailure = null;
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (Exception exception)
        {
            cleanupFailure = exception;
        }

        ResolveStoreOutcome(operationFailure, cleanupFailure);
    }

    internal static void ValidateSourceIdentity(
        FileStream source,
        GraphTileHeader sourceHeader,
        GraphTile plannedTile,
        byte[] copyBuffer,
        CancellationToken cancellationToken)
    {
        ValidateSource(source, sourceHeader);
        ValidateCurrentBodyChecksum(
            source,
            sourceHeader,
            copyBuffer,
            cancellationToken);
        if (sourceHeader.Graphid().TileBase() !=
                plannedTile.Id().TileBase() ||
            sourceHeader.RawChecksum() !=
                plannedTile.Header().RawChecksum() ||
            sourceHeader.EndOffset() !=
                plannedTile.Header().EndOffset())
        {
            throw new InvalidDataException(
                "The graph tile changed after restriction mutation " +
                "planning; refusing to apply stale edge indexes.");
        }
    }

    private void Rewrite(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] copyBuffer = GC.AllocateUninitializedArray<byte>(
            copyBufferBytes);
        Span<byte> headerBytes =
            stackalloc byte[GraphTileHeader.HeaderSize];
        Span<byte> edgeBytes =
            stackalloc byte[DirectedEdgeSize];
        Span<byte> restrictionBytes =
            stackalloc byte[MaximumRestrictionSize];
        Span<byte> digest =
            stackalloc byte[MD5.HashSizeInBytes];

        using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.SequentialScan);
        source.ReadExactly(headerBytes);
        GraphTileHeader sourceHeader =
            GraphTileHeader.FromBytes(headerBytes);
        ValidateSourceIdentity(
            source,
            sourceHeader,
            tile,
            copyBuffer,
            cancellationToken);

        long addedForwardBytes =
            SumRestrictionBytes(
                forwardRestrictions,
                forwardRestrictionCount);
        long addedReverseBytes =
            SumRestrictionBytes(
                reverseRestrictions,
                reverseRestrictionCount);
        long addedBytes = checked(
            addedForwardBytes + addedReverseBytes);
        GraphTileHeader targetHeader =
            GraphTileHeader.FromBytes(headerBytes);
        ApplyOffsetChanges(
            targetHeader,
            sourceHeader,
            addedForwardBytes,
            addedBytes);

        using var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.SequentialScan);
        destination.Position = GraphTileHeader.HeaderSize;
        using IncrementalHash bodyHash =
            IncrementalHash.CreateHash(HashAlgorithmName.MD5);

        long directedEdgeOffset = checked(
            GraphTileHeader.HeaderSize +
            ((long)sourceHeader.Nodecount() * NodeInfoSize) +
            ((long)sourceHeader.Transitioncount() *
             NodeTransitionSize));
        long directedEdgeEnd = checked(
            directedEdgeOffset +
            ((long)sourceHeader.Directededgecount() *
             DirectedEdgeSize));
        source.Position = GraphTileHeader.HeaderSize;
        CopyExact(
            source,
            destination,
            bodyHash,
            copyBuffer,
            directedEdgeOffset - GraphTileHeader.HeaderSize,
            cancellationToken);

        for (int edgeIndex = 0;
             edgeIndex < sourceHeader.Directededgecount();
             edgeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.ReadExactly(edgeBytes);
            if (TryGetPatch(edgeIndex, out DirectedEdge patch))
            {
                MemoryMarshal.Write(edgeBytes, in patch);
            }

            WriteAndHash(destination, bodyHash, edgeBytes);
        }

        CopyExact(
            source,
            destination,
            bodyHash,
            copyBuffer,
            sourceHeader.ComplexRestrictionForwardOffset() -
            directedEdgeEnd,
            cancellationToken);
        CopyExact(
            source,
            destination,
            bodyHash,
            copyBuffer,
            sourceHeader.ComplexRestrictionReverseOffset() -
            sourceHeader.ComplexRestrictionForwardOffset(),
            cancellationToken);
        WriteRestrictions(
            destination,
            bodyHash,
            forwardRestrictions,
            forwardRestrictionCount,
            restrictionBytes,
            cancellationToken);
        CopyExact(
            source,
            destination,
            bodyHash,
            copyBuffer,
            sourceHeader.EdgeinfoOffset() -
            sourceHeader.ComplexRestrictionReverseOffset(),
            cancellationToken);
        WriteRestrictions(
            destination,
            bodyHash,
            reverseRestrictions,
            reverseRestrictionCount,
            restrictionBytes,
            cancellationToken);
        CopyExact(
            source,
            destination,
            bodyHash,
            copyBuffer,
            sourceHeader.EndOffset() -
            sourceHeader.EdgeinfoOffset(),
            cancellationToken);

        if (source.Position != sourceHeader.EndOffset())
        {
            throw new InvalidDataException(
                "The streaming restriction mutation did not consume the " +
                "complete source tile.");
        }

        if (!bodyHash.TryGetHashAndReset(
                digest,
                out int digestBytes) ||
            digestBytes != MD5.HashSizeInBytes)
        {
            throw new InvalidOperationException(
                "The tile-body checksum could not be finalized.");
        }

        ulong buildIdBits =
            (ulong)sourceHeader.BuildId() <<
            GraphTileHeader.TileHashBits;
        targetHeader.SetRawChecksum(
            buildIdBits |
            GraphTileChecksum.FoldMd5Digest(digest));
        destination.Position = 0;
        destination.Write(targetHeader.AsSpan());
        destination.Flush(flushToDisk: true);
    }

    private static void ValidateSource(
        FileStream source,
        GraphTileHeader header)
    {
        long directedEdgeOffset = checked(
            GraphTileHeader.HeaderSize +
            ((long)header.Nodecount() * NodeInfoSize) +
            ((long)header.Transitioncount() *
             NodeTransitionSize));
        long directedEdgeEnd = checked(
            directedEdgeOffset +
            ((long)header.Directededgecount() *
             DirectedEdgeSize));
        uint forwardOffset =
            header.ComplexRestrictionForwardOffset();
        uint reverseOffset =
            header.ComplexRestrictionReverseOffset();
        uint edgeInfoOffset = header.EdgeinfoOffset();
        uint endOffset = header.EndOffset();
        if (header.Graphid().IsValid() is false ||
            directedEdgeOffset < GraphTileHeader.HeaderSize ||
            directedEdgeEnd > forwardOffset ||
            forwardOffset > reverseOffset ||
            reverseOffset > edgeInfoOffset ||
            edgeInfoOffset > endOffset ||
            endOffset != source.Length)
        {
            throw new InvalidDataException(
                "The source graph tile has invalid fixed-section counts " +
                "or variable-section offsets.");
        }
    }

    private static void ValidateCurrentBodyChecksum(
        FileStream source,
        GraphTileHeader header,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        source.Position = GraphTileHeader.HeaderSize;
        using IncrementalHash hash =
            IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long remaining = checked(
            (long)header.EndOffset() -
            GraphTileHeader.HeaderSize);
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int request = (int)Math.Min(remaining, buffer.Length);
            int read = source.Read(buffer, 0, request);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            hash.AppendData(buffer.AsSpan(0, read));
            remaining -= read;
        }

        Span<byte> digest = stackalloc byte[MD5.HashSizeInBytes];
        if (hash.TryGetHashAndReset(digest, out int written) is false ||
            written != digest.Length ||
            GraphTileChecksum.FoldMd5Digest(digest) !=
            header.TileChecksum())
        {
            throw new InvalidDataException(
                "The current graph tile body does not match its checksum.");
        }

        source.Position = GraphTileHeader.HeaderSize;
    }


    private static void ApplyOffsetChanges(
        GraphTileHeader target,
        GraphTileHeader source,
        long addedForwardBytes,
        long addedBytes)
    {
        target.SetComplexRestrictionReverseOffset(
            AddOffset(
                source.ComplexRestrictionReverseOffset(),
                addedForwardBytes));
        target.SetEdgeinfoOffset(
            AddOffset(source.EdgeinfoOffset(), addedBytes));
        target.SetTextlistOffset(
            AddOffset(source.TextlistOffset(), addedBytes));
        target.SetLaneConnectivityOffset(
            AddOptionalOffset(
                source.LaneConnectivityOffset(),
                addedBytes));
        target.SetPredictedspeedsOffset(
            AddOptionalOffset(
                source.PredictedspeedsOffset(),
                addedBytes));
        target.SetEndOffset(
            AddOffset(source.EndOffset(), addedBytes));
    }

    private static uint AddOffset(uint offset, long additional) =>
        checked((uint)(offset + additional));

    private static uint AddOptionalOffset(
        uint offset,
        long additional) =>
        offset == 0 ? 0 : AddOffset(offset, additional);

    private static long SumRestrictionBytes(
        ComplexRestrictionBuilder?[] restrictions,
        int count)
    {
        long bytes = 0;
        for (int index = 0; index < count; index++)
        {
            bytes = checked(
                bytes +
                restrictions[index]!.SizeOf());
        }

        return bytes;
    }

    private static void WriteRestrictions(
        Stream destination,
        IncrementalHash hash,
        ComplexRestrictionBuilder?[] restrictions,
        int count,
        Span<byte> buffer,
        CancellationToken cancellationToken)
    {
        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int written = restrictions[index]!.Serialize(buffer);
            WriteAndHash(
                destination,
                hash,
                buffer[..written]);
        }
    }

    private static void CopyExact(
        Stream source,
        Stream destination,
        IncrementalHash hash,
        byte[] buffer,
        long bytes,
        CancellationToken cancellationToken)
    {
        if (bytes < 0)
        {
            throw new InvalidDataException(
                "The source tile section offsets are not monotonic.");
        }

        long remaining = bytes;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int request = (int)Math.Min(
                remaining,
                buffer.Length);
            int read = source.Read(
                buffer,
                0,
                request);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            WriteAndHash(
                destination,
                hash,
                buffer.AsSpan(0, read));
            remaining -= read;
        }
    }

    private static void WriteAndHash(
        Stream destination,
        IncrementalHash hash,
        ReadOnlySpan<byte> bytes)
    {
        destination.Write(bytes);
        hash.AppendData(bytes);
    }

    private bool TryGetPatch(
        int edgeIndex,
        out DirectedEdge edge)
    {
        for (int patchIndex = 0;
             patchIndex < edgePatchCount;
             patchIndex++)
        {
            DirectedEdgePatch candidate =
                edgePatches[patchIndex];
            if (candidate.Index == edgeIndex)
            {
                edge = candidate.Edge;
                return true;
            }
        }

        edge = default;
        return false;
    }

    private static int ValidateCapacity(
        int capacity,
        string parameterName)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }

        return capacity;
    }

    internal static void ResolveStoreOutcome(
        Exception? operationFailure,
        Exception? cleanupFailure)
    {
        if (operationFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                operationFailure.Data[
                    "StreamingRestrictionTileMutation.CleanupFailure"] =
                    cleanupFailure;
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }

    private readonly record struct DirectedEdgePatch(
        int Index,
        DirectedEdge Edge);
}
