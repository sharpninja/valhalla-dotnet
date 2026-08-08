using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetTopologySuite.IO;
using SharpNinja.Valhalla.Generation.TimeZones;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.TimeZones;

public sealed class TimeZoneGenerationParityTests
{
    private const string OfficialCanonicalGeometrySha256 =
        "141D9C3EC6D1CE32A665011B2D3E80C43B80C2D2E7C58ADADE417C47161E05EC";

    [Fact]
    public async Task ManagedTimeZones_MatchOfficial2026cFixture()
    {
        string sourceShapefile = FindRepositoryArtifact(
            "tests",
            "SharpNinja.Valhalla.Generation.Tests",
            "Fixtures",
            "Timezone",
            "2026c-jamaica",
            "timezone-2026c-jamaica.shp");
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-managed-timezone-{Guid.NewGuid():N}");
        string workingDirectory = Path.Combine(root, "work");
        string outputPath = Path.Combine(root, "tz_world.sqlite");
        Directory.CreateDirectory(root);

        try
        {
            ITimeZoneDatabaseBuilder builder = new ManagedTimeZoneDatabaseBuilder();
            TimeZoneDatabaseBuildResult result = await builder.BuildAsync(
                new TimeZoneDatabaseBuildRequest(
                    sourceShapefile,
                    "2026c",
                    workingDirectory,
                    outputPath,
                    64 * 1024 * 1024),
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(outputPath), result.DatabasePath);
            Assert.Equal("2026c", result.SourceVersion);
            Assert.Equal(1, result.TimeZoneCount);
            Assert.Equal(1, result.SpatialIndexCount);
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(outputPath))),
                result.Sha256);

            await AssertDatabaseAsync(outputPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static async Task AssertDatabaseAsync(string outputPath)
    {
        SQLitePCL.Batteries_V2.Init();
        await using SqliteConnection connection = new(
            $"Data Source={outputPath};Mode=ReadOnly;Pooling=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using SqliteCommand timezoneCommand = connection.CreateCommand();
        timezoneCommand.CommandText =
            """
            SELECT pk_uid, tzid, geom
            FROM tz_world
            ORDER BY pk_uid
            """;
        await using SqliteDataReader reader = await timezoneCommand.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal("America/Jamaica", reader.GetString(1));

        byte[] geometryBlob = (byte[])reader[2];
        GaiaGeoReader gaiaReader = new();
        var geometry = gaiaReader.Read(geometryBlob);
        geometry.Normalize();
        byte[] canonicalWkb = new WKBWriter(
            ByteOrder.LittleEndian,
            handleSRID: false).Write(geometry);

        Assert.Equal("MultiPolygon", geometry.GeometryType);
        Assert.Equal(4326, geometry.SRID);
        Assert.Equal(1, geometry.NumGeometries);
        Assert.Equal(56, geometry.NumPoints);
        Assert.Equal(-78.578237, geometry.EnvelopeInternal.MinX, 6);
        Assert.Equal(16.589944, geometry.EnvelopeInternal.MinY, 6);
        Assert.Equal(-75.754114, geometry.EnvelopeInternal.MaxX, 6);
        Assert.Equal(18.725639, geometry.EnvelopeInternal.MaxY, 6);
        Assert.Equal(
            OfficialCanonicalGeometrySha256,
            Convert.ToHexString(SHA256.HashData(canonicalWkb)));
        Assert.False(await reader.ReadAsync(TestContext.Current.CancellationToken));
        await reader.DisposeAsync();

        await using SqliteCommand metadataCommand = connection.CreateCommand();
        metadataCommand.CommandText =
            """
            SELECT geometry_type, coord_dimension, srid, spatial_index_enabled
            FROM geometry_columns
            WHERE f_table_name = 'tz_world' AND f_geometry_column = 'geom'
            """;
        await using SqliteDataReader metadata = await metadataCommand.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await metadata.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(6L, metadata.GetInt64(0));
        Assert.Equal(2L, metadata.GetInt64(1));
        Assert.Equal(4326L, metadata.GetInt64(2));
        Assert.Equal(1L, metadata.GetInt64(3));
        await metadata.DisposeAsync();

        await using SqliteCommand indexCommand = connection.CreateCommand();
        indexCommand.CommandText =
            """
            SELECT COUNT(*)
            FROM idx_tz_world_geom
            WHERE xmin <= -77.30 AND xmax >= -77.30
              AND ymin <= 18.10 AND ymax >= 18.10
            """;
        Assert.Equal(
            1L,
            (long)(await indexCommand.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
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
