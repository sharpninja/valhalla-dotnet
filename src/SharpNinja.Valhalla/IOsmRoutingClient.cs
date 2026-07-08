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
	bool EnableStaticFriction = true);

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
	public static OsmRouteResult Failure(string error) => new(Array.Empty<OsmRouteCandidate>(), error);
}

public sealed record OsmRouteCandidate(
	double DistanceMeters,
	int DurationSeconds,
	string? EncodedPolyline,
	IReadOnlyList<GeoCoordinate> RoutePoints,
	IReadOnlyList<OsmRouteManeuver> Maneuvers,
	OsmRouteFrictionInputs FrictionInputs);

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
