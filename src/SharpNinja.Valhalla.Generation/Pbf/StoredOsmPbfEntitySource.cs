using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Pbf;

/// <summary>
/// Decodes every physical PBF block once, writes normalized entities to bounded typed stores, and
/// replays the canonical way, node, and relation passes required by the core graph parser.
/// </summary>
public sealed class StoredOsmPbfEntitySource : IOsmPbfEntitySource, IDisposable
{
    private const int StoreCount = 4;
    private readonly IntermediateSequenceStore<StoredEntityRecord> nodes;
    private readonly IntermediateSequenceStore<StoredEntityRecord> ways;
    private readonly IntermediateSequenceStore<StoredEntityRecord> relations;
    private readonly IntermediateBlobStore payloads;
    private readonly long[,] fileCounts;
    private readonly int[] maximumPayloadLengths = new int[3];
    private int completedReplayPassCount;
    private bool disposed;

    private StoredOsmPbfEntitySource(
        int fileCount,
        IntermediateSequenceStore<StoredEntityRecord> nodes,
        IntermediateSequenceStore<StoredEntityRecord> ways,
        IntermediateSequenceStore<StoredEntityRecord> relations,
        IntermediateBlobStore payloads)
    {
        FileCount = fileCount;
        this.nodes = nodes;
        this.ways = ways;
        this.relations = relations;
        this.payloads = payloads;
        fileCounts = new long[fileCount, 3];
        ReadResult = new StreamingOsmPbfReadResult(
            new StreamingOsmPbfReadMetrics(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []));
    }

    public int FileCount { get; }

    public StreamingOsmPbfReadResult ReadResult { get; private set; }

    public int CompletedReplayPassCount => Volatile.Read(ref completedReplayPassCount);

    public long PeakIntermediateMemoryBytes =>
        nodes.State.PeakMemoryBytes +
        ways.State.PeakMemoryBytes +
        relations.State.PeakMemoryBytes +
        payloads.State.PeakMemoryBytes;

    public long ScratchHighWaterMarkBytes =>
        nodes.State.ScratchHighWaterMarkBytes +
        ways.State.ScratchHighWaterMarkBytes +
        relations.State.ScratchHighWaterMarkBytes +
        payloads.State.ScratchHighWaterMarkBytes;

