using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

internal interface IRestrictionMutationPlanReader
{
    long RestrictionCount { get; }

    long EdgePatchCount { get; }

    RestrictionMutationPlanPayload ReadRestriction(long index);

    void CopyRestrictionPayload(
        long index,
        Span<byte> destination);

    RestrictionMutationPlanEdgePatch ReadEdgePatch(long index);
}

internal readonly record struct RestrictionMutationPlanPayload(
    ulong TileValue,
    RestrictionMutationDirection Direction,
    ulong CanonicalOrdinal,
    ushort PayloadLength);

internal readonly record struct RestrictionMutationPlanEdgePatch(
    ulong TileValue,
    uint EdgeIndex,
    uint StartMaskOr,
    uint EndMaskOr,
    bool SetComplexRestriction,
    ulong CanonicalOrdinal);

internal static class StreamingRestrictionPlanApplier
{
    private const int NodeInfoSize = 32;
    private const int NodeTransitionSize = 8;
    private const int DirectedEdgeSize = DirectedEdge.SizeOf;

    internal static void Apply(
        string tileDirectory,
        GraphTile sourceTile,
        IRestrictionMutationPlanReader plan,
        int copyBufferBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileDirectory);
        ArgumentNullException.ThrowIfNull(sourceTile);
        ArgumentNullException.ThrowIfNull(plan);
        if (copyBufferBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(copyBufferBytes));
        }

        GraphId tileId = sourceTile.Id().TileBase();
        FindTileRanges(
            plan,
            tileId.Value,
            out long patchStart,
            out long patchCount,
            out long forwardStart,
            out long forwardCount,
            out long reverseStart,
            out long reverseCount);
        if (patchCount == 0 &&
            forwardCount == 0 &&
            reverseCount == 0)
        {
            return;
        }

        string sourcePath = Path.Combine(
            tileDirectory,
            GraphTile.FileSuffix(tileId));
        string temporaryPath =
            sourcePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Exception? operationFailure = null;
        try
        {
            Rewrite(
                sourcePath,
                temporaryPath,
                sourceTile,
                plan,
                copyBufferBytes,
                patchStart,
                patchCount,
                forwardStart,
                forwardCount,
                reverseStart,
                reverseCount,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, sourcePath, overwrite: true);
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

        ResolveApplyOutcome(operationFailure, cleanupFailure);
    }

    private static void Rewrite(
        string sourcePath,
        string destinationPath,
        GraphTile plannedTile,
        IRestrictionMutationPlanReader plan,
        int copyBufferBytes,
        long patchStart,
        long patchCount,
        long forwardStart,
        long forwardCount,
        long reverseStart,
        long reverseCount,
        CancellationToken cancellationToken)
    {
        byte[] copyBuffer =
            GC.AllocateUninitializedArray<byte>(copyBufferBytes);
        Span<byte> headerBytes =
            stackalloc byte[GraphTileHeader.HeaderSize];
        Span<byte> edgeBytes = stackalloc byte[DirectedEdgeSize];
        Span<byte> digest = stackalloc byte[MD5.HashSizeInBytes];

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
        StreamingRestrictionTileMutation.ValidateSourceIdentity(
            source,
            sourceHeader,
            plannedTile,
            copyBuffer,
            cancellationToken);

        long addedForwardBytes =
            SumPayloadBytes(plan, forwardStart, forwardCount);
        long addedReverseBytes =
            SumPayloadBytes(plan, reverseStart, reverseCount);
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

        long patchIndex = patchStart;
        long patchEnd = checked(patchStart + patchCount);
        for (uint edgeIndex = 0;
             edgeIndex < sourceHeader.Directededgecount();
             edgeIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.ReadExactly(edgeBytes);
            if (patchIndex < patchEnd)
            {
                RestrictionMutationPlanEdgePatch patch =
                    plan.ReadEdgePatch(patchIndex);
                if (patch.EdgeIndex == edgeIndex)
                {
                    DirectedEdge edge =
                        System.Runtime.InteropServices.MemoryMarshal
                            .Read<DirectedEdge>(edgeBytes);
                    edge.SetStartRestriction(
                        edge.StartRestriction |
                        patch.StartMaskOr);
                    edge.SetEndRestriction(
                        edge.EndRestriction |
                        patch.EndMaskOr);
                    if (patch.SetComplexRestriction)
                    {
                        edge.SetComplexRestriction(true);
                    }

                    System.Runtime.InteropServices.MemoryMarshal.Write(
                        edgeBytes,
                        in edge);
                    patchIndex++;
                }
            }

            WriteAndHash(destination, bodyHash, edgeBytes);
        }

        if (patchIndex != patchEnd)
        {
            throw new InvalidDataException(
                "A planned directed-edge patch is outside the tile.");
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
        WritePayloads(
            destination,
            bodyHash,
            plan,
            forwardStart,
            forwardCount,
            cancellationToken);
        CopyExact(
            source,
            destination,
            bodyHash,
            copyBuffer,
            sourceHeader.EdgeinfoOffset() -
            sourceHeader.ComplexRestrictionReverseOffset(),
            cancellationToken);
        WritePayloads(
            destination,
            bodyHash,
            plan,
            reverseStart,
            reverseCount,
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
                "The source tile was not consumed to its declared end offset.");
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
        destination.Write(targetHeader.ToBytes());
        destination.Flush(flushToDisk: true);
    }

    private static void FindTileRanges(
        IRestrictionMutationPlanReader plan,
        ulong tileValue,
        out long patchStart,
        out long patchCount,
        out long forwardStart,
        out long forwardCount,
        out long reverseStart,
        out long reverseCount)
    {
        patchStart = LowerBoundPatches(plan, tileValue);
        long patchEnd = patchStart;
        while (patchEnd < plan.EdgePatchCount &&
               plan.ReadEdgePatch(patchEnd).TileValue == tileValue)
        {
            patchEnd++;
        }

        patchCount = patchEnd - patchStart;

        forwardStart = LowerBoundRestrictions(
            plan,
            tileValue,
            RestrictionMutationDirection.Forward);
        long forwardEnd = forwardStart;
        while (forwardEnd < plan.RestrictionCount)
        {
            RestrictionMutationPlanPayload payload =
                plan.ReadRestriction(forwardEnd);
            if (payload.TileValue != tileValue ||
                payload.Direction !=
                RestrictionMutationDirection.Forward)
            {
                break;
            }

            forwardEnd++;
        }

        forwardCount = forwardEnd - forwardStart;
        reverseStart = LowerBoundRestrictions(
            plan,
            tileValue,
            RestrictionMutationDirection.Reverse);
        long reverseEnd = reverseStart;
        while (reverseEnd < plan.RestrictionCount)
        {
            RestrictionMutationPlanPayload payload =
                plan.ReadRestriction(reverseEnd);
            if (payload.TileValue != tileValue ||
                payload.Direction !=
                RestrictionMutationDirection.Reverse)
            {
                break;
            }

            reverseEnd++;
        }

        reverseCount = reverseEnd - reverseStart;
    }

    private static long LowerBoundPatches(
        IRestrictionMutationPlanReader plan,
        ulong tileValue)
    {
        long low = 0;
        long high = plan.EdgePatchCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            if (plan.ReadEdgePatch(middle).TileValue < tileValue)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static long LowerBoundRestrictions(
        IRestrictionMutationPlanReader plan,
        ulong tileValue,
        RestrictionMutationDirection direction)
    {
        long low = 0;
        long high = plan.RestrictionCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            RestrictionMutationPlanPayload current =
                plan.ReadRestriction(middle);
            int comparison = current.TileValue.CompareTo(tileValue);
            if (comparison == 0)
            {
                comparison = current.Direction.CompareTo(direction);
            }

            if (comparison < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static long SumPayloadBytes(
        IRestrictionMutationPlanReader plan,
        long start,
        long count)
    {
        long total = 0;
        for (long index = start;
             index < checked(start + count);
             index++)
        {
            total = checked(
                total +
                plan.ReadRestriction(index).PayloadLength);
        }

        return total;
    }

    private static void WritePayloads(
        Stream destination,
        IncrementalHash hash,
        IRestrictionMutationPlanReader plan,
        long start,
        long count,
        CancellationToken cancellationToken)
    {
        Span<byte> payload = stackalloc byte[
            ComplexRestriction.SizeOfStruct +
            (ComplexRestriction.MaxViasPerRestriction *
             ComplexRestriction.SizeOfGraphId)];
        for (long index = start;
             index < checked(start + count);
             index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RestrictionMutationPlanPayload metadata =
                plan.ReadRestriction(index);
            plan.CopyRestrictionPayload(index, payload);
            WriteAndHash(
                destination,
                hash,
                payload[..metadata.PayloadLength]);
        }
    }

    private static void ApplyOffsetChanges(
        GraphTileHeader target,
        GraphTileHeader source,
        long addedForwardBytes,
        long addedBytes)
    {
        target.SetComplexRestrictionReverseOffset(
            checked((uint)(
                source.ComplexRestrictionReverseOffset() +
                addedForwardBytes)));
        target.SetEdgeinfoOffset(
            checked((uint)(source.EdgeinfoOffset() + addedBytes)));
        target.SetTextlistOffset(
            checked((uint)(source.TextlistOffset() + addedBytes)));
        target.SetLaneConnectivityOffset(
            source.LaneConnectivityOffset() == 0
                ? 0
                : checked((uint)(
                    source.LaneConnectivityOffset() + addedBytes)));
        target.SetPredictedspeedsOffset(
            source.PredictedspeedsOffset() == 0
                ? 0
                : checked((uint)(
                    source.PredictedspeedsOffset() + addedBytes)));
        target.SetEndOffset(
            checked((uint)(source.EndOffset() + addedBytes)));
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
            int request = (int)Math.Min(remaining, buffer.Length);
            int read = source.Read(buffer, 0, request);
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

    internal static void ResolveApplyOutcome(
        Exception? operationFailure,
        Exception? cleanupFailure)
    {
        if (operationFailure is not null)
        {
            if (cleanupFailure is not null)
            {
                operationFailure.Data[
                    "StreamingRestrictionPlanApplier.CleanupFailure"] =
                    cleanupFailure;
            }

            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }

        if (cleanupFailure is not null)
        {
            ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
        }
    }
}
