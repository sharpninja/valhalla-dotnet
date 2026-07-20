using System.Text;

using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers;
using SharpNinja.Valhalla.Traffic.Providers.Here;
using SharpNinja.Valhalla.Traffic.Providers.TomTom;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficFeedAdapterValidationTests
{
	private static readonly TrafficNormalizationContext Context = new(
		DateTimeOffset.Parse("2026-07-18T12:00:00Z"));

	[Fact]
	public async Task TomTomAdapter_WithDifferentProvider_ReturnsDiagnostic()
	{
		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			Payload("here", TrafficFeedKind.Flow, "{}"),
			Context,
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Events);
		Assert.Equal("TrafficProviderMismatch", Assert.Single(result.Diagnostics).Code);
	}

	[Fact]
	public async Task HereAdapter_WithDifferentProvider_ReturnsDiagnostic()
	{
		TrafficFeedNormalizationResult result = await new HereTrafficFeedAdapter().NormalizeAsync(
			Payload("tomtom", TrafficFeedKind.Flow, "{}"),
			Context,
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Events);
		Assert.Equal("TrafficProviderMismatch", Assert.Single(result.Diagnostics).Code);
	}

	[Theory]
	[InlineData("-1", "70", "100", "90")]
	[InlineData("\"NaN\"", "70", "100", "90")]
	[InlineData("35", "70", "-1", "90")]
	public async Task TomTomFlow_WithInvalidSpeedOrTravelTime_ReturnsDiagnostic(
		string speed,
		string freeFlow,
		string travelTime,
		string freeFlowTravelTime)
	{
		string json = $$"""
			{
			  "flowSegmentData": {
			    "currentSpeed": {{speed}},
			    "freeFlowSpeed": {{freeFlow}},
			    "currentTravelTime": {{travelTime}},
			    "freeFlowTravelTime": {{freeFlowTravelTime}},
			    "coordinates": { "coordinate": [
			      { "latitude": 36.12, "longitude": -86.67 },
			      { "latitude": 36.13, "longitude": -86.70 }
			    ] }
			  }
			}
			""";

		TrafficFeedNormalizationResult result = await new TomTomTrafficFeedAdapter().NormalizeAsync(
			Payload("tomtom", TrafficFeedKind.Flow, json),
			Context,
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Events);
		Assert.Equal("MalformedTrafficRecord", Assert.Single(result.Diagnostics).Code);
	}

	[Theory]
	[InlineData("-1", "70", "100", "90")]
	[InlineData("\"Infinity\"", "70", "100", "90")]
	[InlineData("35", "70", "100", "-1")]
	public async Task HereFlow_WithInvalidSpeedOrTravelTime_ReturnsDiagnostic(
		string speed,
		string freeFlow,
		string travelTime,
		string freeFlowTravelTime)
	{
		string json = $$"""
			{
			  "results": [
			    {
			      "location": { "points": [
			        { "lat": 36.12, "lng": -86.67 },
			        { "lat": 36.13, "lng": -86.70 }
			      ] },
			      "currentFlow": {
			        "speed": {{speed}},
			        "freeFlow": {{freeFlow}},
			        "traversalTime": {{travelTime}},
			        "freeFlowTravelTime": {{freeFlowTravelTime}}
			      }
			    }
			  ]
			}
			""";

		TrafficFeedNormalizationResult result = await new HereTrafficFeedAdapter().NormalizeAsync(
			Payload("here", TrafficFeedKind.Flow, json),
			Context,
			TestContext.Current.CancellationToken);

		Assert.Empty(result.Events);
		Assert.Equal("MalformedTrafficRecord", Assert.Single(result.Diagnostics).Code);
	}

	private static RawTrafficFeedPayload Payload(
		string providerId,
		TrafficFeedKind feedKind,
		string json)
		=> new(
			providerId,
			feedKind,
			"application/json",
			Encoding.UTF8.GetBytes(json),
			DateTimeOffset.Parse("2026-07-18T11:59:30Z"),
			new Uri($"https://{providerId}.example.test/feed"),
			new Dictionary<string, string>());
}
