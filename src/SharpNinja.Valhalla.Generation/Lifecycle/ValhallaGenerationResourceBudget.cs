namespace SharpNinja.Valhalla.Generation;

public sealed class ValhallaGenerationResourceBudget : IValhallaGenerationResourceBudget, IDisposable
{
    private readonly object reservationGate = new();
    private readonly SemaphoreSlim workers;
    private long reservedMemoryBytes;
    private long reservedScratchDiskBytes;
    private int activeWorkers;
    private int peakWorkerCount;
    private bool disposed;

    public ValhallaGenerationResourceBudget(
        long memoryBudgetBytes,
        long scratchDiskBudgetBytes,
        int maxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(memoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDegreeOfParallelism);

        MemoryBudgetBytes = memoryBudgetBytes;
        ScratchDiskBudgetBytes = scratchDiskBudgetBytes;
        MaxDegreeOfParallelism = maxDegreeOfParallelism;
        workers = new SemaphoreSlim(maxDegreeOfParallelism, maxDegreeOfParallelism);
    }

    public long MemoryBudgetBytes { get; }

    public long ScratchDiskBudgetBytes { get; }

    public int MaxDegreeOfParallelism { get; }

    public int PeakWorkerCount => Volatile.Read(ref peakWorkerCount);

    public IDisposable ReserveMemory(long bytes) =>
        Reserve(bytes, MemoryBudgetBytes, isMemory: true);

    public IDisposable ReserveScratchDisk(long bytes) =>
        Reserve(bytes, ScratchDiskBudgetBytes, isMemory: false);

    public async ValueTask<IAsyncDisposable> AcquireWorkerAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (!await workers.WaitAsync(TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            throw new ValhallaGenerationResourceLimitException(
                $"The configured worker limit of {MaxDegreeOfParallelism} is fully admitted; work is rejected rather than queued.");
        }

        var current = Interlocked.Increment(ref activeWorkers);
        UpdatePeakWorkerCount(current);
        return new WorkerLease(this);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        workers.Dispose();
    }

    private IDisposable Reserve(long bytes, long limit, bool isMemory)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);

        lock (reservationGate)
        {
            ref var reserved = ref isMemory
                ? ref reservedMemoryBytes
                : ref reservedScratchDiskBytes;
            if (bytes > limit - reserved)
            {
                var resource = isMemory ? "memory" : "scratch-disk";
                throw new ValhallaGenerationResourceLimitException(
                    $"The {resource} reservation of {bytes} bytes exceeds the configured {limit}-byte budget with {reserved} bytes already reserved.");
            }

            reserved += bytes;
        }

        return new ReservationLease(this, bytes, isMemory);
    }

    private void Release(long bytes, bool isMemory)
    {
        lock (reservationGate)
        {
            ref var reserved = ref isMemory
                ? ref reservedMemoryBytes
                : ref reservedScratchDiskBytes;
            reserved -= bytes;
        }
    }

    private void ReleaseWorker()
    {
        Interlocked.Decrement(ref activeWorkers);
        workers.Release();
    }

    private void UpdatePeakWorkerCount(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref peakWorkerCount);
            if (candidate <= current ||
                Interlocked.CompareExchange(ref peakWorkerCount, candidate, current) == current)
            {
                return;
            }
        }
    }

    private sealed class ReservationLease(
        ValhallaGenerationResourceBudget owner,
        long bytes,
        bool isMemory) : IDisposable
    {
        private ValhallaGenerationResourceBudget? currentOwner = owner;

        public void Dispose()
        {
            Interlocked.Exchange(ref currentOwner, null)?.Release(bytes, isMemory);
        }
    }

    private sealed class WorkerLease(ValhallaGenerationResourceBudget owner) : IAsyncDisposable
    {
        private ValhallaGenerationResourceBudget? currentOwner = owner;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref currentOwner, null)?.ReleaseWorker();
            return ValueTask.CompletedTask;
        }
    }
}

public sealed class ValhallaGenerationResourceLimitException : InvalidOperationException
{
    public ValhallaGenerationResourceLimitException(string message)
        : base(message)
    {
    }
}
