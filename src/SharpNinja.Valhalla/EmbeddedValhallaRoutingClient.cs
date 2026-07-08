using System.Text.Json;

using Microsoft.Extensions.Logging;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;

namespace SharpNinja.Valhalla;

/// <summary>
/// In-process (embedded) <see cref="IOsmRoutingClient"/> that drives the ported Valhalla engine
/// (Baldr.GraphReader + Sif costing + Thor.RouteEngine + Odin.DirectionsBuilder) directly from a
/// local tile directory, replacing the removed legacy HTTP Valhalla client's path. It keeps the
/// exact result-shaping behavior the strategies and <c>FrictionModel</c> consume and reuses the
/// canonical <see cref="OsmRoutingErrorCodes"/> verbatim, so
/// <see cref="OsmNavigationStrategySupport"/> and the two strategies need no changes.
/// </summary>
/// <remarks>
/// Phase-4 known gaps (do not block):
/// <list type="bullet">
/// <item>Maneuver <c>Instruction</c> text is empty (the Odin narrative/prose pass is not ported).
/// Friction ranking and shape rendering are unaffected.</item>
/// <item>No alternate routes: the ported <see cref="RouteEngine.Route"/> returns a single
/// <see cref="TripLeg"/>. Friction ranking degrades to "rank of one", which is correct behavior.
/// <see cref="OsmRouteRequest.ComputeAlternativeRoutes"/> is therefore a no-op here.</item>
/// </list>
/// </remarks>
public sealed class EmbeddedValhallaRoutingClient : IOsmRoutingClient
{
	private readonly EmbeddedValhallaGraphReaderFactory _readerFactory;
	private readonly IOsmTileDirectoryProvider _tileDirectoryProvider;
	private readonly ILogger<EmbeddedValhallaRoutingClient> _logger;

	public EmbeddedValhallaRoutingClient(
		EmbeddedValhallaGraphReaderFactory readerFactory,
		IOsmTileDirectoryProvider tileDirectoryProvider,
		ILogger<EmbeddedValhallaRoutingClient> logger)
	{
		_readerFactory = readerFactory ?? throw new ArgumentNullException(nameof(readerFactory));
		_tileDirectoryProvider = tileDirectoryProvider ?? throw new ArgumentNullException(nameof(tileDirectoryProvider));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
	}

	public async Task<OsmRouteResult> CalculateRouteAsync(
		OsmRouteRequest request,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(request);

		// Tile dir is host configuration, not per-request data: the host resolves it (settings store,
		// tiles downloaded on demand from a tiles API, a bundled dir). request.Endpoint (the legacy
		// HTTP URL) is ignored here.
		var tileDirectory = await _tileDirectoryProvider.GetTileDirectoryAsync(cancellationToken).ConfigureAwait(false);

		if (!_readerFactory.TryGetReader(tileDirectory, out var lease))
		{
			// Tile dir unset / missing on disk / no tiles => same gate the legacy HTTP client applied
			// for a null endpoint.
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.NotConfigured);
		}

