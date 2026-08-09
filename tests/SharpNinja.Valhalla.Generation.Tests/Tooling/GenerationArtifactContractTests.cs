using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Tooling;

public sealed class GenerationContainerContractTests
{
    [Fact]
    public void Image_IsNonRootAndSecretFree()
    {
        var root = RepositoryRoot.Find();
        var dockerfilePath = Path.Combine(root, "src", "SharpNinja.Valhalla.Generation.Tool", "Dockerfile");
        Assert.True(File.Exists(dockerfilePath), $"Missing generation Dockerfile: {dockerfilePath}");

        var dockerfile = File.ReadAllText(dockerfilePath);
        Assert.Contains("mcr.microsoft.com/dotnet/sdk:10.0", dockerfile, StringComparison.Ordinal);
        Assert.Contains("mcr.microsoft.com/dotnet/runtime:10.0", dockerfile, StringComparison.Ordinal);
        Assert.Contains("USER app", dockerfile, StringComparison.Ordinal);
        Assert.Contains("ENTRYPOINT [\"valhalla-dotnet\"]", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("API_KEY", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASSWORD", dockerfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET=", dockerfile, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GenerationArtifactContractTests
{
    [Fact]
    public void ArtifactsContainVersionSbomAndChecksums()
    {
        var root = RepositoryRoot.Find();
        var build = RepositoryRoot.ReadBuildSources(root);
        var toolProject = File.ReadAllText(
            Path.Combine(root, "src", "SharpNinja.Valhalla.Generation.Tool", "SharpNinja.Valhalla.Generation.Tool.csproj"));

        Assert.Contains("GenerateGenerationSbom", build, StringComparison.Ordinal);
        Assert.Contains("generation-artifacts.manifest.json", build, StringComparison.Ordinal);
        Assert.Contains("SHA256", build, StringComparison.Ordinal);
        Assert.Contains("SourceRevisionId", toolProject, StringComparison.Ordinal);
        Assert.Contains("a60c7cbfc83e073f50887cd27e0109d02e6b64e5", toolProject, StringComparison.Ordinal);
    }
}

public sealed class GenerationBuildContractTests
{
    private static readonly string[] RequiredTargets =
    [
        "TestGeneration",
        "PackGeneration",
        "PackGenerationTool",
        "BuildGenerationContainer",
        "ValidateGenerationContainer",
        "BenchmarkNashville",
        "QualifyNashville",
        "QualifyLower48",
        "GenerateUpstreamParityReport",
    ];

    [Fact]
    public void NukeTargetsExistAndBuildProjectIsNotInSolution()
    {
        var root = RepositoryRoot.Find();
        var build = RepositoryRoot.ReadBuildSources(root);
        var solution = File.ReadAllText(Path.Combine(root, "SharpNinja.Valhalla.slnx"));

        foreach (var target in RequiredTargets)
        {
            Assert.Contains($"Target {target} =>", build, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("build/_build.csproj", solution, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("build\\_build.csproj", solution, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class GenerationPackageBoundaryTests
{
    [Fact]
    public void PackagesContainNoCloudOrTruckMateDependency()
    {
        var root = RepositoryRoot.Find();
        var projectPaths = new[]
        {
            Path.Combine(root, "src", "SharpNinja.Valhalla.Generation", "SharpNinja.Valhalla.Generation.csproj"),
            Path.Combine(root, "src", "SharpNinja.Valhalla.Generation.Tool", "SharpNinja.Valhalla.Generation.Tool.csproj"),
        };
        var bannedTokens = new[]
        {
            "Google.Cloud",
            "Google.Apis",
            "TruckMate",
            "Avalonia",
            "Mapsui",
        };

        foreach (var projectPath in projectPaths)
        {
            var project = File.ReadAllText(projectPath);
            foreach (var bannedToken in bannedTokens)
            {
                Assert.DoesNotContain(bannedToken, project, StringComparison.OrdinalIgnoreCase);
            }
        }
    }
}

internal static class RepositoryRoot
{
    internal static string ReadBuildSources(string root) =>
        string.Join(
            Environment.NewLine,
            Directory
                .EnumerateFiles(Path.Combine(root, "build"), "*.cs", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

    internal static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpNinja.Valhalla.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the valhalla-dotnet repository root.");
    }
}
