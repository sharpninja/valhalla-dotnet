using System.Reflection;
using SharpNinja.Valhalla.Generations;
using SharpNinja.Valhalla.Traffic.Tiles;

namespace SharpNinja.Valhalla.Tests.Generations;

public sealed class ActiveValhallaGenerationResolverTests
{
    [Fact]
    public void TrafficEnabled_UsesEnabledTrafficGeneration()
    {
        ValhallaGenerationCohortManifest cohort = GenerationTestData.CreateCohort();
        var resolver = new ActiveValhallaGenerationResolver();

        ActiveValhallaGenerationResolution result = resolver.Resolve(
            cohort,
            TrafficSnapshotPolicy.Enabled,
            GenerationTestData.Now);

        Assert.True(result.IsAvailable);
        Assert.Same(cohort.TrafficEnabled, result.GenerationSet!.Overlay);
        Assert.Equal(TrafficSnapshotPolicy.Enabled, result.GenerationSet.Stamp.OverlayPolicy);
        Assert.True(result.GenerationSet.TrafficPolicy.IncludeTrafficDelayInEta);
        Assert.True(result.GenerationSet.TrafficPolicy.IncludeTrafficDelayInFriction);
    }

    [Fact]
    public void TrafficDisabled_UsesClosureOnlyGeneration()
    {
        ValhallaGenerationCohortManifest cohort = GenerationTestData.CreateCohort();
        var resolver = new ActiveValhallaGenerationResolver();

        ActiveValhallaGenerationResolution result = resolver.Resolve(
            cohort,
            TrafficSnapshotPolicy.ClosureOnly,
            GenerationTestData.Now);

        Assert.True(result.IsAvailable);
        Assert.Same(cohort.ClosureOnly, result.GenerationSet!.Overlay);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, result.GenerationSet.Stamp.OverlayPolicy);
        Assert.False(result.GenerationSet.TrafficPolicy.IncludeTrafficDelayInEta);
        Assert.False(result.GenerationSet.TrafficPolicy.IncludeTrafficDelayInFriction);
        Assert.True(result.GenerationSet.TrafficPolicy.KeepClosuresAsRouteConstraints);
    }

    [Fact]
    public void ExpiredTrafficGeneration_ReturnsTrafficUnavailableState()
    {
        ValhallaGenerationCohortManifest cohort = GenerationTestData.CreateCohort(
            trafficDataAsOfUtc: GenerationTestData.Now - TimeSpan.FromMinutes(6));
        var resolver = new ActiveValhallaGenerationResolver();

        ActiveValhallaGenerationResolution result = resolver.Resolve(
            cohort,
            TrafficSnapshotPolicy.Enabled,
            GenerationTestData.Now);

        Assert.Equal(ActiveValhallaGenerationStatus.TrafficUnavailable, result.Status);
        Assert.Equal(ValhallaGenerationFailureCode.TrafficUnavailable, result.FailureCode);
        Assert.Null(result.GenerationSet);
        Assert.NotNull(result.ClosureOnlyFallback);
        Assert.Equal(TrafficSnapshotPolicy.ClosureOnly, result.ClosureOnlyFallback!.Overlay.Policy);
    }

    [Fact]
    public void ExpiredClosureGeneration_ReturnsClosureUnavailableState()
    {
        ValhallaGenerationCohortManifest cohort = GenerationTestData.CreateCohort(
            closureDataAsOfUtc: GenerationTestData.Now - TimeSpan.FromMinutes(3));
        var resolver = new ActiveValhallaGenerationResolver();

        ActiveValhallaGenerationResolution result = resolver.Resolve(
            cohort,
            TrafficSnapshotPolicy.Enabled,
            GenerationTestData.Now);

        Assert.Equal(ActiveValhallaGenerationStatus.ClosureUnavailable, result.Status);
        Assert.Equal(ValhallaGenerationFailureCode.ClosureUnavailable, result.FailureCode);
        Assert.Null(result.GenerationSet);
        Assert.Null(result.ClosureOnlyFallback);
    }

    [Fact]
    public void RequiredResolutionMatrix_IsComplete()
    {
        var resolver = new ActiveValhallaGenerationResolver();
        ActiveValhallaGenerationResolution enabled = resolver.Resolve(
            GenerationTestData.CreateCohort(),
            TrafficSnapshotPolicy.Enabled,
            GenerationTestData.Now);
        ActiveValhallaGenerationResolution closureOnly = resolver.Resolve(
            GenerationTestData.CreateCohort(),
            TrafficSnapshotPolicy.ClosureOnly,
            GenerationTestData.Now);
        ActiveValhallaGenerationResolution staleTraffic = resolver.Resolve(
            GenerationTestData.CreateCohort(
                trafficDataAsOfUtc: GenerationTestData.Now - TimeSpan.FromMinutes(6)),
            TrafficSnapshotPolicy.Enabled,
            GenerationTestData.Now);
        ActiveValhallaGenerationResolution staleClosure = resolver.Resolve(
            GenerationTestData.CreateCohort(
                closureDataAsOfUtc: GenerationTestData.Now - TimeSpan.FromMinutes(3)),
            TrafficSnapshotPolicy.ClosureOnly,
            GenerationTestData.Now);

        Assert.Equal(ActiveValhallaGenerationStatus.Available, enabled.Status);
        Assert.Equal(ActiveValhallaGenerationStatus.Available, closureOnly.Status);
        Assert.Equal(ActiveValhallaGenerationStatus.TrafficUnavailable, staleTraffic.Status);
        Assert.Equal(ActiveValhallaGenerationStatus.ClosureUnavailable, staleClosure.Status);
    }
}

