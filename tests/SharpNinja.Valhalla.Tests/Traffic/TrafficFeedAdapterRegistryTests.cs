using SharpNinja.Valhalla.Traffic;
using SharpNinja.Valhalla.Traffic.Providers;

namespace SharpNinja.Valhalla.Tests.Traffic;

public sealed class TrafficFeedAdapterRegistryTests
{
	[Fact]
	public void CreateDefault_ResolvesTomTomHereAndAdditionalAdapters()
	{
		var futureAdapter = new FutureTrafficFeedAdapter("zeta-provider");
		var alphaAdapter = new FutureTrafficFeedAdapter("alpha-provider");

		TrafficFeedAdapterRegistry registry =
			TrafficFeedAdapterRegistry.CreateDefault([futureAdapter, alphaAdapter]);

		Assert.True(registry.TryResolve("tomtom", out ITrafficFeedAdapter? tomTom));
		Assert.Equal("TomTomTrafficFeedAdapter", tomTom!.GetType().Name);
		Assert.True(registry.TryResolve("HERE", out ITrafficFeedAdapter? here));
		Assert.Equal("HereTrafficFeedAdapter", here!.GetType().Name);
		Assert.True(registry.TryResolve("zeta-provider", out ITrafficFeedAdapter? future));
		Assert.Same(futureAdapter, future);
		Assert.Equal(
			["tomtom", "here", "alpha-provider", "zeta-provider"],
			registry.ProviderIds);
	}

	[Fact]
	public void CreateDefault_RejectsAdditionalAdapterThatConflictsWithBuiltInCaseInsensitively()
	{
		var exception = Assert.Throws<ArgumentException>(() =>
			TrafficFeedAdapterRegistry.CreateDefault(
				[new FutureTrafficFeedAdapter("TOMTOM")]));

		Assert.Contains("tomtom", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void CreateDefault_RejectsWhitespacePaddedProviderIdsBeforeConflictResolution()
	{
		var exception = Assert.Throws<ArgumentException>(() =>
			TrafficFeedAdapterRegistry.CreateDefault(
				[new FutureTrafficFeedAdapter(" TOMTOM ")]));

		Assert.Contains("whitespace", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void RegisteringFutureProvider_ResolvesWithoutBuiltInSwitch()
	{
		var futureAdapter = new FutureTrafficFeedAdapter();
		var registry = new TrafficFeedAdapterRegistry([futureAdapter]);

		Assert.True(registry.TryResolve("future-provider", out ITrafficFeedAdapter? resolved));
		Assert.Same(futureAdapter, resolved);
	}

	[Fact]
	public void DuplicateProviderRegistration_IsRejectedCaseInsensitively()
	{
		var exception = Assert.Throws<ArgumentException>(() =>
			new TrafficFeedAdapterRegistry(
			[
				new FutureTrafficFeedAdapter("future-provider"),
				new FutureTrafficFeedAdapter("FUTURE-PROVIDER"),
			]));

		Assert.Contains("future-provider", exception.Message, StringComparison.OrdinalIgnoreCase);
	}

	private sealed class FutureTrafficFeedAdapter(string providerId = "future-provider") : ITrafficFeedAdapter
	{
		public string ProviderId { get; } = providerId;

		public Task<TrafficFeedNormalizationResult> NormalizeAsync(
			RawTrafficFeedPayload payload,
			TrafficNormalizationContext context,
			CancellationToken cancellationToken = default)
		{
			cancellationToken.ThrowIfCancellationRequested();
			return Task.FromResult(TrafficFeedNormalizationResult.Empty);
		}
	}
}
