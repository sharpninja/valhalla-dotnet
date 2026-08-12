using SharpNinja.Valhalla.Generation.Roads;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class ManagedRoadGraphPipelineContractTests
{
    [Fact]
    public void BuildRequest_DefaultsToLegacyPipeline()
    {
        var request = CreateRequest();

        Assert.Equal(ManagedRoadGraphPipeline.Legacy, request.Pipeline);
    }

    [Fact]
    public void BuildRequest_PooledFrontierIsInitOnlyAndExplicit()
    {
        ManagedRoadGraphBuildRequest request = CreateRequest() with
        {
            Pipeline = ManagedRoadGraphPipeline.PooledFrontier,
        };

        Assert.Equal(ManagedRoadGraphPipeline.PooledFrontier, request.Pipeline);
    }

    [Theory]
    [InlineData(ValhallaGenerationProfile.Full)]
    [InlineData(ValhallaGenerationProfile.RoadOnly)]
    [InlineData(ValhallaGenerationProfile.Truck)]
    public void NonLegacyProfiles_PreserveExplicitPipeline(
        ValhallaGenerationProfile profile)
    {
        Assert.Equal(
            ManagedRoadGraphPipeline.PooledFrontier,
            ManagedRoadGraphPipelineSelector.Resolve(
                profile,
                ManagedRoadGraphPipeline.PooledFrontier));
    }

    [Fact]
    public void LegacyEmbedded_AlwaysSelectsLegacyPipeline()
    {
        Assert.Equal(
            ManagedRoadGraphPipeline.Legacy,
            ManagedRoadGraphPipelineSelector.Resolve(
                ValhallaGenerationProfile.LegacyEmbedded,
                ManagedRoadGraphPipeline.PooledFrontier));
    }

    private static ManagedRoadGraphBuildRequest CreateRequest() => new(
        ["fixture.osm.pbf"],
        "work",
        "output",
        IntermediateStorageMode.Memory,
        MemoryBudgetBytes: 1024,
        ScratchDiskBudgetBytes: 1024,
        TileBuilderConfig: new TileBuilderConfig());
}