public sealed class ClosureOnlyGenerationTests
{
    [Fact]
    public void ClosureOnlyPolicy_ExcludesDynamicDelayAndKeepsHardDenies()
    {
        ActiveValhallaGenerationResolution result = new ActiveValhallaGenerationResolver().Resolve(
            GenerationTestData.CreateCohort(),
            TrafficSnapshotPolicy.ClosureOnly,
            GenerationTestData.Now);

        Assert.True(result.IsAvailable);
        Assert.Null(result.GenerationSet!.Overlay.TrafficDataAsOfUtc);
        Assert.False(result.GenerationSet.TrafficPolicy.IncludeTrafficDelayInEta);
        Assert.False(result.GenerationSet.TrafficPolicy.IncludeTrafficDelayInFriction);
        Assert.True(result.GenerationSet.TrafficPolicy.KeepClosuresAsRouteConstraints);
        Assert.NotEmpty(result.GenerationSet.Stamp.ClosureSourceVersion);
    }
}

public sealed class EmbeddedValhallaRoutingClientGenerationTests
{
    [Fact]
    public void PinnedRouteResponse_IncludesExactGenerationStamp()
    {
        ActiveValhallaGenerationResolution resolution = new ActiveValhallaGenerationResolver().Resolve(
            GenerationTestData.CreateCohort(),
            TrafficSnapshotPolicy.Enabled,
            GenerationTestData.Now);
        var lease = new ValhallaGenerationLease(resolution.GenerationSet!);
        var request = new OsmRouteRequest(
            Endpoint: null,
            Origin: new GeoCoordinate(36.1263, -86.6774),
            Destination: new GeoCoordinate(36.1627, -86.7816))
        {
            GenerationLease = lease,
        };
        var unstamped = new OsmRouteResult(Array.Empty<OsmRouteCandidate>(), Error: null);

        OsmRouteResult stamped = EmbeddedValhallaRoutingClient.AttachGenerationStamp(
            unstamped,
            request);

        Assert.Equal(lease.Stamp, stamped.GenerationStamp);
        Assert.Equal("us-tn-nashville", stamped.GenerationStamp!.RegionId);
        Assert.Equal("base-20260723-001", stamped.GenerationStamp.BaseGenerationId);
        Assert.Equal("enabled-20260723-001", stamped.GenerationStamp.OverlayGenerationId);
        Assert.Equal("cohort-20260723-001", stamped.GenerationStamp.CohortId);
        Assert.Equal(GenerationTestData.GraphSha, stamped.GenerationStamp.GraphSha256);
        Assert.Equal("traffic-001", stamped.GenerationStamp.TrafficSourceVersion);
        Assert.Equal("closures-001", stamped.GenerationStamp.ClosureSourceVersion);
    }
}

