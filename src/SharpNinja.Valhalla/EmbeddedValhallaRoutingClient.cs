using System.Text.Json;

using Microsoft.Extensions.Logging;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;
using SharpNinja.Valhalla.Thor;
using SharpNinja.Valhalla.Traffic.Routing;
using SharpNinja.Valhalla.Traffic.Tiles;

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
/// Known gaps (do not block):
/// <list type="bullet">
/// <item>Maneuver <c>Instruction</c> prose is produced by the ported Odin NarrativeBuilder (en-US
/// written turn-by-turn text). Verbal / additional-locale strings are later parity slices but are not
/// surfaced by <see cref="OsmRouteManeuver"/>.</item>
/// <item>Alternate routes: when <see cref="OsmRouteRequest.ComputeAlternativeRoutes"/> is set and no
/// vias are supplied, the engine computes multiple distinct routes (sharing / stretch viability
/// filtered) and <see cref="OsmRouteResult.Routes"/> carries them primary-first, then by ascending
/// cost. Small maps may still yield a single route when no viable alternate exists.</item>
/// </list>
/// </remarks>
public sealed class EmbeddedValhallaRoutingClient : IOsmRoutingClient
{
	// Number of alternate routes requested when ComputeAlternativeRoutes is set (primary + up to this
	// many alternates). The viability filters may return fewer if the map has no distinct alternates.
	private const uint DefaultAlternateRouteCount = 2u;

	private readonly EmbeddedValhallaGraphReaderFactory _readerFactory;
	private readonly IOsmTileDirectoryProvider _tileDirectoryProvider;
	private readonly ILogger<EmbeddedValhallaRoutingClient> _logger;
	private readonly TimeProvider _timeProvider;

	public EmbeddedValhallaRoutingClient(
		EmbeddedValhallaGraphReaderFactory readerFactory,
		IOsmTileDirectoryProvider tileDirectoryProvider,
		ILogger<EmbeddedValhallaRoutingClient> logger,
		TimeProvider? timeProvider = null)
	{
		_readerFactory = readerFactory ?? throw new ArgumentNullException(nameof(readerFactory));
		_tileDirectoryProvider = tileDirectoryProvider ?? throw new ArgumentNullException(nameof(tileDirectoryProvider));
		_logger = logger ?? throw new ArgumentNullException(nameof(logger));
		_timeProvider = timeProvider ?? TimeProvider.System;
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

		EmbeddedValhallaGraphReaderFactory.AsyncLease lease;
		try
		{
			lease = await _readerFactory.AcquireAsync(
				tileDirectory,
				request.TrafficSnapshot,
				cancellationToken).ConfigureAwait(false);
		}
		catch (TrafficSnapshotStoreException exception)
		{
			return OsmRouteResult.TrafficFailure(new TrafficSnapshotFailure(
				exception.Code,
				exception.Message,
				request.TrafficSnapshot?.Version));
		}
		catch (DirectoryNotFoundException exception) when (request.TrafficSnapshot is not null)
		{
			return OsmRouteResult.TrafficFailure(new TrafficSnapshotFailure(
				TrafficSnapshotFailureCode.Missing,
				exception.Message,
				request.TrafficSnapshot.Version));
		}
		catch (Exception exception) when (request.TrafficSnapshot is not null
		                                  && (exception is IOException || exception is UnauthorizedAccessException))
		{
			return OsmRouteResult.TrafficFailure(new TrafficSnapshotFailure(
				TrafficSnapshotFailureCode.Unreadable,
				"Traffic snapshot data could not be acquired.",
				request.TrafficSnapshot.Version));
		}
		catch (DirectoryNotFoundException)
		{
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.NotConfigured);
		}

		await using (lease.ConfigureAwait(false))
		{
			// The engine work is CPU-bound and synchronous; run it on a worker thread while the
			// async lease pins this exact reader and traffic generation through result materialization.
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
	}