		// The engine work is CPU-bound and synchronous; run it on a worker thread so the async
		// contract holds. The reader's tile cache is not thread-safe and is shared across requests,
		// so serialize on the lease gate for the whole computation.
		return await Task.Run(
			() =>
			{
				lock (lease.Gate)
				{
					return RouteCore(request, lease.Reader, cancellationToken);
				}
			},
			cancellationToken).ConfigureAwait(false);
	}

	private OsmRouteResult RouteCore(OsmRouteRequest request, GraphReader reader, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			var costing = BuildCosting(request);

			var origin = new Location(ToPoint(request.Origin), Location.StopTypeValue.Break);
			var destination = new Location(ToPoint(request.Destination), Location.StopTypeValue.Break);
			var vias = BuildVias(request.Via);

			var engine = new RouteEngine(reader, () => cancellationToken.ThrowIfCancellationRequested());
			var tripLeg = engine.Route(reader, costing, origin, destination, vias);

			var options = new Options
			{
				DirectionsType = DirectionsType.Maneuvers,
				Units = OptionsUnits.Kilometers,
				RoundaboutExits = true,
			};
			var directionsLeg = DirectionsBuilder.Build(options, tripLeg);

			var candidate = MapCandidate(tripLeg, directionsLeg);
			return new OsmRouteResult(new[] { candidate }, null);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			// Mirror ValhallaRoutingClient: rethrow cancellation, never convert.
			throw;
		}
		catch (ValhallaException ex)
		{
			// DirectionsBuilder(210) empty node list or any engine ValhallaException.
			_logger.LogWarning(ex, "Embedded Valhalla directions build failed");
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.Parse);
		}
		catch (InvalidOperationException ex)
		{
			// No snap / no path / intermediate waypoint unreachable (RouteEngine throws this).
			_logger.LogWarning(ex, "Embedded Valhalla routing found no path");
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.Parse);
		}
		catch (IOException ex)
		{
			// Disk I/O reading tiles is the local analog of an HTTP transport failure.
			_logger.LogWarning(ex, "Embedded Valhalla tile I/O failure");
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.Transport);
		}
		catch (UnauthorizedAccessException ex)
		{
			_logger.LogWarning(ex, "Embedded Valhalla tile access failure");
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.Transport);
		}
		catch (Exception ex)
		{
			// Conservative: never surface a partial/corrupt route.
			_logger.LogWarning(ex, "Embedded Valhalla routing failed unexpectedly");
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.Parse);
		}
	}

	// ------------------------------------------------------------------
	// (a) OsmRouteRequest -> Sif costing
	// ------------------------------------------------------------------

	// Mirrors ValhallaRoutingClient.NormalizeCosting exactly: "truck" (case-insensitive) => truck,
	// anything else => auto.
	private static bool IsTruck(string? costing)
		=> string.Equals(costing, OsmRouteCostings.Truck, StringComparison.OrdinalIgnoreCase);

	private static DynamicCost BuildCosting(OsmRouteRequest request)
	{
		// Build the request-derived JSON keys, then run the real parser. The parser is the only
		// thing that populates the CostingOptions the coster reads (defaults + clamping + Has flags),
		// so driving it with the request-derived keys reproduces the exact stock-Valhalla behavior
		// ValhallaRoutingClient relied on (it only sent the keys below; everything else fell back to
		// the coster defaults the parser fills in). This is the lowest-drift faithful path.
		var warnings = new List<string>();
		var costing = new Costing();

		if (IsTruck(request.Costing))
		{
			using var doc = BuildTruckOptionsJson(request);
			TruckCostFactory.ParseTruckCostOptions(doc.RootElement, costing, warnings);
			return TruckCostFactory.CreateTruckCost(costing);
		}

		using (var doc = BuildAutoOptionsJson(request))
		{
			// ParseAutoCostOptions reads its keys from a child object under costingOptionsKey.
			AutoCostFactory.ParseAutoCostOptions(doc.RootElement, "auto", costing, warnings);
		}

		return AutoCostFactory.CreateAutoCost(costing);
	}

	private static JsonDocument BuildTruckOptionsJson(OsmRouteRequest request)
	{
		// Same null-options fallback the HTTP client used.
		var truck = request.TruckOptions ?? new OsmTruckRouteOptions(
			HeightMeters: 4.11,
			WidthMeters: 2.6,
			LengthMeters: 21.64,
			GrossWeightKilograms: 36_000,
			AxleCount: 5);

		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();

			// ParseTruckCostOptions reads top-level keys directly (no child object).
			writer.WriteNumber("height", truck.HeightMeters);
			writer.WriteNumber("width", truck.WidthMeters);
			writer.WriteNumber("length", truck.LengthMeters);
			writer.WriteNumber("weight", truck.WeightMetricTons);
			writer.WriteNumber("axle_count", truck.AxleCount);
			writer.WriteBoolean("exclude_tolls", request.AvoidTolls);
			writer.WriteBoolean("exclude_highways", request.AvoidHighways);

			// 0 disables the rule (TruckCost.TransitionCost); emit only when a positive threshold is
			// set, exactly like the HTTP client.
			if (request.UnprotectedLeftAvoidanceMeters > 0d)
			{
				writer.WriteNumber("unprotected_left_avoidance_meters", request.UnprotectedLeftAvoidanceMeters);
			}

			writer.WriteBoolean("enable_static_friction", request.EnableStaticFriction);

			writer.WriteEndObject();
		}

		return JsonDocument.Parse(stream.ToArray());
	}

	private static JsonDocument BuildAutoOptionsJson(OsmRouteRequest request)
	{
		using var stream = new MemoryStream();
		using (var writer = new Utf8JsonWriter(stream))
		{
			writer.WriteStartObject();

			// ParseAutoCostOptions reads keys from a child object under the costing-options key.
			writer.WritePropertyName("auto");
			writer.WriteStartObject();

			// Do NOT set truck dims for auto. AutoCost honors the shared avoid toggles AND the custom
			// unprotected_left_avoidance_meters (the hard left-turn safety rule applies to auto/taxi
			// too); it ignores enable_static_friction, which stays truck-only (auto comfort friction is
			// always on), so that key is intentionally not emitted here.
			writer.WriteBoolean("exclude_tolls", request.AvoidTolls);
			writer.WriteBoolean("exclude_highways", request.AvoidHighways);

			// 0 disables the rule; emit only when a positive threshold is set, exactly like truck.
			if (request.UnprotectedLeftAvoidanceMeters > 0d)
			{
				writer.WriteNumber("unprotected_left_avoidance_meters", request.UnprotectedLeftAvoidanceMeters);
			}

			writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return JsonDocument.Parse(stream.ToArray());
	}

	// ------------------------------------------------------------------
	// (b) GeoCoordinate -> Loki Location
	// ------------------------------------------------------------------

	// PointLL takes (lng, lat) - longitude FIRST.
	private static PointLL ToPoint(GeoCoordinate c) => new(c.Longitude, c.Latitude);

	private static List<Location>? BuildVias(IReadOnlyList<GeoCoordinate>? via)
	{
		if (via is null || via.Count == 0)
		{
			return null;
		}

		// Vias were "via" in the HTTP client; the ported engine routes THROUGH them and enables the
		// through-point low-reachability retry / u-turn avoidance via Through/BreakThrough.
		var list = new List<Location>(via.Count);
		foreach (var v in via)
		{
			list.Add(new Location(ToPoint(v), Location.StopTypeValue.Through));
		}

		return list;
	}

	// ------------------------------------------------------------------
	// (c) TripLeg + DirectionsLeg -> OsmRouteCandidate
	// ------------------------------------------------------------------

	private static OsmRouteCandidate MapCandidate(TripLeg tripLeg, DirectionsLeg directionsLeg)
	{
		// Distance: sum the leg edge lengths (km) -> meters. Equivalent to the HTTP summary.length*1000
		// without maneuver rounding drift.
		double distanceMeters = 0d;
		foreach (var edge in tripLeg.Edges)
		{
			distanceMeters += edge.LengthKm;
		}

		distanceMeters *= 1000d;

		// Duration: the leg's total elapsed time is on the last node. Round away-from-zero to int,
		// exactly like ValhallaRoutingClient.ReadDurationSeconds.
		var durationSeconds = tripLeg.Nodes.Count > 0
			? (int)Math.Round(tripLeg.Nodes[^1].ElapsedCost.Secs, MidpointRounding.AwayFromZero)
			: 0;

		// Encoded polyline6 (same string the HTTP client surfaced; DirectionsLeg.Shape mirrors it).
		var encodedShape = tripLeg.EncodedShape;

		// Decoded route points: the merged single leg already has a continuous shape, so no per-leg
		// offset/dedup is needed. Map PointLL(lng, lat) -> GeoCoordinate(lat, lng).
		var routePoints = new List<GeoCoordinate>(tripLeg.Shape.Count);
		foreach (var p in tripLeg.Shape)
		{
			routePoints.Add(new GeoCoordinate(p.Lat, p.Lng));
		}

		var maneuvers = new List<OsmRouteManeuver>(directionsLeg.Maneuvers.Count);
		foreach (var maneuver in directionsLeg.Maneuvers)
		{
			maneuvers.Add(MapManeuver(maneuver));
		}

		// Friction inputs: computed identically to ValhallaRoutingClient.TryParseTrip. This is the
		// single most important parity point: RouteCandidateMetrics.FromOsm + FrictionModel consume
		// these and the existing tests assert on them.
		var frictionInputs = new OsmRouteFrictionInputs(
			maneuvers.Count,
			maneuvers.Count(static m => m.Toll),
			maneuvers.Count(static m => m.Highway),
			maneuvers.Count(static m => m.Ferry),
			tripLeg.Summary.HasToll,
			tripLeg.Summary.HasHighway,
			tripLeg.Summary.HasFerry);

		return new OsmRouteCandidate(
			distanceMeters,
			durationSeconds,
			encodedShape,
			routePoints,
			maneuvers,
			frictionInputs);
	}

	private static OsmRouteManeuver MapManeuver(Maneuver maneuver)
		=> new(
			Type: (int)maneuver.Type(),
			// Prose is NOT ported (Odin narrative pass deferred); empty string is safe - the friction
			// model and shape rendering do not use it.
			Instruction: string.Empty,
			DistanceMeters: maneuver.Length(false) * 1000d,
			DurationSeconds: (int)Math.Round(maneuver.Time(), MidpointRounding.AwayFromZero),
			// Single merged leg: shape indices are leg-local with no offset.
			BeginShapeIndex: (int)maneuver.BeginShapeIndex(),
			EndShapeIndex: (int)maneuver.EndShapeIndex(),
			Toll: maneuver.PortionsToll(),
			Highway: maneuver.PortionsHighway(),
			Ferry: maneuver.Ferry(),
			// Not consumed by FrictionModel/strategies; null is safe.
			TravelMode: null,
			TravelType: null);
}