public sealed class ValhallaPackageBoundaryTests
{
    [Fact]
    public void DistributedContracts_PreserveLocalRuntimeWithoutGoogleCloudDependency()
    {
        Assembly assembly = typeof(ValhallaGenerationSchema).Assembly;
        string[] references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();
        Type[] publicTypes = assembly.GetExportedTypes();

        Assert.DoesNotContain(references, name => name.StartsWith("Google.", StringComparison.Ordinal));
        Assert.DoesNotContain(publicTypes, type =>
            (type.Namespace ?? string.Empty).StartsWith("Google.", StringComparison.Ordinal));
        Assert.NotNull(typeof(TrafficSnapshotReference));
        Assert.NotNull(typeof(TrafficSnapshotStore));
        Assert.NotNull(typeof(EmbeddedValhallaRoutingClient));
    }
}

public sealed class GenerationRequirementTraceabilityTests
{
    [Fact]
    public void FR_VALHALLA_014_AllAcceptanceCriteriaMapToXunitV3Tests()
    {
        string[] exactTests =
        [
            "ValhallaGraphGenerationManifestTests.ValidManifest_PreservesRegionIdentityFreshnessAndIntegrity",
            "ValhallaGenerationCohortManifestTests.ValidManifest_PreservesOverlayIdentityPolicyAndFreshness",
            "ValhallaGenerationCohortManifestTests.ClosurePromotion_RepublishesPoliciesWithSameClosureSourceVersion",
            "ValhallaGenerationManifestValidationTests.InvalidOrUnsupportedManifest_FailsClosed",
            "ValhallaGenerationArtifactSourceRegistryTests.FutureArtifactSource_RegistersWithoutCoreSwitchChange",
            "ValhallaGenerationArtifactAcquisitionTests.CorruptOrCancelledArtifact_DoesNotReplaceValidGeneration",
            "ValhallaGenerationArtifactAcquisitionTests.ConcurrentSameGeneration_IsIdempotentAndAtomicallyMaterialized",
            "ValhallaGenerationCompatibilityTests.IncompatibleGenerationSet_IsRejectedBeforeRouting",
            "ActiveValhallaGenerationResolverTests.TrafficEnabled_UsesEnabledTrafficGeneration",
            "ActiveValhallaGenerationResolverTests.TrafficDisabled_UsesClosureOnlyGeneration",
            "ActiveValhallaGenerationResolverTests.ExpiredTrafficGeneration_ReturnsTrafficUnavailableState",
            "ActiveValhallaGenerationResolverTests.ExpiredClosureGeneration_ReturnsClosureUnavailableState",
            "ClosureOnlyGenerationTests.ClosureOnlyPolicy_ExcludesDynamicDelayAndKeepsHardDenies",
            "EmbeddedValhallaRoutingClientGenerationTests.PinnedRouteResponse_IncludesExactGenerationStamp",
            "ValhallaPackageBoundaryTests.DistributedContracts_PreserveLocalRuntimeWithoutGoogleCloudDependency",
        ];

        Assembly assembly = typeof(GenerationRequirementTraceabilityTests).Assembly;
        string[] actual = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Select(method => $"{type.Name}.{method.Name}"))
            .ToArray();

        Assert.Equal(15, exactTests.Length);
        Assert.All(exactTests, test => Assert.Contains(test, actual));
    }
}

public sealed class BuildToolchainContractTests
{
    [Fact]
    public void DistributedGenerationGate_HasZeroFailuresZeroSkipsAndNoGoogleDependency()
    {
        Assembly data = typeof(ValhallaGenerationSchema).Assembly;
        Assert.DoesNotContain(
            data.GetReferencedAssemblies(),
            reference => (reference.Name ?? string.Empty).StartsWith("Google.", StringComparison.Ordinal));
        Assert.Contains(
            typeof(GenerationRequirementTraceabilityTests).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "xunit.v3.core", StringComparison.OrdinalIgnoreCase)
                || string.Equals(reference.Name, "xunit.v3.assert", StringComparison.OrdinalIgnoreCase));
    }
}