	private OsmRouteResult RouteCore(OsmRouteRequest request, GraphReader reader, CancellationToken cancellationToken)
	{
		try
		{
			cancellationToken.ThrowIfCancellationRequested();

			var costing = BuildCosting(request);

			TimeInfo? trafficTime = request.TrafficSnapshot is null
				? null
				: InvariantTrafficTime.Create(request.DepartureTimeUtc ?? _timeProvider.GetUtcNow());
			var origin = new Location(ToPoint(request.Origin), Location.StopTypeValue.Break)
			{
				TimeInfo = trafficTime,
			};
			var destination = new Location(ToPoint(request.Destination), Location.StopTypeValue.Break)
			{
				TimeInfo = trafficTime,
			};
			var vias = BuildVias(request.Via);
			if (trafficTime is not null && vias is not null)
			{
				foreach (Location via in vias)
				{
					via.TimeInfo = trafficTime;
				}
			}

			// Alternate routes are the route axis: distinct whole routes for a single origin/destination
			// pair. They are not meaningful when the caller pins via/through waypoints (that is the leg
			// axis), so alternates are only requested when ComputeAlternativeRoutes is set and there are
			// no vias. Default count is 2 (primary + up to 2 alternates).
			uint alternates = request.ComputeAlternativeRoutes && (request.Via is null || request.Via.Count == 0)
				? DefaultAlternateRouteCount
				: 0u;

			var options = new Options
			{
				// Instructions runs the Odin NarrativeBuilder to populate maneuver prose (Instruction);
				// Maneuvers would produce structure only. en-US is the surfaced language.
				DirectionsType = DirectionsType.Instructions,
				Units = OptionsUnits.Kilometers,
				RoundaboutExits = true,
				Language = "en-US",
				Alternates = alternates,
				HasAlternates = alternates != 0,
				DateTimeType = request.TrafficSnapshot is null
					? DateTimeType.NoTime
					: DateTimeType.Invariant,
				HasDateTimeType = request.TrafficSnapshot is not null,
			};

			var engine = new RouteEngine(reader, () => cancellationToken.ThrowIfCancellationRequested());
			IReadOnlyList<TripLeg> legs = engine.RouteAlternates(reader, costing, origin, destination, vias, options);

			// Fan out: one OsmRouteCandidate per route (primary first, alternates by ascending cost).
			var candidates = new List<OsmRouteCandidate>(legs.Count);
			foreach (var leg in legs)
			{
				var directionsLeg = DirectionsBuilder.Build(options, leg);
				EngineTrafficApplication trafficApplication =
					request.TrafficSnapshot is null || trafficTime is null
						? default
						: CalculateEngineAppliedTraffic(
							leg,
							reader,
							request,
							trafficTime.Value);
				candidates.Add(MapCandidate(
					leg,
					directionsLeg,
					request.TrafficSnapshot,
					reader,
					trafficApplication.DelaySeconds));
			}

			return new OsmRouteResult(candidates, null);
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
		catch (TrafficSnapshotStoreException ex)
		{
			_logger.LogWarning(ex, "Embedded Valhalla traffic snapshot failure");
			return OsmRouteResult.TrafficFailure(new TrafficSnapshotFailure(
				ex.Code,
				ex.Message,
				request.TrafficSnapshot?.Version));
		}
		catch (IOException ex) when (request.TrafficSnapshot is not null)
		{
			_logger.LogWarning(ex, "Embedded Valhalla traffic snapshot I/O failure");
			return OsmRouteResult.TrafficFailure(new TrafficSnapshotFailure(
				TrafficSnapshotFailureCode.Unreadable,
				"Traffic snapshot data became unreadable during routing.",
				request.TrafficSnapshot.Version));
		}
		catch (IOException ex)
		{
			// Disk I/O reading tiles is the local analog of an HTTP transport failure.
			_logger.LogWarning(ex, "Embedded Valhalla tile I/O failure");
			return OsmRouteResult.Failure(OsmRoutingErrorCodes.Transport);
		}
		catch (UnauthorizedAccessException ex) when (request.TrafficSnapshot is not null)
		{
			_logger.LogWarning(ex, "Embedded Valhalla traffic snapshot access failure");
			return OsmRouteResult.TrafficFailure(new TrafficSnapshotFailure(
				TrafficSnapshotFailureCode.Unreadable,
				"Traffic snapshot data became unreadable during routing.",
				request.TrafficSnapshot.Version));
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

	private static DynamicCost BuildCosting(
		OsmRouteRequest request,
		bool includeCurrentTraffic = true)
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
			using var doc = BuildTruckOptionsJson(request, includeCurrentTraffic);
			TruckCostFactory.ParseTruckCostOptions(doc.RootElement, costing, warnings);
			return TruckCostFactory.CreateTruckCost(costing);
		}

		using (var doc = BuildAutoOptionsJson(request, includeCurrentTraffic))
		{
			// ParseAutoCostOptions reads its keys from a child object under costingOptionsKey.
			AutoCostFactory.ParseAutoCostOptions(doc.RootElement, "auto", costing, warnings);
		}

		return AutoCostFactory.CreateAutoCost(costing);
	}

	private static JsonDocument BuildTruckOptionsJson(
		OsmRouteRequest request,
		bool includeCurrentTraffic)
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
				WriteTrafficSpeedTypesIfNeeded(writer, request, includeCurrentTraffic);

			writer.WriteEndObject();
		}

