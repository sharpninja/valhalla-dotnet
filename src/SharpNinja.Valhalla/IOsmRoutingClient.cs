using SharpNinja.Valhalla.Generations;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla;

/// <summary>
/// HTTP seam for self-hosted OSM routing engines. Phase 2 targets Valhalla,
/// but the request/result names stay provider-neutral so later strategy and
/// friction-selection code does not depend on Valhalla-specific DTOs.
/// </summary>
public interface IOsmRoutingClient
{
	Task<OsmRouteResult> CalculateRouteAsync(
		OsmRouteRequest request,
		CancellationToken cancellationToken = default);
}

public static class OsmRouteCostings
{
	public const string Auto = "auto";
	public const string Truck = "truck";
}

public sealed record OsmRouteRequest(
	Uri? Endpoint,
	GeoCoordinate Origin,
	GeoCoordinate Destination,
	string Costing = OsmRouteCostings.Auto,
	OsmTruckRouteOptions? TruckOptions = null,
	bool ComputeAlternativeRoutes = true,
	IReadOnlyList<GeoCoordinate>? Via = null,
	bool AvoidTolls = false,
	bool AvoidHighways = false,
	double UnprotectedLeftAvoidanceMeters = 0d,
	bool EnableStaticFriction = true)
{
	/// <summary>Optional immutable traffic generation pinned for this route.</summary>
	public TrafficSnapshotReference? TrafficSnapshot { get; init; }

	/// <summary>Single invariant departure instant used throughout traffic-aware routing.</summary>
	public DateTimeOffset? DepartureTimeUtc { get; init; }

	/// <summary>Optional exact distributed generation set pinned for the complete route acquisition.</summary>
	public ValhallaGenerationLease? GenerationLease { get; init; }
}

public sealed record OsmTruckRouteOptions(
	double HeightMeters,
	double WidthMeters,
	double LengthMeters,
	int GrossWeightKilograms,
	int AxleCount)
{
	public double WeightMetricTons => GrossWeightKilograms / 1000d;
}

public sealed record OsmRouteResult(
	IReadOnlyList<OsmRouteCandidate> Routes,
	string? Error)
{
	/// <summary>Typed snapshot failure when traffic-aware routing cannot be claimed safely.</summary>
	public TrafficSnapshotFailure? TrafficSnapshotFailure { get; init; }

	/// <summary>Exact immutable base/overlay generation evidence used by the route.</summary>
	public ValhallaRouteGenerationStamp? GenerationStamp { get; init; }

	public static OsmRouteResult Failure(string error) => new(Array.Empty<OsmRouteCandidate>(), error);

	public static OsmRouteResult TrafficFailure(TrafficSnapshotFailure failure)
	{
		ArgumentNullException.ThrowIfNull(failure);
		return new OsmRouteResult(Array.Empty<OsmRouteCandidate>(), "traffic_snapshot_invalid")
		{
			TrafficSnapshotFailure = failure,
		};
	}
}

public sealed record OsmRouteCandidate(
	double DistanceMeters,
	int DurationSeconds,
	string? EncodedPolyline,
	IReadOnlyList<GeoCoordinate> RoutePoints,
	IReadOnlyList<OsmRouteManeuver> Maneuvers,
	OsmRouteFrictionInputs FrictionInputs)
{
	/// <summary>
	/// Gets the packed canonical Valhalla <c>GraphId.Value</c> for each directed edge in route order.
	/// Legacy and non-graph providers may leave this value unspecified.
	/// </summary>
	public IReadOnlyList<ulong>? DirectedEdgeIds { get; init; }

	/// <summary>Authoritative source of the returned duration.</summary>
	public RouteDurationSource DurationSource { get; init; } = RouteDurationSource.FreeFlow;

	/// <summary>Content version of the traffic generation applied by the engine.</summary>
	public string? TrafficSnapshotVersion { get; init; }

	/// <summary>Delay already included by the engine; downstream consumers must not add it again.</summary>
	public int EngineAppliedTrafficDelaySeconds { get; init; }
}

public sealed record OsmRouteManeuver(
	int Type,
	string Instruction,
	double DistanceMeters,
	int DurationSeconds,
	int BeginShapeIndex,
	int EndShapeIndex,
	bool Toll = false,
	bool Highway = false,
	bool Ferry = false,
	string? TravelMode = null,
	string? TravelType = null);

public sealed record OsmRouteFrictionInputs(
	int ManeuverCount,
	int TollManeuverCount,
	int HighwayManeuverCount,
	int FerryManeuverCount,
	bool HasToll,
	bool HasHighway,
	bool HasFerry);
