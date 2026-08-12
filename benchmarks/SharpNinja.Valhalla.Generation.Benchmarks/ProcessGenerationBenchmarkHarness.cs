using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpNinja.Valhalla.Generation.Benchmarks;

public sealed record GenerationBenchmarkConfiguration
{
    public int SchemaVersion { get; init; } = 1;

    public required string DatasetName { get; init; }

    public required string InputPath { get; init; }

    public required ProcessCommandTemplate Managed { get; init; }

    public required ProcessCommandTemplate Official { get; init; }

    public int WarmupRuns { get; init; } = 1;

    public int MeasuredRuns { get; init; } = 5;

    public double RequiredManagedTimeRatio { get; init; } = 0.8;

    public double MaximumManagedMemoryRatio { get; init; } = 1.25;

    public double MaximumManagedOutputSizeRatio { get; init; } = 1.05;

    public double MaximumManagedHours { get; init; } = 24;

    public string? ExpectedInputSha256 { get; init; }
}

public sealed record ProcessCommandTemplate
{
    public required string Id { get; init; }

    public required string FileName { get; init; }

    public required IReadOnlyList<string> Arguments { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public DockerBenchmarkCommand? Docker { get; init; }
}

public sealed record DockerBenchmarkCommand
{
    public required string Image { get; init; }

    public string? EntryPoint { get; init; }

    public string InputFileName { get; init; } = "input.osm.pbf";

