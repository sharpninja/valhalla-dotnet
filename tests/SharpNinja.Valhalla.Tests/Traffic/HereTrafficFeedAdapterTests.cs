using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers.Here;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class HereTrafficFeedAdapterTests
{
	private static readonly DateTimeOffset EvaluationTime = DateTimeOffset.Parse("2026-07-18T12:00:00Z");
	private static readonly DateTimeOffset FetchedAt = DateTimeOffset.Parse("2026-07-18T11:59:30Z");

	[Fact]
	public async Task NormalizeFlowPayload_ProducesFlowEvent()
	{
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Flow,
			"Here",
			"flow.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal("here-flow-1", trafficEvent.Id);
		Assert.Equal("here", trafficEvent.ProviderId);
		Assert.Equal(NormalizedTrafficEventKind.Flow, trafficEvent.Kind);
		Assert.Equal(TrafficGeometryKind.LineString, trafficEvent.Geometry.Kind);
		Assert.Equal(3, trafficEvent.Geometry.Points.Count);
		Assert.Equal(144d, trafficEvent.CurrentSpeedKph);
		Assert.Equal(288d, trafficEvent.FreeFlowSpeedKph);
		Assert.Equal(240, trafficEvent.CurrentTravelTimeSeconds);
		Assert.Equal(120, trafficEvent.FreeFlowTravelTimeSeconds);
		Assert.Equal(120, trafficEvent.DelaySeconds);
		Assert.Equal(TrafficSeverity.Heavy, trafficEvent.Severity);
		Assert.Equal(DateTimeOffset.Parse("2026-07-18T11:57:00Z"), trafficEvent.ObservedAtUtc);
		Assert.Equal(DateTimeOffset.Parse("2026-07-18T11:59:00Z"), trafficEvent.UpdatedAtUtc);
		Assert.Equal(FetchedAt, trafficEvent.FetchedAtUtc);
		Assert.Equal(new Uri("https://here.example.test/redacted-path"), trafficEvent.SourceUri);
		Assert.DoesNotContain("must-be-redacted", trafficEvent.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task NormalizeIncidentPayload_ProducesIncidentEvent()
	{
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Incident,
			"Here",
			"incident.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await adapter.NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal("here-incident-1", trafficEvent.Id);
		Assert.Equal(NormalizedTrafficEventKind.Incident, trafficEvent.Kind);
		Assert.Equal(300, trafficEvent.DelaySeconds);
		Assert.Equal(TrafficSeverity.Major, trafficEvent.Severity);
		Assert.Equal("Disabled vehicle on shoulder", trafficEvent.Description);
		Assert.Equal("HERE-ORIGINAL-1", trafficEvent.ProviderReferences["originalId"]);
	}

	[Fact]
	public async Task NormalizeClosurePayload_ProducesClosureEvent()
	{
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Closure,
			"Here",
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
		Assert.Equal(DateTimeOffset.Parse("2026-07-18T14:00:00Z"), trafficEvent.ValidUntilUtc);
	}

	[Fact]
	public async Task NormalizeFlowPayload_ReversibleNotRoutable_ProducesClosureConstraint()
	{
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Flow,
			"Here",
			"reversible-not-routable.json",
			FetchedAt);

		TrafficFeedNormalizationResult result = await new HereTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Diagnostics);
		NormalizedTrafficEvent trafficEvent = Assert.Single(result.Events);
		Assert.Equal(NormalizedTrafficEventKind.Closure, trafficEvent.Kind);
		Assert.True(trafficEvent.RoadClosure);
		Assert.Equal(TrafficSeverity.Closed, trafficEvent.Severity);
		Assert.Equal(
			"reversibleNotRoutable",
			trafficEvent.ProviderReferences["traversability"]);
	}

	[Fact]
	public async Task NormalizeRestrictionPayload_ProducesRestrictionEvent()
	{
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Restriction,
			"Here",
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
		Assert.Equal("HERE-RESTRICTION-1", trafficEvent.ProviderReferences["originalId"]);
	}

	[Fact]
	public async Task NormalizeCompositePayload_ProducesAllSupportedKinds()
	{
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Composite,
			"Here",
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
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Incident,
			"Here",
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
			"here",
			TrafficFeedKind.Incident,
			"Here",
			"incident-with-malformed-record.json",
			FetchedAt) with
		{
			SourceUri = new Uri("feed?apiKey=relative-secret", UriKind.Relative),
		};

		TrafficFeedNormalizationResult result = await new HereTrafficFeedAdapter().NormalizeAsync(
			payload,
			new TrafficNormalizationContext(EvaluationTime),
			CancellationToken.None);

		Assert.Null(Assert.Single(result.Events).SourceUri);
		Assert.DoesNotContain("relative-secret", Assert.Single(result.Diagnostics).ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task NormalizeAsync_WithExpiredEvent_ExcludesEventAndReturnsDiagnostic()
	{
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Incident,
			"Here",
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
		var adapter = new HereTrafficFeedAdapter();
		RawTrafficFeedPayload payload = TrafficNormalizationFixture.Load(
			"here",
			TrafficFeedKind.Flow,
			"Here",
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
