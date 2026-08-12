using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpNinja.Valhalla.Generation.Storage;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record DurableFrontierEdgeSinkOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int ShapeBufferSizeBytes = 64 * 1024,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed record DurableFrontierEdgeStoreReceipt(
    IntermediateBlobManifest ShapeManifest,
    IntermediateSequenceManifest EdgeManifest,
    long PeakMemoryBytes,
    long ScratchHighWaterMarkBytes);

internal sealed class DurableFrontierEdgeSink :
    IFrontierEdgeSink,
    IFrontierEdgeSource,
    IDisposable
{
    private readonly IntermediateBlobStore shapes;
    private readonly IntermediateSequenceStore<GenerationEdgeRecord> edges;
    private readonly int shapeBufferSizeBytes;
    private ShapeWriter? activeWriter;
    private long lastEdgeRecordId = -1;
    private bool complete;
    private bool disposed;

    long IFrontierEdgeSource.EdgeCount => EdgeCount;

    GenerationEdgeRecord IFrontierEdgeSource.ReadEdge(long ordinal) =>
        ReadEdge(ordinal);

    internal DurableFrontierEdgeSink(DurableFrontierEdgeSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ShapeBufferSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);

        int nodeRecordSize = Unsafe.SizeOf<GenerationNodeRecord>();
        int edgeRecordSize = Unsafe.SizeOf<GenerationEdgeRecord>();
        shapeBufferSizeBytes = Math.Max(
            nodeRecordSize,
            options.ShapeBufferSizeBytes / nodeRecordSize * nodeRecordSize);
        long availableStoreMemory = options.MemoryBudgetBytes - shapeBufferSizeBytes;
        if (availableStoreMemory < nodeRecordSize + edgeRecordSize)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The frontier edge-store budget cannot fit its shape writer and stores.");
        }

        long shapeMemory = Math.Max(nodeRecordSize, availableStoreMemory / 2);
        long edgeMemory = Math.Max(
            edgeRecordSize,
            availableStoreMemory - shapeMemory);
        long shapeScratch = Math.Max(
            nodeRecordSize,
            options.ScratchDiskBudgetBytes / 2);
        long edgeScratch = Math.Max(
            edgeRecordSize,
            options.ScratchDiskBudgetBytes - shapeScratch);
        string root = Path.Combine(options.WorkingDirectory, "frontier-edge-store");
        Directory.CreateDirectory(root);

        shapes = new IntermediateBlobStore(new IntermediateBlobStoreOptions(
            root,
            "shapes",
            options.StorageMode,
            shapeMemory,
            shapeScratch,
            options.SegmentSizeBytes,
            ReadPageSizeBytes: checked((int)Math.Min(64 * 1024L, shapeMemory)),
            MaxCachedPages: 1));
        edges = new IntermediateSequenceStore<GenerationEdgeRecord>(
            new IntermediateSequenceStoreOptions(
                root,
                "edges",
                options.StorageMode,
                edgeMemory,
                edgeScratch,
                options.SegmentSizeBytes));
    }

    internal long CurrentMemoryBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                shapes.State.CurrentMemoryBytes +
                edges.State.CurrentMemoryBytes);
        }
    }

    internal long CurrentScratchBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                shapes.State.CurrentScratchBytes +
                edges.State.CurrentScratchBytes);
        }
    }

    internal long PeakMemoryBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                shapes.State.PeakMemoryBytes +
                edges.State.PeakMemoryBytes);
        }
    }

    internal long EdgeCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return edges.State.RecordCount;
        }
    }

    public IFrontierShapeWriter BeginShape(long wayId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (complete)
        {
            throw new InvalidOperationException("The frontier edge store is complete.");
        }

        if (activeWriter is not null)
        {
            throw new InvalidOperationException(
                "A frontier edge store supports one worker-owned shape writer.");
        }

        activeWriter = new ShapeWriter(
            this,
            shapes,
            wayId,
            shapeBufferSizeBytes);
        return activeWriter;
    }

    public void PersistEdge(GenerationEdgeRecord edge)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (complete)
        {
            throw new InvalidOperationException("The frontier edge store is complete.");
        }

        if (activeWriter is not null)
        {
            throw new InvalidOperationException(
                "The active shape must complete before its edge is persisted.");
        }

        if (edge.EdgeRecordId < 0 ||
            (edges.State.RecordCount != 0 && edge.EdgeRecordId <= lastEdgeRecordId))
        {
            throw new InvalidDataException(
                "Durable frontier edges must have unique increasing record identities.");
        }

        edges.Append(edge);
        lastEdgeRecordId = edge.EdgeRecordId;
    }

    internal async ValueTask<DurableFrontierEdgeStoreReceipt> CompleteAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (activeWriter is not null)
        {
            throw new InvalidOperationException(
                "The active shape must complete before the store is sealed.");
        }

        IntermediateBlobManifest shapeManifest =
            await shapes.CompleteAsync(cancellationToken).ConfigureAwait(false);
        IntermediateSequenceManifest edgeManifest =
            await edges.CompleteAsync(cancellationToken).ConfigureAwait(false);
        complete = true;
        return new DurableFrontierEdgeStoreReceipt(
            shapeManifest,
            edgeManifest,
            checked(shapes.State.PeakMemoryBytes + edges.State.PeakMemoryBytes),
            checked(
                shapes.State.ScratchHighWaterMarkBytes +
                edges.State.ScratchHighWaterMarkBytes));
    }

    internal GenerationEdgeRecord ReadEdge(long ordinal)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return edges.Read(ordinal);
    }

    internal bool TryReadEdgeByRecordId(
        long edgeRecordId,
        out GenerationEdgeRecord edge)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        long low = 0;
        long high = edges.State.RecordCount;
        while (low < high)
        {
            long middle = low + ((high - low) / 2);
            GenerationEdgeRecord candidate = edges.Read(middle);
            if (candidate.EdgeRecordId < edgeRecordId)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low < edges.State.RecordCount)
        {
            GenerationEdgeRecord candidate = edges.Read(low);
            if (candidate.EdgeRecordId == edgeRecordId)
            {
                edge = candidate;
                return true;
            }
        }

        edge = default;
        return false;
    }

    internal GenerationNodeRecord[] ReadShape(EdgeShapeReference shape)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        int recordSize = Unsafe.SizeOf<GenerationNodeRecord>();
        if (shape.PointCount < 0 ||
            shape.ByteLength != checked(shape.PointCount * recordSize))
        {
            throw new InvalidDataException("The edge shape reference is invalid.");
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>(shape.ByteLength);
        shapes.ReadRange(shape.Offset, bytes);
        var result = GC.AllocateUninitializedArray<GenerationNodeRecord>(
            shape.PointCount);
        MemoryMarshal.Cast<byte, GenerationNodeRecord>(bytes).CopyTo(result);
        return result;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        activeWriter?.Dispose();
        activeWriter = null;
        edges.Dispose();
        shapes.Dispose();
        disposed = true;
    }

    private void ReleaseWriter(ShapeWriter writer)
    {
        if (!ReferenceEquals(activeWriter, writer))
        {
            throw new InvalidOperationException(
                "The shape writer is not owned by this frontier edge store.");
        }

        activeWriter = null;
    }

    private sealed class ShapeWriter : IFrontierShapeWriter
    {
        private readonly DurableFrontierEdgeSink owner;
        private readonly IntermediateBlobStore store;
        private readonly long firstOffset;
        private byte[] buffer;
        private int bufferedBytes;
        private int pointCount;
        private bool completed;
        private bool disposed;

        internal ShapeWriter(
            DurableFrontierEdgeSink owner,
            IntermediateBlobStore store,
            long wayId,
            int bufferSizeBytes)
        {
            this.owner = owner;
            this.store = store;
            WayId = wayId;
            firstOffset = store.State.ByteLength;
            buffer = ArrayPool<byte>.Shared.Rent(bufferSizeBytes);
            BufferLength = bufferSizeBytes;
        }

        internal long WayId { get; }

        internal int BufferLength { get; }

        public void Append(in GenerationNodeRecord node)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed)
            {
                throw new InvalidOperationException("The shape is already complete.");
            }

            int recordSize = Unsafe.SizeOf<GenerationNodeRecord>();
            if (BufferLength - bufferedBytes < recordSize)
            {
                Flush();
            }

            MemoryMarshal.Write(
                buffer.AsSpan(bufferedBytes, recordSize),
                in node);
            bufferedBytes += recordSize;
            pointCount = checked(pointCount + 1);
        }

        public EdgeShapeReference Complete()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed)
            {
                throw new InvalidOperationException("The shape is already complete.");
            }

            Flush();
            completed = true;
            owner.ReleaseWriter(this);
            return new EdgeShapeReference(
                firstOffset,
                pointCount,
                checked(pointCount * Unsafe.SizeOf<GenerationNodeRecord>()));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            if (!completed)
            {
                owner.ReleaseWriter(this);
            }

            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            buffer = [];
            bufferedBytes = 0;
            disposed = true;
        }

        private void Flush()
        {
            if (bufferedBytes == 0)
            {
                return;
            }

            store.Append(buffer.AsSpan(0, bufferedBytes));
            bufferedBytes = 0;
        }
    }
}
