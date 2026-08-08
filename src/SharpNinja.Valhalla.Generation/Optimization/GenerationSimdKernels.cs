using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace SharpNinja.Valhalla.Generation.Optimization;

public enum GenerationSimdPath
{
    Scalar,
    Avx2,
    Arm64AdvSimd,
}

public sealed record GenerationSimdCapabilities(
    bool Avx2,
    bool Arm64AdvSimd)
{
    public static GenerationSimdCapabilities Current { get; } = new(
        System.Runtime.Intrinsics.X86.Avx2.IsSupported,
        AdvSimd.Arm64.IsSupported);
}

public sealed record GenerationSimdExecutionReceipt(
    GenerationSimdPath Path,
    int ElementsProcessed);

public sealed record GenerationBoundsReductionResult(
    int Minimum,
    int Maximum,
    GenerationSimdPath Path);

public static class GenerationSimdKernels
{
    public static GenerationSimdExecutionReceipt BulkXor(
        ReadOnlySpan<byte> source,
        Span<byte> destination,
        byte mask,
        GenerationSimdPath requestedPath = GenerationSimdPath.Scalar,
        GenerationSimdCapabilities? capabilities = null)
    {
        ValidateDestination(source, destination);
        GenerationSimdPath path = ResolvePath(
            requestedPath,
            capabilities ?? GenerationSimdCapabilities.Current);
        int index = 0;
        if (path == GenerationSimdPath.Avx2)
        {
            ref byte sourceReference = ref MemoryMarshal.GetReference(source);
            ref byte destinationReference = ref MemoryMarshal.GetReference(destination);
            Vector256<byte> maskVector = Vector256.Create(mask);
            for (; index <= source.Length - Vector256<byte>.Count;
                 index += Vector256<byte>.Count)
            {
                Vector256<byte> value = Vector256.LoadUnsafe(
                    ref sourceReference,
                    (nuint)index);
                Avx2.Xor(value, maskVector).StoreUnsafe(
                    ref destinationReference,
                    (nuint)index);
            }
        }
        else if (path == GenerationSimdPath.Arm64AdvSimd)
        {
            ref byte sourceReference = ref MemoryMarshal.GetReference(source);
            ref byte destinationReference = ref MemoryMarshal.GetReference(destination);
            Vector128<byte> maskVector = Vector128.Create(mask);
            for (; index <= source.Length - Vector128<byte>.Count;
                 index += Vector128<byte>.Count)
            {
                Vector128<byte> value = Vector128.LoadUnsafe(
                    ref sourceReference,
                    (nuint)index);
                AdvSimd.Xor(value, maskVector).StoreUnsafe(
                    ref destinationReference,
                    (nuint)index);
            }
        }

        for (; index < source.Length; index++)
        {
            destination[index] = (byte)(source[index] ^ mask);
        }

        return new GenerationSimdExecutionReceipt(path, source.Length);
    }

    public static GenerationSimdExecutionReceipt ExtractPackedKeys(
        ReadOnlySpan<ulong> source,
        Span<ulong> destination,
        byte shift,
        ulong mask,
        GenerationSimdPath requestedPath = GenerationSimdPath.Scalar,
        GenerationSimdCapabilities? capabilities = null)
    {
        ValidateDestination(source, destination);
        if (shift > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(shift));
        }

