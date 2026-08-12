using System.Text.Json;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Parity;

public sealed class UpstreamParityManifestTests
{
    private const string ExpectedCommit =
        "a60c7cbfc83e073f50887cd27e0109d02e6b64e5";

    private static readonly string[] ExpectedStages =
    [
        "kInitialize",
        "kParseWays",
        "kParseRelations",
        "kParseNodes",
        "kConstructEdges",
        "kBuild",
        "kEnhance",
        "kFilter",
        "kTransit",
        "kBss",
        "kHierarchy",
        "kShortcuts",
        "kRestrictions",
        "kElevation",
        "kValidate",
        "kCleanup",
    ];

    private static readonly string[] RequiredUpstreamTests =
    [
        "test/access_restriction.cc",
        "test/admin.cc",
        "test/complexrestriction.cc",
        "test/countryaccess.cc",
        "test/elevation_builder.cc",
        "test/graphbuilder.cc",
        "test/graphparser.cc",
        "test/graphtile.cc",
        "test/graphtilebuilder.cc",
        "test/hierarchylimits.cc",
        "test/lua.cc",
        "test/matrix_bss.cc",
        "test/nodetransition.cc",
        "test/refs.cc",
        "test/scripts/test_valhalla_build_elevation.py",
        "test/scripts/test_valhalla_build_extract.py",
        "test/sequence.cc",
        "test/signinfo.cc",
        "test/transitdeparture.cc",
        "test/transitroute.cc",
        "test/transitschedule.cc",
        "test/transitstop.cc",
        "test/util_mjolnir.cc",
        "test/gurka/test_64bit_wayid.cc",
        "test/gurka/test_access.cc",
        "test/gurka/test_admin_sidewalk_crossing_override.cc",
        "test/gurka/test_admin.cc",
        "test/gurka/test_area_routing.cc",
        "test/gurka/test_build_admin.cc",
        "test/gurka/test_conditional_restrictions.cc",
        "test/gurka/test_config_speed.cc",
        "test/gurka/test_deadend.cc",
        "test/gurka/test_elevation.cc",
        "test/gurka/test_ferry_connections.cc",
        "test/gurka/test_filter.cc",
        "test/gurka/test_graphfilter.cc",
        "test/gurka/test_gtfs.cc",
        "test/gurka/test_languages.cc",
        "test/gurka/test_maxspeed.cc",
        "test/gurka/test_only_restrictions.cc",
        "test/gurka/test_parse_osm.cc",
        "test/gurka/test_pbf_api.cc",
        "test/gurka/test_phonemes.cc",
        "test/gurka/test_phonemes_w_langs.cc",
        "test/gurka/test_probable_restrictions.cc",
        "test/gurka/test_ramps_tc.cc",
        "test/gurka/test_reproduce_tile_build.cc",
        "test/gurka/test_restricted_area.cc",
        "test/gurka/test_shortcut.cc",
        "test/gurka/test_simple_restrictions.cc",
        "test/gurka/test_speeds.cc",
        "test/gurka/test_stop_signs.cc",
        "test/gurka/test_tagged_values.cc",
        "test/gurka/test_time_dependent_restrictions.cc",
        "test/gurka/test_time_dependent_tags.cc",
        "test/gurka/test_traffic_signals.cc",
        "test/gurka/test_truck.cc",
        "test/gurka/test_turn_lanes.cc",
        "test/gurka/test_yield_signs.cc",
    ];

    [Fact]
    public void Valhalla383GenerationSurface_HasNoUnexplainedGaps()
    {
        string repositoryRoot = FindRepositoryRoot();
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Parity",
            "valhalla-3.8.3-generation-surface.json");

        Assert.True(File.Exists(manifestPath), $"Missing parity manifest: {manifestPath}");

        ParityManifest? manifest = JsonSerializer.Deserialize<ParityManifest>(
            File.ReadAllText(manifestPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest.SchemaVersion);
        Assert.Equal("3.8.3", manifest.UpstreamVersion);
        Assert.Equal(ExpectedCommit, manifest.UpstreamCommit);
        Assert.False(string.IsNullOrWhiteSpace(manifest.AuditCommand));

        Assert.Equal(
            ExpectedStages,
            manifest.Stages.Select(stage => stage.Name).ToArray());
        Assert.Equal(
            ExpectedStages.Length,
            manifest.Stages.Select(stage => stage.Name).Distinct(StringComparer.Ordinal).Count());

        foreach (ParityStage stage in manifest.Stages)
        {
            Assert.Equal("implemented", stage.Status);
            Assert.False(string.IsNullOrWhiteSpace(stage.Rationale));
            Assert.NotEmpty(stage.UpstreamSources);
            Assert.NotEmpty(stage.ManagedEvidence);
            ValidateManagedEvidence(repositoryRoot, stage.ManagedEvidence);
        }

        Assert.Equal(
            RequiredUpstreamTests,
            manifest.UpstreamTests.Select(test => test.Path).ToArray());
        Assert.Equal(
            RequiredUpstreamTests.Length,
            manifest.UpstreamTests.Select(test => test.Path).Distinct(StringComparer.Ordinal).Count());

        foreach (ParityUpstreamTest test in manifest.UpstreamTests)
        {
            Assert.Contains(
                test.Status,
                new[] { "implemented", "excluded", "notApplicable" });
            Assert.False(string.IsNullOrWhiteSpace(test.Rationale));
            Assert.DoesNotContain("pending", test.Status, StringComparison.OrdinalIgnoreCase);

            if (test.Status == "implemented")
            {
                Assert.NotEmpty(test.ManagedEvidence);
                ValidateManagedEvidence(repositoryRoot, test.ManagedEvidence);
            }
            else
            {
                Assert.NotEmpty(test.UpstreamEvidence);
            }
        }
    }

    private static void ValidateManagedEvidence(
        string repositoryRoot,
        IReadOnlyList<string> evidenceItems)
    {
        foreach (string evidence in evidenceItems)
        {
            string[] parts = evidence.Split('#', 2);
            Assert.Equal(2, parts.Length);

            string evidencePath = Path.Combine(
                repositoryRoot,
                parts[0].Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(evidencePath), $"Missing managed evidence file: {parts[0]}");

            string source = File.ReadAllText(evidencePath);
            Assert.Contains(parts[1], source, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpNinja.Valhalla.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    private sealed record ParityManifest(
        int SchemaVersion,
        string UpstreamVersion,
        string UpstreamCommit,
        string AuditCommand,
        IReadOnlyList<ParityStage> Stages,
        IReadOnlyList<ParityUpstreamTest> UpstreamTests);

    private sealed record ParityStage(
        string Name,
        string Status,
        string Rationale,
        IReadOnlyList<string> UpstreamSources,
        IReadOnlyList<string> ManagedEvidence);

    private sealed record ParityUpstreamTest(
        string Path,
        string Status,
        string Rationale,
        IReadOnlyList<string> UpstreamEvidence,
        IReadOnlyList<string> ManagedEvidence);
}
