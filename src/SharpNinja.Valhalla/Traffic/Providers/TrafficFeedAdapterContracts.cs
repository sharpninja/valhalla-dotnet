using SharpNinja.Valhalla.Traffic.Providers.Here;
using SharpNinja.Valhalla.Traffic.Providers.TomTom;

namespace SharpNinja.Valhalla.Traffic.Providers;

/// <summary>
/// Provider adapter boundary. New providers participate through registration;
/// normalization orchestration does not contain provider switch statements.
/// </summary>
public interface ITrafficFeedAdapter
{
	string ProviderId { get; }

	Task<TrafficFeedNormalizationResult> NormalizeAsync(
		RawTrafficFeedPayload payload,
		TrafficNormalizationContext context,
		CancellationToken cancellationToken = default);
}

public sealed class TrafficFeedAdapterRegistry
{
	private readonly IReadOnlyDictionary<string, ITrafficFeedAdapter> _adapters;

	/// <summary>
	/// Creates the package-owned provider composition. Built-ins are always registered first;
	/// host-supplied future adapters follow in provider-ID order.
	/// </summary>
	public static TrafficFeedAdapterRegistry CreateDefault(
		IEnumerable<ITrafficFeedAdapter>? additionalAdapters = null)
	{
		ITrafficFeedAdapter[] additional = additionalAdapters?.ToArray() ?? [];
		foreach (ITrafficFeedAdapter adapter in additional)
		{
			ArgumentNullException.ThrowIfNull(adapter);
		}

		return new TrafficFeedAdapterRegistry(
		[
			new TomTomTrafficFeedAdapter(),
			new HereTrafficFeedAdapter(),
			.. additional
				.OrderBy(static adapter => adapter.ProviderId, StringComparer.OrdinalIgnoreCase)
				.ThenBy(static adapter => adapter.ProviderId, StringComparer.Ordinal),
		]);
	}

	public TrafficFeedAdapterRegistry(IEnumerable<ITrafficFeedAdapter> adapters)
	{
		ArgumentNullException.ThrowIfNull(adapters);
		var registered = new Dictionary<string, ITrafficFeedAdapter>(StringComparer.OrdinalIgnoreCase);
		var providerIds = new List<string>();
		foreach (ITrafficFeedAdapter adapter in adapters)
		{
			ArgumentNullException.ThrowIfNull(adapter);
			if (string.IsNullOrWhiteSpace(adapter.ProviderId))
			{
				throw new ArgumentException("A traffic feed adapter provider id cannot be empty.", nameof(adapters));
			}

			if (!string.Equals(
				adapter.ProviderId,
				adapter.ProviderId.Trim(),
				StringComparison.Ordinal))
			{
				throw new ArgumentException(
					$"Traffic feed adapter provider id '{adapter.ProviderId}' cannot contain leading or trailing whitespace.",
					nameof(adapters));
			}

			if (!registered.TryAdd(adapter.ProviderId, adapter))
			{
				throw new ArgumentException(
					$"A traffic feed adapter is already registered for provider '{adapter.ProviderId}'.",
					nameof(adapters));
			}

			providerIds.Add(adapter.ProviderId);
		}

		_adapters = registered;
		ProviderIds = Array.AsReadOnly(providerIds.ToArray());
	}

	/// <summary>Provider IDs in deterministic registration order.</summary>
	public IReadOnlyList<string> ProviderIds { get; }

	public bool TryResolve(string providerId, out ITrafficFeedAdapter? adapter)
	{
		if (string.IsNullOrWhiteSpace(providerId))
		{
			adapter = null;
			return false;
		}

		return _adapters.TryGetValue(providerId, out adapter);
	}
}
