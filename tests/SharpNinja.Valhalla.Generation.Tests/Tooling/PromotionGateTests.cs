using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Tooling;

/// <summary>
/// Lock-in: CLI default must remain Legacy until promotion-ready stamp exists
/// and formal L48 report is formal-pass. These tests assert the gate files'
/// fail-closed contract without flipping production defaults.
/// </summary>
public sealed class PromotionGateTests
{
    [Fact]
    public void PromotionScripts_ExistInRepo()
    {
        string root = FindRepoRoot();
        Assert.True(
            File.Exists(Path.Combine(root, "build", "Run-Lower48PooledQualification.ps1")));
        Assert.True(
            File.Exists(Path.Combine(root, "build", "Run-Lower48PooledQualification.Runner.ps1")));
        Assert.True(
            File.Exists(Path.Combine(root, "build", "Run-PooledFrontierPromotionCampaign.ps1")));
        Assert.True(
            File.Exists(Path.Combine(root, "build", "Promote-PooledFrontierCliDefault.ps1")));
    }

    [Fact]
    public void PromoteScript_RequiresPromotionReadyStamp_AndFormalL48Pass()
    {
        string root = FindRepoRoot();
        string promote = File.ReadAllText(
            Path.Combine(root, "build", "Promote-PooledFrontierCliDefault.ps1"));
        Assert.Contains("promotion-ready.json", promote, StringComparison.Ordinal);
        Assert.Contains("formal-pass", promote, StringComparison.Ordinal);
        Assert.Contains("PooledFrontier", promote, StringComparison.Ordinal);
        Assert.Contains("throw", promote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormalL48Harness_RefusesWithoutHostBar()
    {
        string root = FindRepoRoot();
        string harness = File.ReadAllText(
            Path.Combine(root, "build", "Run-Lower48PooledQualification.ps1"));
        Assert.Contains("32", harness, StringComparison.Ordinal);
        Assert.Contains("64", harness, StringComparison.Ordinal);
        Assert.Contains("1024", harness, StringComparison.Ordinal);
        Assert.Contains("blocked-formal-capacity-and-pbf-missing", harness, StringComparison.Ordinal);
    }

    [Fact]
    public void CliDefault_RemainsLegacy_UntilPromotionFlag()
    {
        string root = FindRepoRoot();
        string cli = File.ReadAllText(
            Path.Combine(root, "src", "SharpNinja.Valhalla.Generation.Tool", "ValhallaGenerationCli.cs"));
        // Default pipeline resolution must still center on Legacy for CLI path.
        Assert.Contains("Legacy", cli, StringComparison.Ordinal);
        Assert.Contains("road-pipeline", cli, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "build", "Run-Lower48PooledQualification.ps1")) ||
                File.Exists(Path.Combine(dir, "SharpNinja.Valhalla.sln")) ||
                File.Exists(Path.Combine(dir, "Directory.Build.props")))
            {
                // walk up until build scripts found
                string candidate = dir;
                for (int i = 0; i < 6; i++)
                {
                    if (File.Exists(Path.Combine(candidate, "build", "Run-Lower48PooledQualification.ps1")))
                    {
                        return candidate;
                    }

                    DirectoryInfo? parent = Directory.GetParent(candidate);
                    if (parent is null) break;
                    candidate = parent.FullName;
                }
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate valhalla-dotnet repo root with build scripts.");
    }
}
