using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SharpNinja.Valhalla.Generation.Differential;

namespace SharpNinja.Valhalla.Generation;

internal static class ValhallaGenerationLifecycleRunner
{
    private const int LifecycleSchemaVersion = 1;
    private const string CheckpointFileName = ".generation-checkpoint.json";
    private const string ManifestFileName = "generation-manifest.json";
    private const string ActivePointerFileName = "active-generation.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async ValueTask<ValhallaGenerationBuildResult> RunAsync(
        ValhallaGenerationBuildRequest request,
        IReadOnlyList<IValhallaGenerationStageExecutor> stages,
        IValhallaGenerationValidator validator,
        IProgress<ValhallaGenerationBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(stages);
        ArgumentNullException.ThrowIfNull(validator);

        var receipts = new List<ValhallaGenerationStageReceipt>();
        var currentStage = ValhallaGenerationStage.ValidateRequest;
        string? requestIdentity = null;

        try
        {
            ValidateRequest(request, stages);
            requestIdentity = await ComputeRequestIdentityAsync(request, cancellationToken)
                .ConfigureAwait(false);
            var orderedStages = stages.OrderBy(stage => stage.Stage).ToArray();
            var totalStageCount = orderedStages.Length + 3;
            ReportProgress(
                progress,
                currentStage,
                receipts.Count,
                totalStageCount,
                "Generation request validated.");
            receipts.Add(CreateSystemReceipt(
                currentStage,
                requestIdentity,
                requestIdentity,
                requestIdentity,
                maximumConcurrency: 1));

            var stagingDirectory = Path.Combine(request.WorkingDirectory, "staging");
            var checkpointPath = Path.Combine(stagingDirectory, CheckpointFileName);
            var checkpoint = await PrepareCheckpointAsync(
                request,
                requestIdentity,
                stagingDirectory,
                checkpointPath,
                cancellationToken).ConfigureAwait(false);
            if (checkpoint.Failure is not null)
            {
                return Failed(receipts, checkpoint.Failure);
            }

            using var resources = new ValhallaGenerationResourceBudget(
                request.Options.MemoryBudgetBytes,
                request.Options.ScratchDiskBudgetBytes,
                request.Options.MaxDegreeOfParallelism);
            var context = new ValhallaGenerationStageContext(
                request,
                requestIdentity,
                stagingDirectory,
                resources);
            var completedStages = checkpoint.Document?.CompletedStages.ToHashSet() ?? [];
            var persistedReceipts = checkpoint.Document?.StageReceipts
                .ToDictionary(receipt => receipt.Stage) ?? [];
            var stageOutputHashes = checkpoint.Document?.StageOutputHashes
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal) ??
                new Dictionary<string, string>(StringComparer.Ordinal);
            var inputIdentity = requestIdentity;

            foreach (var stageExecutor in orderedStages)
            {
                currentStage = stageExecutor.Stage;
                cancellationToken.ThrowIfCancellationRequested();
                ReportProgress(
                    progress,
                    currentStage,
                    receipts.Count,
                    totalStageCount,
                    completedStages.Contains(currentStage)
                        ? $"Resuming completed {currentStage} stage."
                        : $"Running {currentStage} stage.");

                if (completedStages.Contains(currentStage))
                {
                    if (!persistedReceipts.TryGetValue(currentStage, out var persistedReceipt))
                    {
                        return Failed(
                            receipts,
                            new ValhallaGenerationFailure(
                                ValhallaGenerationFailureCode.IncompatibleCheckpoint,
                                $"Checkpoint marks {currentStage} complete without its stage receipt.",
                                currentStage));
                    }

                    receipts.Add(persistedReceipt);
                    inputIdentity = persistedReceipt.OutputIdentity;
                    continue;
                }

                var startedAtUtc = DateTimeOffset.UtcNow;
                var result = await stageExecutor.ExecuteAsync(context, cancellationToken)
                    .ConfigureAwait(false);
                var receipt = CreateStageReceipt(
                    currentStage,
                    startedAtUtc,
                    inputIdentity,
                    requestIdentity,
                    result,
                    resources.PeakWorkerCount);
                receipts.Add(receipt);
                if (result.Failures.Count > 0)
                {
                    return Failed(receipts, result.Failures[0]);
                }

                completedStages.Add(currentStage);
                inputIdentity = result.OutputIdentity;
                foreach (var hash in result.OutputHashes)
                {
                    stageOutputHashes[$"{currentStage}:{hash.Key}"] = hash.Value;
                }

                await SaveCheckpointAsync(
                    checkpointPath,
                    new LifecycleCheckpointDocument(
                        LifecycleSchemaVersion,
                        requestIdentity,
                        ValhallaGenerationBuilder.UpstreamCompatibilityVersion,
                        completedStages.Order().ToArray(),
                        stageOutputHashes,
                        receipts.Where(receiptItem =>
                            receiptItem.Stage is not ValhallaGenerationStage.ValidateRequest).ToArray()),
                    cancellationToken).ConfigureAwait(false);
            }

