using Microsoft.Data.Sqlite;
using SharpNinja.Valhalla.Generation.Admin;
using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Admin;

public sealed class AdminGenerationRobustnessTests
{
    private static readonly byte[] PublishedDatabaseSentinel = [0x56, 0x41, 0x4C, 0x48];

    [Fact]
    public async Task InvalidConfiguration_FailsBeforeMutation()
    {
        string root = CreateRoot();
        string outputPath = Path.Combine(root, "admin.sqlite");
        await File.WriteAllBytesAsync(
            outputPath,
            PublishedDatabaseSentinel,
            TestContext.Current.CancellationToken);

        try
        {
            AdminDatabaseBuildException exception =
                await Assert.ThrowsAsync<AdminDatabaseBuildException>(
                    () => BuildAsync(
                        Path.Combine(root, "missing.osm.pbf"),
                        root,
                        outputPath,
                        cancellationToken: TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(
                AdminDatabaseFailureCode.InvalidConfiguration,
                exception.FailureCode);
            Assert.Equal(
                PublishedDatabaseSentinel,
                await File.ReadAllBytesAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ScratchBudgetExhaustion_PreservesPublishedDatabaseAndCleansTemporaryFiles()
    {
        string root = CreateRoot();
        string outputPath = Path.Combine(root, "admin.sqlite");
        await File.WriteAllBytesAsync(
            outputPath,
            PublishedDatabaseSentinel,
            TestContext.Current.CancellationToken);

        try
        {
            AdminDatabaseBuildException exception =
                await Assert.ThrowsAsync<AdminDatabaseBuildException>(
                    () => BuildAsync(
                        FindRepositoryArtifact("artifacts", "monaco.osm.pbf"),
                        root,
                        outputPath,
                        scratchDiskBudgetBytes: 1,
                        cancellationToken: TestContext.Current.CancellationToken).AsTask());

            Assert.Equal(
                AdminDatabaseFailureCode.ScratchDiskBudgetExceeded,
                exception.FailureCode);
            Assert.Equal(
                PublishedDatabaseSentinel,
                await File.ReadAllBytesAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                path => path.EndsWith(".tmp", StringComparison.Ordinal)
                    || path.Contains(".staging.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_PreservesPublishedDatabaseAndCleansTemporaryFiles()
    {
        string root = CreateRoot();
        string outputPath = Path.Combine(root, "admin.sqlite");
        await File.WriteAllBytesAsync(
            outputPath,
            PublishedDatabaseSentinel,
            TestContext.Current.CancellationToken);
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => BuildAsync(
                    FindRepositoryArtifact("artifacts", "monaco.osm.pbf"),
                    root,
                    outputPath,
                    cancellationToken: cancellation.Token).AsTask());

            Assert.Equal(
                PublishedDatabaseSentinel,
                await File.ReadAllBytesAsync(
                    outputPath,
                    TestContext.Current.CancellationToken));
            Assert.DoesNotContain(
                Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories),
                path => path.EndsWith(".tmp", StringComparison.Ordinal)
                    || path.Contains(".staging.", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedBuilds_ProduceIdenticalDatabases()
    {
        string root = CreateRoot();
        string sourcePbf = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string firstOutput = Path.Combine(root, "first.sqlite");
        string secondOutput = Path.Combine(root, "second.sqlite");

        try
        {
            AdminDatabaseBuildResult first = await BuildAsync(
                sourcePbf,
                Path.Combine(root, "first-work"),
                firstOutput,
                cancellationToken: TestContext.Current.CancellationToken);
            AdminDatabaseBuildResult second = await BuildAsync(
                sourcePbf,
                Path.Combine(root, "second-work"),
                secondOutput,
                cancellationToken: TestContext.Current.CancellationToken);

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

    [Fact]
    public async Task SpatialIndexContract_ResolvesOfficialBoundingBoxLookup()
    {
        string root = CreateRoot();
        string outputPath = Path.Combine(root, "admin.sqlite");

        try
        {
            await BuildAsync(
                FindRepositoryArtifact("artifacts", "monaco.osm.pbf"),
                Path.Combine(root, "work"),
                outputPath,
                cancellationToken: TestContext.Current.CancellationToken);

            SQLitePCL.Batteries_V2.Init();
            await using SqliteConnection connection = new(
                $"Data Source={outputPath};Mode=ReadOnly;Pooling=False");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT admins.name
                FROM admins
                INNER JOIN idx_admins_geom
                    ON idx_admins_geom.pkid = admins.rowid
                WHERE idx_admins_geom.xmin <= $max_x
                  AND idx_admins_geom.xmax >= $min_x
                  AND idx_admins_geom.ymin <= $max_y
                  AND idx_admins_geom.ymax >= $min_y
                """;
            command.Parameters.AddWithValue("$min_x", 7.41);
            command.Parameters.AddWithValue("$max_x", 7.42);
            command.Parameters.AddWithValue("$min_y", 43.72);
            command.Parameters.AddWithValue("$max_y", 43.74);

            Assert.Equal(
                "Monaco",
                (string)(await command.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ValueTask<AdminDatabaseBuildResult> BuildAsync(
        string sourcePbf,
        string workingDirectory,
        string outputPath,
        long scratchDiskBudgetBytes = 256 * 1024 * 1024,
        CancellationToken cancellationToken = default) =>
        new ManagedAdminDatabaseBuilder().BuildAsync(
            new AdminDatabaseBuildRequest(
                [sourcePbf],
                workingDirectory,
                outputPath,
                IntermediateStorageMode.Auto,
                16 * 1024 * 1024,
                scratchDiskBudgetBytes),
            cancellationToken);

    private static string CreateRoot()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-admin-robustness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string FindRepositoryArtifact(params string[] parts)
    {
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
}