        GenerationSimdPath path = ResolvePath(
            requestedPath,
            capabilities ?? GenerationSimdCapabilities.Current);
        int index = 0;
        if (path == GenerationSimdPath.Avx2)
        {
            ref ulong sourceReference = ref MemoryMarshal.GetReference(source);
            ref ulong destinationReference = ref MemoryMarshal.GetReference(destination);
            Vector256<ulong> maskVector = Vector256.Create(mask);
            for (; index <= source.Length - Vector256<ulong>.Count;
                 index += Vector256<ulong>.Count)
            {
                Vector256<ulong> value = Vector256.LoadUnsafe(
                    ref sourceReference,
                    (nuint)index);
                Vector256<ulong> extracted = Avx2.And(
                    value >>> shift,
                    maskVector);
                extracted.StoreUnsafe(ref destinationReference, (nuint)index);
            }
        }
        else if (path == GenerationSimdPath.Arm64AdvSimd)
        {
            ref ulong sourceReference = ref MemoryMarshal.GetReference(source);
            ref ulong destinationReference = ref MemoryMarshal.GetReference(destination);
            Vector128<ulong> maskVector = Vector128.Create(mask);
            for (; index <= source.Length - Vector128<ulong>.Count;
                 index += Vector128<ulong>.Count)
            {
                Vector128<ulong> value = Vector128.LoadUnsafe(
                    ref sourceReference,
                    (nuint)index);
                Vector128<ulong> shifted = value >>> shift;
                Vector128<ulong> extracted = AdvSimd.And(
                    shifted.AsByte(),
                    maskVector.AsByte()).AsUInt64();
                extracted.StoreUnsafe(ref destinationReference, (nuint)index);
            }
        }

        for (; index < source.Length; index++)
        {
            destination[index] = (source[index] >> shift) & mask;
        }

        return new GenerationSimdExecutionReceipt(path, source.Length);
    }

    public static GenerationBoundsReductionResult ReduceBounds(
        ReadOnlySpan<int> values,
        GenerationSimdPath requestedPath = GenerationSimdPath.Scalar,
        GenerationSimdCapabilities? capabilities = null)
    {
        if (values.IsEmpty)
        {
            throw new ArgumentException("At least one value is required.", nameof(values));
        }

        GenerationSimdPath path = ResolvePath(
            requestedPath,
            capabilities ?? GenerationSimdCapabilities.Current);
        int minimum = int.MaxValue;
        int maximum = int.MinValue;
        int index = 0;
        if (path == GenerationSimdPath.Avx2 &&
            values.Length >= Vector256<int>.Count)
        {
            ref int sourceReference = ref MemoryMarshal.GetReference(values);
            Vector256<int> minimumVector = Vector256.LoadUnsafe(ref sourceReference);
            Vector256<int> maximumVector = minimumVector;
            index = Vector256<int>.Count;
            for (; index <= values.Length - Vector256<int>.Count;
                 index += Vector256<int>.Count)
            {
                Vector256<int> value = Vector256.LoadUnsafe(
                    ref sourceReference,
                    (nuint)index);
                minimumVector = Avx2.Min(minimumVector, value);
                maximumVector = Avx2.Max(maximumVector, value);
            }

            for (int lane = 0; lane < Vector256<int>.Count; lane++)
            {
                minimum = Math.Min(minimum, minimumVector.GetElement(lane));
                maximum = Math.Max(maximum, maximumVector.GetElement(lane));
            }
        }
        else if (path == GenerationSimdPath.Arm64AdvSimd &&
                 values.Length >= Vector128<int>.Count)
        {
            ref int sourceReference = ref MemoryMarshal.GetReference(values);
            Vector128<int> minimumVector = Vector128.LoadUnsafe(ref sourceReference);
            Vector128<int> maximumVector = minimumVector;
            index = Vector128<int>.Count;
            for (; index <= values.Length - Vector128<int>.Count;
                 index += Vector128<int>.Count)
            {
                Vector128<int> value = Vector128.LoadUnsafe(
                    ref sourceReference,
                    (nuint)index);
                minimumVector = AdvSimd.Min(minimumVector, value);
                maximumVector = AdvSimd.Max(maximumVector, value);
            }

            for (int lane = 0; lane < Vector128<int>.Count; lane++)
            {
                minimum = Math.Min(minimum, minimumVector.GetElement(lane));
                maximum = Math.Max(maximum, maximumVector.GetElement(lane));
            }
        }

        for (; index < values.Length; index++)
        {
            minimum = Math.Min(minimum, values[index]);
            maximum = Math.Max(maximum, values[index]);
        }

        return new GenerationBoundsReductionResult(minimum, maximum, path);
    }

    public static GenerationSimdPath ResolvePath(
        GenerationSimdPath requestedPath,
        GenerationSimdCapabilities capabilities)
    {
        ArgumentNullException.ThrowIfNull(capabilities);
        if (!Enum.IsDefined(requestedPath))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedPath));
        }

        return requestedPath switch
        {
            GenerationSimdPath.Avx2
                when capabilities.Avx2 && Avx2.IsSupported =>
                GenerationSimdPath.Avx2,
            GenerationSimdPath.Arm64AdvSimd
                when capabilities.Arm64AdvSimd && AdvSimd.Arm64.IsSupported =>
                GenerationSimdPath.Arm64AdvSimd,
            _ => GenerationSimdPath.Scalar,
        };
    }

    private static void ValidateDestination<T>(
        ReadOnlySpan<T> source,
        Span<T> destination)
    {
        if (destination.Length < source.Length)
        {
            throw new ArgumentException(
                "The destination is shorter than the source.",
                nameof(destination));
        }

        if (source.Overlaps(destination, out int elementOffset) && elementOffset != 0)
        {
            throw new ArgumentException(
                "Partially overlapping source and destination spans are not supported.",
                nameof(destination));
        }
    }
}

