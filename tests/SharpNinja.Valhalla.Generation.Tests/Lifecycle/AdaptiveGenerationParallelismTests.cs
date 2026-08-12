using SharpNinja.Valhalla.Generation;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Lifecycle;

public sealed class AdaptiveGenerationParallelismTests
{
    [Fact]
    public void FitWorkerCount_ReturnsMinOfFittedAndRequested()
    {
        int fitted = AdaptiveGenerationParallelism.FitWorkerCount(
            remainingMemoryBytes: 3 * 1024 * 1024,
            remainingScratchBytes: 100 * 1024 * 1024,
            perWorkerMemoryBytes: 1024 * 1024,
            perWorkerScratchBytes: 1024 * 1024,
            requestedMaxDegreeOfParallelism: 8);
        Assert.Equal(3, fitted);
    }

    [Fact]
    public void FitWorkerCount_ReturnsRequested_WhenBudgetAllowsMore()
    {
        int fitted = AdaptiveGenerationParallelism.FitWorkerCount(
            remainingMemoryBytes: 64 * 1024 * 1024,
            remainingScratchBytes: 64 * 1024 * 1024,
            perWorkerMemoryBytes: 1024 * 1024,
            perWorkerScratchBytes: 1024 * 1024,
            requestedMaxDegreeOfParallelism: 4);
        Assert.Equal(4, fitted);
    }

    [Fact]
    public void FitWorkerCount_ReturnsZero_WhenBudgetBelowOneWorker()
    {
        int fitted = AdaptiveGenerationParallelism.FitWorkerCount(
            remainingMemoryBytes: 512 * 1024,
            remainingScratchBytes: 64 * 1024 * 1024,
            perWorkerMemoryBytes: 1024 * 1024,
            perWorkerScratchBytes: 1024 * 1024,
            requestedMaxDegreeOfParallelism: 8);
        Assert.Equal(0, fitted);
    }

    [Fact]
    public void FitWorkerCount_UsesBothMemoryAndScratchLimits()
    {
        int fitted = AdaptiveGenerationParallelism.FitWorkerCount(
            remainingMemoryBytes: 16 * 1024 * 1024,
            remainingScratchBytes: 2 * 1024 * 1024,
            perWorkerMemoryBytes: 1024 * 1024,
            perWorkerScratchBytes: 1024 * 1024,
            requestedMaxDegreeOfParallelism: 32);
        Assert.Equal(2, fitted);
    }

    [Fact]
    public void FitWorkerCount_RejectsNonPositiveInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AdaptiveGenerationParallelism.FitWorkerCount(
                remainingMemoryBytes: 1,
                remainingScratchBytes: 1,
                perWorkerMemoryBytes: 0,
                perWorkerScratchBytes: 1,
                requestedMaxDegreeOfParallelism: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AdaptiveGenerationParallelism.FitWorkerCount(
                remainingMemoryBytes: 1,
                remainingScratchBytes: 1,
                perWorkerMemoryBytes: 1,
                perWorkerScratchBytes: 1,
                requestedMaxDegreeOfParallelism: 0));
    }
}
