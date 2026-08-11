using System.Text.Json;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class ValhallaGenerationFrontierMetricsTests
{
    [Fact]
    public void StageModels_PreserveAndSerializeFrontierMetrics()
    {
        var metrics = new ValhallaGenerationFrontierMetrics(
            CanonicalNodesRead: 10,
            WayNodeOccurrencesProcessed: 20,
            GraphAnchorsCreated: 3,
            SecondaryNodesProcessed: 17,
            SecondarySlotsReleased: 17,
            TotalSlotRents: 20,
            SlotReuseCount: 15,
            PeakLiveSlots: 3,
            TotalSlabsRented: 1,
            PeakSlabBytes: 4096,
            MaximumUnresolvedPathAnchors: 2,
            IncidenceStoreBytes: 100,
            NodeStoreBytes: 200,
            ShapeStoreBytes: 300,
            EdgeStoreBytes: 400,
            SelectedDegreeOfParallelism: 2,
            PerWorkerMemoryReservationBytes: 8192,
            MappedStorageHighWaterMarkBytes: 1000,
            StaleHandleRejections: 0);

        ValhallaGenerationStageResult result =
            ValhallaGenerationStageResult.Empty("frontier") with
            {
                FrontierMetrics = metrics,
            };
        var receipt = new ValhallaGenerationStageReceipt(
            ValhallaGenerationStage.BuildEdges,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            "input",
            "output",
            20,
            100,
            200,
            2,
            0,
            4096,
            1000,
            "checkpoint",
            [],
            [],
            new Dictionary<string, string>())
        {
            FrontierMetrics = result.FrontierMetrics,
        };

        Assert.Same(metrics, result.FrontierMetrics);
        Assert.Same(metrics, receipt.FrontierMetrics);
        string json = JsonSerializer.Serialize(receipt);
        Assert.Contains("\"frontierMetrics\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"peakLiveSlots\":3", json, StringComparison.OrdinalIgnoreCase);
    }
}
