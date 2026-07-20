using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpNinja.Valhalla.Traffic.Routing;

public enum LaneTopologyOverlayProvenance
{
    Curated = 0,
    Surveyed = 1,
    Provider = 2,
    Test = 3,
}

public sealed record LaneTopologyOverlayDescriptor(
    int SchemaVersion,
    string DatasetId,
    string DatasetVersion,
    string GraphSignature,
    LaneTopologyOverlayProvenance Provenance,
    string? SourceReference = null);

public sealed record CanonicalLaneEdgeOverlay(
    ulong CanonicalDirectedEdgeId,
    ulong CanonicalStartNodeId,
    ulong CanonicalEndNodeId,
    int LaneCount);

public sealed record CanonicalLaneTransitionOverlay
{
    private IReadOnlyList<LaneTransitionOption> _options =
        Array.Empty<LaneTransitionOption>();

    [JsonConstructor]
    public CanonicalLaneTransitionOverlay(
        ulong FromCanonicalDirectedEdgeId,
        ulong ToCanonicalDirectedEdgeId,
        ulong SharedCanonicalNodeId,
        IReadOnlyList<LaneTransitionOption> Options,
        LaneTopologyChangeKind ChangeKind,
        bool TruckSensitive,
        string Rationale)
    {
        this.FromCanonicalDirectedEdgeId = FromCanonicalDirectedEdgeId;
        this.ToCanonicalDirectedEdgeId = ToCanonicalDirectedEdgeId;
        this.SharedCanonicalNodeId = SharedCanonicalNodeId;
        this.Options = Options;
        this.ChangeKind = ChangeKind;
        this.TruckSensitive = TruckSensitive;
        this.Rationale = Rationale;
    }

    public ulong FromCanonicalDirectedEdgeId { get; init; }

    public ulong ToCanonicalDirectedEdgeId { get; init; }

    public ulong SharedCanonicalNodeId { get; init; }

    public IReadOnlyList<LaneTransitionOption> Options
    {
        get => _options;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _options = Array.AsReadOnly(value.ToArray());
        }
    }

    public LaneTopologyChangeKind ChangeKind { get; init; }

    public bool TruckSensitive { get; init; }

    public string Rationale { get; init; } = string.Empty;
}

public sealed record CanonicalLaneFrictionOverlay(
    ulong CanonicalDirectedEdgeId,
    int LaneNumber,
    double DistanceAlongEdgeMeters,
    LaneFrictionContributionKind Kind,
    int Severity,
    bool TruckSensitive,
    string Rationale);

public sealed record CanonicalLaneTopologyOverlay
{
    private IReadOnlyList<CanonicalLaneEdgeOverlay> _edges =
        Array.Empty<CanonicalLaneEdgeOverlay>();
    private IReadOnlyList<CanonicalLaneTransitionOverlay> _transitions =
        Array.Empty<CanonicalLaneTransitionOverlay>();
    private IReadOnlyList<CanonicalLaneFrictionOverlay> _frictionPoints =
        Array.Empty<CanonicalLaneFrictionOverlay>();

    [JsonConstructor]
    public CanonicalLaneTopologyOverlay(
        LaneTopologyOverlayDescriptor Descriptor,
        IReadOnlyList<CanonicalLaneEdgeOverlay> Edges,
        IReadOnlyList<CanonicalLaneTransitionOverlay> Transitions,
        IReadOnlyList<CanonicalLaneFrictionOverlay> FrictionPoints)
    {
        this.Descriptor = Descriptor ??
            throw new ArgumentNullException(nameof(Descriptor));
        this.Edges = Edges;
        this.Transitions = Transitions;
        this.FrictionPoints = FrictionPoints;
    }

    public LaneTopologyOverlayDescriptor Descriptor { get; init; }

    public IReadOnlyList<CanonicalLaneEdgeOverlay> Edges
    {
        get => _edges;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Any(static edge => edge is null))
            {
                throw new ArgumentException(
                    "Overlay edges cannot contain null entries.",
                    nameof(value));
            }

