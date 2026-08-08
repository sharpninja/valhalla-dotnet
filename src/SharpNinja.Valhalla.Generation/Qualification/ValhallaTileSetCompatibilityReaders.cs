using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Differential;

namespace SharpNinja.Valhalla.Generation.Qualification;

/// <summary>
/// Records a managed read of every graph tile in a generation artifact.
/// </summary>
public sealed record ValhallaTileSetReadReceipt(
    int TileCount,
    long TotalBytes,
    ulong NodeCount,
    ulong DirectedEdgeCount,
    string OutputTreeSha256,
    bool AllHeaderGraphIdsMatchPaths,
    bool AllHeaderLengthsMatchFiles,
    bool AllTileChecksumsMatch);

/// <summary>
/// Reads and validates a complete Valhalla graph tile set with the managed reader.
/// </summary>
public sealed class ManagedValhallaTileSetReader
{
    /// <summary>
    /// Opens every graph tile, traverses its fixed routing records, and validates header integrity.
    /// </summary>
    public async ValueTask<ValhallaTileSetReadReceipt> ReadAsync(
        string tileDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileDirectory);
        string fullDirectory = Path.GetFullPath(tileDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException($"Valhalla tile directory was not found: '{fullDirectory}'.");
        }

        string[] tilePaths = Directory
            .EnumerateFiles(fullDirectory, $"*{GraphTile.SuffixNonCompressed}", SearchOption.AllDirectories)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        if (tilePaths.Length == 0)
        {
            throw new InvalidDataException("The Valhalla tile directory contains no graph tiles.");
        }

        long totalBytes = 0;
        ulong nodeCount = 0;
        ulong directedEdgeCount = 0;
        bool graphIdsMatch = true;
        bool lengthsMatch = true;
        bool checksumsMatch = true;

        foreach (string tilePath in tilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphId pathId = GraphTile.GetTileId(tilePath).TileBase();
            byte[] bytes = await File.ReadAllBytesAsync(tilePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < GraphTileHeader.HeaderSize)
            {
                throw new InvalidDataException("A graph tile is shorter than the Valhalla header.");
            }

            GraphTile tile = GraphTile.Create(pathId, bytes);
            GraphTileHeader header = tile.Header();
            graphIdsMatch &= header.Graphid().TileBase() == pathId;
            lengthsMatch &= header.EndOffset() == bytes.Length;
            checksumsMatch &=
                GraphTileChecksum.ComputeTileHash(bytes.AsSpan(GraphTileHeader.HeaderSize)) ==
                header.TileChecksum();

            for (uint nodeIndex = 0; nodeIndex < header.Nodecount(); nodeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = tile.Node(checked((int)nodeIndex)).EdgeCount;
            }

            for (uint edgeIndex = 0; edgeIndex < header.Directededgecount(); edgeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DirectedEdge edge = tile.DirectedEdge(checked((int)edgeIndex));
                _ = edge.EndNode;
                _ = edge.Length;
                if (!edge.IsShortcut && !edge.IsTransitLine)
                {
                    _ = tile.EdgeInfo(edge).Shape().Count;
                }
            }

            totalBytes = checked(totalBytes + bytes.LongLength);
            nodeCount = checked(nodeCount + header.Nodecount());
            directedEdgeCount = checked(directedEdgeCount + header.Directededgecount());
        }

        if (!graphIdsMatch)
        {
            throw new InvalidDataException(
                "A Valhalla tile header graph identity does not match its artifact path.");
        }

        if (!lengthsMatch)
        {
            throw new InvalidDataException(
                "A Valhalla tile header length does not match its artifact length.");
        }

        if (!checksumsMatch)
        {
            throw new InvalidDataException(
                "A Valhalla tile body checksum does not match its 3.8 header.");
        }

        string treeHash = await new GenerationOutputTreeHasher()
            .ComputeSha256Async(fullDirectory, cancellationToken)
            .ConfigureAwait(false);

