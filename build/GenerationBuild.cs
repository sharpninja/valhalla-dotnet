using System.Diagnostics;
using System.Text;

using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;

using static Nuke.Common.Tools.DotNet.DotNetTasks;

partial class Build
{
    private const string GenerationVersion = "2.0.0";
    private const string OfficialValhallaImage =
        "ghcr.io/valhalla/valhalla:3.8.3@sha256:70b45295d81035e3562e1bbf996a28d5fc55e1ccc5d7e3fff9f297d3b1a1359f";

    [Parameter("Versioned JSON configuration for the Nashville generation benchmark.")]
    readonly string? NashvilleConfig;

    [Parameter("Versioned JSON configuration for the Lower-48 qualification run.")]
    readonly string? Lower48Config;

    [Parameter("Container tag for the managed generation image.")]
    readonly string GenerationContainerTag = "sharpninja/valhalla-dotnet-generation:2.0.0";

    AbsolutePath GenerationProject =>
        SourceDirectory / "SharpNinja.Valhalla.Generation" / "SharpNinja.Valhalla.Generation.csproj";

    AbsolutePath GenerationToolProject =>
        SourceDirectory / "SharpNinja.Valhalla.Generation.Tool" /
        "SharpNinja.Valhalla.Generation.Tool.csproj";

    AbsolutePath GenerationTestProject =>
        TestsDirectory / "SharpNinja.Valhalla.Generation.Tests" /
        "SharpNinja.Valhalla.Generation.Tests.csproj";

    AbsolutePath GenerationBenchmarkProject =>
        RootDirectory / "benchmarks" / "SharpNinja.Valhalla.Generation.Benchmarks" /
        "SharpNinja.Valhalla.Generation.Benchmarks.csproj";

    AbsolutePath GenerationArtifactDirectory =>
        RootDirectory / "artifacts" / "generation";

    AbsolutePath GenerationPackageDirectory =>
        GenerationArtifactDirectory / "nuget";

    AbsolutePath GenerationSbomDirectory =>
        GenerationArtifactDirectory / "sbom";

    AbsolutePath GenerationReceiptDirectory =>
        GenerationArtifactDirectory / "receipts";

    AbsolutePath GenerationTestResultsDirectory =>
        TestResultsDirectory / "generation";

    Target PrepareGenerationArtifacts => _ => _
        .Description("Prepare clean generation package, SBOM, and receipt directories.")
        .Executes(() =>
        {
            GenerationArtifactDirectory.CreateOrCleanDirectory();
            GenerationPackageDirectory.CreateDirectory();
            GenerationSbomDirectory.CreateDirectory();
            GenerationReceiptDirectory.CreateDirectory();
        });

    Target TestGeneration => _ => _
        .Description("Build and run the complete xUnit v3 generation suite.")
        .Executes(() =>
        {
            GenerationTestResultsDirectory.CreateOrCleanDirectory();
            DotNetRestore(s => s.SetProjectFile(GenerationTestProject));
            DotNetTest(s => s
                .SetProjectFile(GenerationTestProject)
                .SetConfiguration(Configuration)
                .SetProperty("SourceRevisionId", ReadSourceCommit())
                .SetLoggers("trx")
                .SetResultsDirectory(GenerationTestResultsDirectory));
        });

    Target PackGeneration => _ => _
        .Description("Pack the deterministic SharpNinja.Valhalla.Generation NuGet package.")
        .DependsOn(PrepareGenerationArtifacts, TestGeneration)
        .Executes(() =>
        {
            PackAndCanonicalize(GenerationProject);
        });

    Target PackGenerationTool => _ => _
        .Description("Pack the deterministic valhalla-dotnet .NET tool package.")
        .DependsOn(PackGeneration)
        .Executes(() =>
        {
            PackAndCanonicalize(GenerationToolProject);
        });