            _edges = Array.AsReadOnly(value.ToArray());
        }
    }

    public IReadOnlyList<CanonicalLaneTransitionOverlay> Transitions
    {
        get => _transitions;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Any(static transition => transition is null))
            {
                throw new ArgumentException(
                    "Overlay transitions cannot contain null entries.",
                    nameof(value));
            }

            _transitions = Array.AsReadOnly(value
            .Select(static transition => new CanonicalLaneTransitionOverlay(
                transition.FromCanonicalDirectedEdgeId,
                transition.ToCanonicalDirectedEdgeId,
                transition.SharedCanonicalNodeId,
                transition.Options,
                transition.ChangeKind,
                transition.TruckSensitive,
                transition.Rationale))
            .ToArray());
        }
    }

    public IReadOnlyList<CanonicalLaneFrictionOverlay> FrictionPoints
    {
        get => _frictionPoints;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (value.Any(static point => point is null))
            {
                throw new ArgumentException(
                    "Overlay friction points cannot contain null entries.",
                    nameof(value));
            }

            _frictionPoints = Array.AsReadOnly(value.ToArray());
        }
    }
}

public sealed record LaneTopologyOverlayRequest
{
    public LaneTopologyOverlayRequest(
        string graphSignature,
        IReadOnlyList<ulong> canonicalDirectedEdgeIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(graphSignature);
        ArgumentNullException.ThrowIfNull(canonicalDirectedEdgeIds);
        GraphSignature = graphSignature;
        CanonicalDirectedEdgeIds = canonicalDirectedEdgeIds;
    }

    public string GraphSignature { get; }

    public IReadOnlyList<ulong> CanonicalDirectedEdgeIds { get; }
}

public interface ILaneTopologyOverlaySource
{
    ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
        LaneTopologyOverlayRequest request,
        CancellationToken cancellationToken = default);
}

public enum LaneTopologyOverlayLoadStatus
{
    NotFound = 0,
    Loaded = 1,
    Invalid = 2,
}

public enum LaneTopologyOverlayDiagnosticCode
{
    UnsupportedSchemaVersion = 0,
    MalformedPayload = 1,
    TransportFailure = 2,
    PayloadTooLarge = 3,
    GraphSignatureMismatch = 4,
    CanonicalEdgeMissing = 5,
    CanonicalNodeMismatch = 6,
    LaneCountMismatch = 7,
    SharedCanonicalNodeMismatch = 8,
    LaneOutOfRange = 9,
    DuplicateCanonicalEdge = 10,
    DuplicateCanonicalTransition = 11,
    InvalidMetadata = 12,
    DuplicateCanonicalFrictionPoint = 13,
}

public sealed record LaneTopologyOverlayDiagnostic(
    LaneTopologyOverlayDiagnosticCode Code,
    string Message);

public sealed record LaneTopologyOverlayLoadResult(
    LaneTopologyOverlayLoadStatus Status,
    string SourceId,
    CanonicalLaneTopologyOverlay? Overlay,
    IReadOnlyList<LaneTopologyOverlayDiagnostic> Diagnostics)
{
    public static LaneTopologyOverlayLoadResult NotFound(string sourceId)
        => new(
            LaneTopologyOverlayLoadStatus.NotFound,
            sourceId,
            null,
            Array.Empty<LaneTopologyOverlayDiagnostic>());

    public static LaneTopologyOverlayLoadResult Loaded(
        CanonicalLaneTopologyOverlay overlay,
        string? sourceId = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        return new(
            LaneTopologyOverlayLoadStatus.Loaded,
            sourceId ?? overlay.Descriptor.DatasetId,
            overlay,
            Array.Empty<LaneTopologyOverlayDiagnostic>());
    }

    public static LaneTopologyOverlayLoadResult Invalid(
        string sourceId,
        params LaneTopologyOverlayDiagnostic[] diagnostics)
        => new(
            LaneTopologyOverlayLoadStatus.Invalid,
            sourceId,
            null,
            Array.AsReadOnly(diagnostics ?? []));
}

public sealed record LaneTopologyOverlayValidationResult(
    bool IsValid,
    CanonicalLaneTopologyOverlay? Overlay,
    IReadOnlyList<LaneTopologyOverlayDiagnostic> Diagnostics);

public static class LaneTopologyOverlayValidator
{
    public const int MaximumFrictionSeverity = 10_000;

    public static LaneTopologyOverlayValidationResult Validate(
        CanonicalLaneTopologyOverlay overlay,
        string exactGraphSignature,
        IReadOnlyDictionary<ulong, LaneTopologySegment> graphSegments)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentException.ThrowIfNullOrWhiteSpace(exactGraphSignature);
        ArgumentNullException.ThrowIfNull(graphSegments);
        var diagnostics = new List<LaneTopologyOverlayDiagnostic>();

