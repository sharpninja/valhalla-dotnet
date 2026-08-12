using SharpNinja.Valhalla.Generation.Parallel;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Parallel;

public sealed class ParallelGenerationHostileTests
{
    [Fact]
    public async Task WorkerFailure_CancelsBoundedPipelineWithoutHanging()
    {
        var scheduler = CreateScheduler();
        var inputs = Enumerable.Range(0, 1024).ToArray();

        var operation = scheduler.MapAsync(
            inputs,
            static _ => 2,
            static (_, _) =>
                ValueTask.FromException<int>(
                    new InvalidOperationException("worker failure")),
            TestContext.Current.CancellationToken).AsTask();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => operation.WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken));
        Assert.Equal("worker failure", exception.Message);
    }

    [Fact]
    public async Task CallerCancellation_DrainsBoundedPipelineWithoutHanging()
    {
        var scheduler = CreateScheduler();
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);

        var operation = scheduler.MapAsync(
            Enumerable.Range(0, 1024).ToArray(),
            static _ => 2,
            async (_, cancellationToken) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            },
            cancellation.Token).AsTask();

        await started.Task.WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => operation.WaitAsync(
                TimeSpan.FromSeconds(3),
                TestContext.Current.CancellationToken));
    }

    private static DeterministicGenerationScheduler CreateScheduler() =>
        new(
            new GenerationParallelExecutionOptions(
                MaxDegreeOfParallelism: 4,
                MemoryBudgetBytes: 8,
                QueueCapacity: 2));
}
