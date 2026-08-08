using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SharpNinja.Valhalla.Generation.Storage;

public sealed record ExternalSequenceSortOptions(
    string WorkingDirectory,
    string SortName,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int MaxMergeFanIn = 32);

public sealed record ExternalSequenceSortReceipt(
    int InitialRunCount,
    int MergePassCount,
    long PeakMemoryBytes,
    long ScratchHighWaterMarkBytes,
    IntermediateSequenceManifest OutputManifest);

public sealed class ExternalSequenceSortResult<T> : IDisposable
    where T : unmanaged
{
    public ExternalSequenceSortResult(
        IIntermediateSequenceStore<T> output,
        ExternalSequenceSortReceipt receipt)
    {
        Output = output;
        Receipt = receipt;
    }

    public IIntermediateSequenceStore<T> Output { get; }

    public ExternalSequenceSortReceipt Receipt { get; }

    public void Dispose() => Output.Dispose();
}

public static class ExternalSequenceSorter
{
    public static async ValueTask<ExternalSequenceSortResult<T>> SortAsync<T>(
        IIntermediateSequenceStore<T> input,
        IntermediateSequenceStoreOptions outputOptions,
        ExternalSequenceSortOptions sortOptions,
        Comparison<T> comparison,
        CancellationToken cancellationToken = default)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(outputOptions);
        ArgumentNullException.ThrowIfNull(sortOptions);
        ArgumentNullException.ThrowIfNull(comparison);
        ValidateOptions(sortOptions);
        if (!input.State.IsComplete)
        {
            throw new InvalidOperationException(
                "External sorting requires a completed immutable input sequence.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var valueSize = Unsafe.SizeOf<T>();
        var stableRecordSize = Unsafe.SizeOf<StableRecord<T>>();
        if (sortOptions.MemoryBudgetBytes < stableRecordSize)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"External sort memory budget of {sortOptions.MemoryBudgetBytes} bytes cannot hold one record.");
        }

