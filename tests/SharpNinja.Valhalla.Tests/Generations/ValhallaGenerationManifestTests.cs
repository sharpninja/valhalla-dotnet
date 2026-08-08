using SharpNinja.Valhalla.Generations;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Generations;

public sealed class ValhallaGraphGenerationManifestTests
{
    [Fact]
    public void ValidManifest_PreservesRegionIdentityFreshnessAndIntegrity()
    {
        ValhallaGraphGenerationManifest manifest = GenerationTestData.CreateBase();

        Assert.Equal(ValhallaGenerationSchema.CurrentVersion, manifest.SchemaVersion);
        Assert.Equal("us-tn-nashville", manifest.RegionId);
        Assert.Equal("base-20260723-001", manifest.GenerationId);
        Assert.Equal(GenerationTestData.GraphSha, manifest.GraphSha256);
        Assert.Equal(GenerationTestData.BaseArtifactSha, manifest.ArtifactSha256);
        Assert.Equal(1_024, manifest.ByteLength);
        Assert.True(manifest.FreshnessDeadlineUtc > manifest.CreatedAtUtc);
        Assert.True(manifest.CreatedAtUtc >= manifest.OsmSourceTimestampUtc);
        Assert.Equal(new Uri("gs://truckmate-staging-graphs/nashville/base-20260723-001.tar.zst"), manifest.ArtifactUri);
    }
}

public sealed class ValhallaGenerationCohortManifestTests
{
    [Fact]
    public void ValidManifest_PreservesOverlayIdentityPolicyAndFreshness()
    {
        ValhallaGenerationCohortManifest cohort = GenerationTestData.CreateCohort();

        Assert.Equal("cohort-20260723-001", cohort.CohortId);
        Assert.Equal(TrafficSnapshotPolicy.Enabled, cohort.TrafficEnabled.Policy);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, cohort.ClosureOnly.Policy);
        Assert.Equal(cohort.BaseGraph.GenerationId, cohort.TrafficEnabled.BaseGenerationId);
        Assert.Equal(cohort.BaseGraph.GenerationId, cohort.ClosureOnly.BaseGenerationId);
        Assert.Equal(cohort.BaseGraph.GraphSha256, cohort.TrafficEnabled.GraphSha256);
        Assert.NotNull(cohort.TrafficEnabled.TrafficDataAsOfUtc);
        Assert.Null(cohort.ClosureOnly.TrafficDataAsOfUtc);
    }

    [Fact]
    public void ClosurePromotion_RepublishesPoliciesWithSameClosureSourceVersion()
    {
        ValhallaGraphGenerationManifest graph = GenerationTestData.CreateBase();
        ValhallaOverlayGenerationManifest enabled = GenerationTestData.CreateOverlay(
            TrafficSnapshotPolicy.Enabled,
            generationId: "enabled-002",
            cohortId: "cohort-002",
            closureSourceVersion: "closures-002");
        ValhallaOverlayGenerationManifest closureOnly = GenerationTestData.CreateOverlay(
            TrafficSnapshotPolicy.ClosureOnly,
            generationId: "closure-002",
            cohortId: "cohort-002",
            closureSourceVersion: "closures-002");

        var cohort = new ValhallaGenerationCohortManifest(graph, enabled, closureOnly);

        Assert.Equal("closures-002", cohort.ClosureSourceVersion);
        Assert.Equal(enabled.ClosureSourceVersion, closureOnly.ClosureSourceVersion);
        Assert.Equal(enabled.CohortId, closureOnly.CohortId);
    }
}

public sealed class ValhallaGenerationManifestValidationTests
{
    [Fact]
    public void InvalidOrUnsupportedManifest_FailsClosed()
    {
        ValhallaGenerationException schema = Assert.Throws<ValhallaGenerationException>(
            () => GenerationTestData.CreateBase(schemaVersion: 99));
        Assert.Equal(ValhallaGenerationFailureCode.UnsupportedSchema, schema.Code);

        ValhallaGenerationException relative = Assert.Throws<ValhallaGenerationException>(
            () => GenerationTestData.CreateBase(artifactUri: new Uri("relative.bin", UriKind.Relative)));
        Assert.Equal(ValhallaGenerationFailureCode.InvalidManifest, relative.Code);

        ValhallaGenerationException credential = Assert.Throws<ValhallaGenerationException>(
            () => GenerationTestData.CreateBase(
                artifactUri: new Uri("https://storage.example/base.bin?api_key=do-not-log")));
        Assert.Equal(ValhallaGenerationFailureCode.InvalidManifest, credential.Code);
        Assert.DoesNotContain("do-not-log", credential.Message, StringComparison.Ordinal);

        Assert.Throws<ValhallaGenerationException>(
            () => GenerationTestData.CreateBase(graphSha: "not-a-sha"));
        Assert.Throws<ValhallaGenerationException>(
            () => GenerationTestData.CreateBase(byteLength: 0));
    }