        if (overlay.Descriptor is null)
        {
            Add(
                diagnostics,
                LaneTopologyOverlayDiagnosticCode.InvalidMetadata,
                "The overlay descriptor is required.");
        }
        else
        {
            ValidateDescriptor(overlay.Descriptor, exactGraphSignature, diagnostics);
        }

        var overlayEdges = new Dictionary<ulong, CanonicalLaneEdgeOverlay>();
        foreach (CanonicalLaneEdgeOverlay edge in overlay.Edges ?? [])
        {
            if (!overlayEdges.TryAdd(edge.CanonicalDirectedEdgeId, edge))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.DuplicateCanonicalEdge,
                    "The overlay contains a duplicate canonical directed edge.");
                continue;
            }

            if (!graphSegments.TryGetValue(
                    edge.CanonicalDirectedEdgeId,
                    out LaneTopologySegment? segment))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.CanonicalEdgeMissing,
                    "A canonical directed edge from the overlay is absent from the active graph.");
                continue;
            }

            LaneTopologyGraphEvidence? evidence = segment.GraphEvidence;
            if (evidence is null ||
                evidence.CanonicalStartNodeId != edge.CanonicalStartNodeId ||
                evidence.CanonicalEndNodeId != edge.CanonicalEndNodeId)
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.CanonicalNodeMismatch,
                    "The overlay canonical edge endpoints do not match the active graph.");
            }

            if (segment.LaneCount != edge.LaneCount)
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.LaneCountMismatch,
                    "The overlay lane count does not match the active graph.");
            }
        }

        var transitionKeys = new HashSet<LaneTransitionKey>();
        foreach (CanonicalLaneTransitionOverlay transition in overlay.Transitions ?? [])
        {
            if (!Enum.IsDefined(transition.ChangeKind) ||
                string.IsNullOrWhiteSpace(transition.Rationale))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.InvalidMetadata,
                    "An overlay transition contains invalid metadata.");
            }

            var key = new LaneTransitionKey(
                transition.FromCanonicalDirectedEdgeId,
                transition.ToCanonicalDirectedEdgeId);
            if (!transitionKeys.Add(key))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.DuplicateCanonicalTransition,
                    "The overlay contains a duplicate canonical transition.");
                continue;
            }

            if (!graphSegments.TryGetValue(
                    transition.FromCanonicalDirectedEdgeId,
                    out LaneTopologySegment? from) ||
                !graphSegments.TryGetValue(
                    transition.ToCanonicalDirectedEdgeId,
                    out LaneTopologySegment? to))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.CanonicalEdgeMissing,
                    "A canonical transition edge is absent from the active graph.");
                continue;
            }

            if (from.GraphEvidence is null ||
                to.GraphEvidence is null ||
                from.GraphEvidence.CanonicalEndNodeId !=
                    transition.SharedCanonicalNodeId ||
                to.GraphEvidence.CanonicalStartNodeId !=
                    transition.SharedCanonicalNodeId)
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.SharedCanonicalNodeMismatch,
                    "The overlay transition shared node does not match the active graph.");
            }

            if (transition.Options is null || transition.Options.Count == 0)
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.LaneOutOfRange,
                    "The overlay transition must contain at least one lane option.");
                continue;
            }

            if (transition.Options.Any(option =>
                    option.FromLane < 1 ||
                    option.FromLane > from.LaneCount ||
                    option.ToLane < 1 ||
                    option.ToLane > to.LaneCount))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.LaneOutOfRange,
                    "An overlay transition lane is outside the active graph lane count.");
            }
        }

        var frictionKeys = new HashSet<(
            ulong CanonicalDirectedEdgeId,
            int LaneNumber,
            long DistanceMillimeters,
            LaneFrictionContributionKind Kind)>();
        foreach (CanonicalLaneFrictionOverlay point in overlay.FrictionPoints ?? [])
        {
            bool distanceCanBeKeyed =
                double.IsFinite(point.DistanceAlongEdgeMeters) &&
                point.DistanceAlongEdgeMeters >= 0d &&
                point.DistanceAlongEdgeMeters <= long.MaxValue / 1_000d;
            long distanceMillimeters = distanceCanBeKeyed
                ? (long)Math.Round(
                    point.DistanceAlongEdgeMeters * 1_000d,
                    MidpointRounding.AwayFromZero)
                : long.MinValue;
            if (!frictionKeys.Add((
                    point.CanonicalDirectedEdgeId,
                    point.LaneNumber,
                    distanceMillimeters,
                    point.Kind)))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.DuplicateCanonicalFrictionPoint,
                    "The overlay contains a duplicate canonical friction point.");
                continue;
            }

            if (!graphSegments.TryGetValue(
                    point.CanonicalDirectedEdgeId,
                    out LaneTopologySegment? segment))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.CanonicalEdgeMissing,
                    "A canonical friction point edge is absent from the active graph.");
                continue;
            }

            if (point.LaneNumber < 1 ||
                point.LaneNumber > segment.LaneCount ||
                !distanceCanBeKeyed ||
                point.DistanceAlongEdgeMeters > segment.LengthMeters)
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.LaneOutOfRange,
                    "A canonical friction point is outside the active graph edge bounds.");
            }

            if (!Enum.IsDefined(point.Kind) ||
                point.Severity < 0 ||
                point.Severity > MaximumFrictionSeverity ||
                string.IsNullOrWhiteSpace(point.Rationale))
            {
                Add(
                    diagnostics,
                    LaneTopologyOverlayDiagnosticCode.InvalidMetadata,
                    "A canonical friction point contains invalid metadata.");
            }
        }

        return diagnostics.Count == 0
            ? new LaneTopologyOverlayValidationResult(
                true,
                overlay,
                Array.Empty<LaneTopologyOverlayDiagnostic>())
            : new LaneTopologyOverlayValidationResult(
                false,
                null,
                diagnostics.AsReadOnly());
    }

    private static void ValidateDescriptor(
        LaneTopologyOverlayDescriptor descriptor,
        string exactGraphSignature,
        List<LaneTopologyOverlayDiagnostic> diagnostics)
    {
        if (descriptor.SchemaVersion != LaneTopologyOverlayJson.CurrentSchemaVersion)
        {
            Add(
                diagnostics,
                LaneTopologyOverlayDiagnosticCode.UnsupportedSchemaVersion,
                "The lane topology overlay schema version is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(descriptor.DatasetId) ||
            string.IsNullOrWhiteSpace(descriptor.DatasetVersion) ||
            !Enum.IsDefined(descriptor.Provenance))
        {
            Add(
                diagnostics,
                LaneTopologyOverlayDiagnosticCode.InvalidMetadata,
                "The lane topology overlay dataset identity is invalid.");
        }

        if (!string.Equals(
                descriptor.GraphSignature,
                exactGraphSignature,
                StringComparison.Ordinal))
        {
            Add(
                diagnostics,
                LaneTopologyOverlayDiagnosticCode.GraphSignatureMismatch,
                "The lane topology overlay graph signature does not match the active graph.");
        }
    }

    private static void Add(
        List<LaneTopologyOverlayDiagnostic> diagnostics,
        LaneTopologyOverlayDiagnosticCode code,
        string message)
        => diagnostics.Add(new LaneTopologyOverlayDiagnostic(code, message));
}

