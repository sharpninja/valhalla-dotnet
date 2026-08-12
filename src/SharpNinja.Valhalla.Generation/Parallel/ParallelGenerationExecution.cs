using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace SharpNinja.Valhalla.Generation.Parallel;

public sealed record ParallelSequenceSortOptions(
    int MaxDegreeOfParallelism,
    long MemoryBudgetBytes);

public sealed record ParallelSequenceSortReceipt(
    int PartitionCount,
    int MaxObservedConcurrency,
    long PeakMemoryBytes);

public static class ParallelSequenceSorter
{
    public static ParallelSequenceSortReceipt Sort<T>(
        T[] values,
        ParallelSequenceSortOptions options,
        Comparison<T> comparison,
        CancellationToken cancellationToken = default)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(comparison);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();
        if (values.Length == 0)
        {
            return new ParallelSequenceSortReceipt(0, 0, 0);
        }

        var partitionCount = Math.Min(
            options.MaxDegreeOfParallelism,
            values.Length);
        var recordSize = Unsafe.SizeOf<StableRecord<T>>();
        var sampleCount = checked(partitionCount * (partitionCount - 1));
        var peakMemoryBytes = checked(
            ((long)values.Length * recordSize * (partitionCount == 1 ? 1 : 2)) +
            ((long)sampleCount * recordSize) +
            ((long)Math.Max(0, partitionCount - 1) * recordSize) +
            ((long)partitionCount * (partitionCount + 1) * sizeof(int)) +
            ((long)(partitionCount + 1) * sizeof(int)));
        if (peakMemoryBytes > options.MemoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Parallel sort requires {peakMemoryBytes} bytes, exceeding the {options.MemoryBudgetBytes}-byte budget.");
        }

        var stable = new StableRecord<T>[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            stable[index] = new StableRecord<T>(values[index], index);
        }

        var comparer = new StableRecordComparer<T>(comparison);
        if (partitionCount == 1)
        {
            Array.Sort(stable, comparer);
            CopyValues(stable, values, cancellationToken);
            return new ParallelSequenceSortReceipt(1, 1, peakMemoryBytes);
        }