		return JsonDocument.Parse(stream.ToArray());
	}

	private static JsonDocument BuildAutoOptionsJson(
		OsmRouteRequest request,
		bool includeCurrentTraffic)
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

				WriteTrafficSpeedTypesIfNeeded(writer, request, includeCurrentTraffic);
				writer.WriteEndObject();
			writer.WriteEndObject();
		}

		return JsonDocument.Parse(stream.ToArray());
	}

		private static void WriteTrafficSpeedTypesIfNeeded(
			Utf8JsonWriter writer,
			OsmRouteRequest request,
			bool includeCurrentTraffic)
		{
			if (request.TrafficSnapshot is null)
			{
				return;
			}

			writer.WritePropertyName("speed_types");
			writer.WriteStartArray();
			writer.WriteStringValue("freeflow");
			writer.WriteStringValue("constrained");
			if (includeCurrentTraffic)
			{
				writer.WriteStringValue("current");
			}
			writer.WriteEndArray();
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

	internal static OsmRouteCandidate MapCandidate(
		TripLeg tripLeg,
		DirectionsLeg directionsLeg,
		TrafficSnapshotReference? trafficSnapshot = null,
		GraphReader? reader = null,
		int engineAppliedTrafficDelaySeconds = 0)
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

		// Preserve the canonical packed GraphId.Value for every directed edge in the exact Thor path
		// order. Primary and alternate TripLeg instances are mapped independently, so downstream
		// traffic-control and modifier joins operate on the candidate's real graph identity.
		IReadOnlyList<ulong> directedEdgeIds = Array.AsReadOnly(
			tripLeg.Edges.Select(static edge => edge.EdgeId.Value).ToArray());
		bool liveTrafficApplied = trafficSnapshot is not null
			&& reader is not null
			&& HasEngineAppliedTraffic(tripLeg, reader);
		// The final TripLeg node already contains Thor's traffic-aware elapsed cost. Map it unchanged;
		// the supplied delay is provenance from a same-path, no-current engine recost and must never
		// be added here or by downstream ranking.

		return new OsmRouteCandidate(
			distanceMeters,
			durationSeconds,
			encodedShape,
			routePoints,
			maneuvers,
			frictionInputs)
		{
				DirectedEdgeIds = directedEdgeIds,
				DurationSource = liveTrafficApplied
					? RouteDurationSource.LiveTraffic
					: RouteDurationSource.FreeFlow,
				TrafficSnapshotVersion = trafficSnapshot?.Version,
				EngineAppliedTrafficDelaySeconds = liveTrafficApplied
					? engineAppliedTrafficDelaySeconds
					: 0,
		};
	}

	private readonly record struct EngineTrafficApplication(bool Applied, int DelaySeconds);

	private static EngineTrafficApplication CalculateEngineAppliedTraffic(
		TripLeg tripLeg,
		GraphReader reader,
		OsmRouteRequest request,
		TimeInfo timeInfo)
	{
		if (tripLeg.Edges.Count == 0
			|| tripLeg.Nodes.Count == 0
			|| !HasEngineAppliedTraffic(tripLeg, reader))
		{
			return default;
		}

		DynamicCost noCurrentCosting = BuildCosting(request, includeCurrentTraffic: false);
		GraphId[] edgeIds = tripLeg.Edges
			.Select(static edge => edge.EdgeId)
			.ToArray();
		var labels = new List<PathEdgeLabel>(edgeIds.Length);
		int edgeIndex = 0;
		GraphId NextEdge() => edgeIndex < edgeIds.Length
			? edgeIds[edgeIndex++]
			: GraphId.Invalid;

		Recost.Forward(
			reader,
			noCurrentCosting,
			NextEdge,
			labels.Add,
			tripLeg.Edges[0].SourceAlongEdge,
			tripLeg.Edges[^1].TargetAlongEdge,
			timeInfo,
			invariant: true,
			ignoreAccess: true);
		if (labels.Count != edgeIds.Length)
		{
			throw new InvalidOperationException(
				"Same-path no-current recost did not materialize every routed edge.");
		}

		int activeDurationSeconds = (int)Math.Round(
			tripLeg.Nodes[^1].ElapsedCost.Secs,
			MidpointRounding.AwayFromZero);
		int noCurrentDurationSeconds = (int)Math.Round(
			labels[^1].Cost().Secs,
			MidpointRounding.AwayFromZero);
		return new EngineTrafficApplication(
			Applied: true,
			DelaySeconds: Math.Max(0, activeDurationSeconds - noCurrentDurationSeconds));
	}

	private static bool HasEngineAppliedTraffic(TripLeg tripLeg, GraphReader reader)
	{
		foreach (TripEdge tripEdge in tripLeg.Edges)
		{
			GraphTile? tile = reader.GetGraphTile(tripEdge.EdgeId);
			if (tile?.GetTrafficTile().TrafficSpeed(tripEdge.EdgeId.Id()).SpeedValid() == true)
			{
				return true;
			}
		}

		return false;
	}

		private static OsmRouteManeuver MapManeuver(Maneuver maneuver)
		=> new(
			Type: (int)maneuver.Type(),
			// Odin NarrativeBuilder prose (written turn-by-turn text), produced when the route is built
			// with DirectionsType.Instructions above.
			Instruction: maneuver.Instruction(),
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