public sealed class JsonFileLaneTopologyOverlaySource : ILaneTopologyOverlaySource
{
    private readonly string _path;

    public JsonFileLaneTopologyOverlaySource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public async ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
        LaneTopologyOverlayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_path))
        {
            return LaneTopologyOverlayLoadResult.NotFound(_path);
        }

        try
        {
            await using FileStream stream = new(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                useAsync: true);
            return await LaneTopologyOverlayJson.ReadAsync(
                    stream,
                    _path,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return LaneTopologyOverlayLoadResult.Invalid(
                _path,
                new LaneTopologyOverlayDiagnostic(
                    LaneTopologyOverlayDiagnosticCode.MalformedPayload,
                    "The local lane topology overlay could not be read."));
        }
    }
}

public sealed class HttpLaneTopologyOverlaySource : ILaneTopologyOverlaySource
{
    public const int DefaultMaximumPayloadBytes = 4 * 1024 * 1024;

    private readonly HttpMessageInvoker _transport;
    private readonly Uri _exactUrl;
    private readonly int _maximumPayloadBytes;

    public HttpLaneTopologyOverlaySource(
        HttpMessageInvoker transport,
        Uri exactUrl,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(exactUrl);
        if (!exactUrl.IsAbsoluteUri ||
            (exactUrl.Scheme != Uri.UriSchemeHttp &&
             exactUrl.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException(
                "An absolute HTTP(S) lane topology overlay URL is required.",
                nameof(exactUrl));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPayloadBytes, 1);
        _transport = transport;
        _exactUrl = exactUrl;
        _maximumPayloadBytes = maximumPayloadBytes;
    }

    public async ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
        LaneTopologyOverlayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, _exactUrl);
        try
        {
            using HttpResponseMessage response = await _transport
                .SendAsync(httpRequest, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return LaneTopologyOverlayLoadResult.NotFound(
                    RedactUrl(_exactUrl));
            }

            if (!response.IsSuccessStatusCode)
            {
                return InvalidTransport();
            }

            if (response.Content.Headers.ContentLength > _maximumPayloadBytes)
            {
                return PayloadTooLarge();
            }

            await using Stream responseStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var bounded = new MemoryStream(
                Math.Min(_maximumPayloadBytes, 16_384));
            byte[] buffer = new byte[Math.Min(_maximumPayloadBytes, 16_384)];
            while (true)
            {
                int read = await responseStream
                    .ReadAsync(buffer, cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (bounded.Length + read > _maximumPayloadBytes)
                {
                    return PayloadTooLarge();
                }

                await bounded.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            bounded.Position = 0;
            return await LaneTopologyOverlayJson.ReadAsync(
                    bounded,
                    RedactUrl(_exactUrl),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return InvalidTransport();
        }
    }

    private LaneTopologyOverlayLoadResult InvalidTransport()
        => LaneTopologyOverlayLoadResult.Invalid(
            RedactUrl(_exactUrl),
            new LaneTopologyOverlayDiagnostic(
                LaneTopologyOverlayDiagnosticCode.TransportFailure,
                "The remote lane topology overlay request failed."));

    private LaneTopologyOverlayLoadResult PayloadTooLarge()
        => LaneTopologyOverlayLoadResult.Invalid(
            RedactUrl(_exactUrl),
            new LaneTopologyOverlayDiagnostic(
                LaneTopologyOverlayDiagnosticCode.PayloadTooLarge,
                "The remote lane topology overlay exceeded the configured payload limit."));

    private static string RedactUrl(Uri url)
    {
        var redacted = new UriBuilder(
            url.Scheme,
            url.Host,
            url.IsDefaultPort ? -1 : url.Port,
            "/redacted")
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return redacted.Uri.AbsoluteUri;
    }
}

public sealed class CompositeLaneTopologyOverlaySource : ILaneTopologyOverlaySource
{
    private readonly ILaneTopologyOverlaySource[] _sources;

    public CompositeLaneTopologyOverlaySource(
        IReadOnlyList<ILaneTopologyOverlaySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0 || sources.Any(static source => source is null))
        {
            throw new ArgumentException(
                "At least one non-null overlay source is required.",
                nameof(sources));
        }

        _sources = sources.ToArray();
    }

    public async ValueTask<LaneTopologyOverlayLoadResult> LoadAsync(
        LaneTopologyOverlayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        foreach (ILaneTopologyOverlaySource source in _sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LaneTopologyOverlayLoadResult result = await source
                .LoadAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (result.Status != LaneTopologyOverlayLoadStatus.NotFound)
            {
                return result;
            }
        }

        return LaneTopologyOverlayLoadResult.NotFound("composite");
    }
}

internal static class LaneTopologyOverlayJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static async Task<LaneTopologyOverlayLoadResult> ReadAsync(
        Stream stream,
        string sourceId,
        CancellationToken cancellationToken)
    {
        try
        {
            CanonicalLaneTopologyOverlay? overlay =
                await JsonSerializer.DeserializeAsync<CanonicalLaneTopologyOverlay>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (overlay is null)
            {
                return Malformed(sourceId);
            }

            if (overlay.Descriptor.SchemaVersion != CurrentSchemaVersion)
            {
                return LaneTopologyOverlayLoadResult.Invalid(
                    sourceId,
                    new LaneTopologyOverlayDiagnostic(
                        LaneTopologyOverlayDiagnosticCode.UnsupportedSchemaVersion,
                        "The lane topology overlay schema version is unsupported."));
            }

            return LaneTopologyOverlayLoadResult.Loaded(overlay, sourceId);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or ArgumentException or InvalidOperationException)
        {
            return Malformed(sourceId);
        }
    }

    private static LaneTopologyOverlayLoadResult Malformed(string sourceId)
        => LaneTopologyOverlayLoadResult.Invalid(
            sourceId,
            new LaneTopologyOverlayDiagnostic(
                LaneTopologyOverlayDiagnosticCode.MalformedPayload,
                "The lane topology overlay payload is malformed."));
}
