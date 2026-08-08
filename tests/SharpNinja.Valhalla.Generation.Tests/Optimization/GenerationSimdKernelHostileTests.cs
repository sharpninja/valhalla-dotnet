using SharpNinja.Valhalla.Generation.Optimization;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Optimization;

public sealed class GenerationSimdKernelHostileTests
{
    [Fact]
    public void DestinationShorterThanSource_FailsClosed()
    {
        Assert.Throws<ArgumentException>(
            () => GenerationSimdKernels.BulkXor(
                new byte[17],
                new byte[16],
                0xff));
    }

    [Fact]
    public void PartiallyOverlappingSpans_FailClosed()
    {
        var buffer = new byte[64];

        Assert.Throws<ArgumentException>(
            () => GenerationSimdKernels.BulkXor(
                buffer.AsSpan(0, 32),
                buffer.AsSpan(1, 32),
                0x5a));
    }

    [Fact]
    public void EmptyBoundsReduction_FailsClosed()
    {
        Assert.Throws<ArgumentException>(
            () => GenerationSimdKernels.ReduceBounds([]));
    }

    [Fact]
    public void InvalidPackedKeyShift_FailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GenerationSimdKernels.ExtractPackedKeys(
                [1UL],
                new ulong[1],
                64,
                ulong.MaxValue));
    }

    [Fact]
    public void InvalidRequestedPath_FailsClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GenerationSimdKernels.ResolvePath(
                (GenerationSimdPath)int.MaxValue,
                GenerationSimdCapabilities.Current));
    }

    [Theory]
    [InlineData(2, 5)]
    [InlineData(5, 2)]
    public void IncompleteBenchmarkSampleSet_FailsClosed(
        int kernelSamples,
        int measuredRuns)
    {
        var kernel = new GenerationKernelBenchmarkReceipt(
            "packed-key-extraction",
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(75),
            kernelSamples);
        var endToEnd = new GenerationEndToEndBenchmarkReceipt(
            "Nashville",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(9.8),
            measuredRuns);

        Assert.Throws<ArgumentException>(
            () => GenerationOptimizationGuard.Evaluate(kernel, endToEnd));
    }
}
