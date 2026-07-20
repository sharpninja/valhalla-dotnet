using SharpNinja.Valhalla.Traffic;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficNormalizationModelTests
{
	[Fact]
	public void CreateSnapshot_DefensivelyCopiesPublishedCollections()
	{
		var points = new List<GeoCoordinate>
		{
			new(36.1263, -86.6774),
			new(36.1370, -86.7040),
		};
		var references = new Dictionary<string, string>
		{
			["originalId"] = "provider-1",
			["Authorization"] = "Bearer header-secret",
			["apiKey"] = "query-secret",
			["sourceUrl"] = "https://traffic.example.test/path-secret?key=query-secret",
		};
		var geometry = new TrafficGeometry(TrafficGeometryKind.LineString, points);
		var trafficEvent = new NormalizedTrafficEvent(
			"event-1",
			"provider",
			NormalizedTrafficEventKind.Incident,
			geometry,
			null,
			null,
			null,
			null,
			60,
			false,
			TrafficSeverity.Minor,
			0.9,
			"Incident",
			null,
			null,
			DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
			null,
			null,
			new Uri("https://username:password@traffic.example.test/feed?apiKey=secret#fragment"),
			references);
		var events = new List<NormalizedTrafficEvent> { trafficEvent };
		var diagnostics = new List<TrafficProviderDiagnostic>
		{
			new(
				"code",
				"provider",
				TrafficFeedKind.Incident,
				"message",
				"https://traffic.example.test/feed"),
		};
		var result = new TrafficFeedNormalizationResult(events, diagnostics);

		points.Clear();
		references["originalId"] = "mutated";
		events.Clear();
		diagnostics.Clear();

		Assert.Equal(2, trafficEvent.Geometry.Points.Count);
		Assert.Equal("provider-1", trafficEvent.ProviderReferences["originalId"]);
		Assert.Single(result.Events);
		Assert.Single(result.Diagnostics);
		Assert.Equal(new Uri("https://traffic.example.test/redacted-path"), trafficEvent.SourceUri);
		Assert.DoesNotContain("Authorization", trafficEvent.ProviderReferences.Keys);
		Assert.DoesNotContain("apiKey", trafficEvent.ProviderReferences.Keys);
		Assert.DoesNotContain("sourceUrl", trafficEvent.ProviderReferences.Keys);
		Assert.DoesNotContain("username", trafficEvent.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("password", trafficEvent.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("feed", trafficEvent.ToString(), StringComparison.Ordinal);
		Assert.DoesNotContain("secret", trafficEvent.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public void PublishedCollections_AreReadOnly()
	{
		var geometry = new TrafficGeometry(
			TrafficGeometryKind.Point,
			new[] { new GeoCoordinate(36.1263, -86.6774) });
		var trafficEvent = new NormalizedTrafficEvent(
			"event-1",
			"provider",
			NormalizedTrafficEventKind.Incident,
			geometry,
			null,
			null,
			null,
			null,
			null,
			false,
			TrafficSeverity.Unknown,
			0,
			null,
			null,
			null,
			DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
			null,
			null,
			null,
			new Dictionary<string, string>());

		Assert.Throws<NotSupportedException>(
			() => ((IList<GeoCoordinate>)trafficEvent.Geometry.Points).Add(
				new GeoCoordinate(36.14, -86.72)));
		Assert.Throws<NotSupportedException>(
			() => ((IDictionary<string, string>)trafficEvent.ProviderReferences)
				.Add("key", "value"));
	}
}