        return new ValhallaTileSetReadReceipt(
            tilePaths.Length,
            totalBytes,
            nodeCount,
            directedEdgeCount,
            treeHash,
            graphIdsMatch,
            lengthsMatch,
            checksumsMatch);
    }
}

/// <summary>
/// Controls the pinned official Valhalla container used as an independent graph-reader oracle.
/// </summary>
public sealed record OfficialValhallaContainerTileSetReaderOptions(
    string ImageReference,
    TimeSpan Timeout,
    long MemoryLimitBytes,
    int CpuLimit,
    int MaximumOutputBytes,
    string DockerExecutable = "docker")
{
    private static readonly Regex DigestPattern = new(
        @"^[a-z0-9._/-]+@sha256:[0-9a-fA-F]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>
    /// Validates that qualification is immutable and resource bounded.
    /// </summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ImageReference);
        ArgumentException.ThrowIfNullOrWhiteSpace(DockerExecutable);
        if (!DigestPattern.IsMatch(ImageReference))
        {
            throw new ArgumentException(
                "The official Valhalla image must be an immutable sha256 digest reference.",
                nameof(ImageReference));
        }

        if (Timeout <= TimeSpan.Zero || Timeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(Timeout),
                "The official reader timeout must be positive and no greater than one hour.");
        }

        if (MemoryLimitBytes < 64L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MemoryLimitBytes),
                "The official reader requires at least 64 MiB of bounded memory.");
        }

        if (CpuLimit is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CpuLimit),
                "The official reader CPU limit must be between 1 and 64.");
        }

        if (MaximumOutputBytes is < 1024 or > 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumOutputBytes),
                "The official reader output limit must be between 1 KiB and 1 GiB.");
        }
    }
}
/// <summary>
/// Records the stock Valhalla reader's successful location lookup over a managed tile set.
/// </summary>
public sealed record OfficialValhallaTileSetReadReceipt(
    string ReaderVersion,
    int MatchedEdgeCount,
    long ResponseBytes,
    string ResponseSha256,
    string SafeDiagnostics);

/// <summary>
/// Defines one bounded one-shot action executed by the pinned stock Valhalla service.
/// </summary>
public sealed record OfficialValhallaActionRequest(
    string Action,
    string RequestJson);

/// <summary>
/// Records the bounded output of one pinned stock Valhalla service action.
/// </summary>
public sealed record OfficialValhallaActionReceipt(
    string ReaderVersion,
    string Action,
    int ExitCode,
    ReadOnlyMemory<byte> Response,
    long ResponseBytes,
    string ResponseSha256,
    string SafeDiagnostics);

