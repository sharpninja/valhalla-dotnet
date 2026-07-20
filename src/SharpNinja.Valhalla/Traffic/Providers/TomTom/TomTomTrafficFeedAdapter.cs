using System.Text.Json;

using SharpNinja.Valhalla.Traffic.Providers;

namespace SharpNinja.Valhalla.Traffic.Providers.TomTom;

public sealed class TomTomTrafficFeedAdapter : ITrafficFeedAdapter
{
	public string ProviderId => "tomtom";

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
		if (!string.Equals(payload.ProviderId, "tomtom", StringComparison.OrdinalIgnoreCase))
		{
			return new TrafficFeedNormalizationResult(
				Array.Empty<NormalizedTrafficEvent>(),
				new[]
				{
					TrafficNormalizationJson.Diagnostic(
						payload,
						"TrafficProviderMismatch",
						$"TomTom adapter cannot normalize provider '{payload.ProviderId}'."),
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
						"TomTom traffic payload is not valid JSON."),
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
		if (payload.FeedKind is TrafficFeedKind.Flow or TrafficFeedKind.Composite)
		{
			NormalizeFlow(root, payload, context, events, diagnostics, cancellationToken);
		}

		if (payload.FeedKind is TrafficFeedKind.Incident
			or TrafficFeedKind.Closure
			or TrafficFeedKind.Restriction
			or TrafficFeedKind.Composite)
		{
			NormalizeIncidents(root, payload, context, events, diagnostics, cancellationToken);
		}

		return new TrafficFeedNormalizationResult(events, diagnostics);
	}

	private static void NormalizeFlow(
		JsonElement root,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		List<NormalizedTrafficEvent> events,
		List<TrafficProviderDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		JsonElement? flowData = TrafficNormalizationJson.Property(root, "flowSegmentData");
		if (flowData is null)
		{
			if (payload.FeedKind == TrafficFeedKind.Flow)
			{
				diagnostics.Add(TrafficNormalizationJson.Diagnostic(
					payload,
					"MalformedTrafficRecord",
					"TomTom flow payload does not contain flowSegmentData."));
			}

			return;
		}

		if (flowData.Value.ValueKind == JsonValueKind.Array)
		{
			var ordinal = 0;
			foreach (JsonElement record in flowData.Value.EnumerateArray())
			{
				cancellationToken.ThrowIfCancellationRequested();
				NormalizeFlowRecord(record, payload, context, events, diagnostics, ordinal++);
			}

			return;
		}

		NormalizeFlowRecord(flowData.Value, payload, context, events, diagnostics, 0);
	}

	private static void NormalizeFlowRecord(
		JsonElement record,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		List<NormalizedTrafficEvent> events,
		List<TrafficProviderDiagnostic> diagnostics,
		int ordinal)
	{
		JsonElement? geometryElement = TrafficNormalizationJson.Property(record, "coordinates", "geometry");
		IReadOnlyList<GeoCoordinate> points = geometryElement is null
			? Array.Empty<GeoCoordinate>()
			: TrafficNormalizationJson.Coordinates(geometryElement.Value);
		TrafficGeometryDirection geometryDirection =
			context.AllowNormalizedProxyExtensions
				? TrafficNormalizationJson.ExplicitGeometryDirection(record)
				: TrafficGeometryDirection.Unknown;
		double? currentSpeed = TrafficNormalizationJson.Double(record, "currentSpeed");
		double? freeFlowSpeed = TrafficNormalizationJson.Double(record, "freeFlowSpeed");
		if (!TryNormalizeSpeedUnit(payload, ref currentSpeed, ref freeFlowSpeed))
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"UnsupportedTrafficSpeedUnit",
				$"TomTom flow record {ordinal} uses an unsupported speed unit."));
			return;
		}
		int? currentTravelTime = TrafficNormalizationJson.Int(record, "currentTravelTime", "currentTravelTimeSeconds");
		int? freeFlowTravelTime = TrafficNormalizationJson.Int(record, "freeFlowTravelTime", "freeFlowTravelTimeSeconds");
		if (points.Count < 2
			|| currentSpeed is null
			|| !double.IsFinite(currentSpeed.Value)
			|| currentSpeed.Value < 0d
			|| freeFlowSpeed is null
			|| !double.IsFinite(freeFlowSpeed.Value)
			|| freeFlowSpeed.Value <= 0d
			|| currentTravelTime is < 0
			|| freeFlowTravelTime is < 0)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				$"TomTom flow record {ordinal} has invalid geometry, speed, or travel-time data."));
			return;
		}

		string id = TrafficNormalizationJson.String(record, "id")
			?? TrafficNormalizationJson.FallbackId(payload, ordinal);
		int? delay = TrafficNormalizationJson.Delay(
			TrafficNormalizationJson.Int(record, "delay", "delaySeconds"),
			currentTravelTime,
			freeFlowTravelTime);
		bool closure = TrafficNormalizationJson.Bool(record, "roadClosure", "roadClosed") == true;
		DateTimeOffset? observed = TrafficNormalizationJson.DateTimeOffset(record, "observedAt", "observationTime")
			?? TrafficNormalizationJson.MetadataDate(payload.ProviderMetadata, "observedAtUtc", "observedAt");
		DateTimeOffset? updated = TrafficNormalizationJson.DateTimeOffset(record, "updatedAt", "lastUpdatedTime")
			?? TrafficNormalizationJson.MetadataDate(
				payload.ProviderMetadata,
				"updatedAtUtc",
				"updatedAt",
				"Last-Modified",
				"Date");
		DateTimeOffset? validFrom = TrafficNormalizationJson.DateTimeOffset(record, "validFrom", "startTime");
		DateTimeOffset? validUntil = TrafficNormalizationJson.DateTimeOffset(record, "validUntil", "endTime");
		if (IsExpired(validUntil, context))
		{
			diagnostics.Add(ExpiredDiagnostic(payload, id));
			return;
		}

		var references = new Dictionary<string, string>(StringComparer.Ordinal);
		AddReference(references, "frc", TrafficNormalizationJson.String(record, "frc"));
		AddReference(references, "TrafficModelID", MetadataValue(payload.ProviderMetadata, "TrafficModelID"));
		AddReference(references, "ETag", MetadataValue(payload.ProviderMetadata, "ETag"));
		events.Add(new NormalizedTrafficEvent(
			id,
			payload.ProviderId,
			closure ? NormalizedTrafficEventKind.Closure : NormalizedTrafficEventKind.Flow,
			TrafficNormalizationJson.Geometry(points, geometryDirection),
			currentSpeed,
			freeFlowSpeed,
			currentTravelTime,
			freeFlowTravelTime,
			delay,
			closure,
			FlowSeverity(currentSpeed.Value, freeFlowSpeed.Value, closure),
			TrafficNormalizationJson.Confidence(TrafficNormalizationJson.Double(record, "confidence")),
			TrafficNormalizationJson.String(record, "description"),
			observed,
			updated,
			payload.FetchedAtUtc,
			validFrom,
			validUntil,
			TrafficNormalizationJson.RedactedSourceUri(payload.SourceUri),
			references));
	}

	private static void NormalizeIncidents(
		JsonElement root,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		List<NormalizedTrafficEvent> events,
		List<TrafficProviderDiagnostic> diagnostics,
		CancellationToken cancellationToken)
	{
		JsonElement? records = TrafficNormalizationJson.Property(root, "incidents", "features");
		if (records is null || records.Value.ValueKind != JsonValueKind.Array)
		{
			if (payload.FeedKind != TrafficFeedKind.Composite)
			{
				diagnostics.Add(TrafficNormalizationJson.Diagnostic(
					payload,
					"MalformedTrafficRecord",
					"TomTom incident payload does not contain an incidents array."));
			}

			return;
		}

		var ordinal = 0;
		foreach (JsonElement record in records.Value.EnumerateArray())
		{
			cancellationToken.ThrowIfCancellationRequested();
			NormalizeIncidentRecord(record, payload, context, events, diagnostics, ordinal++);
		}
	}

	private static void NormalizeIncidentRecord(
		JsonElement record,
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		List<NormalizedTrafficEvent> events,
		List<TrafficProviderDiagnostic> diagnostics,
		int ordinal)
	{
		JsonElement properties = TrafficNormalizationJson.Property(record, "properties") ?? record;
		JsonElement? geometryElement = TrafficNormalizationJson.Property(record, "geometry")
			?? TrafficNormalizationJson.Property(properties, "geometry");
		TrafficGeometryDirection geometryDirection =
			context.AllowNormalizedProxyExtensions
				? TrafficNormalizationJson.ExplicitGeometryDirection(properties, record)
				: TrafficGeometryDirection.Unknown;
		TrafficRestrictionApplicability restrictionApplicability =
			context.AllowNormalizedProxyExtensions
				? TrafficNormalizationJson.RestrictionApplicability(properties, record)
				: TrafficRestrictionApplicability.Unknown;
		IReadOnlyList<GeoCoordinate> points = geometryElement is null
			? Array.Empty<GeoCoordinate>()
			: TrafficNormalizationJson.Coordinates(geometryElement.Value);
		if (points.Count == 0)
		{
			diagnostics.Add(TrafficNormalizationJson.Diagnostic(
				payload,
				"MalformedTrafficRecord",
				$"TomTom incident record {ordinal} is missing usable geometry."));
			return;
		}

		string id = TrafficNormalizationJson.String(properties, "id")
			?? TrafficNormalizationJson.FallbackId(payload, ordinal);
		int magnitude = Math.Max(0, TrafficNormalizationJson.Int(properties, "magnitudeOfDelay") ?? 0);
		int iconCategory = Math.Max(0, TrafficNormalizationJson.Int(properties, "iconCategory") ?? 0);
		string? eventType = TrafficNormalizationJson.String(properties, "eventKind", "type");
		bool closure = payload.FeedKind == TrafficFeedKind.Closure
			|| TrafficNormalizationJson.Bool(properties, "roadClosed", "roadClosure") == true
			|| iconCategory == 8
			|| (!string.IsNullOrWhiteSpace(eventType)
				&& eventType.Contains("closure", StringComparison.OrdinalIgnoreCase));
		bool restriction = payload.FeedKind == TrafficFeedKind.Restriction
			|| (!string.IsNullOrWhiteSpace(eventType)
				&& eventType.Contains("restriction", StringComparison.OrdinalIgnoreCase));
		DateTimeOffset? validFrom = TrafficNormalizationJson.DateTimeOffset(properties, "startTime", "validFrom");
		DateTimeOffset? validUntil = TrafficNormalizationJson.DateTimeOffset(properties, "endTime", "validUntil");
		if (IsExpired(validUntil, context))
		{
			diagnostics.Add(ExpiredDiagnostic(payload, id));
			return;
		}

		DateTimeOffset? observed = TrafficNormalizationJson.DateTimeOffset(properties, "observedAt", "entryTime")
			?? TrafficNormalizationJson.MetadataDate(payload.ProviderMetadata, "observedAtUtc", "observedAt");
		DateTimeOffset? updated = TrafficNormalizationJson.DateTimeOffset(properties, "updatedAt", "lastUpdatedTime", "lastReportTime")
			?? TrafficNormalizationJson.MetadataDate(payload.ProviderMetadata, "updatedAtUtc", "updatedAt");
		var references = new Dictionary<string, string>(StringComparer.Ordinal);
		AddReference(references, "originalId", TrafficNormalizationJson.String(properties, "originalId"));
		AddReference(references, "eventType", eventType);
		if (iconCategory > 0)
		{
			references["iconCategory"] = iconCategory.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		events.Add(new NormalizedTrafficEvent(
			id,
			payload.ProviderId,
			closure
				? NormalizedTrafficEventKind.Closure
				: restriction
					? NormalizedTrafficEventKind.Restriction
					: NormalizedTrafficEventKind.Incident,
			TrafficNormalizationJson.Geometry(points, geometryDirection),
			null,
			null,
			null,
			null,
			TrafficNormalizationJson.Delay(TrafficNormalizationJson.Int(properties, "delay", "delaySeconds"), null, null),
			closure,
			IncidentSeverity(magnitude, closure),
			TrafficNormalizationJson.Confidence(TrafficNormalizationJson.Double(properties, "confidence")),
			IncidentDescription(properties),
			observed,
			updated,
			payload.FetchedAtUtc,
			validFrom,
			validUntil,
			TrafficNormalizationJson.RedactedSourceUri(payload.SourceUri),
			references,
			restrictionApplicability));
	}

	private static string? IncidentDescription(JsonElement properties)
	{
		JsonElement? events = TrafficNormalizationJson.Property(properties, "events");
		if (events is { ValueKind: JsonValueKind.Array })
		{
			foreach (JsonElement item in events.Value.EnumerateArray())
			{
				string? description = TrafficNormalizationJson.String(item, "description");
				if (!string.IsNullOrWhiteSpace(description))
				{
					return description;
				}
			}
		}

		JsonElement? descriptionElement = TrafficNormalizationJson.Property(properties, "description");
		if (descriptionElement is { ValueKind: JsonValueKind.Object })
		{
			return TrafficNormalizationJson.String(descriptionElement.Value, "value");
		}

		return TrafficNormalizationJson.String(properties, "description");
	}

	private static bool TryNormalizeSpeedUnit(
		RawTrafficFeedPayload payload,
		ref double? currentSpeed,
		ref double? freeFlowSpeed)
	{
		string? unit = MetadataValue(payload.ProviderMetadata, "speedUnit")
			?? MetadataValue(payload.ProviderMetadata, "unit")
			?? "kmph";
		double factor;
		if (unit.Equals("kmph", StringComparison.OrdinalIgnoreCase))
		{
			factor = 1d;
		}
		else if (unit.Equals("mph", StringComparison.OrdinalIgnoreCase))
		{
			factor = 1.609344d;
		}
		else
		{
			return false;
		}

		if (currentSpeed.HasValue)
		{
			currentSpeed *= factor;
		}

		if (freeFlowSpeed.HasValue)
		{
			freeFlowSpeed *= factor;
		}

		return true;
	}

	private static TrafficSeverity FlowSeverity(double currentSpeed, double freeFlowSpeed, bool closure)
	{
		if (closure)
		{
			return TrafficSeverity.Closed;
		}

		if (freeFlowSpeed <= 0d)
		{
			return TrafficSeverity.Unknown;
		}

		double ratio = Math.Max(0d, currentSpeed) / freeFlowSpeed;
		return ratio switch
		{
			<= 0.5d => TrafficSeverity.Heavy,
			< 0.85d => TrafficSeverity.Moderate,
			_ => TrafficSeverity.FreeFlow,
		};
	}

	private static TrafficSeverity IncidentSeverity(int magnitude, bool closure)
		=> closure
			? TrafficSeverity.Closed
			: magnitude switch
			{
				>= 4 => TrafficSeverity.Critical,
				3 => TrafficSeverity.Major,
				2 => TrafficSeverity.Moderate,
				1 => TrafficSeverity.Minor,
				_ => TrafficSeverity.Unknown,
			};

	private static bool IsExpired(DateTimeOffset? validUntil, TrafficNormalizationContext context)
		=> validUntil is not null && validUntil.Value <= context.EvaluationTimeUtc;

	private static TrafficProviderDiagnostic ExpiredDiagnostic(RawTrafficFeedPayload payload, string id)
		=> TrafficNormalizationJson.Diagnostic(
			payload,
			"ExpiredTrafficEvent",
			$"TomTom event '{id}' expired before the normalization evaluation time.");

	private static string? MetadataValue(
		IReadOnlyDictionary<string, string> metadata,
		string name)
	{
		if (metadata.TryGetValue(name, out string? value))
		{
			return value;
		}

		foreach (KeyValuePair<string, string> pair in metadata)
		{
			if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
			{
				return pair.Value;
			}
		}

		return null;
	}

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
