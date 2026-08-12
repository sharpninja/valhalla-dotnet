using System.Text.Json;
using System.Text.Json.Serialization;

namespace SharpNinja.Valhalla.Generation.Qualification;

/// <summary>
/// Durable schema for Nashville / Lower-48 pooled qualification receipts.
/// </summary>
public sealed record PooledQualificationReport
{
    public required string Campaign { get; init; }

    public required string Pipeline { get; init; }

    public required string BranchSha { get; init; }

    public required string PbfPath { get; init; }

    public required string PbfSha256 { get; init; }

    public required string ConfigPath { get; init; }

    public required string Oracle { get; init; }

    public required MachineProfile Machine { get; init; }

    public required IReadOnlyList<QualificationRun> Runs { get; init; }

    public required QualificationVerdict Verdict { get; init; }

    public string? Notes { get; init; }

    public static PooledQualificationReport Parse(string json) =>
        JsonSerializer.Deserialize<PooledQualificationReport>(json, JsonOptions)
        ?? throw new InvalidOperationException("Qualification report JSON deserialized to null.");

    public string ToJson() =>
        JsonSerializer.Serialize(this, JsonOptions);

    public void ValidateSchemaOrThrow()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Campaign);
        ArgumentException.ThrowIfNullOrWhiteSpace(Pipeline);
        ArgumentException.ThrowIfNullOrWhiteSpace(BranchSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(PbfPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(PbfSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(ConfigPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(Oracle);
        ArgumentNullException.ThrowIfNull(Machine);
        ArgumentNullException.ThrowIfNull(Runs);
        ArgumentNullException.ThrowIfNull(Verdict);
        if (Runs.Count == 0)
        {
            throw new InvalidOperationException("Qualification report must include at least one run.");
        }

        if (Campaign.Equals("Nashville", StringComparison.OrdinalIgnoreCase) &&
            Runs.Count < 6)
        {
            // warm-up + five measured
            throw new InvalidOperationException(
                "Nashville reports require warm-up plus five measured runs (6 total).");
        }

        if (Campaign.Equals("Lower48", StringComparison.OrdinalIgnoreCase))
        {
            if (Machine.VCpu < 32 || Machine.MemoryGiB < 64 || Machine.DiskGiB < 1024)
            {
                throw new InvalidOperationException(
                    "Lower-48 formal bar requires at least 32 vCPU / 64 GiB / 1 TiB.");
            }

            if (Runs.Count < 3)
            {
                throw new InvalidOperationException(
                    "Lower-48 reports require at least three pooled runs.");
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public sealed record MachineProfile
{
    public required int VCpu { get; init; }

    public required int MemoryGiB { get; init; }

    public required int DiskGiB { get; init; }

    public required string HostName { get; init; }
}

public sealed record QualificationRun
{
    public required int Index { get; init; }

    public required string Role { get; init; }

    public required string Pipeline { get; init; }

    public required double DurationSeconds { get; init; }

    public required long PeakWorkingSetBytes { get; init; }

    public required long PeakLiveNodes { get; init; }

    public required string OutputTreeSha256 { get; init; }

    public required bool Success { get; init; }

    public string? Failure { get; init; }
}

public sealed record QualificationVerdict
{
    public required bool MemoryGatePassed { get; init; }

    public required bool PerformanceGatePassed { get; init; }

    public required bool SemanticGatePassed { get; init; }

    public required string Status { get; init; }

    public double? MemoryRatioVsOfficial { get; init; }

    public double? PerformanceRatioVsOfficial { get; init; }

    public decimal? EstimatedGcpCostUsd { get; init; }
}
