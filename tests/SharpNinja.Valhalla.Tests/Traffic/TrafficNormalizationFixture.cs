using SharpNinja.Valhalla.Traffic;

namespace SharpNinja.Valhalla.Tests.Traffic;

internal static class TrafficNormalizationFixture
{
	public static RawTrafficFeedPayload Load(
		string providerId,
		TrafficFeedKind feedKind,
		string providerDirectory,
		string fileName,
		DateTimeOffset fetchedAtUtc,
		IReadOnlyDictionary<string, string>? providerMetadata = null)
	{
		string path = Path.Combine(
			FindRepositoryRoot(),
			"tests",
			"SharpNinja.Valhalla.Tests",
			"Traffic",
			"Fixtures",
			providerDirectory,
			fileName);
		return new RawTrafficFeedPayload(
			providerId,
			feedKind,
			"application/json",
			File.ReadAllBytes(path),
			fetchedAtUtc,
			new Uri($"https://{providerId}.example.test/{feedKind.ToString().ToLowerInvariant()}?apiKey=must-be-redacted", UriKind.Absolute),
			providerMetadata ?? new Dictionary<string, string>(StringComparer.Ordinal));
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null)
		{
			if (File.Exists(Path.Combine(directory.FullName, "SharpNinja.Valhalla.slnx")))
			{
				return directory.FullName;
			}

			directory = directory.Parent;
		}

		throw new DirectoryNotFoundException(
			$"Could not locate SharpNinja.Valhalla.slnx from {AppContext.BaseDirectory}.");
	}
}
