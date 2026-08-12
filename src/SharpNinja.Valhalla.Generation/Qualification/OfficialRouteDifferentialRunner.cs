using System.Globalization;
using System.Text.Json;
using SharpNinja.Valhalla.Generation.Differential;

namespace SharpNinja.Valhalla.Generation.Qualification;

/// <summary>
/// Defines one deterministic route-matrix case executed against both tile sets.
/// </summary>
public sealed record ValhallaRouteMatrixCase(
    string CaseId,
    string Costing,
    double OriginLatitude,
    double OriginLongitude,
    double DestinationLatitude,
    double DestinationLongitude);

/// <summary>
/// Runs a route matrix through the same pinned stock Valhalla 3.8.3 engine so the
/// differential measures generated tile semantics instead of runtime-engine differences.
/// </summary>
public sealed class OfficialValhallaContainerRouteMatrixRunner
{
    private static readonly HashSet<string> SupportedCostings =
        new(StringComparer.Ordinal)
        {
            "auto",
            "truck",
            "bicycle",
            "pedestrian",
            "transit",
        };

    private readonly OfficialValhallaContainerTileSetReader _reader;

    public OfficialValhallaContainerRouteMatrixRunner(
        OfficialValhallaContainerTileSetReaderOptions options)
    {
        _reader = new OfficialValhallaContainerTileSetReader(options);
    }

    /// <summary>
    /// Executes every case in stable input order and returns route metrics plus ordered
    /// OpenLR edge references. A structured Valhalla no-route response is represented as
    /// an unsuccessful matrix entry; infrastructure and malformed responses fail closed.
    /// </summary>
    public async ValueTask<IReadOnlyList<ValhallaRouteMatrixEntry>> RunAsync(
        string tileDirectory,
        IReadOnlyList<ValhallaRouteMatrixCase> cases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ValidateCases(cases);

        OfficialValhallaActionRequest[] requests = cases
            .Select(
                static routeCase =>
                    new OfficialValhallaActionRequest(
                        "route",
                        CreateRequestJson(routeCase)))
            .ToArray();

        IReadOnlyList<OfficialValhallaActionReceipt> receipts =
            await _reader.ExecuteActionsAsync(
                    tileDirectory,
                    requests,
                    cancellationToken)
                .ConfigureAwait(false);

        if (receipts.Count != cases.Count)
        {
            throw new InvalidDataException(
                "The official route qualification returned an incomplete action batch.");
        }

        ValhallaRouteMatrixEntry[] routes = new ValhallaRouteMatrixEntry[cases.Count];
        for (int index = 0; index < cases.Count; index++)
        {
            routes[index] = ParseRoute(cases[index].CaseId, receipts[index]);
        }

        return routes;
    }

    private static string CreateRequestJson(ValhallaRouteMatrixCase routeCase)
    {
        var request = new
        {
            locations = new[]
            {
                new
                {
                    lat = routeCase.OriginLatitude,
                    lon = routeCase.OriginLongitude,
                },
                new
                {
                    lat = routeCase.DestinationLatitude,
                    lon = routeCase.DestinationLongitude,
                },
            },
            costing = routeCase.Costing,
            units = "kilometers",
            linear_references = true,
            verbose = false,
        };
        return JsonSerializer.Serialize(request);
    }

    private static ValhallaRouteMatrixEntry ParseRoute(
        string caseId,
        OfficialValhallaActionReceipt receipt)
    {
        if (receipt.Response.IsEmpty)
        {
            throw new InvalidDataException(
                $"Route matrix case '{caseId}' returned no structured response. " +
                SafeDiagnosticSuffix(receipt.SafeDiagnostics));
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(receipt.Response);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error_code", out _))
            {
                return new ValhallaRouteMatrixEntry(caseId, false, 0, 0, []);
            }

            if (receipt.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"Route matrix case '{caseId}' exited with code {receipt.ExitCode}. " +
                    SafeDiagnosticSuffix(receipt.SafeDiagnostics));
            }

            JsonElement trip = root.GetProperty("trip");
            JsonElement summary = trip.GetProperty("summary");
            double lengthKilometers = summary.GetProperty("length").GetDouble();
            double durationSeconds = summary.GetProperty("time").GetDouble();
            string[] edgeReferences = trip
                .GetProperty("linear_references")
                .EnumerateArray()
                .Select(static item => item.GetString())
                .Where(static item => !string.IsNullOrEmpty(item))
                .Select(static item => item!)
                .ToArray();

            if (!double.IsFinite(lengthKilometers) ||
                lengthKilometers < 0 ||
                !double.IsFinite(durationSeconds) ||
                durationSeconds < 0 ||
                edgeReferences.Length == 0)
            {
                throw new InvalidDataException(
                    $"Route matrix case '{caseId}' returned incomplete route semantics.");
            }

            return new ValhallaRouteMatrixEntry(
                caseId,
                true,
                lengthKilometers * 1000,
                durationSeconds,
                edgeReferences);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Route matrix case '{caseId}' returned invalid JSON. " +
                SafeDiagnosticSuffix(receipt.SafeDiagnostics),
                exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new InvalidDataException(
                $"Route matrix case '{caseId}' returned an incomplete JSON contract. " +
                SafeDiagnosticSuffix(receipt.SafeDiagnostics),
                exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException(
                $"Route matrix case '{caseId}' returned an invalid JSON value. " +
                SafeDiagnosticSuffix(receipt.SafeDiagnostics),
                exception);
        }
    }

    private static void ValidateCases(IReadOnlyList<ValhallaRouteMatrixCase> cases)
    {
        if (cases.Count is < 1 or > 128)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cases),
                "A route matrix must contain between 1 and 128 cases.");
        }

        HashSet<string> caseIds = new(StringComparer.Ordinal);
        foreach (ValhallaRouteMatrixCase routeCase in cases)
        {
            ArgumentNullException.ThrowIfNull(routeCase);
            if (string.IsNullOrWhiteSpace(routeCase.CaseId) ||
                !caseIds.Add(routeCase.CaseId))
            {
                throw new ArgumentException(
                    "Route matrix case identities must be nonempty and unique.",
                    nameof(cases));
            }

            if (!SupportedCostings.Contains(routeCase.Costing))
            {
                throw new ArgumentException(
                    $"Route matrix case '{routeCase.CaseId}' uses unsupported costing " +
                    $"'{routeCase.Costing}'.",
                    nameof(cases));
            }

            ValidateCoordinate(
                routeCase.OriginLatitude,
                routeCase.OriginLongitude,
                routeCase.CaseId,
                "origin");
            ValidateCoordinate(
                routeCase.DestinationLatitude,
                routeCase.DestinationLongitude,
                routeCase.CaseId,
                "destination");
        }
    }

    private static void ValidateCoordinate(
        double latitude,
        double longitude,
        string caseId,
        string endpoint)
    {
        if (!double.IsFinite(latitude) ||
            latitude is < -90 or > 90 ||
            !double.IsFinite(longitude) ||
            longitude is < -180 or > 180)
        {
            throw new ArgumentOutOfRangeException(
                endpoint,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Route matrix case '{caseId}' has invalid {endpoint} coordinates " +
                    $"({latitude:R}, {longitude:R})."));
        }
    }

    private static string SafeDiagnosticSuffix(string diagnostics) =>
        string.IsNullOrWhiteSpace(diagnostics)
            ? "No diagnostics were returned."
            : diagnostics;
}