    public IReadOnlyDictionary<string, string> AdditionalInputFiles { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public double CpuLimit { get; init; } = 1;

    public long MemoryLimitBytes { get; init; } = 1_073_741_824;

    public int PidsLimit { get; init; } = 256;

    public bool UseNativeVolumes { get; init; } = true;
}

public sealed record GenerationBenchmarkAttempt(
    int Sequence,
    string Implementation,
    bool Warmup,
    bool Success,
    int ExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double WallTimeSeconds,
    double CpuTimeSeconds,
    long PeakWorkingSetBytes,
    long OutputBytes,
    string? OutputTreeSha256,
    string? DiagnosticCode,
    string? DiagnosticTail);

public sealed record GenerationBenchmarkReceipt(
    int SchemaVersion,
    string DatasetName,
    string InputSha256,
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<GenerationBenchmarkAttempt> Attempts,
    GenerationBenchmarkSummary Summary);

public sealed record GenerationBenchmarkSummary(
    bool Qualified,
    IReadOnlyList<string> Failures,
    double ManagedMedianSeconds,
    double OfficialMedianSeconds,
    double ManagedTimeRatio,
    long ManagedPeakWorkingSetBytes,
    long OfficialPeakWorkingSetBytes,
    double ManagedMemoryRatio,
    long ManagedMedianOutputBytes,
    long OfficialMedianOutputBytes,
    double ManagedOutputSizeRatio,
    IReadOnlyList<string> ManagedOutputTreeHashes);

public sealed partial class ProcessGenerationBenchmarkHarness
{
    private const int MaximumDiagnosticCharacters = 16_384;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public async Task<GenerationBenchmarkReceipt> RunAsync(
        GenerationBenchmarkConfiguration configuration,
        string outputDirectory,
        string? managedImage,
        string? officialImage,
        bool enforceQualification,
        bool lower48,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateConfiguration(configuration, outputDirectory, lower48);
        Directory.CreateDirectory(outputDirectory);

        var inputSha256 = ComputeFileSha256(configuration.InputPath);
        if (!string.IsNullOrWhiteSpace(configuration.ExpectedInputSha256) &&
            !string.Equals(
                inputSha256,
                configuration.ExpectedInputSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new GenerationBenchmarkConfigurationException(
                "The input PBF SHA-256 does not match the configured identity.");
        }

        var attempts = new List<GenerationBenchmarkAttempt>();
        var sequence = 0;
        for (var warmup = 0; warmup < configuration.WarmupRuns; warmup++)
        {
            attempts.Add(await RunAttemptAsync(
                    ++sequence,
                    "managed",
                    configuration.Managed,
                    configuration,
                    outputDirectory,
                    managedImage,
                    officialImage,
                    warmup: true,
                    cancellationToken)
                .ConfigureAwait(false));
            attempts.Add(await RunAttemptAsync(
                    ++sequence,
                    "official",
                    configuration.Official,
                    configuration,
                    outputDirectory,
                    managedImage,
                    officialImage,
                    warmup: true,
                    cancellationToken)
                .ConfigureAwait(false));
        }

        if (lower48)
        {
            attempts.Add(await RunAttemptAsync(
                    ++sequence,
                    "official",
                    configuration.Official,
                    configuration,
                    outputDirectory,
                    managedImage,
                    officialImage,
                    warmup: false,
                    cancellationToken)
                .ConfigureAwait(false));
            for (var run = 0; run < configuration.MeasuredRuns; run++)
            {
                attempts.Add(await RunAttemptAsync(
                        ++sequence,
                        "managed",
                        configuration.Managed,
                        configuration,
                        outputDirectory,
                        managedImage,
                        officialImage,
                        warmup: false,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }
        else
        {
            for (var run = 0; run < configuration.MeasuredRuns; run++)
            {
                var firstImplementation = run % 2 == 0 ? "managed" : "official";
                var secondImplementation = run % 2 == 0 ? "official" : "managed";
                attempts.Add(await RunNamedAttemptAsync(
                        ++sequence,
                        firstImplementation,
                        configuration,
                        outputDirectory,
                        managedImage,
                        officialImage,
                        cancellationToken)
                    .ConfigureAwait(false));
                attempts.Add(await RunNamedAttemptAsync(
                        ++sequence,
                        secondImplementation,
                        configuration,
                        outputDirectory,
                        managedImage,
                        officialImage,
                        cancellationToken)
                    .ConfigureAwait(false));
            }
        }

        var summary = Summarize(configuration, attempts, lower48);
        var receipt = new GenerationBenchmarkReceipt(
            1,
            configuration.DatasetName,
            inputSha256,
            DateTimeOffset.UtcNow,
            attempts,
            summary);
        WriteReceipts(outputDirectory, receipt);

        if (enforceQualification && !summary.Qualified)
        {
            throw new GenerationQualificationException(summary.Failures);
        }

        return receipt;
    }

    public static GenerationBenchmarkConfiguration LoadConfiguration(string path)
    {
        if (!File.Exists(path))
        {
            throw new GenerationBenchmarkConfigurationException(
                $"Benchmark configuration does not exist: {path}");
        }

        try
        {
            var configuration = JsonSerializer.Deserialize<GenerationBenchmarkConfiguration>(
                File.ReadAllText(path),
                JsonOptions) ??
                throw new GenerationBenchmarkConfigurationException(
                    "Benchmark configuration is empty.");
            string configurationDirectory =
                Path.GetDirectoryName(Path.GetFullPath(path)) ??
                Environment.CurrentDirectory;
            return ResolveConfigurationPaths(configuration, configurationDirectory);
        }
        catch (JsonException exception)
        {
            throw new GenerationBenchmarkConfigurationException(
                "Benchmark configuration is not valid versioned JSON.",
                exception);
        }
    }

    private static GenerationBenchmarkConfiguration ResolveConfigurationPaths(
        GenerationBenchmarkConfiguration configuration,
        string configurationDirectory) =>
        configuration with
        {
            InputPath = ResolveConfigurationPath(
                configuration.InputPath,
                configurationDirectory),
            Managed = ResolveCommandPaths(
                configuration.Managed,
                configurationDirectory),
            Official = ResolveCommandPaths(
                configuration.Official,
                configurationDirectory),
        };

    private static ProcessCommandTemplate ResolveCommandPaths(
        ProcessCommandTemplate command,
        string configurationDirectory)
    {
        DockerBenchmarkCommand? docker = command.Docker;
        if (docker is not null)
        {
            docker = docker with
            {
                AdditionalInputFiles = docker.AdditionalInputFiles.ToDictionary(
                    pair => pair.Key,
                    pair => ResolveConfigurationPath(
                        pair.Value,
                        configurationDirectory),
                    StringComparer.Ordinal),
            };
        }

        return command with
        {
            WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory)
                ? command.WorkingDirectory
                : ResolveConfigurationPath(
                    command.WorkingDirectory,
                    configurationDirectory),
            Docker = docker,
        };
    }

    private static string ResolveConfigurationPath(
        string path,
        string configurationDirectory) =>
        Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, configurationDirectory);

    private Task<GenerationBenchmarkAttempt> RunNamedAttemptAsync(
        int sequence,
        string implementation,
        GenerationBenchmarkConfiguration configuration,
        string outputDirectory,
        string? managedImage,
        string? officialImage,
        CancellationToken cancellationToken) =>
        RunAttemptAsync(
            sequence,
            implementation,
            string.Equals(implementation, "managed", StringComparison.Ordinal)
                ? configuration.Managed
                : configuration.Official,
            configuration,
            outputDirectory,
            managedImage,
            officialImage,
            warmup: false,
            cancellationToken);

    private static async Task<GenerationBenchmarkAttempt> RunAttemptAsync(
        int sequence,
        string implementation,
        ProcessCommandTemplate command,
        GenerationBenchmarkConfiguration configuration,
        string outputDirectory,
        string? managedImage,
        string? officialImage,
        bool warmup,
        CancellationToken cancellationToken)
    {
        var attemptDirectory = Path.Combine(
            outputDirectory,
            "attempts",
            $"{sequence:D2}-{implementation}-{(warmup ? "warmup" : "measured")}");
        EnsureFreshAttemptDirectory(outputDirectory, attemptDirectory);
        var diagnosticBuffer = new BoundedDiagnosticBuffer(MaximumDiagnosticCharacters);

        if (command.Docker is not null)
        {
            return await RunDockerAttemptAsync(
                    sequence,
                    implementation,
                    command,
                    configuration,
                    attemptDirectory,
                    managedImage,
                    officialImage,
                    warmup,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(command.FileName)
            {
                WorkingDirectory = string.IsNullOrWhiteSpace(command.WorkingDirectory)
                    ? Environment.CurrentDirectory
                    : command.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
            EnableRaisingEvents = true,
        };

        foreach (var argument in command.Arguments)
        {
            process.StartInfo.ArgumentList.Add(
                Expand(
                    argument,
                    configuration,
                    attemptDirectory,
                    managedImage,
                    officialImage));
        }

        foreach (var pair in command.Environment)
        {
            process.StartInfo.Environment[pair.Key] = Expand(
                pair.Value,
                configuration,
                attemptDirectory,
                managedImage,
                officialImage);
        }

        process.OutputDataReceived += (_, eventArgs) => diagnosticBuffer.Append(eventArgs.Data);
        process.ErrorDataReceived += (_, eventArgs) => diagnosticBuffer.Append(eventArgs.Data);

        var startedAtUtc = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var exitCode = -1;
        var cpuTime = TimeSpan.Zero;
        var peakWorkingSet = 0L;
        string? diagnosticCode = null;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    $"Could not start benchmark command '{command.Id}'.");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            process.WaitForExit();

            exitCode = process.ExitCode;
            cpuTime = process.TotalProcessorTime;
            peakWorkingSet = process.PeakWorkingSet64;
            if (exitCode != 0)
            {
                diagnosticCode = "ProcessFailed";
            }
        }
        catch (OperationCanceledException)
        {
            diagnosticCode = "Canceled";
            TryKillProcessTree(process);
            throw;
        }
        catch (Exception)
        {
            diagnosticCode = "ProcessStartOrReadFailed";
            TryKillProcessTree(process);
            throw;
        }
        finally
        {
            stopwatch.Stop();
        }

        var completedAtUtc = DateTimeOffset.UtcNow;
        (long outputBytes, string? treeHash) = MeasureAndRemoveAttemptOutput(
            attemptDirectory,
            exitCode == 0);

        return new GenerationBenchmarkAttempt(
            sequence,
            implementation,
            warmup,
            exitCode == 0,
            exitCode,
            startedAtUtc,
            completedAtUtc,
            stopwatch.Elapsed.TotalSeconds,
            cpuTime.TotalSeconds,
            peakWorkingSet,
            outputBytes,
            treeHash,
            diagnosticCode,
            exitCode == 0 ? null : Redact(diagnosticBuffer.ToString()));
    }

    private static async Task<GenerationBenchmarkAttempt> RunDockerAttemptAsync(
        int sequence,
        string implementation,
        ProcessCommandTemplate command,
        GenerationBenchmarkConfiguration configuration,
        string attemptDirectory,
        string? managedImage,
        string? officialImage,
        bool warmup,
        CancellationToken cancellationToken)
    {
        DockerBenchmarkCommand docker = command.Docker! with
        {
            Image = Expand(
                command.Docker!.Image,
                configuration,
                attemptDirectory,
                managedImage,
                officialImage),
        };
        string[] arguments = command.Arguments
            .Select(argument => Expand(
                argument,
                configuration,
                attemptDirectory,
                managedImage,
                officialImage))
            .ToArray();
        Dictionary<string, string> environment = command.Environment.ToDictionary(
            pair => pair.Key,
            pair => Expand(
                pair.Value,
                configuration,
                attemptDirectory,
                managedImage,
                officialImage),
            StringComparer.Ordinal);

        DockerBenchmarkProcessResult process =
            await DockerGenerationBenchmarkRunner.RunAsync(
                    docker,
                    arguments,
                    environment,
                    configuration.InputPath,
                    attemptDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        bool processSucceeded =
            process.ExitCode == 0 &&
            process.DiagnosticCode is null;
        (long outputBytes, string? treeHash) = MeasureAndRemoveAttemptOutput(
            attemptDirectory,
            processSucceeded);
        bool success = processSucceeded && outputBytes > 0;

        return new GenerationBenchmarkAttempt(
            sequence,
            implementation,
            warmup,
            success,
            process.ExitCode,
            process.StartedAtUtc,
            process.CompletedAtUtc,
            process.WallTimeSeconds,
            process.CpuTimeSeconds,
            process.PeakMemoryBytes,
            outputBytes,
            treeHash,
            process.DiagnosticCode,
            process.DiagnosticTail);
    }

    private static GenerationBenchmarkSummary Summarize(
        GenerationBenchmarkConfiguration configuration,
        IReadOnlyList<GenerationBenchmarkAttempt> attempts,
        bool lower48)
    {
        var measured = attempts.Where(attempt => !attempt.Warmup).ToArray();
        var managed = measured
            .Where(attempt => string.Equals(attempt.Implementation, "managed", StringComparison.Ordinal))
            .ToArray();
        var official = measured
            .Where(attempt => string.Equals(attempt.Implementation, "official", StringComparison.Ordinal))
            .ToArray();
        var failures = new List<string>();

        foreach (var failedAttempt in measured.Where(attempt => !attempt.Success))
        {
            failures.Add(
                $"{failedAttempt.Implementation} attempt {failedAttempt.Sequence} exited {failedAttempt.ExitCode}.");
        }

        var managedMedian = Median(managed.Select(attempt => attempt.WallTimeSeconds));
        var officialMedian = Median(official.Select(attempt => attempt.WallTimeSeconds));
        var timeRatio = Divide(managedMedian, officialMedian);
        var managedMemory = managed.Select(attempt => attempt.PeakWorkingSetBytes).DefaultIfEmpty().Max();
        var officialMemory = official.Select(attempt => attempt.PeakWorkingSetBytes).DefaultIfEmpty().Max();
        var memoryRatio = managedMemory > 0 && officialMemory > 0
            ? Divide(managedMemory, officialMemory)
            : 0;
        var managedOutput = (long)Median(managed.Select(attempt => (double)attempt.OutputBytes));
        var officialOutput = (long)Median(official.Select(attempt => (double)attempt.OutputBytes));
        var outputRatio = Divide(managedOutput, officialOutput);
        var treeHashes = managed
            .Select(attempt => attempt.OutputTreeSha256)
            .Where(hash => hash is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(hash => hash, StringComparer.Ordinal)
            .ToArray();

        if (managedMemory <= 0 || officialMemory <= 0)
        {
            failures.Add("Container memory metrics were unavailable for one or both implementations.");
        }

        if (lower48)
        {
            if (managed.Any(attempt =>
                    attempt.WallTimeSeconds > TimeSpan.FromHours(configuration.MaximumManagedHours).TotalSeconds))
            {
                failures.Add(
                    $"Managed Lower-48 generation exceeded {configuration.MaximumManagedHours:F2} hours.");
            }

            if (managed.Length != configuration.MeasuredRuns)
            {
                failures.Add("Lower-48 qualification did not retain every configured managed run.");
            }
        }
        else
        {
            if (timeRatio > configuration.RequiredManagedTimeRatio)
            {
                failures.Add(
                    $"Managed median time ratio {timeRatio:F4} exceeded {configuration.RequiredManagedTimeRatio:F4}.");
            }

            if (memoryRatio > configuration.MaximumManagedMemoryRatio)
            {
                failures.Add(
                    $"Managed memory ratio {memoryRatio:F4} exceeded {configuration.MaximumManagedMemoryRatio:F4}.");
            }

            if (outputRatio > configuration.MaximumManagedOutputSizeRatio)
            {
                failures.Add(
                    $"Managed output-size ratio {outputRatio:F4} exceeded {configuration.MaximumManagedOutputSizeRatio:F4}.");
            }
        }

        if (treeHashes.Length != 1)
        {
            failures.Add(
                $"Managed output was not deterministic; observed {treeHashes.Length} output-tree hashes.");
        }

        return new GenerationBenchmarkSummary(
            failures.Count == 0,
            failures,
            managedMedian,
            officialMedian,
            timeRatio,
            managedMemory,
            officialMemory,
            memoryRatio,
            managedOutput,
            officialOutput,
            outputRatio,
            treeHashes);
    }

    private static void ValidateConfiguration(
        GenerationBenchmarkConfiguration configuration,
        string outputDirectory,
        bool lower48)
    {
        if (configuration.SchemaVersion != 1)
        {
            throw new GenerationBenchmarkConfigurationException(
                $"Unsupported benchmark schema version: {configuration.SchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(configuration.DatasetName) ||
            string.IsNullOrWhiteSpace(configuration.InputPath) ||
            !Path.IsPathFullyQualified(configuration.InputPath) ||
            !File.Exists(configuration.InputPath))
        {
            throw new GenerationBenchmarkConfigurationException(
                "Dataset name and an existing absolute input path are required.");
        }

        ValidateCommand(configuration.Managed, "managed");
        ValidateCommand(configuration.Official, "official");

        if (configuration.WarmupRuns < 0 ||
            configuration.MeasuredRuns <= 0 ||
            (!lower48 && configuration.MeasuredRuns != 5) ||
            (lower48 && configuration.MeasuredRuns != 3))
        {
            throw new GenerationBenchmarkConfigurationException(
                lower48
                    ? "Lower-48 qualification requires exactly three managed measured runs."
                    : "Nashville qualification requires exactly five alternating measured runs.");
        }

        if (!Path.IsPathFullyQualified(outputDirectory))
        {
            throw new GenerationBenchmarkConfigurationException(
                "The benchmark output directory must be absolute.");
        }
    }

    private static void ValidateCommand(ProcessCommandTemplate command, string role)
    {
        if (string.IsNullOrWhiteSpace(command.Id) ||
            string.IsNullOrWhiteSpace(command.FileName) ||
            command.Arguments is null)
        {
            throw new GenerationBenchmarkConfigurationException(
                $"The {role} command is incomplete.");
        }

        if (command.Arguments.Any(argument =>
                argument.Contains("api_key", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("secret=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new GenerationBenchmarkConfigurationException(
                $"The {role} command contains a credential-shaped argument.");
        }

        DockerBenchmarkCommand? docker = command.Docker;
        if (docker is null)
        {
            return;
        }

        if (!string.Equals(command.FileName, "docker", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(docker.Image) ||
            !docker.UseNativeVolumes ||
            docker.CpuLimit <= 0 ||
            docker.MemoryLimitBytes <= 0 ||
            docker.PidsLimit <= 0 ||
            !IsSafeContainerFileName(docker.InputFileName) ||
            docker.AdditionalInputFiles.Any(pair =>
                !IsSafeContainerFileName(pair.Key) ||
                !Path.IsPathFullyQualified(pair.Value) ||
                !File.Exists(pair.Value)))
        {
            throw new GenerationBenchmarkConfigurationException(
                $"The {role} Docker command does not define safe native-volume execution.");
        }
    }

    private static bool IsSafeContainerFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, Path.GetFileName(value), StringComparison.Ordinal) &&
        value is not "." and not "..";

    private static string Expand(
        string value,
        GenerationBenchmarkConfiguration configuration,
        string attemptDirectory,
        string? managedImage,
        string? officialImage) =>
        value
            .Replace("{input}", configuration.InputPath, StringComparison.Ordinal)
            .Replace("{output}", attemptDirectory, StringComparison.Ordinal)
            .Replace("{managed-image}", managedImage ?? string.Empty, StringComparison.Ordinal)
            .Replace("{official-image}", officialImage ?? string.Empty, StringComparison.Ordinal);

    private static (long OutputBytes, string? TreeHash) MeasureAndRemoveAttemptOutput(
        string attemptDirectory,
        bool computeTreeHash)
    {
        long outputBytes = Directory.Exists(attemptDirectory)
            ? Directory
                .EnumerateFiles(attemptDirectory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length)
            : 0;
        string? treeHash = computeTreeHash && outputBytes > 0
            ? ComputeTreeSha256(attemptDirectory)
            : null;

        if (Directory.Exists(attemptDirectory))
        {
            Directory.Delete(attemptDirectory, recursive: true);
        }

        return (outputBytes, treeHash);
    }

    private static void EnsureFreshAttemptDirectory(string root, string attemptDirectory)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedAttempt = Path.GetFullPath(attemptDirectory);
        if (!normalizedAttempt.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new GenerationBenchmarkConfigurationException(
                "Attempt output escaped the configured receipt directory.");
        }

        if (Directory.Exists(normalizedAttempt))
        {
            Directory.Delete(normalizedAttempt, recursive: true);
        }

        Directory.CreateDirectory(normalizedAttempt);
    }

    private static void WriteReceipts(
        string outputDirectory,
        GenerationBenchmarkReceipt receipt)
    {
        File.WriteAllText(
            Path.Combine(outputDirectory, "generation-benchmark-receipt.json"),
            JsonSerializer.Serialize(receipt, JsonOptions) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var markdown = new StringBuilder()
            .AppendLine($"# {receipt.DatasetName} generation benchmark")
            .AppendLine()
            .AppendLine($"Qualified: {receipt.Summary.Qualified}")
            .AppendLine($"Input SHA-256: {receipt.InputSha256}")
            .AppendLine($"Managed median: {receipt.Summary.ManagedMedianSeconds:F3} seconds")
            .AppendLine($"Official median: {receipt.Summary.OfficialMedianSeconds:F3} seconds")
            .AppendLine($"Time ratio: {receipt.Summary.ManagedTimeRatio:F4}")
            .AppendLine($"Memory ratio: {receipt.Summary.ManagedMemoryRatio:F4}")
            .AppendLine($"Output-size ratio: {receipt.Summary.ManagedOutputSizeRatio:F4}")
            .AppendLine()
            .AppendLine("## Attempts")
            .AppendLine();

        foreach (var attempt in receipt.Attempts)
        {
            markdown.AppendLine(
                $"- {attempt.Sequence:D2} {attempt.Implementation} " +
                $"{(attempt.Warmup ? "warm-up" : "measured")}: " +
                $"exit {attempt.ExitCode}, {attempt.WallTimeSeconds:F3}s, " +
                $"{attempt.PeakWorkingSetBytes} bytes peak");
        }

        if (receipt.Summary.Failures.Count > 0)
        {
            markdown.AppendLine().AppendLine("## Qualification failures").AppendLine();
            foreach (var failure in receipt.Summary.Failures)
            {
                markdown.AppendLine($"- {failure}");
            }
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "generation-benchmark-report.md"),
            markdown.ToString(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static string ComputeFileSha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string ComputeTreeSha256(string directory)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in Directory
                     .EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => Path.GetRelativePath(directory, path), StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(directory, path).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            using var stream = File.OpenRead(path);
            var buffer = new byte[128 * 1024];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer)) > 0)
            {
                hash.AppendData(buffer.AsSpan(0, bytesRead));
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
        {
            return double.PositiveInfinity;
        }

        var middle = ordered.Length / 2;
        return ordered.Length % 2 == 0
            ? (ordered[middle - 1] + ordered[middle]) / 2
            : ordered[middle];
    }

    private static double Divide(double numerator, double denominator) =>
        denominator > 0 && double.IsFinite(denominator)
            ? numerator / denominator
            : double.MaxValue;

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    [GeneratedRegex(
        "(?i)(authorization|api[_-]?key|password|secret|token)(\\s*[:=]\\s*)([^\\s,;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretPattern();

    private static string Redact(string value) =>
        SecretPattern().Replace(value, "$1$2[REDACTED]");

    private sealed class BoundedDiagnosticBuffer(int capacity)
    {
        private readonly object sync = new();
        private readonly StringBuilder buffer = new();

        public void Append(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            lock (sync)
            {
                buffer.AppendLine(value);
                if (buffer.Length > capacity)
                {
                    buffer.Remove(0, buffer.Length - capacity);
                }
            }
        }

        public override string ToString()
        {
            lock (sync)
            {
                return buffer.ToString();
            }
        }
    }
}

public sealed class GenerationBenchmarkConfigurationException : Exception
{
    public GenerationBenchmarkConfigurationException(string message)
        : base(message)
    {
    }

    public GenerationBenchmarkConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class GenerationQualificationException : Exception
{
    public GenerationQualificationException(IReadOnlyList<string> failures)
        : base(string.Join(Environment.NewLine, failures))
    {
        Failures = failures;
    }

    public IReadOnlyList<string> Failures { get; }
}
