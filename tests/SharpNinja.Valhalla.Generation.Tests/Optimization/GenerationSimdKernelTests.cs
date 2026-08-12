using SharpNinja.Valhalla.Generation.Optimization;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Optimization;

public sealed class GenerationSimdKernelTests
{
    [Fact]
    public void AllAcceleratedKernels_MatchScalarReference()
    {
        GenerationSimdCapabilities capabilities = GenerationSimdCapabilities.Current;
        GenerationSimdPath[] paths = SupportedPaths(capabilities);
        var random = new Random(77129);

        foreach (int length in new[] { 0, 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 1024 })
        {
            var source = new byte[length];
            random.NextBytes(source);
            foreach (byte mask in new byte[] { 0, 1, 0x5a, 0xff })
            {
                var expected = new byte[length];
                GenerationSimdKernels.BulkXor(
                    source,
                    expected,
                    mask,
                    GenerationSimdPath.Scalar,
                    capabilities);
                foreach (GenerationSimdPath path in paths)
                {
                    var actual = new byte[length];
                    GenerationSimdExecutionReceipt receipt = GenerationSimdKernels.BulkXor(
                        source,
                        actual,
                        mask,
                        path,
                        capabilities);
                    Assert.Equal(expected, actual);
                    Assert.Equal(length, receipt.ElementsProcessed);
                    Assert.Equal(path, receipt.Path);
                }
            }
        }

        var packedSource = new ulong[1025];
        for (int index = 0; index < packedSource.Length; index++)
        {
            packedSource[index] = NextUInt64(random);
        }

        foreach (byte shift in new byte[] { 0, 1, 7, 31, 63 })
        {
            foreach (ulong mask in new[] { 0UL, 1UL, 0xffffUL, ulong.MaxValue })
            {
                var expected = new ulong[packedSource.Length];
                GenerationSimdKernels.ExtractPackedKeys(
                    packedSource,
                    expected,
                    shift,
                    mask,
                    GenerationSimdPath.Scalar,
                    capabilities);
                foreach (GenerationSimdPath path in paths)
                {
                    var actual = new ulong[packedSource.Length];
                    GenerationSimdExecutionReceipt receipt =
                        GenerationSimdKernels.ExtractPackedKeys(
                            packedSource,
                            actual,
                            shift,
                            mask,
                            path,
                            capabilities);
                    Assert.Equal(expected, actual);
                    Assert.Equal(packedSource.Length, receipt.ElementsProcessed);
                    Assert.Equal(path, receipt.Path);
                }
            }
        }

        int[] boundsSource =
        [
            int.MinValue,
            -1,
            0,
            1,
            int.MaxValue,
            .. Enumerable.Range(0, 1027).Select(_ => random.Next()),
        ];
        GenerationBoundsReductionResult expectedBounds =
            GenerationSimdKernels.ReduceBounds(
                boundsSource,
                GenerationSimdPath.Scalar,
                capabilities);
        foreach (GenerationSimdPath path in paths)
        {
            GenerationBoundsReductionResult actual = GenerationSimdKernels.ReduceBounds(
                boundsSource,
                path,
                capabilities);
            Assert.Equal(expectedBounds.Minimum, actual.Minimum);
            Assert.Equal(expectedBounds.Maximum, actual.Maximum);
            Assert.Equal(path, actual.Path);
        }
    }

    [Fact]
    public void UnsupportedInstructionSet_UsesScalarFallback()
    {
        var unsupported = new GenerationSimdCapabilities(false, false);
        var source = Enumerable.Range(0, 97).Select(static value => (byte)value).ToArray();
        var expected = new byte[source.Length];
        GenerationSimdKernels.BulkXor(
            source,
            expected,
            0x5a,
            GenerationSimdPath.Scalar,
            unsupported);

        foreach (GenerationSimdPath requested in
                 new[] { GenerationSimdPath.Avx2, GenerationSimdPath.Arm64AdvSimd })
        {
            var actual = new byte[source.Length];
            GenerationSimdExecutionReceipt receipt = GenerationSimdKernels.BulkXor(
                source,
                actual,
                0x5a,
                requested,
                unsupported);
            Assert.Equal(expected, actual);
            Assert.Equal(GenerationSimdPath.Scalar, receipt.Path);
            Assert.Equal(
                GenerationSimdPath.Scalar,
                GenerationSimdKernels.ResolvePath(requested, unsupported));
        }
    }

    private static GenerationSimdPath[] SupportedPaths(
        GenerationSimdCapabilities capabilities)
    {
        var paths = new List<GenerationSimdPath> { GenerationSimdPath.Scalar };
        if (capabilities.Avx2)
        {
            paths.Add(GenerationSimdPath.Avx2);
        }

        if (capabilities.Arm64AdvSimd)
        {
            paths.Add(GenerationSimdPath.Arm64AdvSimd);
        }

        return [.. paths];
    }

    private static ulong NextUInt64(Random random)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        random.NextBytes(bytes);
        return BitConverter.ToUInt64(bytes);
    }
}

public sealed class GenerationOptimizationGuardTests
{
    [Fact]
    public void SimdKernel_RequiresMeasuredEndToEndBenefit()
    {
        var beneficialKernel = new GenerationKernelBenchmarkReceipt(
            "packed-key-extraction",
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(75));
        var neutralNashville = new GenerationEndToEndBenchmarkReceipt(
            "Nashville",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(9.8));

        GenerationOptimizationDecision accepted = GenerationOptimizationGuard.Evaluate(
            beneficialKernel,
            neutralNashville);
        Assert.True(accepted.Retain);
        Assert.True(accepted.KernelImprovement >= 0.05);
        Assert.True(accepted.EndToEndChange <= 0);

        GenerationOptimizationDecision slowKernel = GenerationOptimizationGuard.Evaluate(
            beneficialKernel with
            {
                AcceleratedMedian = TimeSpan.FromMilliseconds(99),
            },
            neutralNashville);
        Assert.False(slowKernel.Retain);

        GenerationOptimizationDecision regressedNashville =
            GenerationOptimizationGuard.Evaluate(
                beneficialKernel,
                neutralNashville with
                {
                    CandidateMedian = TimeSpan.FromMinutes(10.1),
                });
        Assert.False(regressedNashville.Retain);
    }

    [Fact]
    public void InvalidBenchmarkReceipt_FailsClosed()
    {
        var invalidKernel = new GenerationKernelBenchmarkReceipt(
            string.Empty,
            TimeSpan.Zero,
            TimeSpan.FromMilliseconds(1));
        var endToEnd = new GenerationEndToEndBenchmarkReceipt(
            "Nashville",
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));

        Assert.Throws<ArgumentException>(
            () => GenerationOptimizationGuard.Evaluate(invalidKernel, endToEnd));
    }
}