            currentStage = ValhallaGenerationStage.ValidateGraph;
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                currentStage,
                receipts.Count,
                totalStageCount,
                "Validating staged generation.");
            var validationStartedAtUtc = DateTimeOffset.UtcNow;
            var validation = await validator.ValidateAsync(context, cancellationToken)
                .ConfigureAwait(false);
            long validationReceiptBytes = validation.ReceiptLength;
            IReadOnlyDictionary<string, string> validationHashes =
                validation.ReceiptSha256 is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["validation-receipt.json"] = validation.ReceiptSha256,
                    };
            var validationReceipt = new ValhallaGenerationStageReceipt(
                currentStage,
                validationStartedAtUtc,
                DateTimeOffset.UtcNow,
                inputIdentity,
                validation.Receipt?.OutputTreeSha256 ??
                    (validation.IsValid ? "validated" : "validation-failed"),
                validation.Receipt?.Statistics.TileCount ?? 0,
                validation.Receipt?.Statistics.TileBytes ?? 0,
                validationReceiptBytes,
                Math.Max(1, resources.PeakWorkerCount),
                0,
                0,
                validation.Receipt?.Statistics.TileBytes ?? 0,
                requestIdentity,
                [],
                validation.Failures,
                validationHashes);
            receipts.Add(validationReceipt);
            if (!validation.IsValid)
            {
                var failure = validation.Failures.FirstOrDefault() ??
                    new ValhallaGenerationFailure(
                        ValhallaGenerationFailureCode.Validation,
                        "Generation validation failed without a diagnostic.",
                        currentStage);
                return Failed(receipts, failure);
            }

            currentStage = ValhallaGenerationStage.Publish;
            cancellationToken.ThrowIfCancellationRequested();
            ReportProgress(
                progress,
                currentStage,
                receipts.Count,
                totalStageCount,
                "Publishing validated generation.");
            var publication = await PublishAsync(
                request,
                requestIdentity,
                stagingDirectory,
                receipts,
                resources.PeakWorkerCount,
                cancellationToken).ConfigureAwait(false);
            receipts.Add(publication.Receipt);
            var manifest = publication.Manifest;

            return new ValhallaGenerationBuildResult(
                true,
                publication.PublishedDirectory,
                manifest,
                receipts,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failed(
                receipts,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.Canceled,
                    "Generation was canceled.",
                    currentStage));
        }
        catch (ValhallaGenerationResourceLimitException exception)
        {
            return Failed(
                receipts,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.ResourceExhaustion,
                    exception.Message,
                    currentStage));
        }
        catch (ArgumentException exception)
        {
            return Failed(
                receipts,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.Configuration,
                    exception.Message,
                    currentStage));
        }
        catch (JsonException exception)
        {
            return Failed(
                receipts,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.IncompatibleCheckpoint,
                    exception.Message,
                    currentStage));
        }
        catch (IOException exception)
        {
            return Failed(
                receipts,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.InputOutput,
                    exception.Message,
                    currentStage));
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failed(
                receipts,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.InputOutput,
                    exception.Message,
                    currentStage));
        }
        catch (Exception exception)
        {
            return Failed(
                receipts,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.Unknown,
                    exception.Message,
                    currentStage));
        }
    }

    private static void ValidateRequest(
        ValhallaGenerationBuildRequest request,
        IReadOnlyList<IValhallaGenerationStageExecutor> stages)
    {
        ArgumentNullException.ThrowIfNull(request.OsmPbfPaths);
        ArgumentNullException.ThrowIfNull(request.Inputs);
        ArgumentNullException.ThrowIfNull(request.Options);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OutputDirectory);

        if (request.OsmPbfPaths.Count == 0)
        {
            throw new ArgumentException("At least one OSM PBF input is required.", nameof(request));
        }

        if (!Path.IsPathFullyQualified(request.WorkingDirectory) ||
            !Path.IsPathFullyQualified(request.OutputDirectory))
        {
            throw new ArgumentException(
                "Working and output directories must be fully qualified.",
                nameof(request));
        }

        if (Path.GetFullPath(request.WorkingDirectory).Equals(
            Path.GetFullPath(request.OutputDirectory),
            StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Working and output directories must be distinct.",
                nameof(request));
        }

        foreach (var path in request.OsmPbfPaths)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            {
                throw new ArgumentException(
                    $"OSM PBF input must be an existing fully qualified file: '{path}'.",
                    nameof(request));
            }
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            request.Options.MaxDegreeOfParallelism);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            request.Options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            request.Options.ScratchDiskBudgetBytes);

        var duplicateStage = stages
            .GroupBy(stage => stage.Stage)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateStage is not null)
        {
            throw new ArgumentException(
                $"Generation stage {duplicateStage.Key} is registered more than once.",
                nameof(stages));
        }

        var reservedStage = stages.FirstOrDefault(stage =>
            stage.Stage is ValhallaGenerationStage.ValidateRequest or
                ValhallaGenerationStage.ValidateGraph or
                ValhallaGenerationStage.Publish);
        if (reservedStage is not null)
        {
            throw new ArgumentException(
                $"Generation stage {reservedStage.Stage} is lifecycle-owned and cannot be registered.",
                nameof(stages));
        }
    }

    private static async Task<string> ComputeRequestIdentityAsync(
        ValhallaGenerationBuildRequest request,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashValue(hash, ValhallaGenerationBuilder.UpstreamCompatibilityVersion);
        AppendHashValue(hash, JsonSerializer.Serialize(request.Options, JsonOptions));
        AppendHashValue(hash, JsonSerializer.Serialize(request.Inputs, JsonOptions));

        foreach (var inputPath in request.OsmPbfPaths.Select(Path.GetFullPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendHashValue(hash, inputPath);
            var info = new FileInfo(inputPath);
            AppendHashValue(hash, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await using var stream = new FileStream(
                inputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var inputHash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);
            hash.AppendData(inputHash);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHashValue(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }

    private static async Task<CheckpointPreparation> PrepareCheckpointAsync(
        ValhallaGenerationBuildRequest request,
        string requestIdentity,
        string stagingDirectory,
        string checkpointPath,
        CancellationToken cancellationToken)
    {
        if (request.Options.ResumePolicy == ResumePolicy.Disabled)
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }

            Directory.CreateDirectory(stagingDirectory);
            return new CheckpointPreparation(null, null);
        }

        Directory.CreateDirectory(stagingDirectory);
        if (!File.Exists(checkpointPath))
        {
            return new CheckpointPreparation(null, null);
        }

        await using var stream = new FileStream(
            checkpointPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var checkpoint = await JsonSerializer.DeserializeAsync<LifecycleCheckpointDocument>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        if (checkpoint is null ||
            checkpoint.SchemaVersion != LifecycleSchemaVersion ||
            !checkpoint.RequestIdentity.Equals(requestIdentity, StringComparison.Ordinal) ||
            !checkpoint.UpstreamCompatibilityVersion.Equals(
                ValhallaGenerationBuilder.UpstreamCompatibilityVersion,
                StringComparison.Ordinal))
        {
            return new CheckpointPreparation(
                null,
                new ValhallaGenerationFailure(
                    ValhallaGenerationFailureCode.IncompatibleCheckpoint,
                    "The existing checkpoint does not match the current inputs, options, schema, or upstream compatibility version."));
        }

        return new CheckpointPreparation(checkpoint, null);
    }

    private static async Task SaveCheckpointAsync(
        string checkpointPath,
        LifecycleCheckpointDocument checkpoint,
        CancellationToken cancellationToken)
    {
        var temporaryPath = checkpointPath + ".tmp";
        await WriteJsonAsync(temporaryPath, checkpoint, cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporaryPath, checkpointPath, overwrite: true);
    }

    private static async Task<PublicationResult> PublishAsync(
        ValhallaGenerationBuildRequest request,
        string requestIdentity,
        string stagingDirectory,
        IReadOnlyList<ValhallaGenerationStageReceipt> receipts,
        int peakWorkerCount,
        CancellationToken cancellationToken)
    {
        var publicationStartedAtUtc = DateTimeOffset.UtcNow;
        var generationId = requestIdentity[..24].ToLowerInvariant();
        Directory.CreateDirectory(request.OutputDirectory);
        var generationsDirectory = Path.Combine(request.OutputDirectory, "generations");
        Directory.CreateDirectory(generationsDirectory);
        var publishedDirectory = Path.Combine(generationsDirectory, generationId);
        var incomingDirectory = Path.Combine(request.OutputDirectory, $".incoming-{generationId}");

        if (Directory.Exists(publishedDirectory))
        {
            throw new IOException(
                $"Generation '{generationId}' is already published; immutable output cannot be replaced.");
        }

        if (Directory.Exists(incomingDirectory))
        {
            Directory.Delete(incomingDirectory, recursive: true);
        }

        Directory.CreateDirectory(incomingDirectory);
        try
        {
            await CopyDirectoryAsync(
                stagingDirectory,
                incomingDirectory,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var outputTreeHash = await new GenerationOutputTreeHasher().ComputeSha256Async(
                incomingDirectory,
                cancellationToken).ConfigureAwait(false);
            var receipt = new ValhallaGenerationStageReceipt(
                ValhallaGenerationStage.Publish,
                publicationStartedAtUtc,
                DateTimeOffset.UtcNow,
                "validated",
                generationId,
                0,
                0,
                GetDirectorySize(incomingDirectory),
                Math.Max(1, peakWorkerCount),
                0,
                0,
                GetDirectorySize(incomingDirectory),
                requestIdentity,
                [],
                [],
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["payload-tree-sha256"] = outputTreeHash,
                });
            var manifest = new ValhallaGenerationManifest(
                LifecycleSchemaVersion,
                generationId,
                requestIdentity,
                ValhallaGenerationBuilder.UpstreamCompatibilityVersion,
                DateTimeOffset.UtcNow,
                publishedDirectory,
                outputTreeHash,
                receipts.Append(receipt).ToArray());
            await WriteJsonAsync(
                Path.Combine(incomingDirectory, ManifestFileName),
                manifest,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(incomingDirectory, publishedDirectory);

            var activePointer = new ActiveGenerationPointer(
                LifecycleSchemaVersion,
                generationId,
                requestIdentity,
                Path.Combine(publishedDirectory, ManifestFileName),
                outputTreeHash,
                DateTimeOffset.UtcNow);
            var activePointerPath = Path.Combine(request.OutputDirectory, ActivePointerFileName);
            var activePointerTemporaryPath = activePointerPath + ".tmp";
            await WriteJsonAsync(
                activePointerTemporaryPath,
                activePointer,
                cancellationToken).ConfigureAwait(false);
            File.Move(activePointerTemporaryPath, activePointerPath, overwrite: true);

            return new PublicationResult(
                publishedDirectory,
                manifest,
                receipt);
        }
        finally
        {
            if (Directory.Exists(incomingDirectory))
            {
                Directory.Delete(incomingDirectory, recursive: true);
            }
        }
    }

    private static async Task CopyDirectoryAsync(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(
            sourceDirectory,
            "*",
            SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
            if (relativePath.Equals(CheckpointFileName, StringComparison.Ordinal) ||
                relativePath.EndsWith(".tmp", StringComparison.Ordinal))
            {
                continue;
            }

            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                destinationPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(destination, 1024 * 1024, cancellationToken)
                .ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ValhallaGenerationStageReceipt CreateSystemReceipt(
        ValhallaGenerationStage stage,
        string inputIdentity,
        string outputIdentity,
        string checkpointIdentity,
        int maximumConcurrency)
    {
        var timestamp = DateTimeOffset.UtcNow;
        return new ValhallaGenerationStageReceipt(
            stage,
            timestamp,
            timestamp,
            inputIdentity,
            outputIdentity,
            0,
            0,
            0,
            maximumConcurrency,
            0,
            0,
            0,
            checkpointIdentity,
            [],
            [],
            new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private static ValhallaGenerationStageReceipt CreateStageReceipt(
        ValhallaGenerationStage stage,
        DateTimeOffset startedAtUtc,
        string inputIdentity,
        string checkpointIdentity,
        ValhallaGenerationStageResult result,
        int peakWorkerCount) =>
        new(
            stage,
            startedAtUtc,
            DateTimeOffset.UtcNow,
            inputIdentity,
            result.OutputIdentity,
            result.RecordsProcessed,
            result.BytesRead,
            result.BytesWritten,
            Math.Max(1, peakWorkerCount),
            result.AllocatedBytes,
            result.PeakWorkingSetBytes,
            result.ScratchDiskHighWaterMarkBytes,
            checkpointIdentity,
            result.Warnings,
            result.Failures,
            result.OutputHashes)
        {
            FrontierMetrics = result.FrontierMetrics,
        };

    private static void ReportProgress(
        IProgress<ValhallaGenerationBuildProgress>? progress,
        ValhallaGenerationStage stage,
        int completedStageCount,
        int totalStageCount,
        string message) =>
        progress?.Report(new ValhallaGenerationBuildProgress(
            stage,
            completedStageCount,
            totalStageCount,
            message));

    private static ValhallaGenerationBuildResult Failed(
        IReadOnlyList<ValhallaGenerationStageReceipt> receipts,
        ValhallaGenerationFailure failure) =>
        new(false, null, null, receipts, failure);

    private static long GetDirectorySize(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Sum(path => new FileInfo(path).Length);

    private sealed record LifecycleCheckpointDocument(
        int SchemaVersion,
        string RequestIdentity,
        string UpstreamCompatibilityVersion,
        IReadOnlyList<ValhallaGenerationStage> CompletedStages,
        IReadOnlyDictionary<string, string> StageOutputHashes,
        IReadOnlyList<ValhallaGenerationStageReceipt> StageReceipts);

    private sealed record CheckpointPreparation(
        LifecycleCheckpointDocument? Document,
        ValhallaGenerationFailure? Failure);

    private sealed record ActiveGenerationPointer(
        int SchemaVersion,
        string GenerationId,
        string RequestIdentity,
        string ManifestPath,
        string OutputTreeSha256,
        DateTimeOffset PromotedAtUtc);

    private sealed record PublicationResult(
        string PublishedDirectory,
        ValhallaGenerationManifest Manifest,
        ValhallaGenerationStageReceipt Receipt);
}