        var maximumConcurrency = 0;
        var activeWorkers = 0;
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = partitionCount,
            CancellationToken = cancellationToken,
        };
        global::System.Threading.Tasks.Parallel.For(
            0,
            partitionCount,
            parallelOptions,
            partition =>
            {
                var active = Interlocked.Increment(ref activeWorkers);
                ObserveMaximum(ref maximumConcurrency, active);
                try
                {
                    GetPartitionRange(
                        values.Length,
                        partitionCount,
                        partition,
                        out var start,
                        out var end);
                    Array.Sort(stable, start, end - start, comparer);
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                }
            });

        var samples = new StableRecord<T>[sampleCount];
        var sampleIndex = 0;
        for (var partition = 0; partition < partitionCount; partition++)
        {
            GetPartitionRange(
                values.Length,
                partitionCount,
                partition,
                out var start,
                out var end);
            var length = end - start;
            for (var sample = 1; sample < partitionCount; sample++)
            {
                var offset = Math.Min(
                    length - 1,
                    checked((int)((long)sample * length / partitionCount)));
                samples[sampleIndex++] = stable[start + offset];
            }
        }

        Array.Sort(samples, comparer);
        var splitters = new StableRecord<T>[partitionCount - 1];
        for (var splitter = 1; splitter < partitionCount; splitter++)
        {
            var index = Math.Min(
                samples.Length - 1,
                checked((int)((long)splitter * samples.Length / partitionCount)));
            splitters[splitter - 1] = samples[index];
        }

        var boundaries = new int[partitionCount, partitionCount + 1];
        for (var partition = 0; partition < partitionCount; partition++)
        {
            GetPartitionRange(
                values.Length,
                partitionCount,
                partition,
                out var start,
                out var end);
            boundaries[partition, 0] = start;
            for (var bucket = 1; bucket < partitionCount; bucket++)
            {
                boundaries[partition, bucket] = UpperBound(
                    stable,
                    start,
                    end,
                    splitters[bucket - 1],
                    comparer);
            }

            boundaries[partition, partitionCount] = end;
        }

        var bucketOffsets = new int[partitionCount + 1];
        for (var bucket = 0; bucket < partitionCount; bucket++)
        {
            var length = 0;
            for (var partition = 0; partition < partitionCount; partition++)
            {
                length = checked(
                    length +
                    boundaries[partition, bucket + 1] -
                    boundaries[partition, bucket]);
            }

            bucketOffsets[bucket + 1] = checked(bucketOffsets[bucket] + length);
        }

        var output = new StableRecord<T>[stable.Length];
        global::System.Threading.Tasks.Parallel.For(
            0,
            partitionCount,
            parallelOptions,
            bucket =>
            {
                var active = Interlocked.Increment(ref activeWorkers);
                ObserveMaximum(ref maximumConcurrency, active);
                try
                {
                    MergeBucket(
                        stable,
                        output,
                        bucketOffsets[bucket],
                        bucket,
                        partitionCount,
                        boundaries,
                        comparer,
                        cancellationToken);
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                }
            });

        CopyValues(output, values, cancellationToken);
        return new ParallelSequenceSortReceipt(
            partitionCount,
            maximumConcurrency,
            peakMemoryBytes);
    }

    private static void MergeBucket<T>(
        StableRecord<T>[] source,
        StableRecord<T>[] output,
        int outputOffset,
        int bucket,
        int partitionCount,
        int[,] boundaries,
        IComparer<StableRecord<T>> comparer,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        var queue = new PriorityQueue<MergeCursor, StableRecord<T>>(comparer);
        for (var partition = 0; partition < partitionCount; partition++)
        {
            var start = boundaries[partition, bucket];
            var end = boundaries[partition, bucket + 1];
            if (start < end)
            {
                queue.Enqueue(
                    new MergeCursor(start, end),
                    source[start]);
            }
        }

        var writeIndex = outputOffset;
        while (queue.TryDequeue(out var cursor, out var record))
        {
            if ((writeIndex & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            output[writeIndex++] = record;
            var next = cursor.Index + 1;
            if (next < cursor.End)
            {
                queue.Enqueue(
                    new MergeCursor(next, cursor.End),
                    source[next]);
            }
        }
    }

    private static int UpperBound<T>(
        StableRecord<T>[] values,
        int start,
        int end,
        StableRecord<T> splitter,
        IComparer<StableRecord<T>> comparer)
        where T : unmanaged
    {
        var low = start;
        var high = end;
        while (low < high)
        {
            var middle = low + ((high - low) / 2);
            if (comparer.Compare(values[middle], splitter) <= 0)
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

    private static void CopyValues<T>(
        StableRecord<T>[] source,
        T[] destination,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        for (var index = 0; index < source.Length; index++)
        {
            if ((index & 0x3FFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            destination[index] = source[index].Value;
        }
    }

    private static void GetPartitionRange(
        int itemCount,
        int partitionCount,
        int partition,
        out int start,
        out int end)
    {
        start = checked((int)((long)partition * itemCount / partitionCount));
        end = checked((int)((long)(partition + 1) * itemCount / partitionCount));
    }

    private static void ValidateOptions(ParallelSequenceSortOptions options)
    {
        if (options.MaxDegreeOfParallelism <= 0 ||
            options.MemoryBudgetBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Parallelism and memory budget must be positive.");
        }
    }

    private static void ObserveMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (candidate <= observed ||
                Interlocked.CompareExchange(ref target, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private readonly record struct StableRecord<T>(T Value, long Ordinal)
        where T : unmanaged;

    private readonly record struct MergeCursor(int Index, int End);

    private sealed class StableRecordComparer<T> : IComparer<StableRecord<T>>
        where T : unmanaged
    {
        private readonly Comparison<T> comparison;

        public StableRecordComparer(Comparison<T> comparison)
        {
            this.comparison = comparison;
        }

        public int Compare(StableRecord<T> left, StableRecord<T> right)
        {
            var result = comparison(left.Value, right.Value);
            return result != 0
                ? result
                : left.Ordinal.CompareTo(right.Ordinal);
        }
    }
}

public sealed record GenerationParallelExecutionOptions(
    int MaxDegreeOfParallelism,
    long MemoryBudgetBytes,
    int QueueCapacity);

public sealed record GenerationParallelExecutionReceipt(
    int WorkItemCount,
    int MaxObservedConcurrency,
    long PeakReservedMemoryBytes,
    int QueueCapacity);

public sealed record GenerationParallelMapResult<T>(
    IReadOnlyList<T> Results,
    GenerationParallelExecutionReceipt Receipt);

public sealed class DeterministicGenerationScheduler
{
    private readonly GenerationParallelExecutionOptions options;

    public DeterministicGenerationScheduler(
        GenerationParallelExecutionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxDegreeOfParallelism <= 0 ||
            options.MemoryBudgetBytes <= 0 ||
            options.QueueCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Parallelism, memory budget, and queue capacity must be positive.");
        }

        this.options = options;
    }

    public async ValueTask<GenerationParallelMapResult<TResult>> MapAsync<
        TInput,
        TResult>(
        IReadOnlyList<TInput> inputs,
        Func<TInput, long> memoryEstimator,
        Func<TInput, CancellationToken, ValueTask<TResult>> worker,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(memoryEstimator);
        ArgumentNullException.ThrowIfNull(worker);
        cancellationToken.ThrowIfCancellationRequested();
        for (var index = 0; index < inputs.Count; index++)
        {
            ValidateEstimate(memoryEstimator(inputs[index]));
        }

        if (inputs.Count == 0)
        {
            return new GenerationParallelMapResult<TResult>(
                [],
                new GenerationParallelExecutionReceipt(
                    0,
                    0,
                    0,
                    options.QueueCapacity));
        }

        var channel = Channel.CreateBounded<IndexedInput<TInput>>(
            new BoundedChannelOptions(options.QueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        var results = new TResult[inputs.Count];
        var memoryGate = new WeightedMemoryGate(options.MemoryBudgetBytes);
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var activeWorkers = 0;
        var maximumConcurrency = 0;

        var producer = ProduceAsync(
            inputs,
            channel.Writer,
            linkedCancellation.Token);
        var workerCount = Math.Min(
            options.MaxDegreeOfParallelism,
            inputs.Count);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => ConsumeAsync())
            .ToArray();

        try
        {
            await Task.WhenAll(workers.Prepend(producer)).ConfigureAwait(false);
        }
        catch
        {
            linkedCancellation.Cancel();
            channel.Writer.TryComplete();
            throw;
        }

        return new GenerationParallelMapResult<TResult>(
            results,
            new GenerationParallelExecutionReceipt(
                inputs.Count,
                maximumConcurrency,
                memoryGate.PeakReservedBytes,
                options.QueueCapacity));

        async Task ConsumeAsync()
        {
            await foreach (var item in channel.Reader.ReadAllAsync(
                linkedCancellation.Token))
            {
                var memoryBytes = memoryEstimator(item.Value);
                await using var lease = await memoryGate.AcquireAsync(
                        memoryBytes,
                        linkedCancellation.Token)
                    .ConfigureAwait(false);
                var active = Interlocked.Increment(ref activeWorkers);
                ObserveMaximum(ref maximumConcurrency, active);
                try
                {
                    results[item.Ordinal] = await worker(
                            item.Value,
                            linkedCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch
                {
                    linkedCancellation.Cancel();
                    channel.Writer.TryComplete();
                    throw;
                }
                finally
                {
                    Interlocked.Decrement(ref activeWorkers);
                }
            }
        }
    }

    private static async Task ProduceAsync<TInput>(
        IReadOnlyList<TInput> inputs,
        ChannelWriter<IndexedInput<TInput>> writer,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            for (var index = 0; index < inputs.Count; index++)
            {
                await writer.WriteAsync(
                        new IndexedInput<TInput>(index, inputs[index]),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            writer.TryComplete(failure);
        }
    }

    private void ValidateEstimate(long memoryBytes)
    {
        if (memoryBytes <= 0 || memoryBytes > options.MemoryBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"Work-item memory estimate {memoryBytes} is outside the 1..{options.MemoryBudgetBytes} byte budget.");
        }
    }

    private static void ObserveMaximum(ref int target, int candidate)
    {
        while (true)
        {
            var observed = Volatile.Read(ref target);
            if (candidate <= observed ||
                Interlocked.CompareExchange(ref target, candidate, observed) == observed)
            {
                return;
            }
        }
    }

    private readonly record struct IndexedInput<T>(int Ordinal, T Value);

    internal sealed class WeightedMemoryGate
    {
        private readonly long budgetBytes;
        private readonly object sync = new();
        private readonly Queue<Waiter> waiters = [];
        private long reservedBytes;

        public WeightedMemoryGate(long budgetBytes)
        {
            this.budgetBytes = budgetBytes;
        }

        public long PeakReservedBytes { get; private set; }

        internal ValueTask<MemoryLease> AcquireAsync(
            long bytes,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sync)
            {
                if (waiters.Count == 0 && bytes <= budgetBytes - reservedBytes)
                {
                    Reserve(bytes);
                    return ValueTask.FromResult(new MemoryLease(this, bytes));
                }

                var waiter = new Waiter(bytes);
                waiters.Enqueue(waiter);
                waiter.CancellationRegistration = cancellationToken.Register(
                    static state =>
                    {
                        var registration = (CancellationState)state!;
                        registration.Gate.CancelWaiter(
                            registration.Waiter,
                            registration.CancellationToken);
                    },
                    new CancellationState(
                        this,
                        waiter,
                        cancellationToken));
                return new ValueTask<MemoryLease>(waiter.Source.Task);
            }
        }

        private void CancelWaiter(
            Waiter waiter,
            CancellationToken cancellationToken)
        {
            lock (sync)
            {
                waiter.Source.TrySetCanceled(cancellationToken);
                PulseWaiters();
            }
        }

        private void Release(long bytes)
        {
            lock (sync)
            {
                reservedBytes = checked(reservedBytes - bytes);
                PulseWaiters();
            }
        }

        private void PulseWaiters()
        {
            while (waiters.Count > 0)
            {
                var waiter = waiters.Peek();
                if (waiter.Source.Task.IsCompleted)
                {
                    waiters.Dequeue();
                    waiter.CancellationRegistration.Dispose();
                    continue;
                }

                if (waiter.Bytes > budgetBytes - reservedBytes)
                {
                    return;
                }

                waiters.Dequeue();
                Reserve(waiter.Bytes);
                if (!waiter.Source.TrySetResult(new MemoryLease(this, waiter.Bytes)))
                {
                    reservedBytes -= waiter.Bytes;
                }

                waiter.CancellationRegistration.Dispose();
            }
        }

        private void Reserve(long bytes)
        {
            reservedBytes = checked(reservedBytes + bytes);
            PeakReservedBytes = Math.Max(PeakReservedBytes, reservedBytes);
        }

        internal sealed class MemoryLease : IAsyncDisposable
        {
            private WeightedMemoryGate? owner;
            private readonly long bytes;

            internal MemoryLease(WeightedMemoryGate owner, long bytes)
            {
                this.owner = owner;
                this.bytes = bytes;
            }

            public ValueTask DisposeAsync()
            {
                Interlocked.Exchange(ref owner, null)?.Release(bytes);
                return ValueTask.CompletedTask;
            }
        }

        private sealed class Waiter
        {
            public Waiter(long bytes)
            {
                Bytes = bytes;
            }

            public long Bytes { get; }

            public TaskCompletionSource<MemoryLease> Source { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public CancellationTokenRegistration CancellationRegistration { get; set; }
        }

        private sealed record CancellationState(
            WeightedMemoryGate Gate,
            Waiter Waiter,
            CancellationToken CancellationToken);
    }
}

public sealed class GenerationGlobalIndex<TKey, TValue>
    where TKey : notnull
{
    private Dictionary<TKey, TValue>? mutable = [];
    private FrozenDictionary<TKey, TValue>? frozen;

    public bool IsFrozen => Volatile.Read(ref frozen) is not null;

    public int Count => IsFrozen
        ? frozen!.Count
        : mutable!.Count;

    public void Add(TKey key, TValue value)
    {
        if (IsFrozen)
        {
            throw new InvalidOperationException(
                "A frozen generation global index is immutable.");
        }

        mutable!.Add(key, value);
    }

    public void Freeze()
    {
        if (IsFrozen)
        {
            return;
        }

        var snapshot = mutable!.ToFrozenDictionary();
        Volatile.Write(ref frozen, snapshot);
        mutable = null;
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        var snapshot = Volatile.Read(ref frozen);
        return snapshot is not null
            ? snapshot.TryGetValue(key, out value!)
            : mutable!.TryGetValue(key, out value!);
    }
}

public sealed record GenerationBarrierStageResult<T>(
    IReadOnlyList<T> Results,
    GenerationParallelExecutionReceipt DiscoveryReceipt,
    GenerationParallelExecutionReceipt WriteReceipt);

public sealed class DeterministicGenerationStageExecutor
{
    private readonly DeterministicGenerationScheduler scheduler;

    public DeterministicGenerationStageExecutor(
        DeterministicGenerationScheduler scheduler)
    {
        this.scheduler = scheduler;
    }

    public async ValueTask<GenerationBarrierStageResult<TResult>> ExecuteAsync<
        TKey,
        TValue,
        TItem,
        TDiscovery,
        TResult>(
        GenerationGlobalIndex<TKey, TValue> globalIndex,
        IReadOnlyList<TItem> items,
        Func<TItem, long> discoveryMemoryEstimator,
        Func<TItem, CancellationToken, ValueTask<TDiscovery>> discover,
        Func<TItem, TDiscovery, long> writeMemoryEstimator,
        Func<
            TItem,
            TDiscovery,
            IReadOnlyList<TDiscovery>,
            CancellationToken,
            ValueTask<TResult>> write,
        CancellationToken cancellationToken = default)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(globalIndex);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(discoveryMemoryEstimator);
        ArgumentNullException.ThrowIfNull(discover);
        ArgumentNullException.ThrowIfNull(writeMemoryEstimator);
        ArgumentNullException.ThrowIfNull(write);
        if (!globalIndex.IsFrozen)
        {
            throw new InvalidOperationException(
                "Tile-local construction requires frozen global indexes.");
        }

        var discoveries = await scheduler.MapAsync(
                items,
                discoveryMemoryEstimator,
                discover,
                cancellationToken)
            .ConfigureAwait(false);
        var ordinals = Enumerable.Range(0, items.Count).ToArray();
        var writes = await scheduler.MapAsync(
                ordinals,
                ordinal => writeMemoryEstimator(
                    items[ordinal],
                    discoveries.Results[ordinal]),
                (ordinal, token) => write(
                    items[ordinal],
                    discoveries.Results[ordinal],
                    discoveries.Results,
                    token),
                cancellationToken)
            .ConfigureAwait(false);
        return new GenerationBarrierStageResult<TResult>(
            writes.Results,
            discoveries.Receipt,
            writes.Receipt);
    }
}
