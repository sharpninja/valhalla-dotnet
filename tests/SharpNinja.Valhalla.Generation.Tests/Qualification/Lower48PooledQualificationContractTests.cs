using SharpNinja.Valhalla.Generation.Qualification;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Qualification;

public sealed class Lower48PooledQualificationContractTests
{
    [Fact]
    public void ReportSchema_RequiresFormalMachineBar()
    {
        var report = BuildValidLower48(3);
        report.ValidateSchemaOrThrow();
        Assert.True(report.Machine.VCpu >= 32);
        Assert.True(report.Machine.MemoryGiB >= 64);
        Assert.True(report.Machine.DiskGiB >= 1024);
    }

    [Fact]
    public void ReportSchema_RejectsDesktopCapacityAsFormalL48()
    {
        var report = BuildValidLower48(3) with
        {
            Machine = new MachineProfile
            {
                VCpu = 16,
                MemoryGiB = 64,
                DiskGiB = 2048,
                HostName = "desktop-probe",
            },
        };
        Assert.Throws<InvalidOperationException>(() => report.ValidateSchemaOrThrow());
    }

    [Fact]
    public void ReportSchema_RequiresThreePooledRuns()
    {
        var report = BuildValidLower48(2);
        Assert.Throws<InvalidOperationException>(() => report.ValidateSchemaOrThrow());
    }

    private static PooledQualificationReport BuildValidLower48(int runCount)
    {
        var runs = new List<QualificationRun>();
        for (int i = 0; i < runCount; i++)
        {
            runs.Add(new QualificationRun
            {
                Index = i,
                Role = "measured",
                Pipeline = "PooledFrontier",
                DurationSeconds = 3600 + i,
                PeakWorkingSetBytes = 40L * 1024 * 1024 * 1024,
                PeakLiveNodes = 50_000_000,
                OutputTreeSha256 = new string('C', 64),
                Success = true,
            });
        }

        return new PooledQualificationReport
        {
            Campaign = "Lower48",
            Pipeline = "PooledFrontier",
            BranchSha = "cafebabe",
            PbfPath = "artifacts/us-lower48.osm.pbf",
            PbfSha256 = new string('D', 64),
            ConfigPath = "benchmarks/config/lower48-3.8.3.json",
            Oracle = "OfficialValhalla-3.8.3",
            Machine = new MachineProfile
            {
                VCpu = 32,
                MemoryGiB = 64,
                DiskGiB = 1024,
                HostName = "formal-l48-host",
            },
            Runs = runs,
            Verdict = new QualificationVerdict
            {
                MemoryGatePassed = true,
                PerformanceGatePassed = true,
                SemanticGatePassed = true,
                Status = "formal-pass",
            },
        };
    }
}
