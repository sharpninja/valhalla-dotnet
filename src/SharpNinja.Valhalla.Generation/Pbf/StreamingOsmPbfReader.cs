using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using K4os.Compression.LZ4;

namespace SharpNinja.Valhalla.Generation.Pbf;

public sealed class StreamingOsmPbfReader
{
    private readonly StreamingOsmPbfReaderOptions options;

    public StreamingOsmPbfReader(StreamingOsmPbfReaderOptions? options = null)
    {
        this.options = options ?? new StreamingOsmPbfReaderOptions();
        ValidateOptions(this.options);
    }

    public ValueTask<StreamingOsmPbfReadResult> ReadAsync(
        string pbfPath,
        IStreamingOsmEntitySink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pbfPath);
        return ReadAsync([pbfPath], sink, cancellationToken);
    }

    public async ValueTask<StreamingOsmPbfReadResult> ReadAsync(
        IReadOnlyList<string> pbfPaths,
        IStreamingOsmEntitySink sink,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pbfPaths);
        ArgumentNullException.ThrowIfNull(sink);
        if (pbfPaths.Count == 0)
        {
            throw new StreamingOsmPbfException(
                StreamingOsmPbfFailureCode.InvalidConfiguration,
                "At least one OSM PBF input is required.");
        }

        var metrics = new StreamingOsmPbfMetricsAccumulator();
        for (var fileOrdinal = 0; fileOrdinal < pbfPaths.Count; fileOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pbfPaths[fileOrdinal];
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new StreamingOsmPbfException(
                    StreamingOsmPbfFailureCode.InvalidConfiguration,
                    "OSM PBF input paths cannot be empty.");
            }

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await ReadFileAsync(stream, fileOrdinal, sink, metrics, cancellationToken)
                .ConfigureAwait(false);
        }

        return new StreamingOsmPbfReadResult(metrics.Snapshot());
    }

    private async ValueTask ReadFileAsync(
        Stream stream,
        int fileOrdinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        CancellationToken cancellationToken)
    {
        var lengthBuffer = ArrayPool<byte>.Shared.Rent(4);
        try
        {
            long blockOrdinal = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lengthRead = await ReadFullAsync(
                    stream,
                    lengthBuffer.AsMemory(0, 4),
                    cancellationToken).ConfigureAwait(false);
                if (lengthRead == 0)
                {
                    break;
                }

                if (lengthRead != 4)
                {
                    throw Failure(
                        StreamingOsmPbfFailureCode.TruncatedInput,
                        "truncated blob-header length");
                }

                metrics.BytesRead += 4;
                var headerLength = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer.AsSpan(0, 4));
                if (headerLength <= 0 || headerLength > options.MaximumBlobHeaderBytes)
                {
                    throw Failure(
                        StreamingOsmPbfFailureCode.OversizedBlobHeader,
                        "blob-header length is outside the configured bound");
                }

                var headerBuffer = ArrayPool<byte>.Shared.Rent(headerLength);
                try
                {
                    var headerRead = await ReadFullAsync(
                        stream,
                        headerBuffer.AsMemory(0, headerLength),
                        cancellationToken).ConfigureAwait(false);
                    if (headerRead != headerLength)
                    {
                        throw Failure(
                            StreamingOsmPbfFailureCode.TruncatedInput,
                            "truncated blob header");
                    }

                    metrics.BytesRead += headerLength;
                    var (blobType, blobLength) = ParseBlobHeader(
                        headerBuffer.AsSpan(0, headerLength));
                    if (blobLength <= 0 || blobLength > options.MaximumCompressedBlobBytes)
                    {
                        throw Failure(
                            StreamingOsmPbfFailureCode.OversizedCompressedBlob,
                            "compressed blob length is outside the configured bound");
                    }

                    var blobBuffer = ArrayPool<byte>.Shared.Rent(blobLength);
                    try
                    {
                        var blobRead = await ReadFullAsync(
                            stream,
                            blobBuffer.AsMemory(0, blobLength),
                            cancellationToken).ConfigureAwait(false);
                        if (blobRead != blobLength)
                        {
                            throw Failure(
                                StreamingOsmPbfFailureCode.TruncatedInput,
                                "truncated blob payload");
                        }

                        metrics.BytesRead += blobLength;
                        ProcessBlob(
                            blobBuffer,
                            blobLength,
                            blobType,
                            fileOrdinal,
                            blockOrdinal,
                            sink,
                            metrics,
                            cancellationToken);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(blobBuffer);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(headerBuffer);
                }

                metrics.FileBlockCount++;
                blockOrdinal++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(lengthBuffer);
        }
    }

    private void ProcessBlob(
        byte[] blobBuffer,
        int blobLength,
        string blobType,
        int fileOrdinal,
        long blockOrdinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        CancellationToken cancellationToken)
    {
        var reader = new PbfSpanReader(blobBuffer.AsSpan(0, blobLength));
        var raw = default(PbfRange);
        var zlib = default(PbfRange);
        var lz4 = default(PbfRange);
        var rawSize = 0;
        var hasRaw = false;
        var hasZlib = false;
        var hasLz4 = false;
        var hasUnsupportedCompression = false;

        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1 when wireType == PbfWireType.LengthDelimited:
                    raw = ToRange(reader.ReadLengthDelimitedRange());
                    hasRaw = true;
                    break;
                case 2 when wireType == PbfWireType.Varint:
                    rawSize = checked((int)reader.ReadVarint());
                    break;
                case 3 when wireType == PbfWireType.LengthDelimited:
                    zlib = ToRange(reader.ReadLengthDelimitedRange());
                    hasZlib = true;
                    break;
                case 6 when wireType == PbfWireType.LengthDelimited:
                    lz4 = ToRange(reader.ReadLengthDelimitedRange());
                    hasLz4 = true;
                    break;
                case 4 or 5 or 7 when wireType == PbfWireType.LengthDelimited:
                    _ = reader.ReadLengthDelimited();
                    hasUnsupportedCompression = true;
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        byte[]? decompressed = null;
        var compression = OsmPbfCompressionKind.Raw;
        var decompressionCount = 0;
        ReadOnlySpan<byte> payload;
        try
        {
            if (hasRaw)
            {
                if (raw.Length > options.MaximumUncompressedBlobBytes)
                {
                    throw Failure(
                        StreamingOsmPbfFailureCode.OversizedUncompressedBlob,
                        "raw blob length is outside the configured bound");
                }

                payload = blobBuffer.AsSpan(raw.Offset, raw.Length);
            }
            else if (hasZlib || hasLz4)
            {
                ValidateRawSize(rawSize);
                decompressed = ArrayPool<byte>.Shared.Rent(rawSize);
                if (hasZlib)
                {
                    DecompressZlib(
                        blobBuffer,
                        zlib,
                        decompressed.AsSpan(0, rawSize),
                        cancellationToken);
                    compression = OsmPbfCompressionKind.Zlib;
                }
                else
                {
                    DecompressLz4(
                        blobBuffer.AsSpan(lz4.Offset, lz4.Length),
                        decompressed.AsSpan(0, rawSize));
                    compression = OsmPbfCompressionKind.Lz4;
                }

                decompressionCount = 1;
                metrics.DecompressionCount++;
                payload = decompressed.AsSpan(0, rawSize);
            }
            else
            {
                throw Failure(
                    StreamingOsmPbfFailureCode.UnsupportedCompression,
                    hasUnsupportedCompression
                        ? "blob uses an unsupported compression mode"
                        : "blob has no supported payload");
            }

            if (string.Equals(blobType, "OSMData", StringComparison.Ordinal))
            {
                metrics.DataBlockCount++;
                ParsePrimitiveBlock(
                    payload,
                    fileOrdinal,
                    blockOrdinal,
                    sink,
                    metrics,
                    cancellationToken);
            }

            metrics.BlockReceipts.Add(new StreamingOsmPbfBlockReceipt(
                fileOrdinal,
                blockOrdinal,
                blobType,
                compression,
                hasRaw ? raw.Length : (hasZlib ? zlib.Length : lz4.Length),
                payload.Length,
                decompressionCount));
        }
        finally
        {
            if (decompressed is not null)
            {
                ArrayPool<byte>.Shared.Return(decompressed);
            }
        }
    }

    private void ParsePrimitiveBlock(
        ReadOnlySpan<byte> data,
        int fileOrdinal,
        long blockOrdinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        CancellationToken cancellationToken)
    {
        using var stringOffsets = new PooledBuffer<int>();
        using var stringLengths = new PooledBuffer<int>();
        using var groups = new PooledBuffer<PbfRange>();
        var reader = new PbfSpanReader(data);
        var stringTable = default(PbfRange);
        var hasStringTable = false;
        var granularity = 100;
        long latitudeOffset = 0;
        long longitudeOffset = 0;

        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1 when wireType == PbfWireType.LengthDelimited:
                    stringTable = ToRange(reader.ReadLengthDelimitedRange());
                    hasStringTable = true;
                    break;
                case 2 when wireType == PbfWireType.LengthDelimited:
                    groups.Add(ToRange(reader.ReadLengthDelimitedRange()));
                    break;
                case 17 when wireType == PbfWireType.Varint:
                    granularity = checked((int)reader.ReadVarint());
                    break;
                case 19 when wireType == PbfWireType.Varint:
                    latitudeOffset = reader.ReadSignedVarint();
                    break;
                case 20 when wireType == PbfWireType.Varint:
                    longitudeOffset = reader.ReadSignedVarint();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (!hasStringTable)
        {
            throw Failure(
                StreamingOsmPbfFailureCode.MalformedProtocolBuffer,
                "primitive block has no string table");
        }

        var stringBytes = data.Slice(stringTable.Offset, stringTable.Length);
        ParseStringTable(stringBytes, stringOffsets, stringLengths);
        var strings = new OsmStringTableView(
            stringBytes,
            stringOffsets.AsSpan(),
            stringLengths.AsSpan(),
            metrics);

        var entityOrdinal = 0;
        var operationCounter = 0;
        foreach (var group in groups.AsSpan())
        {
            ParsePrimitiveGroup(
                data.Slice(group.Offset, group.Length),
                strings,
                granularity,
                latitudeOffset,
                longitudeOffset,
                new OsmEntityOrdinal(fileOrdinal, blockOrdinal, entityOrdinal),
                sink,
                metrics,
                ref entityOrdinal,
                ref operationCounter,
                cancellationToken);
        }
    }

    private void ParseStringTable(
        ReadOnlySpan<byte> data,
        PooledBuffer<int> offsets,
        PooledBuffer<int> lengths)
    {
        var reader = new PbfSpanReader(data);
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            if (field == 1 && wireType == PbfWireType.LengthDelimited)
            {
                var range = reader.ReadLengthDelimitedRange();
                offsets.Add(range.Offset);
                lengths.Add(range.Length);
                if (offsets.Count > options.MaximumStringTableEntries)
                {
                    throw Failure(
                        StreamingOsmPbfFailureCode.EntityLimitExceeded,
                        "string-table entry limit exceeded");
                }
            }
            else
            {
                reader.SkipField(wireType);
            }
        }
    }

    private void ParsePrimitiveGroup(
        ReadOnlySpan<byte> data,
        OsmStringTableView strings,
        int granularity,
        long latitudeOffset,
        long longitudeOffset,
        OsmEntityOrdinal initialOrdinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        ref int entityOrdinal,
        ref int operationCounter,
        CancellationToken cancellationToken)
    {
        var reader = new PbfSpanReader(data);
        while (!reader.End)
        {
            CheckCancellation(ref operationCounter, cancellationToken);
            var (field, wireType) = reader.ReadTag();
            if (wireType != PbfWireType.LengthDelimited)
            {
                reader.SkipField(wireType);
                continue;
            }

            var message = reader.ReadLengthDelimited();
            switch (field)
            {
                case 1:
                    ProcessNode(
                        message,
                        strings,
                        granularity,
                        latitudeOffset,
                        longitudeOffset,
                        initialOrdinal with { EntityOrdinal = entityOrdinal },
                        sink,
                        metrics,
                        ref operationCounter,
                        cancellationToken);
                    entityOrdinal++;
                    break;
                case 2:
                    ProcessDenseNodes(
                        message,
                        strings,
                        granularity,
                        latitudeOffset,
                        longitudeOffset,
                        initialOrdinal,
                        sink,
                        metrics,
                        ref entityOrdinal,
                        ref operationCounter,
                        cancellationToken);
                    break;
                case 3:
                    ProcessWay(
                        message,
                        strings,
                        initialOrdinal with { EntityOrdinal = entityOrdinal },
                        sink,
                        metrics,
                        ref operationCounter,
                        cancellationToken);
                    entityOrdinal++;
                    break;
                case 4:
                    ProcessRelation(
                        message,
                        strings,
                        initialOrdinal with { EntityOrdinal = entityOrdinal },
                        sink,
                        metrics,
                        ref operationCounter,
                        cancellationToken);
                    entityOrdinal++;
                    break;
            }

            if (entityOrdinal > options.MaximumEntityMessagesPerBlock)
            {
                throw Failure(
                    StreamingOsmPbfFailureCode.EntityLimitExceeded,
                    "entity count exceeds the configured block limit");
            }
        }
    }

    private void ProcessNode(
        ReadOnlySpan<byte> data,
        OsmStringTableView strings,
        int granularity,
        long latitudeOffset,
        long longitudeOffset,
        OsmEntityOrdinal ordinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        ref int operationCounter,
        CancellationToken cancellationToken)
    {
        if (!sink.ShouldRetain(OsmEntityKind.Node))
        {
            metrics.SkippedNodeCount++;
            return;
        }

        using var keys = new PooledBuffer<uint>();
        using var values = new PooledBuffer<uint>();
        var reader = new PbfSpanReader(data);
        long id = 0;
        long latitude = 0;
        long longitude = 0;
        while (!reader.End)
        {
            CheckCancellation(ref operationCounter, cancellationToken);
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1 when wireType == PbfWireType.Varint:
                    id = reader.ReadSInt64();
                    break;
                case 2:
                    ReadUInt32Values(ref reader, wireType, keys);
                    break;
                case 3:
                    ReadUInt32Values(ref reader, wireType, values);
                    break;
                case 8 when wireType == PbfWireType.Varint:
                    latitude = reader.ReadSInt64();
                    break;
                case 9 when wireType == PbfWireType.Varint:
                    longitude = reader.ReadSInt64();
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        var tags = new OsmTagView(keys.AsSpan(), values.AsSpan(), strings);
        var node = new OsmNodeView(
            ordinal,
            checked((ulong)id),
            (latitudeOffset + (granularity * latitude)) / 1_000_000_000d,
            (longitudeOffset + (granularity * longitude)) / 1_000_000_000d,
            tags);
        metrics.DecodedNodeCount++;
        sink.AddNode(in node);
    }

    private void ProcessDenseNodes(
        ReadOnlySpan<byte> data,
        OsmStringTableView strings,
        int granularity,
        long latitudeOffset,
        long longitudeOffset,
        OsmEntityOrdinal initialOrdinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        ref int entityOrdinal,
        ref int operationCounter,
        CancellationToken cancellationToken)
    {
        using var ids = new PooledBuffer<long>();
        using var latitudes = new PooledBuffer<long>();
        using var longitudes = new PooledBuffer<long>();
        using var keysValues = new PooledBuffer<int>();
        var reader = new PbfSpanReader(data);
        while (!reader.End)
        {
            CheckCancellation(ref operationCounter, cancellationToken);
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1:
                    ReadSInt64Values(ref reader, wireType, ids);
                    break;
                case 8:
                    ReadSInt64Values(ref reader, wireType, latitudes);
                    break;
                case 9:
                    ReadSInt64Values(ref reader, wireType, longitudes);
                    break;
                case 10:
                    ReadInt32Values(ref reader, wireType, keysValues);
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (ids.Count != latitudes.Count || ids.Count != longitudes.Count)
        {
            throw Failure(
                StreamingOsmPbfFailureCode.MalformedProtocolBuffer,
                "dense node coordinate arrays have different lengths");
        }

        if (!sink.ShouldRetain(OsmEntityKind.Node))
        {
            metrics.SkippedNodeCount += ids.Count;
            entityOrdinal = checked(entityOrdinal + ids.Count);
            return;
        }

        using var keys = new PooledBuffer<uint>();
        using var values = new PooledBuffer<uint>();
        long id = 0;
        long latitude = 0;
        long longitude = 0;
        var keyValueIndex = 0;
        for (var index = 0; index < ids.Count; index++)
        {
            CheckCancellation(ref operationCounter, cancellationToken);
            id = checked(id + ids[index]);
            latitude = checked(latitude + latitudes[index]);
            longitude = checked(longitude + longitudes[index]);
            keys.Clear();
            values.Clear();
            while (keyValueIndex < keysValues.Count && keysValues[keyValueIndex] != 0)
            {
                if (keyValueIndex + 1 >= keysValues.Count)
                {
                    throw Failure(
                        StreamingOsmPbfFailureCode.MalformedProtocolBuffer,
                        "dense node tag stream is truncated");
                }

                keys.Add(checked((uint)keysValues[keyValueIndex++]));
                values.Add(checked((uint)keysValues[keyValueIndex++]));
            }

            if (keyValueIndex < keysValues.Count)
            {
                keyValueIndex++;
            }

            var tags = new OsmTagView(keys.AsSpan(), values.AsSpan(), strings);
            var node = new OsmNodeView(
                initialOrdinal with { EntityOrdinal = entityOrdinal },
                checked((ulong)id),
                (latitudeOffset + (granularity * latitude)) / 1_000_000_000d,
                (longitudeOffset + (granularity * longitude)) / 1_000_000_000d,
                tags);
            metrics.DecodedNodeCount++;
            sink.AddNode(in node);
            entityOrdinal++;
        }
    }

    private void ProcessWay(
        ReadOnlySpan<byte> data,
        OsmStringTableView strings,
        OsmEntityOrdinal ordinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        ref int operationCounter,
        CancellationToken cancellationToken)
    {
        if (!sink.ShouldRetain(OsmEntityKind.Way))
        {
            metrics.SkippedWayCount++;
            return;
        }

        using var keys = new PooledBuffer<uint>();
        using var values = new PooledBuffer<uint>();
        using var deltaReferences = new PooledBuffer<long>();
        using var references = new PooledBuffer<ulong>();
        var reader = new PbfSpanReader(data);
        long id = 0;
        while (!reader.End)
        {
            CheckCancellation(ref operationCounter, cancellationToken);
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1 when wireType == PbfWireType.Varint:
                    id = reader.ReadSignedVarint();
                    break;
                case 2:
                    ReadUInt32Values(ref reader, wireType, keys);
                    break;
                case 3:
                    ReadUInt32Values(ref reader, wireType, values);
                    break;
                case 8:
                    ReadSInt64Values(ref reader, wireType, deltaReferences);
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        long reference = 0;
        foreach (var delta in deltaReferences.AsSpan())
        {
            reference = checked(reference + delta);
            references.Add(checked((ulong)reference));
        }

        var tags = new OsmTagView(keys.AsSpan(), values.AsSpan(), strings);
        var way = new OsmWayView(ordinal, checked((ulong)id), references.AsSpan(), tags);
        metrics.DecodedWayCount++;
        sink.AddWay(in way);
    }

    private void ProcessRelation(
        ReadOnlySpan<byte> data,
        OsmStringTableView strings,
        OsmEntityOrdinal ordinal,
        IStreamingOsmEntitySink sink,
        StreamingOsmPbfMetricsAccumulator metrics,
        ref int operationCounter,
        CancellationToken cancellationToken)
    {
        if (!sink.ShouldRetain(OsmEntityKind.Relation))
        {
            metrics.SkippedRelationCount++;
            return;
        }

        using var keys = new PooledBuffer<uint>();
        using var values = new PooledBuffer<uint>();
        using var roles = new PooledBuffer<int>();
        using var memberIds = new PooledBuffer<long>();
        using var types = new PooledBuffer<int>();
        var reader = new PbfSpanReader(data);
        long id = 0;
        while (!reader.End)
        {
            CheckCancellation(ref operationCounter, cancellationToken);
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1 when wireType == PbfWireType.Varint:
                    id = reader.ReadSignedVarint();
                    break;
                case 2:
                    ReadUInt32Values(ref reader, wireType, keys);
                    break;
                case 3:
                    ReadUInt32Values(ref reader, wireType, values);
                    break;
                case 8:
                    ReadInt32Values(ref reader, wireType, roles);
                    break;
                case 9:
                    ReadSInt64Values(ref reader, wireType, memberIds);
                    break;
                case 10:
                    ReadInt32Values(ref reader, wireType, types);
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (memberIds.Count > options.MaximumRelationMembers)
        {
            throw Failure(
                StreamingOsmPbfFailureCode.RelationMemberLimitExceeded,
                "relation member count exceeds the configured limit");
        }

        long memberId = 0;
        var writableMemberIds = memberIds.AsWritableSpan();
        for (var index = 0; index < writableMemberIds.Length; index++)
        {
            memberId = checked(memberId + writableMemberIds[index]);
            writableMemberIds[index] = memberId;
        }

        var tags = new OsmTagView(keys.AsSpan(), values.AsSpan(), strings);
        var relation = new OsmRelationView(
            ordinal,
            checked((ulong)id),
            memberIds.AsSpan(),
            types.AsSpan(),
            roles.AsSpan(),
            strings,
            tags);
        metrics.DecodedRelationCount++;
        sink.AddRelation(in relation);
    }

    private static void ReadUInt32Values(
        ref PbfSpanReader reader,
        PbfWireType wireType,
        PooledBuffer<uint> target)
    {
        if (wireType == PbfWireType.Varint)
        {
            target.Add(checked((uint)reader.ReadVarint()));
            return;
        }

        if (wireType != PbfWireType.LengthDelimited)
        {
            reader.SkipField(wireType);
            return;
        }

        var packed = new PbfSpanReader(reader.ReadLengthDelimited());
        while (!packed.End)
        {
            target.Add(checked((uint)packed.ReadVarint()));
        }
    }

    private static void ReadInt32Values(
        ref PbfSpanReader reader,
        PbfWireType wireType,
        PooledBuffer<int> target)
    {
        if (wireType == PbfWireType.Varint)
        {
            target.Add(checked((int)reader.ReadVarint()));
            return;
        }

        if (wireType != PbfWireType.LengthDelimited)
        {
            reader.SkipField(wireType);
            return;
        }

        var packed = new PbfSpanReader(reader.ReadLengthDelimited());
        while (!packed.End)
        {
            target.Add(checked((int)packed.ReadVarint()));
        }
    }

    private static void ReadSInt64Values(
        ref PbfSpanReader reader,
        PbfWireType wireType,
        PooledBuffer<long> target)
    {
        if (wireType == PbfWireType.Varint)
        {
            target.Add(reader.ReadSInt64());
            return;
        }

        if (wireType != PbfWireType.LengthDelimited)
        {
            reader.SkipField(wireType);
            return;
        }

        var packed = new PbfSpanReader(reader.ReadLengthDelimited());
        while (!packed.End)
        {
            target.Add(packed.ReadSInt64());
        }
    }

    private void DecompressZlib(
        byte[] blobBuffer,
        PbfRange compressed,
        Span<byte> destination,
        CancellationToken cancellationToken)
    {
        try
        {
            using var input = new MemoryStream(
                blobBuffer,
                compressed.Offset,
                compressed.Length,
                writable: false,
                publiclyVisible: true);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var written = 0;
            while (written < destination.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = zlib.Read(destination[written..]);
                if (count == 0)
                {
                    break;
                }

                written += count;
            }

            if (written != destination.Length || zlib.ReadByte() != -1)
            {
                throw Failure(
                    StreamingOsmPbfFailureCode.DecompressionFailed,
                    "zlib output length does not match raw_size");
            }
        }
        catch (StreamingOsmPbfException)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw new StreamingOsmPbfException(
                StreamingOsmPbfFailureCode.DecompressionFailed,
                "PBF decompression error: invalid zlib payload.",
                exception);
        }
    }

    private static void DecompressLz4(ReadOnlySpan<byte> compressed, Span<byte> destination)
    {
        var decoded = LZ4Codec.Decode(compressed, destination);
        if (decoded != destination.Length)
        {
            throw Failure(
                StreamingOsmPbfFailureCode.DecompressionFailed,
                "LZ4 output length does not match raw_size");
        }
    }

    private void ValidateRawSize(int rawSize)
    {
        if (rawSize <= 0 || rawSize > options.MaximumUncompressedBlobBytes)
        {
            throw Failure(
                StreamingOsmPbfFailureCode.OversizedUncompressedBlob,
                "raw_size is outside the configured bound");
        }
    }

    private void CheckCancellation(
        ref int operationCounter,
        CancellationToken cancellationToken)
    {
        operationCounter++;
        if (operationCounter >= options.CancellationCheckInterval)
        {
            operationCounter = 0;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static (string Type, int Length) ParseBlobHeader(ReadOnlySpan<byte> data)
    {
        var reader = new PbfSpanReader(data);
        string? type = null;
        var length = 0;
        while (!reader.End)
        {
            var (field, wireType) = reader.ReadTag();
            switch (field)
            {
                case 1 when wireType == PbfWireType.LengthDelimited:
                    type = reader.ReadString();
                    break;
                case 3 when wireType == PbfWireType.Varint:
                    length = checked((int)reader.ReadVarint());
                    break;
                default:
                    reader.SkipField(wireType);
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(type) || length <= 0)
        {
            throw Failure(
                StreamingOsmPbfFailureCode.MalformedProtocolBuffer,
                "blob header is missing type or datasize");
        }

        return (type, length);
    }

    private static async ValueTask<int> ReadFullAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await stream.ReadAsync(destination[total..], cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    private static void ValidateOptions(StreamingOsmPbfReaderOptions options)
    {
        if (options.MaximumBlobHeaderBytes <= 0 ||
            options.MaximumCompressedBlobBytes <= 0 ||
            options.MaximumUncompressedBlobBytes <= 0 ||
            options.MaximumStringTableEntries <= 0 ||
            options.MaximumEntityMessagesPerBlock <= 0 ||
            options.MaximumRelationMembers <= 0 ||
            options.CancellationCheckInterval <= 0)
        {
            throw new StreamingOsmPbfException(
                StreamingOsmPbfFailureCode.InvalidConfiguration,
                "All streaming PBF bounds must be positive.");
        }
    }

    private static StreamingOsmPbfException Failure(
        StreamingOsmPbfFailureCode code,
        string detail) =>
        new(code, $"PBF input error: {detail}.");

    private static PbfRange ToRange((int Offset, int Length) value) =>
        new(value.Offset, value.Length);

    private readonly record struct PbfRange(int Offset, int Length);
}
