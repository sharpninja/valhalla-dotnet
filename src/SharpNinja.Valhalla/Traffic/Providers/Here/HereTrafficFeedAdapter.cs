using System.Text.Json;

namespace SharpNinja.Valhalla.Traffic.Providers.Here;

/// <summary>Normalizes HERE flow, incident, closure, and restriction payloads.</summary>
public sealed class HereTrafficFeedAdapter : ITrafficFeedAdapter
{
	public string ProviderId => "here";

	public async Task<TrafficFeedNormalizationResult> NormalizeAsync(
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(payload);
		ArgumentNullException.ThrowIfNull(context);
		cancellationToken.ThrowIfCancellationRequested();

		return await Task.Run(
			() => NormalizeCoreAsync(payload, context, cancellationToken),
			cancellationToken).ConfigureAwait(false);
	}

	private static async Task<TrafficFeedNormalizationResult> NormalizeCoreAsync(
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		CancellationToken cancellationToken)
	{
		if (!string.Equals(payload.ProviderId, "here", StringComparison.OrdinalIgnoreCase))
		{
			return new TrafficFeedNormalizationResult(
				Array.Empty<NormalizedTrafficEvent>(),
				new[]
				{
					TrafficNormalizationJson.Diagnostic(
						payload,
						"TrafficProviderMismatch",
						$"HERE adapter cannot normalize provider '{payload.ProviderId}'."),
				});
		}

		try
		{
			using var stream = new MemoryStream(payload.Content.ToArray(), writable: false);
			using JsonDocument document = await JsonDocument.ParseAsync(
				stream,
				cancellationToken: cancellationToken).ConfigureAwait(false);
			cancellationToken.ThrowIfCancellationRequested();
			return NormalizeDocument(document.RootElement, payload, context, cancellationToken);
		}
		catch (JsonException)
		{
			return new TrafficFeedNormalizationResult(
				Array.Empty<NormalizedTrafficEvent>(),
				new[]
				{
					TrafficNormalizationJson.Diagnostic(
						payload,
						"MalformedTrafficPayload",
						"HERE traffic payload is not valid JSON."),
				});
		}
	}

