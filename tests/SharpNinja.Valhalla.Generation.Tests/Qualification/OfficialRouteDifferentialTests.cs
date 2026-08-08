using SharpNinja.Valhalla.Generation.Differential;
using SharpNinja.Valhalla.Generation.Qualification;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Qualification;

public sealed class OfficialRouteDifferentialTests
{
    private const string OfficialImage =
        "ghcr.io/valhalla/valhalla@sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f";

    private static readonly IReadOnlyList<ValhallaRouteMatrixCase> RouteCases =
    [
        new("auto-monaco", "auto", 43.7384, 7.4246, 43.7325, 7.4189),
        new("truck-monaco", "truck", 43.7384, 7.4246, 43.7325, 7.4189),
        new("bicycle-monaco", "bicycle", 43.7384, 7.4246, 43.7325, 7.4189),
        new("pedestrian-monaco", "pedestrian", 43.7384, 7.4246, 43.7325, 7.4189),
        new("transit-monaco", "transit", 43.7384, 7.4246, 43.7325, 7.4189),
    ];

    [Fact]
    public async Task ManagedTiles_MatchOfficialRouteMatrix()
    {
        string sourcePbf = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string officialTiles = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Official",
            "Valhalla383Monaco",
            "tiles");
        string managedTiles = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-managed-route-matrix-{Guid.NewGuid():N}");

        Assert.True(File.Exists(sourcePbf), $"Missing Monaco PBF: {sourcePbf}");
        Assert.True(Directory.Exists(officialTiles), $"Missing official 3.8.3 fixture: {officialTiles}");

        try
        {
            TileBuilderResult build = TileBuilder.BuildTileSet(
                [sourcePbf],
                managedTiles,
                new TileBuilderConfig
                {
                    Hierarchy = true,
                    Shortcuts = true,
                });

            Assert.True(build.Success);
            Assert.True(build.TileCount > 0);

            OfficialValhallaContainerRouteMatrixRunner runner = new(
                new OfficialValhallaContainerTileSetReaderOptions(
                    OfficialImage,
                    TimeSpan.FromMinutes(2),
                    2L * 1024 * 1024 * 1024,
                    2,
                    16 * 1024 * 1024));

            IReadOnlyList<ValhallaRouteMatrixEntry> official =
                await runner.RunAsync(
                    officialTiles,
                    RouteCases,
                    TestContext.Current.CancellationToken);
            IReadOnlyList<ValhallaRouteMatrixEntry> managed =
                await runner.RunAsync(
                    build.TileDir,
                    RouteCases,
                    TestContext.Current.CancellationToken);

            Assert.All(
                official.Where(static route => route.CaseId != "transit-monaco"),
                static route => Assert.True(route.Succeeded, route.CaseId));
            Assert.False(official.Single(static route => route.CaseId == "transit-monaco").Succeeded);
            Assert.False(managed.Single(static route => route.CaseId == "transit-monaco").Succeeded);

            ValhallaRouteMatrixComparator comparator = new();
            IReadOnlyList<ValhallaSemanticDifference> differences = comparator.Compare(
                official,
                managed,
                new ValhallaRouteMetricTolerances(
                    MaximumDistanceDifferenceMeters: 25,
                    MaximumDurationDifferenceSeconds: 10,
                    MaximumRelativeDifference: 0.02));

            Assert.Empty(differences);
        }
        finally
        {
            if (Directory.Exists(managedTiles))
            {
                Directory.Delete(managedTiles, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RouteMatrix_InvalidCasesFailBeforeDocker()
    {
        OfficialValhallaContainerRouteMatrixRunner runner = new(
            new OfficialValhallaContainerTileSetReaderOptions(
                OfficialImage,
                TimeSpan.FromMinutes(2),
                2L * 1024 * 1024 * 1024,
                2,
                16 * 1024 * 1024));

        ValhallaRouteMatrixCase duplicate =
            new("duplicate", "auto", 43.7384, 7.4246, 43.7325, 7.4189);
        await Assert.ThrowsAsync<ArgumentException>(
            async () => await runner.RunAsync(
                "unused",
                [duplicate, duplicate],
                TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await runner.RunAsync(
                "unused",
                [duplicate with { CaseId = "unsupported", Costing = "hovercraft" }],
                TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("status", "{}")]
    [InlineData("route", "{\"api_key\":\"secret\"}")]
    public async Task OfficialAction_UnsafeRequestFailsBeforeDocker(
        string action,
        string requestJson)
    {
        string officialTiles = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Official",
            "Valhalla383Monaco",
            "tiles");
        OfficialValhallaContainerTileSetReader reader = new(
            new OfficialValhallaContainerTileSetReaderOptions(
                OfficialImage,
                TimeSpan.FromMinutes(2),
                2L * 1024 * 1024 * 1024,
                2,
                16 * 1024 * 1024));

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await reader.ExecuteActionsAsync(
                officialTiles,
                [new OfficialValhallaActionRequest(action, requestJson)],
                TestContext.Current.CancellationToken));
    }

    private static string FindRepositoryArtifact(params string[] parts)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(parts);
    }
}
