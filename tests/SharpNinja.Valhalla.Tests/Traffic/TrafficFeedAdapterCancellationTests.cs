using System.Text;

using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers.Here;
using SharpNinja.Valhalla.Traffic.Providers.TomTom;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficFeedAdapterCancellationTests
{
	private static readonly TrafficNormalizationContext Context = new(
		DateTimeOffset.Parse("2026-07-18T12:00:00Z"));

	[Fact]
	public async Task TomTomNormalizeAsync_CancelsLargeMultiRecordParse()
	{
		RawTrafficFeedPayload payload = Payload(
			"tomtom",
			BuildTomTomPayload(40_000));
		using var cancellation = new CancellationTokenSource();

		Task<TrafficFeedNormalizationResult> normalization =
			new TomTomTrafficFeedAdapter().NormalizeAsync(
				payload,
				Context,
				cancellation.Token);
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => await normalization);
	}

	[Fact]
	public async Task HereNormalizeAsync_CancelsLargeMultiRecordParse()
	{
		RawTrafficFeedPayload payload = Payload(
			"here",
			BuildHerePayload(40_000));
		using var cancellation = new CancellationTokenSource();

		Task<TrafficFeedNormalizationResult> normalization =
			new HereTrafficFeedAdapter().NormalizeAsync(
				payload,
				Context,
				cancellation.Token);
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(
			async () => await normalization);
	}

	private static RawTrafficFeedPayload Payload(string providerId, string json)
		=> new(
			providerId,
			TrafficFeedKind.Incident,
			"application/json",
			Encoding.UTF8.GetBytes(json),
			DateTimeOffset.Parse("2026-07-18T11:59:30Z"),
			new Uri($"https://{providerId}.example.test/incidents"),
			new Dictionary<string, string>());

	private static string BuildTomTomPayload(int count)
	{
		const string record = """
			{"geometry":{"type":"Point","coordinates":[-86.71,36.13]},"properties":{"id":"event","magnitudeOfDelay":1,"endTime":"2026-07-18T13:00:00Z"}}
			""";
		return """{"incidents":[""" + string.Join(',', Enumerable.Repeat(record, count)) + "]}";
	}

	private static string BuildHerePayload(int count)
	{
		const string record = """
			{"location":{"lat":36.13,"lng":-86.71},"incidentDetails":{"id":"event","type":"incident","criticality":"minor","endTime":"2026-07-18T13:00:00Z"}}
			""";
		return """{"results":[""" + string.Join(',', Enumerable.Repeat(record, count)) + "]}";
	}
}