        var recordCount = input.State.RecordCount;
        var runBytes = checked(recordCount * stableRecordSize);
        var outputBytes = checked(recordCount * valueSize);
        var worstCaseScratch = checked((2 * runBytes) + outputBytes);
        if (worstCaseScratch > sortOptions.ScratchDiskBudgetBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"External sort scratch budget of {sortOptions.ScratchDiskBudgetBytes} bytes is below the conservative {worstCaseScratch}-byte merge bound.");
        }

        var sortDirectory = PrepareSortDirectory(sortOptions);
        var scratch = new ScratchTracker(sortOptions.ScratchDiskBudgetBytes);
        var comparer = new StableRecordComparer<T>(comparison);
        var maximumManagedRunRecords = Math.Max(
            1,
            Array.MaxLength / stableRecordSize);
        var recordsPerRun = checked((int)Math.Min(
            maximumManagedRunRecords,
            sortOptions.MemoryBudgetBytes / stableRecordSize));
        var peakMemoryBytes = checked((long)recordsPerRun * stableRecordSize);
        var activeRuns = new List<RunDescriptor>();
        IntermediateSequenceStore<T>? output = null;
        try
        {
            var nextRunOrdinal = 0;
            for (long start = 0; start < recordCount; start += recordsPerRun)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = checked((int)Math.Min(recordsPerRun, recordCount - start));
                var records = new StableRecord<T>[count];
                for (var index = 0; index < count; index++)
                {
                    if ((index & 0x3FFF) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var ordinal = checked(start + index);
                    records[index] = new StableRecord<T>(
                        input.Read(ordinal),
                        ordinal);
                }

                Array.Sort(records, comparer);
                activeRuns.Add(
                    await WriteRunAsync(
                            sortDirectory,
                            nextRunOrdinal++,
                            records,
                            scratch,
                            cancellationToken)
                        .ConfigureAwait(false));
            }

            var initialRunCount = activeRuns.Count;
            var mergePassCount = 0;
            while (activeRuns.Count > sortOptions.MaxMergeFanIn)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var mergedRuns = new List<RunDescriptor>(
                    (activeRuns.Count + sortOptions.MaxMergeFanIn - 1) /
                    sortOptions.MaxMergeFanIn);
                try
                {
                    for (var index = 0; index < activeRuns.Count; index += sortOptions.MaxMergeFanIn)
                    {
                        var count = Math.Min(
                            sortOptions.MaxMergeFanIn,
                            activeRuns.Count - index);
                        var group = activeRuns.GetRange(index, count);
                        var merged = await MergeGroupToRunAsync(
                                sortDirectory,
                                nextRunOrdinal++,
                                group,
                                comparer,
                                scratch,
                                cancellationToken)
                            .ConfigureAwait(false);
                        mergedRuns.Add(merged);
                        DeleteRuns(group, scratch);
                    }
                }
                catch
                {
                    DeleteRuns(mergedRuns, scratch);
                    throw;
                }

                activeRuns = mergedRuns;
                mergePassCount++;
            }

            output = new IntermediateSequenceStore<T>(outputOptions);
            if (activeRuns.Count > 0)
            {
                await MergeRunsToOutputAsync(
                        activeRuns,
                        output,
                        comparer,
                        scratch,
                        sortOptions.ScratchDiskBudgetBytes,
                        cancellationToken)
                    .ConfigureAwait(false);
                mergePassCount++;
            }

            var outputManifest = await output.CompleteAsync(cancellationToken)
                .ConfigureAwait(false);
            var totalScratchAtOutput = checked(
                scratch.CurrentBytes + output.State.CurrentScratchBytes);
            scratch.Observe(totalScratchAtOutput);
            DeleteRuns(activeRuns, scratch);
            activeRuns.Clear();

            var receipt = new ExternalSequenceSortReceipt(
                initialRunCount,
                mergePassCount,
                recordCount == 0 ? 0 : peakMemoryBytes,
                scratch.HighWaterMarkBytes,
                outputManifest);
            var result = new ExternalSequenceSortResult<T>(output, receipt);
            output = null;
            return result;
        }
        catch
        {
            output?.Dispose();
            DeleteRuns(activeRuns, scratch);
            throw;
        }
    }

    private static async ValueTask<RunDescriptor> WriteRunAsync<T>(
        string sortDirectory,
        int ordinal,
        StableRecord<T>[] records,
        ScratchTracker scratch,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        var byteLength = checked((long)records.Length * Unsafe.SizeOf<StableRecord<T>>());
        scratch.Reserve(byteLength);
        var path = Path.Combine(sortDirectory, $"{ordinal:D8}.run");
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            const int cancellationStride = 16 * 1024;
            for (var index = 0; index < records.Length; index += cancellationStride)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(cancellationStride, records.Length - index);
                stream.Write(
                    MemoryMarshal.AsBytes(records.AsSpan(index, count)));
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return new RunDescriptor(path, records.Length, byteLength);
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            scratch.Release(byteLength);
            throw;
        }
    }

    private static async ValueTask<RunDescriptor> MergeGroupToRunAsync<T>(
        string sortDirectory,
        int ordinal,
        IReadOnlyList<RunDescriptor> runs,
        IComparer<StableRecord<T>> comparer,
        ScratchTracker scratch,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        var totalRecords = runs.Sum(static run => run.RecordCount);
        var byteLength = checked(totalRecords * Unsafe.SizeOf<StableRecord<T>>());
        scratch.Reserve(byteLength);
        var path = Path.Combine(sortDirectory, $"{ordinal:D8}.run");
        var readers = runs.Select(static run => new RunReader<T>(run)).ToArray();
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            var queue = CreateQueue(readers, comparer);
            long written = 0;
            while (queue.TryDequeue(out var reader, out var record))
            {
                if ((written & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                WriteRecord(stream, record);
                written++;
                if (reader.MoveNext())
                {
                    queue.Enqueue(reader, reader.Current);
                }
            }

            if (written != totalRecords)
            {
                throw new InvalidDataException(
                    "External merge did not emit the expected record count.");
            }

            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
            return new RunDescriptor(path, totalRecords, byteLength);
        }
        catch
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            scratch.Release(byteLength);
            throw;
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static ValueTask MergeRunsToOutputAsync<T>(
        IReadOnlyList<RunDescriptor> runs,
        IntermediateSequenceStore<T> output,
        IComparer<StableRecord<T>> comparer,
        ScratchTracker scratch,
        long scratchBudgetBytes,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        var readers = runs.Select(static run => new RunReader<T>(run)).ToArray();
        try
        {
            var queue = CreateQueue(readers, comparer);
            long written = 0;
            while (queue.TryDequeue(out var reader, out var record))
            {
                if ((written & 0x3FFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                output.Append(record.Value);
                scratch.Observe(checked(
                    scratch.CurrentBytes + output.State.CurrentScratchBytes));
                if (scratch.HighWaterMarkBytes > scratchBudgetBytes)
                {
                    throw new ValhallaGenerationResourceLimitException(
                        $"External sort scratch budget of {scratchBudgetBytes} bytes would be exceeded.");
                }

                written++;
                if (reader.MoveNext())
                {
                    queue.Enqueue(reader, reader.Current);
                }
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }
    }

    private static PriorityQueue<RunReader<T>, StableRecord<T>> CreateQueue<T>(
        IReadOnlyList<RunReader<T>> readers,
        IComparer<StableRecord<T>> comparer)
        where T : unmanaged
    {
        var queue = new PriorityQueue<RunReader<T>, StableRecord<T>>(comparer);
        foreach (var reader in readers)
        {
            if (reader.MoveNext())
            {
                queue.Enqueue(reader, reader.Current);
            }
        }

        return queue;
    }

    private static void WriteRecord<T>(
        FileStream stream,
        StableRecord<T> record)
        where T : unmanaged
    {
        Span<StableRecord<T>> value = stackalloc StableRecord<T>[1];
        value[0] = record;
        stream.Write(MemoryMarshal.AsBytes(value));
    }

    private static void DeleteRuns(
        IEnumerable<RunDescriptor> runs,
        ScratchTracker scratch)
    {
        foreach (var run in runs)
        {
            if (File.Exists(run.Path))
            {
                File.Delete(run.Path);
                scratch.Release(run.ByteLength);
            }
        }
    }

    private static string PrepareSortDirectory(ExternalSequenceSortOptions options)
    {
        var directory = Path.GetFullPath(
            Path.Combine(options.WorkingDirectory, options.SortName));
        if (Directory.Exists(directory) &&
            Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new InvalidOperationException(
                $"External sort directory '{directory}' is not empty.");
        }

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void ValidateOptions(ExternalSequenceSortOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SortName);
        if (!string.Equals(
                options.SortName,
                Path.GetFileName(options.SortName),
                StringComparison.Ordinal) ||
            options.SortName is "." or "..")
        {
            throw new ArgumentException(
                "The sort name must be a single safe path segment.",
                nameof(options));
        }

        if (options.MemoryBudgetBytes <= 0 ||
            options.ScratchDiskBudgetBytes <= 0 ||
            options.MaxMergeFanIn < 2)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "Memory and scratch bounds must be positive and merge fan-in must be at least two.");
        }
    }

    private readonly record struct StableRecord<T>(T Value, long Ordinal)
        where T : unmanaged;

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

    private sealed record RunDescriptor(
        string Path,
        long RecordCount,
        long ByteLength);

    private sealed class RunReader<T> : IDisposable
        where T : unmanaged
    {
        private static readonly int RecordSize = Unsafe.SizeOf<StableRecord<T>>();
        private readonly SafeFileHandle handle;
        private readonly long recordCount;
        private long index;

        public RunReader(RunDescriptor descriptor)
        {
            handle = File.OpenHandle(
                descriptor.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                FileOptions.RandomAccess);
            recordCount = descriptor.RecordCount;
        }

        public StableRecord<T> Current { get; private set; }

        public bool MoveNext()
        {
            if (index >= recordCount)
            {
                return false;
            }

            Span<StableRecord<T>> value = stackalloc StableRecord<T>[1];
            var read = RandomAccess.Read(
                handle,
                MemoryMarshal.AsBytes(value),
                checked(index * RecordSize));
            if (read != RecordSize)
            {
                throw new EndOfStreamException(
                    "External sort run is truncated.");
            }

            Current = value[0];
            index++;
            return true;
        }

        public void Dispose() => handle.Dispose();
    }

    private sealed class ScratchTracker
    {
        private readonly long budgetBytes;

        public ScratchTracker(long budgetBytes)
        {
            this.budgetBytes = budgetBytes;
        }

        public long CurrentBytes { get; private set; }

        public long HighWaterMarkBytes { get; private set; }

        public void Reserve(long bytes)
        {
            if (bytes < 0 || bytes > budgetBytes - CurrentBytes)
            {
                throw new ValhallaGenerationResourceLimitException(
                    $"External sort scratch budget of {budgetBytes} bytes would be exceeded.");
            }

            CurrentBytes += bytes;
            Observe(CurrentBytes);
        }

        public void Release(long bytes)
        {
            if (bytes < 0 || bytes > CurrentBytes)
            {
                throw new InvalidOperationException(
                    "External sort scratch accounting became inconsistent.");
            }

            CurrentBytes -= bytes;
        }

        public void Observe(long bytes)
        {
            if (bytes > budgetBytes)
            {
                throw new ValhallaGenerationResourceLimitException(
                    $"External sort scratch budget of {budgetBytes} bytes would be exceeded.");
            }

            HighWaterMarkBytes = Math.Max(HighWaterMarkBytes, bytes);
        }
    }
}
