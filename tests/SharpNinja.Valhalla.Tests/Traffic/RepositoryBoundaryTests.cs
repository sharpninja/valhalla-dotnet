namespace SharpNinja.Valhalla.Tests.Traffic;

public class RepositoryBoundaryTests
{
    private static readonly string[] ForbiddenProductionFragments =
    [
        "using TruckMate",
        "namespace TruckMate",
        "class TruckMate",
        "record TruckMate",
        "struct TruckMate",
        "interface ITruckMate",
        "TruckMateTrafficGateway",
        "TruckMateTrafficProvider",
        "TruckMateTrafficClient",
        "TruckMateRouteGateway",
        "ITruckMateTrafficGateway",
    ];

    private static readonly string[] ForbiddenPresentationFragments =
    [
        "using Avalonia",
        "namespace Avalonia",
        "Avalonia.Controls",
        "Mapsui",
        "SharpNinja.Valhalla.Avalonia",
    ];

    [Fact]
    public void TrafficProductionFiles_DoNotReferenceTruckMate()
    {
        string trafficDirectory = GetTrafficDirectory();

        foreach (string file in Directory.EnumerateFiles(trafficDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            foreach (string forbiddenFragment in ForbiddenProductionFragments)
            {
                Assert.DoesNotContain(forbiddenFragment, source, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ValhallaPackageAndCentralVersions_DoNotReferenceTruckMate()
    {
        string root = FindRepositoryRoot();
        string[] packageConfigurationFiles =
        [
            Path.Combine(root, "src", "SharpNinja.Valhalla", "SharpNinja.Valhalla.csproj"),
            Path.Combine(root, "Directory.Packages.props"),
        ];

        foreach (string file in packageConfigurationFiles)
        {
            string project = File.ReadAllText(file);
            Assert.DoesNotContain("TruckMate", project, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TrafficProductionFiles_DoNotReferencePresentationFrameworks()
    {
        string trafficDirectory = GetTrafficDirectory();

        foreach (string file in Directory.EnumerateFiles(
                     trafficDirectory,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            foreach (string forbiddenFragment in ForbiddenPresentationFragments)
            {
                Assert.DoesNotContain(
                    forbiddenFragment,
                    source,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ValhallaPackageAndCentralVersions_DoNotReferencePresentationPackages()
    {
        string root = FindRepositoryRoot();
        string[] packageConfigurationFiles =
        [
            Path.Combine(root, "src", "SharpNinja.Valhalla", "SharpNinja.Valhalla.csproj"),
            Path.Combine(root, "Directory.Packages.props"),
        ];

        foreach (string file in packageConfigurationFiles)
        {
            string project = File.ReadAllText(file);
            foreach (string forbiddenFragment in ForbiddenPresentationFragments)
            {
                Assert.DoesNotContain(
                    forbiddenFragment,
                    project,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void ValhallaProductionFiles_DoNotReferenceExternalHostOrPresentationTypes()
    {
        string sourceDirectory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SharpNinja.Valhalla");

        foreach (string file in Directory.EnumerateFiles(
                     sourceDirectory,
                     "*.cs",
                     SearchOption.AllDirectories))
        {
            string source = File.ReadAllText(file);
            Assert.DoesNotContain("TruckMate.", source, StringComparison.OrdinalIgnoreCase);
            foreach (string forbiddenFragment in ForbiddenPresentationFragments)
            {
                Assert.DoesNotContain(
                    forbiddenFragment,
                    source,
                    StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void TrafficProductionNamespaces_AreSharpNinjaValhallaOwned()
    {
        string trafficDirectory = GetTrafficDirectory();

        foreach (string file in Directory.EnumerateFiles(trafficDirectory, "*.cs", SearchOption.AllDirectories))
        {
            string[] namespaces = File.ReadLines(file)
                .Select(static line => line.Trim())
                .Where(static line => line.StartsWith("namespace ", StringComparison.Ordinal))
                .Select(static line => line["namespace ".Length..].TrimEnd(';', ' ', '{'))
                .ToArray();

            Assert.NotEmpty(namespaces);
            Assert.All(
                namespaces,
                static value => Assert.StartsWith(
                    "SharpNinja.Valhalla.Traffic",
                    value,
                    StringComparison.Ordinal));
        }
    }

    private static string GetTrafficDirectory()
    {
        string directory = Path.Combine(
            FindRepositoryRoot(),
            "src",
            "SharpNinja.Valhalla",
            "Traffic");
        Assert.True(Directory.Exists(directory), $"Missing traffic directory: {directory}");
        return directory;
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
