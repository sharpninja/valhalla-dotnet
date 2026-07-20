using System.Xml.Linq;

namespace SharpNinja.Valhalla.Tests;

public sealed class BuildToolchainContractTests
{
	[Fact]
	public void MicrosoftNetTestSdk_IsCentrallyPinnedAndReferencedWithoutInlineVersion()
	{
		string root = FindRepositoryRoot();
		XDocument packages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
		XElement version = Assert.Single(
			packages.Descendants("PackageVersion"),
			static element => string.Equals(
				(string?)element.Attribute("Include"),
				"Microsoft.NET.Test.Sdk",
				StringComparison.Ordinal));

		Assert.Equal("18.7.0", (string?)version.Attribute("Version"));

		XDocument tests = XDocument.Load(Path.Combine(
			root,
			"tests",
			"SharpNinja.Valhalla.Tests",
			"SharpNinja.Valhalla.Tests.csproj"));
		XElement reference = Assert.Single(
			tests.Descendants("PackageReference"),
			static element => string.Equals(
				(string?)element.Attribute("Include"),
				"Microsoft.NET.Test.Sdk",
				StringComparison.Ordinal));
		Assert.Null(reference.Attribute("Version"));
	}

	[Fact]
	public void TestSuite_UsesXunitV3AndContainsNoXunitV2PackageReference()
	{
		string root = FindRepositoryRoot();
		XDocument packages = XDocument.Load(Path.Combine(root, "Directory.Packages.props"));
		XDocument tests = XDocument.Load(Path.Combine(
			root,
			"tests",
			"SharpNinja.Valhalla.Tests",
			"SharpNinja.Valhalla.Tests.csproj"));

		Assert.Contains(
			packages.Descendants("PackageVersion"),
			static element =>
				string.Equals((string?)element.Attribute("Include"), "xunit.v3", StringComparison.Ordinal) &&
				string.Equals((string?)element.Attribute("Version"), "3.2.2", StringComparison.Ordinal));
		Assert.Contains(
			tests.Descendants("PackageReference"),
			static element =>
				string.Equals((string?)element.Attribute("Include"), "xunit.v3", StringComparison.Ordinal) &&
				element.Attribute("Version") is null);
		Assert.DoesNotContain(
			packages.Descendants("PackageVersion"),
			static element => string.Equals((string?)element.Attribute("Include"), "xunit", StringComparison.Ordinal));
		Assert.DoesNotContain(
			tests.Descendants("PackageReference"),
			static element => string.Equals((string?)element.Attribute("Include"), "xunit", StringComparison.Ordinal));
	}

	[Fact]
	public void WorkspaceProjects_InheritWarningsAsErrorsWithoutDisablingOverride()
	{
		string root = FindRepositoryRoot();
		XDocument buildProps = XDocument.Load(Path.Combine(root, "Directory.Build.props"));
		Assert.Contains(
			buildProps.Descendants("TreatWarningsAsErrors"),
			static element => string.Equals(element.Value, "true", StringComparison.OrdinalIgnoreCase));

		foreach (string projectPath in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories))
		{
			if (projectPath.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
				projectPath.Contains($"{Path.DirectorySeparatorChar}artifacts{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			XDocument project = XDocument.Load(projectPath);
			Assert.DoesNotContain(
				project.Descendants("TreatWarningsAsErrors"),
				static element => string.Equals(element.Value, "false", StringComparison.OrdinalIgnoreCase));
		}
	}


	[Fact]
	public void ProductionData_DoesNotContainNashvilleFixtureLiteralsOrCanonicalIds()
	{
		string sourceRoot = Path.Combine(
			FindRepositoryRoot(),
			"src",
			"SharpNinja.Valhalla");
		string fixturePath = Path.Combine(
			FindRepositoryRoot(),
			"tests",
			"SharpNinja.Valhalla.Tests",
			"Nashville",
			"Fixtures",
			"centennial-park-to-bna-lane-overlay.v1.json");
		using System.Text.Json.JsonDocument fixture =
			System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixturePath));
		System.Text.Json.JsonElement root = fixture.RootElement;
		System.Text.Json.JsonElement descriptor = root.GetProperty("descriptor");
		string[] descriptorAnchors =
		[
			descriptor.GetProperty("datasetId").GetString()!,
			descriptor.GetProperty("graphSignature").GetString()!,
			descriptor.GetProperty("sourceReference").GetString()!,
		];
		string[] canonicalAnchors = root.GetProperty("edges")
			.EnumerateArray()
			.SelectMany(static edge => new[]
			{
				edge.GetProperty("canonicalDirectedEdgeId").GetUInt64(),
				edge.GetProperty("canonicalStartNodeId").GetUInt64(),
				edge.GetProperty("canonicalEndNodeId").GetUInt64(),
			})
			.Distinct()
			.Select(static value => value.ToString(
				System.Globalization.CultureInfo.InvariantCulture))
			.ToArray();
		Assert.NotEmpty(descriptorAnchors);
		Assert.True(canonicalAnchors.Length >= 6);
		string[] exactAnchors = descriptorAnchors
			.Concat(canonicalAnchors)
			.ToArray();
		var locationPattern = new System.Text.RegularExpressions.Regex(
			@"(?:Nashville|Centennial|BNA)",
			System.Text.RegularExpressions.RegexOptions.CultureInvariant |
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);

		foreach (string path in Directory.EnumerateFiles(
			sourceRoot,
			"*",
			SearchOption.AllDirectories))
		{
			if (path.Contains(
					$"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
					StringComparison.OrdinalIgnoreCase) ||
				path.Contains(
					$"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
					StringComparison.OrdinalIgnoreCase) ||
				!new[] { ".cs", ".json", ".xml" }.Contains(
					Path.GetExtension(path),
					StringComparer.OrdinalIgnoreCase))
			{
				continue;
			}

			string content = File.ReadAllText(path);
			Assert.False(
				locationPattern.IsMatch(content),
				$"Production DATA contains a Nashville fixture location literal: '{path}'.");
			Assert.DoesNotContain(
				exactAnchors,
				anchor => content.Contains(anchor, StringComparison.OrdinalIgnoreCase));
		}
	}

	private static string FindRepositoryRoot()
	{
		DirectoryInfo? directory = new(AppContext.BaseDirectory);
		while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SharpNinja.Valhalla.slnx")))
		{
			directory = directory.Parent;
		}

		return directory?.FullName
			?? throw new DirectoryNotFoundException("Could not locate the SharpNinja.Valhalla repository root.");
	}
}
