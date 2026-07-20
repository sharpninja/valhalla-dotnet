using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers.TomTom;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TomTomTrafficFeedAdapterTests
{
	private static readonly DateTimeOffset EvaluationTime = DateTimeOffset.Parse("2026-07-18T12:00:00Z");
	private static readonly DateTimeOffset FetchedAt = DateTimeOffset.Parse("2026-07-18T11:59:30Z");

	[Fact]
	public async Task NormalizeFlowPayload_ProducesFlowEvent()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Flow,
			"TomTom",
			"flow.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal("tomtom-flow-1", trafficEvent.Id);
		Assert.Equal("tomtom", trafficEvent.ProviderId);
		Assert.Equal(NormalizedTrafficEventKind.Flow, trafficEvent.Kind);
		Assert.Equal(TrafficGeometryKind.LineString, trafficEvent.Geometry.Kind);
		Assert.Equal(3, trafficEvent.Geometry.Points.Count);
		Assert.Equal(35d, trafficEvent.CurrentSpeedKph);
		Assert.Equal(70d, trafficEvent.FreeFlowSpeedKph);
		Assert.Equal(180, trafficEvent.CurrentTravelTimeSeconds);
		Assert.Equal(90, trafficEvent.FreeFlowTravelTimeSeconds);
		Assert.Equal(90, trafficEvent.DelaySeconds);
		Assert.False(trafficEvent.RoadClosure);
		Assert.Equal(TrafficSeverity.Heavy, trafficEvent.Severity);
		Assert.Equal(DateTimeOffset.Parse("2026-07-18T11:58:00Z"), trafficEvent.ObservedAtUtc);
		Assert.Equal(DateTimeOffset.Parse("2026-07-18T11:59:00Z"), trafficEvent.UpdatedAtUtc);
		Assert.Equal(FetchedAt, trafficEvent.FetchedAtUtc);
		Assert.Equal(new Uri("https://tomtom.example.test/redacted-path"), trafficEvent.SourceUri);
		Assert.DoesNotContain("must-be-redacted", trafficEvent.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task NormalizeFlowPayload_IgnoresRawSourceUriUnitQuery()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Flow,
			"TomTom",
			"flow.json",
			FetchedAt) with
		{
			SourceUri = new Uri(
				"https://tomtom.example.test/credential-path?unit=mph&apiKey=must-not-survive"),
			ProviderMetadata = new Dictionary<string, string>(),
		};

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal(35d, trafficEvent.CurrentSpeedKph);
		Assert.Equal(70d, trafficEvent.FreeFlowSpeedKph);
		Assert.Equal(new Uri("https://tomtom.example.test/redacted-path"), trafficEvent.SourceUri);
		Assert.DoesNotContain("credential-path", trafficEvent.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("must-not-survive", trafficEvent.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task NormalizeFlowPayload_WithoutBodyFreshness_UsesAllowlistedResponseProvenance()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Flow,
			"TomTom",
			"flow-without-freshness.json",
			FetchedAt) with
		{
			ProviderMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["TrafficModelID"] = "model-20260718-42",
				["Date"] = "Sat, 18 Jul 2026 11:59:00 GMT",
				["ETag"] = "\"model-etag\"",
				["Last-Modified"] = "Sat, 18 Jul 2026 11:58:00 GMT",
			},
		};

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal(
			DateTimeOffset.Parse("2026-07-18T11:58:00Z"),
			trafficEvent.UpdatedAtUtc);
		Assert.Equal("model-20260718-42", trafficEvent.ProviderReferences["TrafficModelID"]);
		Assert.Equal("\"model-etag\"", trafficEvent.ProviderReferences["ETag"]);
	}

	[Fact]
	public async Task NormalizeFlowPayload_BodyFreshness_PrecedesResponseProvenance()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Flow,
			"TomTom",
			"flow.json",
			FetchedAt) with
		{
			ProviderMetadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
			{
				["Last-Modified"] = "Sat, 18 Jul 2026 11:30:00 GMT",
				["Date"] = "Sat, 18 Jul 2026 11:31:00 GMT",
			},
		};

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			TestContext.Current.CancellationToken);

		Assert.Equal(
			DateTimeOffset.Parse("2026-07-18T11:59:00Z"),
			Assert.Single(result.Events).UpdatedAtUtc);
	}

	[Fact]
	public async Task NormalizeIncidentPayload_ProducesIncidentEvent()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Incident,
			"TomTom",
			"incident.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal("tomtom-incident-1", trafficEvent.Id);
		Assert.Equal(NormalizedTrafficEventKind.Incident, trafficEvent.Kind);
		Assert.Equal(420, trafficEvent.DelaySeconds);
		Assert.Equal(TrafficSeverity.Major, trafficEvent.Severity);
		Assert.Equal("Crash blocking the right lane", trafficEvent.Description);
		Assert.False(trafficEvent.RoadClosure);
		Assert.Equal("TT-ORIGINAL-1", trafficEvent.ProviderReferences["originalId"]);
	}

	[Fact]
	public async Task NormalizeClosurePayload_ProducesClosureEvent()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Closure,
			"TomTom",
			"closure.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal(NormalizedTrafficEventKind.Closure, trafficEvent.Kind);
		Assert.True(trafficEvent.RoadClosure);
		Assert.Equal(TrafficSeverity.Closed, trafficEvent.Severity);
		Assert.Equal(DateTimeOffset.Parse("2026-07-18T13:00:00Z"), trafficEvent.ValidUntilUtc);
	}

	[Fact]
	public async Task NormalizeIncidentPayload_IconCategory8WithoutRoadClosed_ProducesClosure()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Incident,
			"TomTom",
			"category-8-closure.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal(NormalizedTrafficEventKind.Closure, trafficEvent.Kind);
		Assert.True(trafficEvent.RoadClosure);
		Assert.Equal(TrafficSeverity.Closed, trafficEvent.Severity);
	}

	[Fact]
	public async Task NormalizeIncidentPayload_IconCategory9WithoutRoadClosed_ProducesIncident()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Incident,
			"TomTom",
			"category-9-incident.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal(NormalizedTrafficEventKind.Incident, trafficEvent.Kind);
		Assert.False(trafficEvent.RoadClosure);
	}

	[Fact]
	public async Task NormalizeIncidentPayload_LastReportTime_SetsProviderFreshness()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Incident,
			"TomTom",
			"last-report-time.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Diagnostics);
		Assert.Equal(
			DateTimeOffset.Parse("2026-07-18T11:58:45Z"),
			Assert.Single(result.Events).UpdatedAtUtc);
	}

	[Fact]
	public async Task NormalizeRestrictionPayload_ProducesRestrictionEvent()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Restriction,
			"TomTom",
			"restriction.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal(NormalizedTrafficEventKind.Restriction, trafficEvent.Kind);
		Assert.False(trafficEvent.RoadClosure);
		Assert.Equal("Vehicles over 12 feet prohibited", trafficEvent.Description);
		Assert.Equal("TT-RESTRICTION-1", trafficEvent.ProviderReferences["originalId"]);
	}

	[Fact]
	public async Task NormalizeCompositePayload_ProducesAllSupportedKinds()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Composite,
			"TomTom",
			"composite.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Diagnostics);
		Assert.Equal(4, result.Events.Count);
		Assert.Equal(
			[
				NormalizedTrafficEventKind.Flow,
				NormalizedTrafficEventKind.Incident,
				NormalizedTrafficEventKind.Closure,
				NormalizedTrafficEventKind.Restriction,
			],
			result.Events.Select(trafficEvent => trafficEvent.Kind).ToArray());
	}

	[Fact]
	public async Task NormalizeAsync_WithMalformedRecord_ReturnsDiagnosticAndValidRecords()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Incident,
			"TomTom",
			"incident-with-malformed-record.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Single(result.Events);
		TrafficProviderDiagnostic diagnostic = Assert.Single(result.Diagnostics);
		Assert.Equal("MalformedTrafficRecord", diagnostic.Code);
		Assert.DoesNotContain("must-be-redacted", diagnostic.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task NormalizeAsync_WithRelativeSecretSource_DoesNotLeakCredential()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Incident,
			"TomTom",
			"incident-with-malformed-record.json",
			FetchedAt) with
		{
			SourceUri = new Uri("feed?apiKey=relative-secret", UriKind.Relative),
		};

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Null(Assert.Single(result.Events).SourceUri);
		Assert.DoesNotContain("relative-secret", Assert.Single(result.Diagnostics).ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task NormalizeAsync_WithExpiredEvent_ExcludesEventAndReturnsDiagnostic()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Incident,
			"TomTom",
			"expired-incident.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Events);
		Assert.Equal("ExpiredTrafficEvent", Assert.Single(result.Diagnostics).Code);
	}

	[Fact]
	public async Task NormalizeAsync_WithCanceledToken_ThrowsOperationCanceledException()
	{
		var adapter = new TomTomTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"tomtom",
			TrafficFeedKind.Flow,
			"TomTom",
			"flow.json",
			FetchedAt);
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
			await adapter.NormalizeAsync(
				payload,
				new TrafficNormalizationContext(EvaluationTime),
				cancellation.Token));
	}
}