    Target GenerateGenerationSbom => _ => _
        .Description("Create a deterministic CycloneDX SBOM for generation artifacts.")
        .DependsOn(PackGenerationTool)
        .Executes(() =>
        {
            GenerationArtifactWriter.GenerateGenerationSbom(
                new[]
                {
                    (string)(SourceDirectory / "SharpNinja.Valhalla.Generation" / "obj" / "project.assets.json"),
                    (string)(SourceDirectory / "SharpNinja.Valhalla.Generation.Tool" / "obj" / "project.assets.json"),
                },
                GenerationSbomDirectory / "sharpninja-valhalla-generation.cdx.json",
                EffectiveGenerationVersion,
                ReadSourceCommit());
            WritePackageChecksums();
            GenerationArtifactWriter.WriteArtifactManifest(
                GenerationArtifactDirectory,
                GenerationReceiptDirectory / "generation-artifacts.manifest.json",
                EffectiveGenerationVersion,
                ReadSourceCommit());
        });

    Target BuildGenerationContainer => _ => _
        .Description("Build the non-root Linux valhalla-dotnet generation container.")
        .DependsOn(TestGeneration)
        .Executes(() =>
        {
            RunExternal(
                "docker",
                new[]
                {
                    "build",
                    "--file",
                    (string)(SourceDirectory / "SharpNinja.Valhalla.Generation.Tool" / "Dockerfile"),
                    "--build-arg",
                    $"SOURCE_COMMIT={ReadSourceCommit()}",
                    "--tag",
                    GenerationContainerTag,
                    (string)RootDirectory,
                },
                RootDirectory);
        });

    Target ValidateGenerationContainer => _ => _
        .Description("Verify the generation container runs as app and carries pinned provenance labels.")
        .DependsOn(BuildGenerationContainer)
        .Executes(() =>
        {
            var user = RunExternalCapture(
                "docker",
                new[] { "image", "inspect", GenerationContainerTag, "--format", "{{.Config.User}}" },
                RootDirectory);
            Assert.True(
                string.Equals(user.Trim(), "app", StringComparison.Ordinal),
                $"Generation image must run as app, but reported '{user.Trim()}'.");

            var labels = RunExternalCapture(
                "docker",
                new[] { "image", "inspect", GenerationContainerTag, "--format", "{{json .Config.Labels}}" },
                RootDirectory);
            Assert.True(
                labels.Contains("a60c7cbfc83e073f50887cd27e0109d02e6b64e5", StringComparison.Ordinal),
                "Generation image is missing the pinned Valhalla 3.8.3 provenance label.");
            Assert.False(
                labels.Contains("API_KEY", StringComparison.OrdinalIgnoreCase),
                "Generation image labels contain a credential-shaped key.");
        });

    Target BenchmarkNashville => _ => _
        .Description("Run the process-level Nashville managed-versus-official benchmark.")
        .DependsOn(TestGeneration)
        .Executes(() =>
        {
            RequireExistingFile(NashvilleConfig, "NashvilleConfig");
            RunBenchmarkHarness(
                "benchmark-nashville",
                NashvilleConfig!,
                GenerationReceiptDirectory / "nashville");
        });

    Target QualifyNashville => _ => _
        .Description("Enforce all Nashville performance, memory, size, and determinism thresholds.")
        .DependsOn(ValidateGenerationContainer, BenchmarkNashville)
        .Executes(() =>
        {
            RequireExistingFile(NashvilleConfig, "NashvilleConfig");
            RunBenchmarkHarness(
                "qualify-nashville",
                NashvilleConfig!,
                GenerationReceiptDirectory / "nashville-qualification");
        });

    Target QualifyLower48 => _ => _
        .Description("Run the 32-vCPU, 64-GiB, 1-TiB-scratch Lower-48 qualification.")
        .DependsOn(ValidateGenerationContainer)
        .Executes(() =>
        {
            RequireExistingFile(Lower48Config, "Lower48Config");
            RunBenchmarkHarness(
                "qualify-lower48",
                Lower48Config!,
                GenerationReceiptDirectory / "lower48-qualification");
        });

