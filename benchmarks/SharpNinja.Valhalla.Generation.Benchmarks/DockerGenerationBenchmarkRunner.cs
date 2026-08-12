using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpNinja.Valhalla.Generation.Benchmarks;

internal sealed record DockerBenchmarkProcessResult(
    int ExitCode,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double WallTimeSeconds,
    double CpuTimeSeconds,
    long PeakMemoryBytes,
    string? DiagnosticCode,
    string? DiagnosticTail);

internal static partial class DockerGenerationBenchmarkRunner
{
    private const int MaximumDiagnosticCharacters = 16_384;

    [GeneratedRegex(
        @"(?i)(authorization|api[-_]?key|password|secret|token)[=:][^ ]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex CredentialRegex();

    public static async Task<DockerBenchmarkProcessResult> RunAsync(
        DockerBenchmarkCommand command,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string primaryInputPath,
        string attemptDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!command.UseNativeVolumes)
        {
            throw new GenerationBenchmarkConfigurationException(
                "Docker qualification commands must use Docker-native volumes.");
        }

        string suffix = Guid.NewGuid().ToString("N");
        string inputVolume = $"sn-valhalla-bench-input-{suffix}";
        string workVolume = $"sn-valhalla-bench-work-{suffix}";
        string outputVolume = $"sn-valhalla-bench-output-{suffix}";
        string stagingContainer = $"sn-valhalla-bench-stage-{suffix}";
        string generationContainer = $"sn-valhalla-bench-run-{suffix}";
        var diagnostics = new BoundedTextBuffer(MaximumDiagnosticCharacters);
        bool stagingContainerCreated = false;
        bool generationContainerCreated = false;

        try
        {
            await RequireDockerSuccessAsync(
                    ["volume", "create", inputVolume],
                    cancellationToken)
                .ConfigureAwait(false);
            await RequireDockerSuccessAsync(
                    ["volume", "create", workVolume],
                    cancellationToken)
                .ConfigureAwait(false);
            await RequireDockerSuccessAsync(
                    ["volume", "create", outputVolume],
                    cancellationToken)
                .ConfigureAwait(false);

            await RequireDockerSuccessAsync(
                    [
                        "create",
                        "--name",
                        stagingContainer,
                        "--mount",
                        $"type=volume,source={inputVolume},target=/input",
                        "--entrypoint",
                        "/bin/true",
                        command.Image,
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
            stagingContainerCreated = true;

            await CopyInputAsync(
                    stagingContainer,
                    primaryInputPath,
                    command.InputFileName,
                    cancellationToken)
                .ConfigureAwait(false);
            foreach (KeyValuePair<string, string> input in command.AdditionalInputFiles)
            {
                await CopyInputAsync(
                        stagingContainer,
                        input.Value,
                        input.Key,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            await RequireDockerSuccessAsync(
                    ["rm", stagingContainer],
                    cancellationToken)
                .ConfigureAwait(false);
            stagingContainerCreated = false;

            var createArguments = new List<string>
            {
                "create",
                "--name",
                generationContainer,
                "--network",
                "none",
                "--cpus",
                command.CpuLimit.ToString("0.###", CultureInfo.InvariantCulture),
                "--memory",
                command.MemoryLimitBytes.ToString(CultureInfo.InvariantCulture),
                "--pids-limit",
                command.PidsLimit.ToString(CultureInfo.InvariantCulture),
                "--read-only",
                "--cap-drop",
                "ALL",
                "--security-opt",
                "no-new-privileges",
                "--tmpfs",
                "/tmp:rw,noexec,nosuid,size=64m",
                "--mount",
                $"type=volume,source={inputVolume},target=/input,readonly",
                "--mount",
                $"type=volume,source={workVolume},target=/work",
                "--mount",
                $"type=volume,source={outputVolume},target=/output",
            };
            if (!string.IsNullOrWhiteSpace(command.EntryPoint))
            {
                createArguments.Add("--entrypoint");
                createArguments.Add(command.EntryPoint);
            }

            foreach (KeyValuePair<string, string> pair in environment)
            {
                createArguments.Add("--env");
                createArguments.Add($"{pair.Key}={pair.Value}");
            }

            createArguments.Add(command.Image);
            createArguments.AddRange(arguments);

            await RequireDockerSuccessAsync(createArguments, cancellationToken)
                .ConfigureAwait(false);
            generationContainerCreated = true;

            DateTimeOffset startedAtUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            string? diagnosticCode = null;
            int containerExitCode = -1;
            using var statsCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<DockerStatsAccumulator> statsTask = Task.FromResult(new DockerStatsAccumulator());

            try
            {
                await RequireDockerSuccessAsync(
                        ["start", generationContainer],
                        cancellationToken)
                    .ConfigureAwait(false);
                statsTask = CaptureStatsAsync(
                    generationContainer,
                    statsCancellation.Token);
                DockerCommandResult wait = await RunDockerAsync(
                        ["wait", generationContainer],
                        cancellationToken)
                    .ConfigureAwait(false);
                if (wait.ExitCode != 0 ||
                    !int.TryParse(
                        wait.StandardOutput.Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out containerExitCode))
                {
                    diagnosticCode = "ContainerWaitFailed";
                    diagnostics.Append(wait.StandardError);
                }
            }
            catch (OperationCanceledException)
            {
                diagnosticCode = "Canceled";
                await TryDockerAsync(
                        ["kill", generationContainer],
                        CancellationToken.None)
                    .ConfigureAwait(false);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                statsCancellation.Cancel();
            }

            DockerStatsAccumulator stats;
            try
            {
                stats = await statsTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                stats = new DockerStatsAccumulator();
            }

            DockerCommandResult logs = await RunDockerAsync(
                    ["logs", generationContainer],
                    CancellationToken.None)
                .ConfigureAwait(false);
            diagnostics.Append(logs.StandardOutput);
            diagnostics.Append(logs.StandardError);

            if (containerExitCode != 0 && diagnosticCode is null)
            {
                diagnosticCode = "ContainerProcessFailed";
            }

            if (stats.SampleCount == 0 && stopwatch.Elapsed >= TimeSpan.FromSeconds(2))
            {
                diagnosticCode ??= "ContainerStatsUnavailable";
            }

            DockerCommandResult copy = await RunDockerAsync(
                    ["cp", $"{generationContainer}:/output/.", attemptDirectory],
                    cancellationToken)
                .ConfigureAwait(false);
            if (copy.ExitCode != 0)
            {
                diagnosticCode ??= "ContainerOutputExportFailed";
                diagnostics.Append(copy.StandardError);
            }

            return new DockerBenchmarkProcessResult(
                containerExitCode,
                startedAtUtc,
                DateTimeOffset.UtcNow,
                stopwatch.Elapsed.TotalSeconds,
                stats.EstimatedCpuTimeSeconds,
                stats.PeakMemoryBytes,
                diagnosticCode,
                diagnosticCode is null
                    ? null
                    : Redact(diagnostics.ToString()));
        }
        finally
        {
            if (generationContainerCreated)
            {
                await TryDockerAsync(
                        ["rm", "--force", generationContainer],
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            if (stagingContainerCreated)
            {
                await TryDockerAsync(
                        ["rm", "--force", stagingContainer],
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await TryDockerAsync(
                    ["volume", "rm", "--force", outputVolume],
                    CancellationToken.None)
                .ConfigureAwait(false);
            await TryDockerAsync(
                    ["volume", "rm", "--force", workVolume],
                    CancellationToken.None)
                .ConfigureAwait(false);
            await TryDockerAsync(
                    ["volume", "rm", "--force", inputVolume],
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    private static async Task CopyInputAsync(
        string stagingContainer,
        string sourcePath,
        string destinationFileName,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath))
        {
            throw new GenerationBenchmarkConfigurationException(
                $"Benchmark input does not exist: {sourcePath}");
        }

        if (string.IsNullOrWhiteSpace(destinationFileName) ||
            !string.Equals(
                destinationFileName,
                Path.GetFileName(destinationFileName),
                StringComparison.Ordinal) ||
            destinationFileName is "." or "..")
        {
            throw new GenerationBenchmarkConfigurationException(
                "Container input names must be safe file names.");
        }

        await RequireDockerSuccessAsync(
                [
                    "cp",
                    sourcePath,
                    $"{stagingContainer}:/input/{destinationFileName}",
                ],
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<DockerStatsAccumulator> CaptureStatsAsync(
        string containerName,
        CancellationToken cancellationToken)
    {
        var accumulator = new DockerStatsAccumulator();
        using var process = CreateDockerProcess(
            ["stats", "--format", "{{json .}}", containerName]);
        if (!process.Start())
        {
            return accumulator;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await process.StandardOutput
                    .ReadLineAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (DockerStatsSampleParser.TryParse(line, out DockerStatsSample sample))
                {
                    accumulator.Add(sample, Stopwatch.GetTimestamp());
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The generation container completed; stop the open stats stream.
        }
        finally
        {
            TryKillProcess(process);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        return accumulator;
    }

    private static async Task RequireDockerSuccessAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        DockerCommandResult result = await RunDockerAsync(arguments, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                "Docker benchmark preparation failed: " +
                Redact(result.StandardError));
        }
    }

    private static async Task TryDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            await RunDockerAsync(arguments, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cleanup is best effort and must not replace the primary failure.
        }
    }

    private static async Task<DockerCommandResult> RunDockerAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        using Process process = CreateDockerProcess(arguments);
        if (!process.Start())
        {
            return new DockerCommandResult(
                -1,
                string.Empty,
                "Docker process could not be started.");
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new DockerCommandResult(
                process.ExitCode,
                await standardOutput.ConfigureAwait(false),
                await standardError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static Process CreateDockerProcess(IReadOnlyList<string> arguments)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("docker")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        return process;
    }

    private static void TryKillProcess(Process process)
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

    private static string Redact(string value) =>
        CredentialRegex().Replace(value, "$1=[REDACTED]").Trim();

    private sealed record DockerCommandResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);

    private sealed class BoundedTextBuffer(int maximumCharacters)
    {
        private readonly StringBuilder _builder = new(maximumCharacters);

        public void Append(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            int remaining = maximumCharacters - _builder.Length;
            if (remaining <= 0)
            {
                return;
            }

            _builder.Append(value.AsSpan(0, Math.Min(remaining, value.Length)));
        }

        public override string ToString() => _builder.ToString();
    }
}

internal sealed class DockerStatsAccumulator
{
    private long _previousTimestamp;
    private double _previousCpuPercentage;

    public int SampleCount { get; private set; }

    public long PeakMemoryBytes { get; private set; }

    public double EstimatedCpuTimeSeconds { get; private set; }

    public void Add(
        DockerStatsSample sample,
        long timestamp)
    {
        if (_previousTimestamp != 0)
        {
            double elapsedSeconds =
                (timestamp - _previousTimestamp) / (double)Stopwatch.Frequency;
            EstimatedCpuTimeSeconds +=
                elapsedSeconds * (_previousCpuPercentage / 100d);
        }

        _previousTimestamp = timestamp;
        _previousCpuPercentage = sample.CpuPercentage;
        PeakMemoryBytes = Math.Max(PeakMemoryBytes, sample.MemoryBytes);
        SampleCount++;
    }
}