public sealed record GenerationKernelBenchmarkReceipt(
    string Kernel,
    TimeSpan ScalarMedian,
    TimeSpan AcceleratedMedian,
    int SampleCount = 5);

public sealed record GenerationEndToEndBenchmarkReceipt(
    string Workload,
    TimeSpan BaselineMedian,
    TimeSpan CandidateMedian,
    int MeasuredRuns = 5);

public sealed record GenerationOptimizationDecision(
    bool Retain,
    double KernelImprovement,
    double EndToEndChange,
    string Reason);

public static class GenerationOptimizationGuard
{
    public static GenerationOptimizationDecision Evaluate(
        GenerationKernelBenchmarkReceipt kernel,
        GenerationEndToEndBenchmarkReceipt endToEnd,
        double minimumKernelImprovement = 0.05,
        double maximumEndToEndRegression = 0)
    {
        ValidateReceipt(kernel, endToEnd);
        if (!double.IsFinite(minimumKernelImprovement) ||
            minimumKernelImprovement < 0 ||
            minimumKernelImprovement >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumKernelImprovement));
        }

        if (!double.IsFinite(maximumEndToEndRegression) ||
            maximumEndToEndRegression < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumEndToEndRegression));
        }

        double kernelImprovement =
            1 - (kernel.AcceleratedMedian.TotalSeconds / kernel.ScalarMedian.TotalSeconds);
        double endToEndChange =
            (endToEnd.CandidateMedian.TotalSeconds / endToEnd.BaselineMedian.TotalSeconds) - 1;
        bool kernelPassed = kernelImprovement >= minimumKernelImprovement;
        bool endToEndPassed = endToEndChange <= maximumEndToEndRegression;
        string reason = (kernelPassed, endToEndPassed) switch
        {
            (true, true) => "Kernel and end-to-end qualification passed.",
            (false, _) => "Kernel improvement did not meet the minimum threshold.",
            (_, false) => "End-to-end workload exceeded the allowed regression.",
        };
        return new GenerationOptimizationDecision(
            kernelPassed && endToEndPassed,
            kernelImprovement,
            endToEndChange,
            reason);
    }

    private static void ValidateReceipt(
        GenerationKernelBenchmarkReceipt kernel,
        GenerationEndToEndBenchmarkReceipt endToEnd)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        ArgumentNullException.ThrowIfNull(endToEnd);
        if (string.IsNullOrWhiteSpace(kernel.Kernel) ||
            kernel.ScalarMedian <= TimeSpan.Zero ||
            kernel.AcceleratedMedian <= TimeSpan.Zero ||
            kernel.SampleCount < 3)
        {
            throw new ArgumentException(
                "The kernel benchmark receipt is incomplete or invalid.",
                nameof(kernel));
        }

        if (string.IsNullOrWhiteSpace(endToEnd.Workload) ||
            endToEnd.BaselineMedian <= TimeSpan.Zero ||
            endToEnd.CandidateMedian <= TimeSpan.Zero ||
            endToEnd.MeasuredRuns < 3)
        {
            throw new ArgumentException(
                "The end-to-end benchmark receipt is incomplete or invalid.",
                nameof(endToEnd));
        }
    }
}