    Target GenerateUpstreamParityReport => _ => _
        .Description("Validate and render the pinned Valhalla 3.8.3 upstream parity manifest.")
        .DependsOn(TestGeneration)
        .Executes(() =>
        {
            DotNetTest(s => s
                .SetProjectFile(GenerationTestProject)
                .SetConfiguration(Configuration)
                .SetFilter("FullyQualifiedName~UpstreamParityManifestTests")
                .EnableNoRestore());
            GenerationArtifactWriter.WriteUpstreamParityReport(
                TestsDirectory / "SharpNinja.Valhalla.Generation.Tests" / "Fixtures" / "Parity" /
                "valhalla-3.8.3-generation-surface.json",
                GenerationReceiptDirectory / "valhalla-3.8.3-parity.md",
                ReadSourceCommit());
        });

    string EffectiveGenerationVersion =>
        string.IsNullOrWhiteSpace(PackageVersion)
            ? GenerationVersion
            : PackageVersion;

    void PackAndCanonicalize(AbsolutePath project)
    {
        DotNetPack(s => s
            .SetProject(project)
            .SetConfiguration(Configuration)
            .SetVersion(EffectiveGenerationVersion)
            .SetProperty("SourceRevisionId", ReadSourceCommit())
            .SetProperty("ContinuousIntegrationBuild", "true")
            .SetOutputDirectory(GenerationPackageDirectory));

        var packageName = Path.GetFileNameWithoutExtension(project) + "." +
            EffectiveGenerationVersion + ".nupkg";
        var packagePath = GenerationPackageDirectory / packageName;
        Assert.True(File.Exists(packagePath), $"Expected package was not produced: {packagePath}");
        GenerationArtifactWriter.CanonicalizeNugetPackage(packagePath);
    }

    void WritePackageChecksums()
    {
        foreach (var package in GenerationPackageDirectory.GlobFiles("*.nupkg"))
        {
            var checksum = GenerationArtifactWriter.ComputeSha256(package);
            File.WriteAllText(
                package + ".sha256",
                $"{checksum}  {Path.GetFileName(package)}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    void RunBenchmarkHarness(
        string command,
        string configurationPath,
        AbsolutePath outputDirectory)
    {
        outputDirectory.CreateOrCleanDirectory();
        RunExternal(
            "dotnet",
            new[]
            {
                "run",
                "--project",
                (string)GenerationBenchmarkProject,
                "--configuration",
                "Release",
                "--",
                command,
                "--config",
                configurationPath,
                "--output",
                (string)outputDirectory,
                "--managed-image",
                GenerationContainerTag,
                "--official-image",
                OfficialValhallaImage,
            },
            RootDirectory);
    }

    string ReadSourceCommit() =>
        RunExternalCapture(
            "git",
            new[] { "rev-parse", "HEAD" },
            RootDirectory)
        .Trim();

    static void RequireExistingFile(string? path, string parameterName)
    {
        Assert.False(
            string.IsNullOrWhiteSpace(path),
            $"--{parameterName} must identify a versioned qualification configuration.");
        Assert.True(
            File.Exists(path),
            $"Qualification configuration does not exist: {path}");
    }

    static void RunExternal(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        var result = RunExternalCore(executable, arguments, workingDirectory);
        Assert.True(result.ExitCode == 0, result.CombinedOutput);
    }

    static string RunExternalCapture(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        var result = RunExternalCore(executable, arguments, workingDirectory);
        Assert.True(result.ExitCode == 0, result.CombinedOutput);
        return result.StandardOutput;
    }

    static ExternalProcessResult RunExternalCore(
        string executable,
        IEnumerable<string> arguments,
        string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Assert.True(process.Start(), $"Could not start '{executable}'.");
        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        var standardOutput = standardOutputTask.GetAwaiter().GetResult();
        var standardError = standardErrorTask.GetAwaiter().GetResult();
        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            Console.Write(standardOutput);
        }

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            Console.Error.Write(standardError);
        }

        return new ExternalProcessResult(
            process.ExitCode,
            standardOutput,
            standardError);
    }

    sealed record ExternalProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError)
    {
        public string CombinedOutput =>
            string.Join(
                Environment.NewLine,
                new[] { StandardOutput, StandardError }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