    [Fact]
    public void RequiredValidationMatrix_IsComplete()
    {
        Action[] invalid =
        [
            () => GenerationTestData.CreateBase(schemaVersion: 0),
            () => GenerationTestData.CreateBase(regionId: " "),
            () => GenerationTestData.CreateBase(generationId: ""),
            () => GenerationTestData.CreateBase(graphSha: new string('Z', 64)),
            () => GenerationTestData.CreateBase(artifactSha: "1234"),
            () => GenerationTestData.CreateBase(byteLength: -1),
            () => GenerationTestData.CreateBase(artifactUri: new Uri("https://user:pass@example/base.bin")),
            () => GenerationTestData.CreateBase(artifactUri: new Uri("https://example/base.bin?token=secret")),
            () => GenerationTestData.CreateBase(freshnessDeadlineUtc: GenerationTestData.Now - TimeSpan.FromMinutes(2)),
            () => GenerationTestData.CreateOverlay(
                TrafficSnapshotPolicy.Enabled,
                omitTrafficData: true),
        ];

        Assert.All(invalid, action => Assert.Throws<ValhallaGenerationException>(action));
    }
}

public sealed class ValhallaGenerationCompatibilityTests
{
    [Fact]
    public void IncompatibleGenerationSet_IsRejectedBeforeRouting()
    {
        ValhallaGraphGenerationManifest graph = GenerationTestData.CreateBase();
        ValhallaOverlayGenerationManifest enabled = GenerationTestData.CreateOverlay(
            TrafficSnapshotPolicy.Enabled);
        ValhallaOverlayGenerationManifest wrong = GenerationTestData.CreateOverlay(
            TrafficSnapshotPolicy.ClosureOnly,
            graphSha: new string('C', 64));

        ValhallaGenerationException exception = Assert.Throws<ValhallaGenerationException>(
            () => new ValhallaGenerationCohortManifest(graph, enabled, wrong));

        Assert.Equal(ValhallaGenerationFailureCode.IncompatibleGenerationSet, exception.Code);
    }
}

internal static class GenerationTestData
{
    public static readonly DateTimeOffset Now = new(2026, 7, 23, 15, 0, 0, TimeSpan.Zero);
    public const string GraphSha = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string BaseArtifactSha = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    public const string EnabledArtifactSha = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC";
    public const string ClosureArtifactSha = "DDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDDD";

    public static ValhallaGraphGenerationManifest CreateBase(
        int schemaVersion = ValhallaGenerationSchema.CurrentVersion,
        string regionId = "us-tn-nashville",
        string generationId = "base-20260723-001",
        string graphSha = GraphSha,
        Uri? artifactUri = null,
        string artifactSha = BaseArtifactSha,
        long byteLength = 1_024,
        DateTimeOffset? freshnessDeadlineUtc = null) =>
        new(
            schemaVersion,
            regionId,
            generationId,
            graphSha,
            artifactUri ?? new Uri("gs://truckmate-staging-graphs/nashville/base-20260723-001.tar.zst"),
            artifactSha,
            byteLength,
            Now - TimeSpan.FromMinutes(1),
            Now - TimeSpan.FromHours(2),
            freshnessDeadlineUtc ?? Now + TimeSpan.FromHours(23));

    public static ValhallaOverlayGenerationManifest CreateOverlay(
        TrafficSnapshotPolicy policy,
        string generationId = "overlay-20260723-001",
        string cohortId = "cohort-20260723-001",
        string baseGenerationId = "base-20260723-001",
        string graphSha = GraphSha,
        string closureSourceVersion = "closures-001",
        DateTimeOffset? trafficDataAsOfUtc = default,
        DateTimeOffset? closureDataAsOfUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        bool omitTrafficData = false)
    {
        DateTimeOffset? trafficAsOf = policy == TrafficSnapshotPolicy.Enabled
            ? omitTrafficData ? null : trafficDataAsOfUtc ?? Now - TimeSpan.FromSeconds(30)
            : trafficDataAsOfUtc;
        string suffix = policy == TrafficSnapshotPolicy.Enabled ? "enabled" : "closure";
        return new(
            ValhallaGenerationSchema.CurrentVersion,
            "us-tn-nashville",
            generationId == "overlay-20260723-001" ? $"{suffix}-20260723-001" : generationId,
            cohortId,
            baseGenerationId,
            graphSha,
            policy,
            new Uri($"gs://truckmate-staging-overlays/nashville/{suffix}-20260723-001.tar.zst"),
            policy == TrafficSnapshotPolicy.Enabled ? EnabledArtifactSha : ClosureArtifactSha,
            policy == TrafficSnapshotPolicy.Enabled ? 512 : 256,
            Now - TimeSpan.FromSeconds(10),
            expiresAtUtc ?? Now + TimeSpan.FromMinutes(1),
            trafficAsOf,
            closureDataAsOfUtc ?? Now - TimeSpan.FromSeconds(20),
            policy == TrafficSnapshotPolicy.Enabled ? "traffic-001" : "traffic-none",
            closureSourceVersion);
    }

    public static ValhallaGenerationCohortManifest CreateCohort(
        DateTimeOffset? trafficDataAsOfUtc = null,
        DateTimeOffset? closureDataAsOfUtc = null,
        DateTimeOffset? enabledExpiresAtUtc = null,
        DateTimeOffset? closureExpiresAtUtc = null) =>
        new(
            CreateBase(),
            CreateOverlay(
                TrafficSnapshotPolicy.Enabled,
                trafficDataAsOfUtc: trafficDataAsOfUtc,
                closureDataAsOfUtc: closureDataAsOfUtc,
                expiresAtUtc: enabledExpiresAtUtc),
            CreateOverlay(
                TrafficSnapshotPolicy.ClosureOnly,
                closureDataAsOfUtc: closureDataAsOfUtc,
                expiresAtUtc: closureExpiresAtUtc));
}