	private static TrafficFeedNormalizationResult NormalizeDocument(
		JsonElement root,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		CancellationToken cancellationToken)
	{
		var events = new List<NormalizedTrafficEvent>();
		var diagnostics = new List<TrafficProviderDiagnostic>();
		JsonElement? records = TrafficNormalizationJson.Property(root, "results");
		if (records is null || records.Value.ValueKind != JsonValueKind.Array)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				"HERE traffic payload does not contain a results array."));
		}
		else
		{
			DateTimeOffset? sourceUpdated = TrafficNormalizationJson.DateTimeOffset(
				root,
				"sourceUpdated",
				"updatedAt");
			var ordinal = 0;
			foreach (JsonElement record in records.Value.EnumerateArray())
			{
				cancellationToken.ThrowIfCancellationRequested();
				NormalizeRecord(
					record,
					payload,
					context,
					sourceUpdated,
					events,
					diagnostics,
					ordinal++);
			}
		}

		return new TrafficFeedNormalizationResult(events, diagnostics);
	}

	private static void NormalizeRecord(
		JsonElement record,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		DateTimeOffset? sourceUpdated,
		List<NormalizedTrafficEvent> events,
		List<TrafficProviderDiagnostic> diagnostics,
		int ordinal)
	{
		JsonElement? currentFlow = TrafficNormalizationJson.Property(record, "currentFlow");
		JsonElement? incidentDetails = TrafficNormalizationJson.Property(record, "incidentDetails");
		bool normalized = false;

		if (currentFlow is not null
			&& payload.FeedKind is TrafficFeedKind.Flow or TrafficFeedKind.Composite)
		{
			normalized = NormalizeFlowRecord(
				record,
				currentFlow.Value,
				payload,
				context,
				sourceUpdated,
				events,
				diagnostics,
				ordinal);
		}

		if (incidentDetails is not null
			&& payload.FeedKind is TrafficFeedKind.Incident
				or TrafficFeedKind.Closure
				or TrafficFeedKind.Restriction
				or TrafficFeedKind.Composite)
		{
			normalized = NormalizeIncidentRecord(
				record,
				incidentDetails.Value,
				payload,
				context,
				sourceUpdated,
				events,
				diagnostics,
				ordinal) || normalized;
		}

		if (!normalized && currentFlow is null && incidentDetails is null)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				$"HERE traffic record {ordinal} contains neither currentFlow nor incidentDetails."));
		}
		else if (!normalized
			&& payload.FeedKind == TrafficFeedKind.Flow
			&& currentFlow is null)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				$"HERE flow record {ordinal} does not contain currentFlow."));
		}
		else if (!normalized
			&& payload.FeedKind is TrafficFeedKind.Incident
				or TrafficFeedKind.Closure
				or TrafficFeedKind.Restriction
			&& incidentDetails is null)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				$"HERE event record {ordinal} does not contain incidentDetails."));
		}
	}

	private static bool NormalizeFlowRecord(
		JsonElement record,
		JsonElement currentFlow,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		DateTimeOffset? sourceUpdated,
		List<NormalizedTrafficEvent> events,
		List<TrafficProviderDiagnostic> diagnostics,
		int ordinal)
	{
		JsonElement? location = TrafficNormalizationJson.Property(record, "location", "geometry");
		IReadOnlyList<GeoCoordinate> points = location is null
			? Array.Empty<GeoCoordinate>()
			: TrafficNormalizationJson.Coordinates(location.Value);
		TrafficGeometryDirection geometryDirection =
			location is null || !context.AllowNormalizedProxyExtensions
				? TrafficGeometryDirection.Unknown
				: TrafficNormalizationJson.ExplicitGeometryDirection(location.Value, currentFlow, record);
		double? speed = TrafficNormalizationJson.Double(currentFlow, "speed", "currentSpeed");
		double? freeFlow = TrafficNormalizationJson.Double(currentFlow, "freeFlow", "freeFlowSpeed");
		if (speed.HasValue)
		{
			speed *= 3.6d;
		}

		if (freeFlow.HasValue)
		{
			freeFlow *= 3.6d;
		}
		int? travelTime = TrafficNormalizationJson.Int(
			currentFlow,
			"traversalTime",
			"currentTravelTime",
			"currentTravelTimeSeconds");
		int? freeFlowTravelTime = TrafficNormalizationJson.Int(
			currentFlow,
			"freeFlowTravelTime",
			"freeFlowTravelTimeSeconds");
		if (points.Count < 2
			|| speed is null
			|| !double.IsFinite(speed.Value)
			|| speed.Value < 0d
			|| freeFlow is null
			|| !double.IsFinite(freeFlow.Value)
			|| freeFlow.Value <= 0d
			|| travelTime is < 0
			|| freeFlowTravelTime is < 0)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				$"HERE flow record {ordinal} has invalid geometry, speed, or travel-time data."));
			return false;
		}

		string id = TrafficNormalizationJson.String(record, "id")
			?? TrafficNormalizationJson.String(currentFlow, "id")
			?? TrafficNormalizationJson.FallbackId(payload, ordinal);
		int? delay = TrafficNormalizationJson.Delay(
			TrafficNormalizationJson.Int(currentFlow, "delay", "delaySeconds"),
			travelTime,
			freeFlowTravelTime);
		string? traversability = TrafficNormalizationJson.String(currentFlow, "traversability");
		double? jamFactor = TrafficNormalizationJson.Double(currentFlow, "jamFactor");
		bool closure = payload.FeedKind == TrafficFeedKind.Closure
			|| TrafficNormalizationJson.Bool(currentFlow, "roadClosed", "roadClosure") == true
			|| jamFactor is >= 10d
			|| string.Equals(traversability, "closed", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(
				traversability,
				"reversibleNotRoutable",
				StringComparison.OrdinalIgnoreCase);
		DateTimeOffset? validUntil = TrafficNormalizationJson.DateTimeOffset(
			currentFlow,
			"validUntil",
			"endTime");
		if (IsExpired(validUntil, context))
		{
			diagnostics.Add(ExpiredDiagnostic(payload, id));
			return false;
		}

		DateTimeOffset? observed = TrafficNormalizationJson.DateTimeOffset(
				currentFlow,
				"observedAt",
				"observationTime")
			?? TrafficNormalizationJson.MetadataDate(
				payload.ProviderMetadata,
				"observedAtUtc",
				"observedAt");
		DateTimeOffset? updated = TrafficNormalizationJson.DateTimeOffset(
				currentFlow,
				"updatedAt",
				"lastUpdatedTime")
			?? sourceUpdated
			?? TrafficNormalizationJson.MetadataDate(
				payload.ProviderMetadata,
				"updatedAtUtc",
				"updatedAt");
		var references = new Dictionary<string, string>(StringComparer.Ordinal);
		AddReference(references, "traversability", traversability);

		events.Add(new NormalizedTrafficEvent(
			id,
			payload.ProviderId,
			closure ? NormalizedTrafficEventKind.Closure : NormalizedTrafficEventKind.Flow,
			TrafficNormalizationJson.Geometry(points, geometryDirection),
			speed,
			freeFlow,
			travelTime,
			freeFlowTravelTime,
			delay,
			closure,
			FlowSeverity(speed.Value, freeFlow.Value, closure),
			TrafficNormalizationJson.Confidence(
				TrafficNormalizationJson.Double(currentFlow, "confidence")),
			TrafficNormalizationJson.String(currentFlow, "description"),
			observed,
			updated,
			payload.FetchedAtUtc,
			TrafficNormalizationJson.DateTimeOffset(currentFlow, "validFrom", "startTime"),
			validUntil,
			TrafficNormalizationJson.RedactedSourceUri(payload.SourceUri),
			references));
		return true;
	}

	private static bool NormalizeIncidentRecord(
		JsonElement record,
		JsonElement details,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		DateTimeOffset? sourceUpdated,
		List<NormalizedTrafficEvent> events,
		List<TrafficProviderDiagnostic> diagnostics,
		int ordinal)
	{
		JsonElement? location = TrafficNormalizationJson.Property(record, "location", "geometry")
			?? TrafficNormalizationJson.Property(details, "location", "geometry");
		TrafficGeometryDirection geometryDirection =
			location is null || !context.AllowNormalizedProxyExtensions
				? TrafficGeometryDirection.Unknown
				: TrafficNormalizationJson.ExplicitGeometryDirection(location.Value, details, record);
		TrafficRestrictionApplicability restrictionApplicability =
			context.AllowNormalizedProxyExtensions
				? TrafficNormalizationJson.RestrictionApplicability(details, record)
				: TrafficRestrictionApplicability.Unknown;
		IReadOnlyList<GeoCoordinate> points = location is null
			? Array.Empty<GeoCoordinate>()
			: TrafficNormalizationJson.Coordinates(location.Value);
		if (points.Count == 0)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				$"HERE event record {ordinal} is missing usable geometry."));
			return false;
		}

		string id = TrafficNormalizationJson.String(details, "id")
			?? TrafficNormalizationJson.String(record, "id")
			?? TrafficNormalizationJson.FallbackId(payload, ordinal);
		string? type = TrafficNormalizationJson.String(details, "type");
		bool closure = payload.FeedKind == TrafficFeedKind.Closure
			|| TrafficNormalizationJson.Bool(details, "roadClosed", "roadClosure") == true
			|| (!string.IsNullOrWhiteSpace(type)
				&& type.Contains("closure", StringComparison.OrdinalIgnoreCase));
		DateTimeOffset? validUntil = TrafficNormalizationJson.DateTimeOffset(
			details,
			"endTime",
			"validUntil");
		if (IsExpired(validUntil, context))
		{
			diagnostics.Add(ExpiredDiagnostic(payload, id));
			return false;
		}

		DateTimeOffset? observed = TrafficNormalizationJson.DateTimeOffset(
				details,
				"entryTime",
				"observedAt")
			?? TrafficNormalizationJson.MetadataDate(
				payload.ProviderMetadata,
				"observedAtUtc",
				"observedAt");
		DateTimeOffset? updated = TrafficNormalizationJson.DateTimeOffset(
				details,
				"lastUpdatedTime",
				"updatedAt")
			?? sourceUpdated
			?? TrafficNormalizationJson.MetadataDate(
				payload.ProviderMetadata,
				"updatedAtUtc",
				"updatedAt");
		var references = new Dictionary<string, string>(StringComparer.Ordinal);
		AddReference(references, "originalId", TrafficNormalizationJson.String(details, "originalId"));
		AddReference(references, "type", type);

		events.Add(new NormalizedTrafficEvent(
			id,
			payload.ProviderId,
			closure
				? NormalizedTrafficEventKind.Closure
				: payload.FeedKind == TrafficFeedKind.Restriction
					|| (!string.IsNullOrWhiteSpace(type)
						&& type.Contains("restriction", StringComparison.OrdinalIgnoreCase))
					? NormalizedTrafficEventKind.Restriction
					: NormalizedTrafficEventKind.Incident,
			TrafficNormalizationJson.Geometry(points, geometryDirection),
			null,
			null,
			null,
			null,
			TrafficNormalizationJson.Delay(
				TrafficNormalizationJson.Int(details, "delay", "delaySeconds"),
				null,
				null),
			closure,
			IncidentSeverity(TrafficNormalizationJson.String(details, "criticality"), closure),
			TrafficNormalizationJson.Confidence(
				TrafficNormalizationJson.Double(details, "confidence")),
			Description(details),
			observed,
			updated,
			payload.FetchedAtUtc,
			TrafficNormalizationJson.DateTimeOffset(details, "startTime", "validFrom"),
			validUntil,
			TrafficNormalizationJson.RedactedSourceUri(payload.SourceUri),
			references,
			restrictionApplicability));
		return true;
	}

	private static string? Description(JsonElement details)
	{
		JsonElement? description = TrafficNormalizationJson.Property(details, "description");
		return description is { ValueKind: JsonValueKind.Object }
			? TrafficNormalizationJson.String(description.Value, "value")
			: TrafficNormalizationJson.String(details, "description");
	}

	private static TrafficSeverity FlowSeverity(double speed, double freeFlow, bool closure)
	{
		if (closure)
		{
			return TrafficSeverity.Closed;
		}

		if (freeFlow <= 0d)
		{
			return TrafficSeverity.Unknown;
		}

		double ratio = Math.Max(0d, speed) / freeFlow;
		return ratio switch
		{
			<= 0.5d => TrafficSeverity.Heavy,
			< 0.85d => TrafficSeverity.Moderate,
			_ => TrafficSeverity.FreeFlow,
		};
	}

	private static TrafficSeverity IncidentSeverity(string? criticality, bool closure)
	{
		if (closure)
		{
			return TrafficSeverity.Closed;
		}

		return criticality?.ToLowerInvariant() switch
		{
			"critical" => TrafficSeverity.Critical,
			"major" => TrafficSeverity.Major,
			"moderate" => TrafficSeverity.Moderate,
			"minor" or "low" => TrafficSeverity.Minor,
			_ => TrafficSeverity.Unknown,
		};
	}

	private static bool IsExpired(DateTimeOffset? validUntil, TrafficNormalizationContext context)
		=> validUntil is not null && validUntil.Value <= context.EvaluationTimeUtc;

	private static TrafficProviderDiagnostic ExpiredDiagnostic(
		RawTrafficFeedPayload payload,
		string id)
		=> TrafficNormalizationJson.Diagnostic(
			payload,
			"ExpiredTrafficEvent",
			$"HERE event '{id}' expired before the normalization evaluation time.");

	private static void AddReference(
		IDictionary<string, string> references,
		string key,
		string? value)
	{
		if (!string.IsNullOrWhiteSpace(value))
		{
			references[key] = value;
		}
	}
}