/// <summary>
/// Uses pinned stock Valhalla executables to prove official-reader compatibility with a
/// managed tile set.
/// </summary>
public sealed class OfficialValhallaContainerTileSetReader
{
    private const int MaximumDiagnosticBytes = 256 * 1024;
    private const int MaximumConfigurationBytes = 4 * 1024 * 1024;
    private const string LocateRequest =
        "{\"locations\":[{\"lat\":43.7384,\"lon\":7.4246}],\"verbose\":true}";
    private static readonly Regex VersionPattern = new(
        @"(?:valhalla_service\s+)?(?<version>\d+\.\d+\.\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);

    private readonly OfficialValhallaContainerTileSetReaderOptions _options;

    public OfficialValhallaContainerTileSetReader(
        OfficialValhallaContainerTileSetReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
    }

    /// <summary>
    /// Runs the stock reader without network access and returns a bounded integrity receipt.
    /// </summary>
    public async ValueTask<OfficialValhallaTileSetReadReceipt> ReadAsync(
        string tileDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileDirectory);
        string fullDirectory = Path.GetFullPath(tileDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException("The managed Valhalla tile directory was not found.");
        }

        if (!Directory.EnumerateFiles(
                fullDirectory,
                $"*{GraphTile.SuffixNonCompressed}",
                SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException("The managed Valhalla tile directory contains no graph tiles.");
        }

        ProcessReceipt inspection = await RunDockerAsync(
                ["image", "inspect", _options.ImageReference],
                maximumStandardOutputBytes: 256 * 1024,
                retainStandardOutput: false,
                containerName: null,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(inspection, fullDirectory, "The pinned official Valhalla image is unavailable.");

        string versionContainer = CreateContainerName("version");
        ProcessReceipt version = await RunDockerAsync(
                CreateContainerArguments(
                    "valhalla_service",
                    ["--version"],
                    versionContainer),
                maximumStandardOutputBytes: 64 * 1024,
                retainStandardOutput: true,
                versionContainer,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(version, fullDirectory, "The official Valhalla reader version probe failed.");

        string versionText = Encoding.UTF8.GetString(version.StandardOutput.Bytes);
        Match versionMatch = VersionPattern.Match(versionText);
        if (!versionMatch.Success)
        {
            throw new InvalidDataException("The official Valhalla reader returned an unrecognized version.");
        }

        string configurationContainer = CreateContainerName("config");
        ProcessReceipt configuration = await RunDockerAsync(
                CreateContainerArguments(
                    "valhalla_build_config",
                    [
                        "--logging-type",
                        "std_err",
                        "--logging-color",
                        "false",
                        "--mjolnir-tile-dir",
                        "/tiles",
                        "--mjolnir-tile-extract",
                        string.Empty,
                        "--mjolnir-admin",
                        "/disabled/admin.sqlite",
                        "--mjolnir-timezone",
                        "/disabled/timezones.sqlite",
                        "--mjolnir-data-processing-use-admin-db",
                        "false",
                    ],
                    configurationContainer),
                MaximumConfigurationBytes,
                retainStandardOutput: true,
                configurationContainer,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(
            configuration,
            fullDirectory,
            "The official Valhalla configuration builder failed.");
        byte[] serviceConfiguration =
            PrepareConfiguration(configuration.StandardOutput.Bytes);

        string configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-official-reader-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configurationDirectory);
        string configurationPath = Path.Combine(configurationDirectory, "valhalla.json");

        try
        {
            await File.WriteAllBytesAsync(
                    configurationPath,
                    serviceConfiguration,
                    cancellationToken)
                .ConfigureAwait(false);

            string locateContainer = CreateContainerName("locate");
            ProcessReceipt locate = await RunDockerAsync(
                    CreateContainerArguments(
                        "valhalla_service",
                        ["/qualification/valhalla.json", "locate", LocateRequest],
                        locateContainer,
                        fullDirectory,
                        configurationDirectory),
                    _options.MaximumOutputBytes,
                    retainStandardOutput: true,
                    locateContainer,
                    cancellationToken)
                .ConfigureAwait(false);
            EnsureSuccess(locate, fullDirectory, "The official Valhalla reader rejected the managed tiles.");
            int matchedEdgeCount = CountMatchedEdges(locate.StandardOutput.Bytes);
            if (matchedEdgeCount == 0)
            {
                throw new InvalidDataException(
                    "The official Valhalla reader found no edges in the managed graph.");
            }

            string diagnostics = RedactDiagnostics(
                Encoding.UTF8.GetString(locate.StandardError.Bytes),
                fullDirectory);
            return new OfficialValhallaTileSetReadReceipt(
                versionMatch.Groups["version"].Value,
                matchedEdgeCount,
                locate.StandardOutput.Length,
                locate.StandardOutput.Sha256,
                diagnostics);
        }
        finally
        {
            if (Directory.Exists(configurationDirectory))
            {
                Directory.Delete(configurationDirectory, recursive: true);
            }
        }
    }

    /// <summary>
    /// Executes one or more bounded stock Valhalla actions against one immutable tile directory.
    /// Image inspection, version verification, and service configuration are performed once.
    /// </summary>
    public async ValueTask<IReadOnlyList<OfficialValhallaActionReceipt>> ExecuteActionsAsync(
        string tileDirectory,
        IReadOnlyList<OfficialValhallaActionRequest> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tileDirectory);
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requests),
                "An official qualification batch must contain between 1 and 128 actions.");
        }

