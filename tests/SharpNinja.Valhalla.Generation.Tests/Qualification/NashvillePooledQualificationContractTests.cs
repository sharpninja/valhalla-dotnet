using SharpNinja.Valhalla.Generation.Qualification;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Qualification;

public sealed class NashvillePooledQualificationContractTests
{
    [Fact]
    public void ReportSchema_RequiresWarmupPlusFiveMeasuredRuns()
    {
        var report = BuildValidNashvilleReport(runCount: 6);
        report.ValidateSchemaOrThrow();
        Assert.Equal(6, report.Runs.Count);
        Assert.Equal("warm-up", report.Runs[0].Role);
        Assert.Equal(5, report.Runs.Count(r => r.Role == "measured"));
    }

    [Fact]
    public void ReportSchema_RejectsFewerThanSixRunsForNashville()
    {
        var report = BuildValidNashvilleReport(runCount: 5);
        Assert.Throws<InvalidOperationException>(() => report.ValidateSchemaOrThrow());
    }

    [Fact]
    public void ReportSchema_FailClosed_WhenMemoryOnlyWin()
    {
        var report = BuildValidNashvilleReport(runCount: 6) with
        {
            Verdict = new QualificationVerdict
            {
                MemoryGatePassed = true,
                PerformanceGatePassed = false,
                SemanticGatePassed = true,
                Status = "experimental-memory-only",
                MemoryRatioVsOfficial = 0.55,
                PerformanceRatioVsOfficial = 1.40,
            },
        };
        report.ValidateSchemaOrThrow();
        Assert.NotEqual("production-ready", report.Verdict.Status);
        Assert.False(report.Verdict.PerformanceGatePassed);
    }

    [Fact]
    public void Report_RoundTripsJson()
    {
        var report = BuildValidNashvilleReport(6);
        string json = report.ToJson();
        PooledQualificationReport parsed = PooledQualificationReport.Parse(json);
        Assert.Equal(report.Campaign, parsed.Campaign);
        Assert.Equal(report.Runs.Count, parsed.Runs.Count);
        Assert.Equal(report.PbfSha256, parsed.PbfSha256);
    }

    private static PooledQualificationReport BuildValidNashvilleReport(int runCount)
    {
        var runs = new List<QualificationRun>();
        for (int i = 0; i < runCount; i++)
        {
            runs.Add(new QualificationRun
            {
                Index = i,
                Role = i == 0 ? "warm-up" : "measured",
                Pipeline = i % 2 == 0 ? "PooledFrontier" : "OfficialValhalla",
                DurationSeconds = 100 + i,
                PeakWorkingSetBytes = 8L * 1024 * 1024 * 1024,
                PeakLiveNodes = 1_000_000,
                OutputTreeSha256 = new string('A', 64),
                Success = true,
            });
        }

        return new PooledQualificationReport
        {
            Campaign = "Nashville",
            Pipeline = "PooledFrontier",
            BranchSha = "deadbeef",
            PbfPath = "artifacts/nashville.osm.pbf",
            PbfSha256 = new string('B', 64),
            ConfigPath = "benchmarks/config/nashville-tennessee-3.8.3.json",
            Oracle = "OfficialValhalla-3.8.3",
            Machine = new MachineProfile
            {
                VCpu = 16,
                MemoryGiB = 64,
                DiskGiB = 512,
                HostName = "lab-host",
            },
            Runs = runs,
            Verdict = new QualificationVerdict
            {
                MemoryGatePassed = true,
                PerformanceGatePassed = true,
                SemanticGatePassed = true,
                Status = "production-ready",
                MemoryRatioVsOfficial = 0.70,
                PerformanceRatioVsOfficial = 0.95,
                EstimatedGcpCostUsd = 12.34m,
            },
        };
    }
}
