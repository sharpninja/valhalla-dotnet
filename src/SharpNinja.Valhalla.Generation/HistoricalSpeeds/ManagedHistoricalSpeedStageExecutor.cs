namespace SharpNinja.Valhalla.Generation.HistoricalSpeeds;

/// <summary>
/// Lifecycle adapter for the Valhalla 3.8.3 ApplyPredictedSpeeds generation stage.
/// </summary>
public sealed class ManagedHistoricalSpeedStageExecutor : IValhallaGenerationStageExecutor
{
    private readonly IHistoricalSpeedDatasetBuilder builder;

    public ManagedHistoricalSpeedStageExecutor(
        IHistoricalSpeedDatasetBuilder builder)
    {
        this.builder = builder ?? throw new ArgumentNullException(nameof(builder));
    }

    public ValhallaGenerationStage Stage =>
        ValhallaGenerationStage.ApplyPredictedSpeeds;

    public async ValueTask<ValhallaGenerationStageResult> ExecuteAsync(
        ValhallaGenerationStageContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        string? historicalSpeedPath =
            context.Request.Inputs.HistoricalSpeedDataPath;
        if (string.IsNullOrWhiteSpace(historicalSpeedPath))
        {
            return ValhallaGenerationStageResult.Empty(
                "historical-speeds-skipped");
        }

        try
        {
            HistoricalSpeedDatasetBuildResult result = await builder.BuildAsync(
                    new HistoricalSpeedDatasetBuildRequest(
                        context.StagingDirectory,
                        historicalSpeedPath,
                        context.Request.Options.MaxDegreeOfParallelism,
                        context.Request.Options.MemoryBudgetBytes,
                        context.Request.Options.ScratchDiskBudgetBytes,
                        context.Request.Options.DeterministicOutput),
                    cancellationToken)
                .ConfigureAwait(false);
            return new ValhallaGenerationStageResult(
                result.OutputTreeSha256,
                result.UpdatedEdgeCount,
                result.BytesRead,
                result.BytesWritten,
                0,
                0,
                result.ScratchDiskHighWaterBytes,
                [],
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["graph-tree"] = result.OutputTreeSha256,
                });
        }
        catch (HistoricalSpeedDatasetBuildException exception)
        {
            return new ValhallaGenerationStageResult(
                "historical-speeds-failed",
                0,
                0,
                0,
                0,
                0,
                0,
                [],
                [
                    new ValhallaGenerationFailure(
                        MapFailureCode(exception.FailureCode),
                        exception.Message,
                        Stage),
                ],
                new Dictionary<string, string>(StringComparer.Ordinal));
        }
    }

    private static ValhallaGenerationFailureCode MapFailureCode(
        HistoricalSpeedDatasetFailureCode code) =>
        code switch
        {
            HistoricalSpeedDatasetFailureCode.InvalidConfiguration =>
                ValhallaGenerationFailureCode.Configuration,
            HistoricalSpeedDatasetFailureCode.MemoryBudgetExceeded
                or HistoricalSpeedDatasetFailureCode.ScratchDiskBudgetExceeded =>
                ValhallaGenerationFailureCode.ResourceExhaustion,
            HistoricalSpeedDatasetFailureCode.GraphTileReadFailed
                or HistoricalSpeedDatasetFailureCode.GraphTileWriteFailed =>
                ValhallaGenerationFailureCode.InputOutput,
            _ => ValhallaGenerationFailureCode.InvalidInput,
        };
}
