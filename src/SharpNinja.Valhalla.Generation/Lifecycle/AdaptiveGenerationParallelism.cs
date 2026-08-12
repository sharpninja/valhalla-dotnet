namespace SharpNinja.Valhalla.Generation;

/// <summary>
/// Pure fit-to-budget helper for generation worker counts. Does not queue work;
/// a zero result means the caller must fail closed rather than oversubscribe.
/// </summary>
public static class AdaptiveGenerationParallelism
{
    /// <summary>
    /// Returns the largest worker count that fits both memory and scratch budgets,
    /// capped by <paramref name="requestedMaxDegreeOfParallelism"/>.
    /// Returns 0 when fewer than one worker can be reserved.
    /// </summary>
    public static int FitWorkerCount(
        long remainingMemoryBytes,
        long remainingScratchBytes,
        long perWorkerMemoryBytes,
        long perWorkerScratchBytes,
        int requestedMaxDegreeOfParallelism)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(remainingMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(remainingScratchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perWorkerMemoryBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(perWorkerScratchBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestedMaxDegreeOfParallelism);

        int memoryFit = remainingMemoryBytes >= perWorkerMemoryBytes
            ? (int)Math.Min(
                int.MaxValue,
                remainingMemoryBytes / perWorkerMemoryBytes)
            : 0;
        int scratchFit = remainingScratchBytes >= perWorkerScratchBytes
            ? (int)Math.Min(
                int.MaxValue,
                remainingScratchBytes / perWorkerScratchBytes)
            : 0;
        int fitted = Math.Min(memoryFit, scratchFit);
        if (fitted <= 0)
        {
            return 0;
        }

        return Math.Min(fitted, requestedMaxDegreeOfParallelism);
    }
}
