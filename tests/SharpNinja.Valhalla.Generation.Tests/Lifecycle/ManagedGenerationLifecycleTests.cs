using System.Diagnostics;
using System.Text.Json;
using SharpNinja.Valhalla.Mjolnir;
using SharpNinja.Valhalla.Osm;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Lifecycle;

public sealed class ManagedGenerationLifecycleTests
{
    [Fact]
    public async Task BuildAsync_ReportsOrderedStagesAndReceipts()
    {
        using var workspace = new LifecycleTestWorkspace();
        var progress = new List<ValhallaGenerationBuildProgress>();
        var builder = new ValhallaGenerationBuilder(
        [
            new RecordingStageExecutor(ValhallaGenerationStage.BuildEdges),
            new RecordingStageExecutor(ValhallaGenerationStage.IngestOsm),
        ],
            new TestGenerationValidator());
        var request = workspace.CreateRequest();

        var result = await builder.BuildAsync(
            request,
            new InlineProgress<ValhallaGenerationBuildProgress>(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Null(result.Failure);
        Assert.NotNull(result.Manifest);
        Assert.Equal(
        [
            ValhallaGenerationStage.ValidateRequest,
            ValhallaGenerationStage.IngestOsm,
            ValhallaGenerationStage.BuildEdges,
            ValhallaGenerationStage.ValidateGraph,
            ValhallaGenerationStage.Publish,
        ],
            result.StageReceipts.Select(receipt => receipt.Stage));
        Assert.Equal(result.StageReceipts.Select(receipt => receipt.Stage), progress.Select(item => item.Stage));
        Assert.All(result.StageReceipts, receipt =>
        {
            Assert.True(receipt.EndedAtUtc >= receipt.StartedAtUtc);
            Assert.False(string.IsNullOrWhiteSpace(receipt.InputIdentity));
            Assert.False(string.IsNullOrWhiteSpace(receipt.OutputIdentity));
            Assert.False(string.IsNullOrWhiteSpace(receipt.CheckpointIdentity));
            Assert.True(receipt.MaximumConcurrency >= 1);
            Assert.NotNull(receipt.Warnings);
            Assert.NotNull(receipt.Failures);
            Assert.NotNull(receipt.OutputHashes);
        });
    }

    [Fact]
    public async Task CancellationAtEveryStage_StopsWithinBoundedLatency()
    {
        var longRunningStages = Enum.GetValues<ValhallaGenerationStage>()
            .Where(stage => stage is not ValhallaGenerationStage.ValidateRequest)
            .ToArray();

        foreach (var stage in longRunningStages)
        {
            using var workspace = new LifecycleTestWorkspace();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.Current.CancellationToken);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var progress = new InlineProgress<ValhallaGenerationBuildProgress>(item =>
            {
                if (stage == ValhallaGenerationStage.Publish &&
                    item.Stage == ValhallaGenerationStage.Publish)
                {
                    cancellation.Cancel();
                }
            });
            IValhallaGenerationValidator validator = stage == ValhallaGenerationStage.ValidateGraph
                ? new BlockingGenerationValidator(entered)
                : new TestGenerationValidator();
            var executors = stage is ValhallaGenerationStage.ValidateGraph or ValhallaGenerationStage.Publish
                ? new IValhallaGenerationStageExecutor[]
                {
                    new RecordingStageExecutor(ValhallaGenerationStage.IngestOsm),
                }
                :
                [
                    new BlockingStageExecutor(stage, entered),
                ];
            var builder = new ValhallaGenerationBuilder(executors, validator);
            var stopwatch = Stopwatch.StartNew();
            var buildTask = builder.BuildAsync(
                workspace.CreateRequest(),
                progress,
                cancellation.Token).AsTask();

            if (stage != ValhallaGenerationStage.Publish)
            {
                await entered.Task.WaitAsync(
                    TimeSpan.FromSeconds(2),
                    TestContext.Current.CancellationToken);
                cancellation.Cancel();
            }

            var result = await buildTask.WaitAsync(
                TimeSpan.FromSeconds(2),
                TestContext.Current.CancellationToken);
            stopwatch.Stop();

            Assert.False(result.Success);
            Assert.NotNull(result.Failure);
            Assert.Equal(ValhallaGenerationFailureCode.Canceled, result.Failure.Code);
            Assert.Equal(stage, result.Failure.Stage);
            Assert.True(
                stopwatch.Elapsed < TimeSpan.FromSeconds(2),
                $"{stage} cancellation took {stopwatch.Elapsed}.");
        }
    }
}

public sealed class ManagedGenerationPublicationTests
{
    [Fact]
    public async Task FailedBuild_DoesNotReplacePublishedGeneration()
    {
        using var workspace = new LifecycleTestWorkspace();
        Directory.CreateDirectory(workspace.OutputDirectory);
        var activePath = Path.Combine(workspace.OutputDirectory, "active-generation.json");
        const string existingPointer = "{\"generationId\":\"known-good\"}";
        await File.WriteAllTextAsync(
            activePath,
            existingPointer,
            TestContext.Current.CancellationToken);
        var builder = new ValhallaGenerationBuilder(
        [
            new FailingStageExecutor(ValhallaGenerationStage.BuildEdges),
        ],
            new TestGenerationValidator());

        var result = await builder.BuildAsync(
            workspace.CreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(
            existingPointer,
            await File.ReadAllTextAsync(activePath, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(workspace.OutputDirectory),
            path => Path.GetFileName(path).StartsWith(".incoming-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidBuild_IsAtomicallyPromoted()
    {
        using var workspace = new LifecycleTestWorkspace();
        var builder = new ValhallaGenerationBuilder(
        [
            new RecordingStageExecutor(ValhallaGenerationStage.IngestOsm),
            new RecordingStageExecutor(ValhallaGenerationStage.BuildEdges),
        ],
            new TestGenerationValidator());

        var result = await builder.BuildAsync(
            workspace.CreateRequest(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(result.Manifest);
        Assert.NotNull(result.PublishedDirectory);
        Assert.True(Directory.Exists(result.PublishedDirectory));
        Assert.True(File.Exists(Path.Combine(result.PublishedDirectory, "generation-manifest.json")));
        Assert.True(File.Exists(Path.Combine(result.PublishedDirectory, "BuildEdges.dat")));
        var activePath = Path.Combine(workspace.OutputDirectory, "active-generation.json");
        using var activeDocument = JsonDocument.Parse(
            await File.ReadAllTextAsync(activePath, TestContext.Current.CancellationToken));
        Assert.Equal(
            result.Manifest.GenerationId,
            activeDocument.RootElement.GetProperty("generationId").GetString());
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(workspace.OutputDirectory),
            path => Path.GetFileName(path).StartsWith(".incoming-", StringComparison.Ordinal));
    }
}

public sealed class ManagedGenerationCheckpointTests
{
    [Fact]
    public async Task CompatibleCheckpoint_ResumesWithoutRepeatingCompletedStages()
    {
        using var workspace = new LifecycleTestWorkspace();
        var firstStage = new RecordingStageExecutor(ValhallaGenerationStage.IngestOsm);
        var finalStage = new SwitchableStageExecutor(ValhallaGenerationStage.BuildEdges);
        var request = workspace.CreateRequest(ResumePolicy.RequireCompatible);
        var firstBuilder = new ValhallaGenerationBuilder(
        [
            firstStage,
            finalStage,
        ],
            new TestGenerationValidator());

        var firstResult = await firstBuilder.BuildAsync(
            request,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(firstResult.Success);
        Assert.Equal(1, firstStage.CallCount);

        finalStage.Fail = false;
        var secondBuilder = new ValhallaGenerationBuilder(
        [
            firstStage,
            finalStage,
        ],
            new TestGenerationValidator());

        var resumedResult = await secondBuilder.BuildAsync(
            request,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(resumedResult.Success);
        Assert.Equal(1, firstStage.CallCount);
        Assert.Equal(2, finalStage.CallCount);
        Assert.Contains(
            resumedResult.StageReceipts,
            receipt => receipt.Stage == ValhallaGenerationStage.IngestOsm);
    }

    [Fact]
    public async Task IncompatibleCheckpoint_FailsClosed()
    {
        using var workspace = new LifecycleTestWorkspace();
        var request = workspace.CreateRequest(ResumePolicy.RequireCompatible);
        var firstBuilder = new ValhallaGenerationBuilder(
        [
            new RecordingStageExecutor(ValhallaGenerationStage.IngestOsm),
            new FailingStageExecutor(ValhallaGenerationStage.BuildEdges),
        ],
            new TestGenerationValidator());
        var firstResult = await firstBuilder.BuildAsync(
            request,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(firstResult.Success);

        var changedOptions = request.Options with { DatasetId = request.Options.DatasetId + 1 };
        var incompatibleBuilder = new ValhallaGenerationBuilder(
        [
            new RecordingStageExecutor(ValhallaGenerationStage.IngestOsm),
        ],
            new TestGenerationValidator());

        var incompatibleResult = await incompatibleBuilder.BuildAsync(
            request with { Options = changedOptions },
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(incompatibleResult.Success);
        Assert.NotNull(incompatibleResult.Failure);
        Assert.Equal(
            ValhallaGenerationFailureCode.IncompatibleCheckpoint,
            incompatibleResult.Failure.Code);
    }
}

public sealed class ManagedGenerationResourceLimitTests
{
    [Fact]
    public async Task ConfiguredLimits_AreStrictlyEnforced()
    {
        using var budget = new ValhallaGenerationResourceBudget(100, 200, 1);
        using var memory = budget.ReserveMemory(100);
        using var scratch = budget.ReserveScratchDisk(200);
        Assert.Throws<ValhallaGenerationResourceLimitException>(() => budget.ReserveMemory(1));
        Assert.Throws<ValhallaGenerationResourceLimitException>(() => budget.ReserveScratchDisk(1));

        await using var worker = await budget.AcquireWorkerAsync(
            TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ValhallaGenerationResourceLimitException>(async () =>
        {
            await using var unexpected = await budget.AcquireWorkerAsync(
                TestContext.Current.CancellationToken);
        });

        using var workspace = new LifecycleTestWorkspace();
        var builder = new ValhallaGenerationBuilder(
        [
            new ResourceExhaustingStageExecutor(),
        ],
            new TestGenerationValidator());
        var result = await builder.BuildAsync(
            workspace.CreateRequest(memoryBudgetBytes: 100),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.NotNull(result.Failure);
        Assert.Equal(ValhallaGenerationFailureCode.ResourceExhaustion, result.Failure.Code);
    }
}

public sealed class LegacyTileSetBuilderCompatibilityTests
{
    [Fact]
    public void ExistingCallers_RemainSourceCompatible()
    {
        ITileSetBuilder builder = new LegacyTileSetBuilderStub();

        Assert.True(builder.BuildTiles("fixture.osm.pbf", "tiles", CancellationToken.None));
        var method = typeof(ITileSetBuilder).GetMethod(nameof(ITileSetBuilder.BuildTiles));
        Assert.NotNull(method);
        Assert.Equal(typeof(bool), method.ReturnType);
        Assert.Equal(
        [
            typeof(string),
            typeof(string),
            typeof(CancellationToken),
        ],
            method.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.NotNull(
            typeof(TileBuilder).GetMethod(
                nameof(TileBuilder.BuildTileSet),
                [typeof(IReadOnlyList<string>), typeof(string), typeof(TileBuilderConfig)]));
    }

    private sealed class LegacyTileSetBuilderStub : ITileSetBuilder
    {
        public bool BuildTiles(
            string pbfPath,
            string tileDirectory,
            CancellationToken cancellationToken = default) =>
            pbfPath.Length > 0 && tileDirectory.Length > 0 && !cancellationToken.IsCancellationRequested;
    }
}

internal sealed class LifecycleTestWorkspace : IDisposable
{
    public LifecycleTestWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "valhalla-generation-tests", Guid.NewGuid().ToString("N"));
        WorkingDirectory = Path.Combine(Root, "work");
        OutputDirectory = Path.Combine(Root, "output");
        Directory.CreateDirectory(Root);
        InputPath = Path.Combine(Root, "input.osm.pbf");
        File.WriteAllBytes(InputPath, [1, 2, 3, 4]);
    }

    public string Root { get; }

    public string WorkingDirectory { get; }

    public string OutputDirectory { get; }

    public string InputPath { get; }

    public ValhallaGenerationBuildRequest CreateRequest(
        ResumePolicy resumePolicy = ResumePolicy.Disabled,
        long memoryBudgetBytes = 1024) =>
        new(
            [InputPath],
            ValhallaGenerationInputSet.Empty,
            WorkingDirectory,
            OutputDirectory,
            new ValhallaGenerationBuildOptions(
                ValhallaGenerationProfile.RoadOnly,
                IntermediateStorageMode.Auto,
                resumePolicy,
                2,
                memoryBudgetBytes,
                2048,
                42,
                84,
                true));

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
{
    public void Report(T value) => callback(value);
}

internal sealed class RecordingStageExecutor(ValhallaGenerationStage stage) :
    IValhallaGenerationStageExecutor
{
    public ValhallaGenerationStage Stage { get; } = stage;

    public int CallCount { get; private set; }

    public async ValueTask<ValhallaGenerationStageResult> ExecuteAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        CallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(context.StagingDirectory);
        var path = Path.Combine(context.StagingDirectory, $"{Stage}.dat");
        await File.WriteAllTextAsync(
            path,
            Stage.ToString(),
            cancellationToken);
        return new ValhallaGenerationStageResult(
            Stage.ToString(),
            1,
            2,
            new FileInfo(path).Length,
            4,
            5,
            6,
            [],
            [],
            new Dictionary<string, string>
            {
                [Stage.ToString()] = "HASH",
            });
    }
}

internal sealed class BlockingStageExecutor(
    ValhallaGenerationStage stage,
    TaskCompletionSource entered) : IValhallaGenerationStageExecutor
{
    public ValhallaGenerationStage Stage { get; } = stage;

    public async ValueTask<ValhallaGenerationStageResult> ExecuteAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The infinite cancellation delay unexpectedly completed.");
    }
}

internal sealed class FailingStageExecutor(ValhallaGenerationStage stage) :
    IValhallaGenerationStageExecutor
{
    public ValhallaGenerationStage Stage { get; } = stage;

    public ValueTask<ValhallaGenerationStageResult> ExecuteAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new ValhallaGenerationStageResult(
                Stage.ToString(),
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                [new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.Validation,
                    "Expected test failure.",
                    Stage)],
                new Dictionary<string, string>()));
    }
}

internal sealed class SwitchableStageExecutor(ValhallaGenerationStage stage) :
    IValhallaGenerationStageExecutor
{
    public ValhallaGenerationStage Stage { get; } = stage;

    public bool Fail { get; set; } = true;

    public int CallCount { get; private set; }

    public async ValueTask<ValhallaGenerationStageResult> ExecuteAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        CallCount++;
        if (Fail)
        {
            return await new FailingStageExecutor(Stage).ExecuteAsync(context, cancellationToken);
        }

        return await new RecordingStageExecutor(Stage).ExecuteAsync(context, cancellationToken);
    }
}

internal sealed class ResourceExhaustingStageExecutor : IValhallaGenerationStageExecutor
{
    public ValhallaGenerationStage Stage => ValhallaGenerationStage.IngestOsm;

    public ValueTask<ValhallaGenerationStageResult> ExecuteAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var reservation = context.Resources.ReserveMemory(
            context.Request.Options.MemoryBudgetBytes + 1);
        return ValueTask.FromResult(ValhallaGenerationStageResult.Empty("unreachable"));
    }
}

internal sealed class TestGenerationValidator : IValhallaGenerationValidator
{
    public ValueTask<ValhallaGenerationValidationResult> ValidateAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ValhallaGenerationValidationResult.Valid);
    }
}

internal sealed class BlockingGenerationValidator(TaskCompletionSource entered) :
    IValhallaGenerationValidator
{
    public async ValueTask<ValhallaGenerationValidationResult> ValidateAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        entered.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        throw new InvalidOperationException("The infinite cancellation delay unexpectedly completed.");
    }
}
