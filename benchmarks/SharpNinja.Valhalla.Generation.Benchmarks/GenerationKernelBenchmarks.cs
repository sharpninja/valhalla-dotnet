using BenchmarkDotNet.Attributes;
using SharpNinja.Valhalla.Generation.Optimization;

namespace SharpNinja.Valhalla.Generation.Benchmarks;

[MemoryDiagnoser]
public class GenerationKernelBenchmarks
{
    private byte[] source = null!;
    private byte[] destination = null!;
    private ulong[] packedKeys = null!;
    private ulong[] extractedKeys = null!;
    private int[] coordinates = null!;

    [Params(4_096, 1_048_576)]
    public int ElementCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var random = new Random(1_983);
        source = new byte[ElementCount];
        destination = new byte[ElementCount];
        random.NextBytes(source);

        packedKeys = new ulong[ElementCount];
        extractedKeys = new ulong[ElementCount];
        coordinates = new int[ElementCount];
        for (var index = 0; index < ElementCount; index++)
        {
            packedKeys[index] = unchecked((ulong)random.NextInt64());
            coordinates[index] = random.Next(int.MinValue, int.MaxValue);
        }
    }

    [Benchmark(Baseline = true)]
    public GenerationSimdExecutionReceipt BulkXorScalar() =>
        GenerationSimdKernels.BulkXor(
            source,
            destination,
            0x5a,
            GenerationSimdPath.Scalar);

    [Benchmark]
    public GenerationSimdExecutionReceipt BulkXorAccelerated() =>
        GenerationSimdKernels.BulkXor(
            source,
            destination,
            0x5a,
            PreferredAcceleratedPath);

    [Benchmark]
    public GenerationSimdExecutionReceipt ExtractPackedKeysScalar() =>
        GenerationSimdKernels.ExtractPackedKeys(
            packedKeys,
            extractedKeys,
            17,
            0x00ff_ffff,
            GenerationSimdPath.Scalar);

    [Benchmark]
    public GenerationSimdExecutionReceipt ExtractPackedKeysAccelerated() =>
        GenerationSimdKernels.ExtractPackedKeys(
            packedKeys,
            extractedKeys,
            17,
            0x00ff_ffff,
            PreferredAcceleratedPath);

    [Benchmark]
    public GenerationBoundsReductionResult ReduceBoundsScalar() =>
        GenerationSimdKernels.ReduceBounds(
            coordinates,
            GenerationSimdPath.Scalar);

    [Benchmark]
    public GenerationBoundsReductionResult ReduceBoundsAccelerated() =>
        GenerationSimdKernels.ReduceBounds(
            coordinates,
            PreferredAcceleratedPath);

    private static GenerationSimdPath PreferredAcceleratedPath =>
        GenerationSimdCapabilities.Current.Avx2
            ? GenerationSimdPath.Avx2
            : GenerationSimdCapabilities.Current.Arm64AdvSimd
                ? GenerationSimdPath.Arm64AdvSimd
                : GenerationSimdPath.Scalar;
}