        string fullDirectory = Path.GetFullPath(tileDirectory);
        if (!Directory.Exists(fullDirectory))
        {
            throw new DirectoryNotFoundException("The Valhalla tile directory was not found.");
        }

        if (!Directory.EnumerateFiles(
                fullDirectory,
                $"*{GraphTile.SuffixNonCompressed}",
                SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException("The Valhalla tile directory contains no graph tiles.");
        }

        foreach (OfficialValhallaActionRequest request in requests)
        {
            ValidateActionRequest(request);
        }

        ProcessReceipt inspection = await RunDockerAsync(
                ["image", "inspect", _options.ImageReference],
                maximumStandardOutputBytes: 256 * 1024,
                retainStandardOutput: false,
                containerName: null,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(inspection, fullDirectory, "The pinned official Valhalla image is unavailable.");

        string versionContainer = CreateContainerName("action-version");
        ProcessReceipt version = await RunDockerAsync(
                CreateContainerArguments(
                    "valhalla_service",
                    ["--version"],
                    versionContainer),
                maximumStandardOutputBytes: 64 * 1024,
                retainStandardOutput: true,
                versionContainer,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(version, fullDirectory, "The official Valhalla action version probe failed.");

        string versionText = Encoding.UTF8.GetString(version.StandardOutput.Bytes);
        Match versionMatch = VersionPattern.Match(versionText);
        if (!versionMatch.Success)
        {
            throw new InvalidDataException("The official Valhalla action returned an unrecognized version.");
        }

        string configurationContainer = CreateContainerName("action-config");
        ProcessReceipt configuration = await RunDockerAsync(
                CreateContainerArguments(
                    "valhalla_build_config",
                    [
                        "--logging-type",
                        "std_err",
                        "--logging-color",
                        "false",
                        "--mjolnir-tile-dir",
                        "/tiles",
                        "--mjolnir-tile-extract",
                        string.Empty,
                        "--mjolnir-admin",
                        "/disabled/admin.sqlite",
                        "--mjolnir-timezone",
                        "/disabled/timezones.sqlite",
                        "--mjolnir-data-processing-use-admin-db",
                        "false",
                    ],
                    configurationContainer),
                MaximumConfigurationBytes,
                retainStandardOutput: true,
                configurationContainer,
                cancellationToken)
            .ConfigureAwait(false);
        EnsureSuccess(
            configuration,
            fullDirectory,
            "The official Valhalla action configuration builder failed.");

        byte[] serviceConfiguration = PrepareConfiguration(configuration.StandardOutput.Bytes);
        string configurationDirectory = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-official-actions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configurationDirectory);
        string configurationPath = Path.Combine(configurationDirectory, "valhalla.json");

        try
        {
            await File.WriteAllBytesAsync(
                    configurationPath,
                    serviceConfiguration,
                    cancellationToken)
                .ConfigureAwait(false);

            List<OfficialValhallaActionReceipt> receipts = new(requests.Count);
            foreach (OfficialValhallaActionRequest request in requests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string actionContainer = CreateContainerName(request.Action);
                ProcessReceipt action = await RunDockerAsync(
                        CreateContainerArguments(
                            "valhalla_service",
                            [
                                "/qualification/valhalla.json",
                                request.Action,
                                request.RequestJson,
                            ],
                            actionContainer,
                            fullDirectory,
                            configurationDirectory),
                        _options.MaximumOutputBytes,
                        retainStandardOutput: true,
                        actionContainer,
                        cancellationToken)
                    .ConfigureAwait(false);

                receipts.Add(
                    new OfficialValhallaActionReceipt(
                        versionMatch.Groups["version"].Value,
                        request.Action,
                        action.ExitCode,
                        action.StandardOutput.Bytes,
                        action.StandardOutput.Length,
                        action.StandardOutput.Sha256,
                        RedactDiagnostics(
                            Encoding.UTF8.GetString(action.StandardError.Bytes),
                            fullDirectory)));
            }

            return receipts;
        }
        finally
        {
            if (Directory.Exists(configurationDirectory))
            {
                Directory.Delete(configurationDirectory, recursive: true);
            }
        }
    }

    private static void ValidateActionRequest(OfficialValhallaActionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action is not ("locate" or "route"))
        {
            throw new ArgumentException(
                "Only locate and route qualification actions are supported.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.RequestJson) ||
            Encoding.UTF8.GetByteCount(request.RequestJson) > 1024 * 1024)
        {
            throw new ArgumentException(
                "The qualification request must be nonempty and no larger than 1 MiB.",
                nameof(request));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(request.RequestJson);
            EnsureSecretFreeJson(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException(
                "The qualification request must contain valid JSON.",
                nameof(request),
                exception);
        }
    }

    private static void EnsureSecretFreeJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    string name = property.Name;
                    if (name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new ArgumentException(
                            "Official qualification requests cannot contain credential-bearing fields.");
                    }

                    EnsureSecretFreeJson(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (JsonElement item in element.EnumerateArray())
                {
                    EnsureSecretFreeJson(item);
                }

                break;
        }
    }

    private IReadOnlyList<string> CreateContainerArguments(
        string entryPoint,
        IReadOnlyList<string> commandArguments,
        string containerName,
        string? tileDirectory = null,
        string? configurationDirectory = null)
    {
        List<string> arguments =
        [
            "run",
            "--rm",
            "--pull",
            "never",
            "--name",
            containerName,
            "--network",
            "none",
            "--memory",
            _options.MemoryLimitBytes.ToString(CultureInfo.InvariantCulture),
            "--cpus",
            _options.CpuLimit.ToString(CultureInfo.InvariantCulture),
        ];

        if (tileDirectory is not null)
        {
            arguments.Add("--mount");
            arguments.Add($"type=bind,source={tileDirectory},target=/tiles,readonly");
        }

        if (configurationDirectory is not null)
        {
            arguments.Add("--mount");
            arguments.Add(
                $"type=bind,source={configurationDirectory},target=/qualification,readonly");
        }

        arguments.Add("--entrypoint");
        arguments.Add(entryPoint);
        arguments.Add(_options.ImageReference);
        arguments.AddRange(commandArguments);
        return arguments;
    }

    private async Task<ProcessReceipt> RunDockerAsync(
        IReadOnlyList<string> arguments,
        int maximumStandardOutputBytes,
        bool retainStandardOutput,
        string? containerName,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = CreateProcessStartInfo(arguments);
        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("The Docker qualification process could not be started.");
        }

        using CancellationTokenSource timeout = new(_options.Timeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        Task<CapturedStream> standardOutput = CaptureAsync(
            process.StandardOutput.BaseStream,
            maximumStandardOutputBytes,
            retainStandardOutput,
            linked.Token);
        Task<CapturedStream> standardError = CaptureAsync(
            process.StandardError.BaseStream,
            MaximumDiagnosticBytes,
            retainBytes: true,
            linked.Token);
        Task processExit = process.WaitForExitAsync(linked.Token);

        try
        {
            Task first = await Task.WhenAny(processExit, standardOutput, standardError)
                .ConfigureAwait(false);
            if (first.IsFaulted || first.IsCanceled)
            {
                TryKill(process);
                await StopContainerAsync(containerName).ConfigureAwait(false);
            }

            await Task.WhenAll(processExit, standardOutput, standardError).ConfigureAwait(false);
        }
        catch
        {
            TryKill(process);
            await StopContainerAsync(containerName).ConfigureAwait(false);
            await WaitForTerminationAsync(process).ConfigureAwait(false);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The official Valhalla reader exceeded its time limit.");
            }

            throw;
        }

        return new ProcessReceipt(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }

    private ProcessStartInfo CreateProcessStartInfo(IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _options.DockerExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private async Task StopContainerAsync(string? containerName)
    {
        if (containerName is null)
        {
            return;
        }

        ProcessStartInfo stopInfo = new()
        {
            FileName = _options.DockerExecutable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        stopInfo.ArgumentList.Add("stop");
        stopInfo.ArgumentList.Add("--timeout");
        stopInfo.ArgumentList.Add("1");
        stopInfo.ArgumentList.Add(containerName);

        using Process stop = new() { StartInfo = stopInfo };
        if (!stop.Start())
        {
            return;
        }

        using CancellationTokenSource cleanupTimeout = new(TimeSpan.FromSeconds(10));
        try
        {
            await stop.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(stop);
        }
    }

    private static async Task<CapturedStream> CaptureAsync(
        Stream stream,
        int maximumBytes,
        bool retainBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using MemoryStream? retained = retainBytes ? new MemoryStream() : null;
        long length = 0;

        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                if (length > maximumBytes)
                {
                    throw new InvalidDataException(
                        "The official Valhalla reader exceeded its bounded output limit.");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                if (retained is not null)
                {
                    retained.Write(buffer, 0, read);
                }
            }

            return new CapturedStream(
                length,
                Convert.ToHexString(hash.GetHashAndReset()),
                retained?.ToArray() ?? []);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static byte[] PrepareConfiguration(byte[] configuration)
    {
        try
        {
            JsonNode? root = JsonNode.Parse(configuration);
            JsonObject? rootObject = root as JsonObject;
            JsonObject? mjolnir = rootObject?["mjolnir"] as JsonObject;
            string? tileDirectory = mjolnir?["tile_dir"]?.GetValue<string>();
            if (!string.Equals(tileDirectory, "/tiles", StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "The official Valhalla configuration did not preserve the exact tile mount.");
            }

            foreach (string optionalPath in new[]
                     {
                         "tile_extract",
                         "traffic_extract",
                         "admin",
                         "landmarks",
                         "timezone",
                         "transit_dir",
                         "transit_feeds_dir",
                         "incident_dir",
                         "incident_log",
                     })
            {
                _ = mjolnir!.Remove(optionalPath);
            }

            if (rootObject?["additional_data"] is JsonObject additionalData)
            {
                _ = additionalData.Remove("elevation");
            }

            return JsonSerializer.SerializeToUtf8Bytes(root);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The official Valhalla configuration builder returned invalid JSON.",
                exception);
        }
    }

    private static int CountMatchedEdges(byte[] response)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            return CountMatchedEdges(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The official Valhalla reader returned invalid JSON.",
                exception);
        }
    }

    private static int CountMatchedEdges(JsonElement element)
    {
        int count = 0;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (property.NameEquals("edges") &&
                    property.Value.ValueKind == JsonValueKind.Array)
                {
                    count = checked(count + property.Value.GetArrayLength());
                }
                else
                {
                    count = checked(count + CountMatchedEdges(property.Value));
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
            {
                count = checked(count + CountMatchedEdges(child));
            }
        }

        return count;
    }

    private static void EnsureSuccess(
        ProcessReceipt receipt,
        string tileDirectory,
        string message)
    {
        if (receipt.ExitCode == 0)
        {
            return;
        }

        string diagnostics = RedactDiagnostics(
            Encoding.UTF8.GetString(receipt.StandardError.Bytes),
            tileDirectory);
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(diagnostics)
                ? message
                : $"{message} {diagnostics}");
    }

    private static string RedactDiagnostics(string diagnostics, string tileDirectory)
    {
        string redacted = diagnostics
            .Replace(tileDirectory, "<tile-directory>", StringComparison.OrdinalIgnoreCase)
            .Replace(
                tileDirectory.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                "<tile-directory>",
                StringComparison.OrdinalIgnoreCase);
        return redacted.Trim();
    }

    private static string CreateContainerName(string operation) =>
        $"valhalla-dotnet-{operation}-{Guid.NewGuid():N}";

    private static void TryKill(Process process)
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

    private static async Task WaitForTerminationAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private sealed record CapturedStream(long Length, string Sha256, byte[] Bytes);

    private sealed record ProcessReceipt(
        int ExitCode,
        CapturedStream StandardOutput,
        CapturedStream StandardError);
}
