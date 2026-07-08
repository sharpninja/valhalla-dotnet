using System;

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;

using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// Nuke build for the SharpNinja.Valhalla package: Clean / Restore / Compile / Test / Pack /
/// Publish. Local default target is Pack; CI runs Publish (which chains the rest) and reads the
/// NuGet API key from the NUGET_API_KEY environment variable.
/// </summary>
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Pack);

    [Parameter("Build configuration - Debug locally, Release on the server by default.")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    [Parameter("NuGet feed the package is pushed to.")]
    readonly string NugetSource = "https://api.nuget.org/v3/index.json";

    [Parameter("NuGet API key for publishing; defaults to the NUGET_API_KEY environment variable."), Secret]
    readonly string NugetApiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY");

    [Parameter("Optional package version override (e.g. from CI); falls back to the csproj <Version>.")]
    readonly string PackageVersion;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts" / "nuget";
    AbsolutePath TestResultsDirectory => RootDirectory / "artifacts" / "test-results";

    AbsolutePath PackageProject => SourceDirectory / "SharpNinja.Valhalla" / "SharpNinja.Valhalla.csproj";
    AbsolutePath TestProject => TestsDirectory / "SharpNinja.Valhalla.Tests" / "SharpNinja.Valhalla.Tests.csproj";

    Target Clean => _ => _
        .Description("Delete bin/obj and the artifacts output.")
        .Executes(() =>
        {
            foreach (var dir in SourceDirectory.GlobDirectories("**/bin", "**/obj"))
            {
                dir.DeleteDirectory();
            }

            foreach (var dir in TestsDirectory.GlobDirectories("**/bin", "**/obj"))
            {
                dir.DeleteDirectory();
            }

            ArtifactsDirectory.CreateOrCleanDirectory();
            TestResultsDirectory.CreateOrCleanDirectory();
        });

    Target Restore => _ => _
        .Description("Restore NuGet packages for the deliverable projects.")
        .Executes(() =>
        {
            // Restore the deliverable projects (not the whole solution), so the Nuke build's own
            // _build project - which is running right now - is never touched. The engine is pure
            // managed code (net10.0 only), so no .NET workloads are required.
            DotNetRestore(s => s.SetProjectFile(PackageProject));
            DotNetRestore(s => s.SetProjectFile(TestProject));
        });

    Target Compile => _ => _
        .Description("Build the engine and the test project.")
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(PackageProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
            DotNetBuild(s => s
                .SetProjectFile(TestProject)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Test => _ => _
        .Description("Run the engine test suite (trx to artifacts/test-results).")
        .DependsOn(Compile)
        .Executes(() =>
        {
            // Start clean so a persisted (self-hosted) agent does not accumulate + re-publish
            // stale trx from prior runs.
            TestResultsDirectory.CreateOrCleanDirectory();
            DotNetTest(s => s
                .SetProjectFile(TestProject)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .SetLoggers("trx")
                .SetResultsDirectory(TestResultsDirectory));
        });

    Target Pack => _ => _
        .Description("Pack the SharpNinja.Valhalla NuGet package into artifacts/nuget.")
        .DependsOn(Test)
        .Produces(ArtifactsDirectory / "*.nupkg")
        .Executes(() =>
        {
            // Empty the output dir first so Publish can only ever push the package produced by
            // THIS run - a stale, different-version nupkg left in a persisted (self-hosted)
            // workspace must never leak to nuget.org (an irreversible publish).
            ArtifactsDirectory.CreateOrCleanDirectory();
            DotNetPack(s =>
            {
                s = s
                    .SetProject(PackageProject)
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    .SetOutputDirectory(ArtifactsDirectory);
                if (!string.IsNullOrWhiteSpace(PackageVersion))
                {
                    s = s.SetVersion(PackageVersion);
                }

                return s;
            });
        });

    Target Publish => _ => _
        .Description("Push the packed package to the NuGet feed using NUGET_API_KEY.")
        .DependsOn(Pack)
        .Requires(() => NugetApiKey)
        .Executes(() =>
        {
            // Fail fast on an unset/misnamed CI secret: Azure DevOps leaves an undefined $(VAR)
            // macro as its literal text, which slips past Requires (non-empty) and would otherwise
            // only fail at push with a confusing 403.
            Assert.False(
                NugetApiKey.StartsWith("$(", StringComparison.Ordinal),
                "NUGET_API_KEY is not set (received an unexpanded pipeline variable). Configure the NUGET_API_KEY secret.");

            var packages = ArtifactsDirectory.GlobFiles("*.nupkg");
            Assert.True(packages.Count > 0, "No .nupkg found to publish; run Pack first.");
            foreach (var package in packages)
            {
                DotNetNuGetPush(s => s
                    .SetTargetPath(package)
                    .SetSource(NugetSource)
                    .SetApiKey(NugetApiKey)
                    .EnableSkipDuplicate());
            }
        });
}
