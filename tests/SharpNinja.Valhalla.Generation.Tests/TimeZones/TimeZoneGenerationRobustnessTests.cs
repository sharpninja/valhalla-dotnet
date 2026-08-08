using SharpNinja.Valhalla.Generation.TimeZones;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.TimeZones;

public sealed class TimeZoneGenerationRobustnessTests
{
    [Fact]
    public async Task InvalidSource_DoesNotReplacePublishedDatabase()
    {
        string root = CreateRoot();
        string outputPath = Path.Combine(root, "tz_world.sqlite");
        await File.WriteAllTextAsync(
            outputPath,
            "published",
            TestContext.Current.CancellationToken);

        try
        {
            ITimeZoneDatabaseBuilder builder = new ManagedTimeZoneDatabaseBuilder();
            TimeZoneDatabaseBuildException exception = await Assert.ThrowsAsync<
                TimeZoneDatabaseBuildException>(
                    () => builder.BuildAsync(
                        new TimeZoneDatabaseBuildRequest(
                            Path.Combine(root, "missing.shp"),
                            "2026c",
                            Path.Combine(root, "work"),
                            outputPath,
                            64 * 1024 * 1024),
                        TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(
                TimeZoneDatabaseFailureCode.InvalidConfiguration,
                exception.FailureCode);
            Assert.Equal(
                "published",
                await File.ReadAllTextAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
            AssertNoTemporaryDatabase(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScratchExhaustion_DoesNotReplacePublishedDatabase()
    {
        string root = CreateRoot();
        string outputPath = Path.Combine(root, "tz_world.sqlite");
        await File.WriteAllTextAsync(
            outputPath,
            "published",
            TestContext.Current.CancellationToken);

        try
        {
            ITimeZoneDatabaseBuilder builder = new ManagedTimeZoneDatabaseBuilder();
            TimeZoneDatabaseBuildException exception = await Assert.ThrowsAsync<
                TimeZoneDatabaseBuildException>(
                    () => builder.BuildAsync(
                        new TimeZoneDatabaseBuildRequest(
                            FindFixture(),
                            "2026c",
                            Path.Combine(root, "work"),
                            outputPath,
                            512),
                        TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(
                TimeZoneDatabaseFailureCode.ScratchDiskBudgetExceeded,
                exception.FailureCode);
            Assert.Equal(
                "published",
                await File.ReadAllTextAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
            AssertNoTemporaryDatabase(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancelledBuild_DoesNotReplacePublishedDatabase()
    {
        string root = CreateRoot();
        string outputPath = Path.Combine(root, "tz_world.sqlite");
        await File.WriteAllTextAsync(
            outputPath,
            "published",
            TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        try
        {
            ITimeZoneDatabaseBuilder builder = new ManagedTimeZoneDatabaseBuilder();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => builder.BuildAsync(
                    new TimeZoneDatabaseBuildRequest(
                        FindFixture(),
                        "2026c",
                        Path.Combine(root, "work"),
                        outputPath,
                        64 * 1024 * 1024),
                    cancellation.Token).AsTask());

            Assert.Equal(
                "published",
                await File.ReadAllTextAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
            AssertNoTemporaryDatabase(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UnsupportedProjection_FailsClosed()
    {
        string root = CreateRoot();
        string shapefile = CopyFixture(root);
        string outputPath = Path.Combine(root, "tz_world.sqlite");
        await File.WriteAllTextAsync(
            Path.ChangeExtension(shapefile, ".prj"),
            "PROJCS[\"not-wgs84\"]",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            outputPath,
            "published",
            TestContext.Current.CancellationToken);

        try
        {
            ITimeZoneDatabaseBuilder builder = new ManagedTimeZoneDatabaseBuilder();
            TimeZoneDatabaseBuildException exception = await Assert.ThrowsAsync<
                TimeZoneDatabaseBuildException>(
                    () => builder.BuildAsync(
                        new TimeZoneDatabaseBuildRequest(
                            shapefile,
                            "2026c",
                            Path.Combine(root, "work"),
                            outputPath,
                            64 * 1024 * 1024),
                        TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(
                TimeZoneDatabaseFailureCode.UnsupportedProjection,
                exception.FailureCode);
            Assert.Equal(
                "published",
                await File.ReadAllTextAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
            AssertNoTemporaryDatabase(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptShapeRecord_FailsTypedAndPreservesPublishedDatabase()
    {
        string root = CreateRoot();
        string shapefile = CopyFixture(root);
        string outputPath = Path.Combine(root, "tz_world.sqlite");
        byte[] shapeBytes = await File.ReadAllBytesAsync(
            shapefile,
            TestContext.Current.CancellationToken);
        shapeBytes[104] = 0x7F;
        shapeBytes[105] = 0xFF;
        shapeBytes[106] = 0xFF;
        shapeBytes[107] = 0xFF;
        await File.WriteAllBytesAsync(
            shapefile,
            shapeBytes,
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            outputPath,
            "published",
            TestContext.Current.CancellationToken);

        try
        {
            ITimeZoneDatabaseBuilder builder = new ManagedTimeZoneDatabaseBuilder();
            TimeZoneDatabaseBuildException exception = await Assert.ThrowsAsync<
                TimeZoneDatabaseBuildException>(
                    () => builder.BuildAsync(
                        new TimeZoneDatabaseBuildRequest(
                            shapefile,
                            "2026c",
                            Path.Combine(root, "work"),
                            outputPath,
                            64 * 1024 * 1024),
                        TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(
                TimeZoneDatabaseFailureCode.InvalidShapefile,
                exception.FailureCode);
            Assert.Equal(
                "published",
                await File.ReadAllTextAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
            AssertNoTemporaryDatabase(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedBuilds_AreByteIdentical()
    {
        string root = CreateRoot();
        string firstOutput = Path.Combine(root, "first.sqlite");
        string secondOutput = Path.Combine(root, "second.sqlite");

        try
        {
            ITimeZoneDatabaseBuilder builder = new ManagedTimeZoneDatabaseBuilder();
            TimeZoneDatabaseBuildResult first = await builder.BuildAsync(
                new TimeZoneDatabaseBuildRequest(
                    FindFixture(),
                    "2026c",
                    Path.Combine(root, "work-first"),
                    firstOutput,
                    64 * 1024 * 1024),
                TestContext.Current.CancellationToken);
            TimeZoneDatabaseBuildResult second = await builder.BuildAsync(
                new TimeZoneDatabaseBuildRequest(
                    FindFixture(),
                    "2026c",
                    Path.Combine(root, "work-second"),
                    secondOutput,
                    64 * 1024 * 1024),
                TestContext.Current.CancellationToken);

            Assert.Equal(first.Sha256, second.Sha256);
            Assert.Equal(
                await File.ReadAllBytesAsync(
                    firstOutput,
                    TestContext.Current.CancellationToken),
                await File.ReadAllBytesAsync(
                    secondOutput,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-timezone-robustness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CopyFixture(string targetDirectory)
    {
        string source = FindFixture();
        string sourceBase = Path.Combine(
            Path.GetDirectoryName(source)!,
            Path.GetFileNameWithoutExtension(source));
        string targetBase = Path.Combine(
            targetDirectory,
            "timezone-copy");
        foreach (string extension in new[] { ".shp", ".shx", ".dbf", ".prj" })
        {
            File.Copy(
                sourceBase + extension,
                targetBase + extension,
                overwrite: true);
        }

        return targetBase + ".shp";
    }

    private static string FindFixture()
    {
        string[] parts =
        [
            "tests",
            "SharpNinja.Valhalla.Generation.Tests",
            "Fixtures",
            "Timezone",
            "2026c-jamaica",
            "timezone-2026c-jamaica.shp",
        ];
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return Path.Combine(parts);
    }

    private static void AssertNoTemporaryDatabase(string root)
    {
        Assert.Empty(
            Directory.EnumerateFiles(
                root,
                "*.tmp",
                SearchOption.AllDirectories));
    }
}