    public static async ValueTask<StoredOsmPbfEntitySource> CreateAsync(
        IReadOnlyList<string> pbfPaths,
        string workingDirectory,
        IntermediateStorageMode storageMode,
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pbfPaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        if (pbfPaths.Count == 0)
        {
            throw new ArgumentException("At least one PBF path is required.", nameof(pbfPaths));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scratchDiskBudgetBytes);

        Directory.CreateDirectory(workingDirectory);
        long storeMemoryBudget = Math.Max(1, memoryBudgetBytes / StoreCount);
        long storeScratchBudget = Math.Max(1, scratchDiskBudgetBytes / StoreCount);
        var nodeStore = CreateSequenceStore(
            workingDirectory,
            "osm-nodes",
            storageMode,
            storeMemoryBudget,
            storeScratchBudget);
        var wayStore = CreateSequenceStore(
            workingDirectory,
            "osm-ways",
            storageMode,
            storeMemoryBudget,
            storeScratchBudget);
        var relationStore = CreateSequenceStore(
            workingDirectory,
            "osm-relations",
            storageMode,
            storeMemoryBudget,
            storeScratchBudget);
        var blobStore = new IntermediateBlobStore(
            new IntermediateBlobStoreOptions(
                workingDirectory,
                "osm-payloads",
                storageMode,
                storeMemoryBudget,
                storeScratchBudget));

        var source = new StoredOsmPbfEntitySource(
            pbfPaths.Count,
            nodeStore,
            wayStore,
            relationStore,
            blobStore);
        try
        {
            var sink = new StoreSink(source);
            source.ReadResult = await new StreamingOsmPbfReader()
                .ReadAsync(pbfPaths, sink, cancellationToken)
                .ConfigureAwait(false);
            await nodeStore.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await wayStore.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await relationStore.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await blobStore.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public void VisitFile(
        int fileOrdinal,
        OsmPbfEntityPass pass,
        IOsmPbfVisitor visitor,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        ArgumentOutOfRangeException.ThrowIfNegative(fileOrdinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fileOrdinal, FileCount);
        ArgumentNullException.ThrowIfNull(visitor);
        cancellationToken.ThrowIfCancellationRequested();

        int kindIndex = pass switch
        {
            OsmPbfEntityPass.Nodes => 0,
            OsmPbfEntityPass.Ways => 1,
            OsmPbfEntityPass.Relations => 2,
            _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, null),
        };
        IntermediateSequenceStore<StoredEntityRecord> store = pass switch
        {
            OsmPbfEntityPass.Nodes => nodes,
            OsmPbfEntityPass.Ways => ways,
            OsmPbfEntityPass.Relations => relations,
            _ => throw new ArgumentOutOfRangeException(nameof(pass), pass, null),
        };

        long start = 0;
        for (var priorFile = 0; priorFile < fileOrdinal; priorFile++)
        {
            start += fileCounts[priorFile, kindIndex];
        }

        long count = fileCounts[fileOrdinal, kindIndex];
        byte[] replayBuffer = ArrayPool<byte>.Shared.Rent(
            Math.Max(1, maximumPayloadLengths[kindIndex]));
        try
        {
            for (long index = 0; index < count; index++)
            {
                if ((index & 4095) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                StoredEntityRecord record = store.Read(start + index);
                var reference = new IntermediateBlobReference(
                    record.PayloadOffset,
                    record.PayloadLength);
                Span<byte> payload = replayBuffer.AsSpan(
                    0,
                    record.PayloadLength);
                payloads.Read(reference, payload);
                Replay(pass, record, payload, visitor);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(replayBuffer);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref completedReplayPassCount);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        relations.Dispose();
        ways.Dispose();
        nodes.Dispose();
        payloads.Dispose();
        disposed = true;
    }

    private static IntermediateSequenceStore<StoredEntityRecord> CreateSequenceStore(
        string workingDirectory,
        string storeName,
        IntermediateStorageMode storageMode,
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes) =>
        new(
            new IntermediateSequenceStoreOptions(
                workingDirectory,
                storeName,
                storageMode,
                memoryBudgetBytes,
                scratchDiskBudgetBytes));

    private static void Replay(
        OsmPbfEntityPass pass,
        StoredEntityRecord record,
        ReadOnlySpan<byte> payload,
        IOsmPbfVisitor visitor)
    {
        var reader = new PayloadReader(payload);
        switch (pass)
        {
            case OsmPbfEntityPass.Nodes:
                visitor.Node(
                    record.Id,
                    record.Latitude,
                    record.Longitude,
                    reader.ReadTags());
                break;
            case OsmPbfEntityPass.Ways:
                visitor.Way(
                    record.Id,
                    reader.ReadUInt64List(),
                    reader.ReadTags());
                break;
            case OsmPbfEntityPass.Relations:
                visitor.Relation(
                    record.Id,
                    reader.ReadMembers(),
                    reader.ReadTags());
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(pass), pass, null);
        }

        reader.RequireEnd();
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct StoredEntityRecord(
        int FileOrdinal,
        long BlockOrdinal,
        int EntityOrdinal,
        ulong Id,
        double Latitude,
        double Longitude,
        long PayloadOffset,
        int PayloadLength);

    private sealed class StoreSink : IStreamingOsmEntitySink
    {
        private readonly StoredOsmPbfEntitySource source;
        private readonly ArrayBufferWriter<byte> writer = new();

        public StoreSink(StoredOsmPbfEntitySource source)
        {
            this.source = source;
        }

        public bool ShouldRetain(OsmEntityKind kind) => true;

        public void AddNode(scoped in OsmNodeView node)
        {
            writer.Clear();
            WriteTags(writer, node.Tags);
            Append(source.nodes, 0, node.Ordinal, node.Id, node.Latitude, node.Longitude, writer);
            source.fileCounts[node.Ordinal.FileOrdinal, 0]++;
        }

        public void AddWay(scoped in OsmWayView way)
        {
            writer.Clear();
            WriteInt32(writer, way.NodeReferences.Length);
            foreach (ulong nodeReference in way.NodeReferences)
            {
                WriteUInt64(writer, nodeReference);
            }

            WriteTags(writer, way.Tags);
            Append(source.ways, 1, way.Ordinal, way.Id, 0, 0, writer);
            source.fileCounts[way.Ordinal.FileOrdinal, 1]++;
        }

        public void AddRelation(scoped in OsmRelationView relation)
        {
            writer.Clear();
            WriteInt32(writer, relation.MemberCount);
            for (var index = 0; index < relation.MemberCount; index++)
            {
                OsmRelationMemberEntity member = relation.GetMember(index);
                WriteUInt64(writer, member.Id);
                WriteInt32(writer, (int)member.Type);
                WriteString(writer, member.Role);
            }

            WriteTags(writer, relation.Tags);
            Append(source.relations, 2, relation.Ordinal, relation.Id, 0, 0, writer);
            source.fileCounts[relation.Ordinal.FileOrdinal, 2]++;
        }

        private void Append(
            IntermediateSequenceStore<StoredEntityRecord> store,
            int kindIndex,
            OsmEntityOrdinal ordinal,
            ulong id,
            double latitude,
            double longitude,
            ArrayBufferWriter<byte> payloadWriter)
        {
            IntermediateBlobReference payload = source.payloads.Append(
                payloadWriter.WrittenSpan);
            source.maximumPayloadLengths[kindIndex] = Math.Max(
                source.maximumPayloadLengths[kindIndex],
                payload.Length);
            store.Append(
                new StoredEntityRecord(
                    ordinal.FileOrdinal,
                    ordinal.BlockOrdinal,
                    ordinal.EntityOrdinal,
                    id,
                    latitude,
                    longitude,
                    payload.Offset,
                    payload.Length));
        }

        private static void WriteTags(
            ArrayBufferWriter<byte> destination,
            OsmTagView tags)
        {
            WriteInt32(destination, tags.Count);
            for (var index = 0; index < tags.Count; index++)
            {
                OsmTag tag = tags[index];
                WriteString(destination, tag.Key);
                WriteString(destination, tag.Value);
            }
        }

        private static void WriteInt32(ArrayBufferWriter<byte> destination, int value)
        {
            Span<byte> target = destination.GetSpan(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(target, value);
            destination.Advance(sizeof(int));
        }

        private static void WriteUInt64(ArrayBufferWriter<byte> destination, ulong value)
        {
            Span<byte> target = destination.GetSpan(sizeof(ulong));
            BinaryPrimitives.WriteUInt64LittleEndian(target, value);
            destination.Advance(sizeof(ulong));
        }

        private static void WriteString(ArrayBufferWriter<byte> destination, string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            WriteInt32(destination, byteCount);
            Span<byte> target = destination.GetSpan(byteCount);
            Encoding.UTF8.GetBytes(value, target);
            destination.Advance(byteCount);
        }
    }

    private ref struct PayloadReader
    {
        private readonly ReadOnlySpan<byte> payload;
        private int offset;

        public PayloadReader(ReadOnlySpan<byte> payload)
        {
            this.payload = payload;
        }

        public ulong[] ReadUInt64List()
        {
            int count = ReadCount();
            var result = new ulong[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = ReadUInt64();
            }

            return result;
        }

        public OsmRelationMember[] ReadMembers()
        {
            int count = ReadCount();
            var result = new OsmRelationMember[count];
            for (var index = 0; index < count; index++)
            {
                result[index] = new OsmRelationMember(
                    ReadUInt64(),
                    (OsmMemberType)ReadInt32(),
                    ReadString());
            }

            return result;
        }

        public IReadOnlyDictionary<string, string> ReadTags()
        {
            int count = ReadCount();
            if (count == 0)
            {
                return EmptyTags.Instance;
            }

            var result = new Dictionary<string, string>(
                count,
                StringComparer.Ordinal);
            for (var index = 0; index < count; index++)
            {
                result.Add(ReadString(), ReadString());
            }

            return result;
        }

        public void RequireEnd()
        {
            if (offset != payload.Length)
            {
                throw new InvalidDataException(
                    "Stored OSM entity payload contains trailing data.");
            }
        }

        private int ReadCount()
        {
            int value = ReadInt32();
            if (value < 0)
            {
                throw new InvalidDataException(
                    "Stored OSM entity payload contains a negative count.");
            }

            return value;
        }

        private int ReadInt32()
        {
            ReadOnlySpan<byte> value = Take(sizeof(int));
            return BinaryPrimitives.ReadInt32LittleEndian(value);
        }

        private ulong ReadUInt64()
        {
            ReadOnlySpan<byte> value = Take(sizeof(ulong));
            return BinaryPrimitives.ReadUInt64LittleEndian(value);
        }

        private string ReadString()
        {
            int length = ReadCount();
            return Encoding.UTF8.GetString(Take(length));
        }

        private ReadOnlySpan<byte> Take(int length)
        {
            if (length < 0 || length > payload.Length - offset)
            {
                throw new InvalidDataException(
                    "Stored OSM entity payload is truncated.");
            }

            ReadOnlySpan<byte> value = payload.Slice(offset, length);
            offset += length;
            return value;
        }
    }

    private sealed class EmptyTags : IReadOnlyDictionary<string, string>
    {
        public static EmptyTags Instance { get; } = new();

        public int Count => 0;

        public IEnumerable<string> Keys => [];

        public IEnumerable<string> Values => [];

        public string this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() =>
            Enumerable.Empty<KeyValuePair<string, string>>().GetEnumerator();

        public bool TryGetValue(string key, out string value)
        {
            value = string.Empty;
            return false;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();
    }
}
