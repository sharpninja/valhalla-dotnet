using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetTopologySuite.IO;
using SharpNinja.Valhalla.Generation.Admin;
using SharpNinja.Valhalla.Generation.Storage;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Admin;

public sealed class AdminGenerationParityTests
{
    private const string OfficialCanonicalGeometrySha256 =
        "2C7BC11BD9E78F486784EF37177C2AFF5D937962FF9451284D50AA74194C22C9";

    [Fact]
    public async Task ManagedAdmins_MatchOfficialSemanticFixture()
    {
        string sourcePbf = FindRepositoryArtifact("artifacts", "monaco.osm.pbf");
        string root = Path.Combine(
            Path.GetTempPath(),
            $"valhalla-managed-admin-{Guid.NewGuid():N}");
        string workingDirectory = Path.Combine(root, "work");
        string outputPath = Path.Combine(root, "admin.sqlite");
        Directory.CreateDirectory(root);

        try
        {
            IAdminDatabaseBuilder builder = new ManagedAdminDatabaseBuilder();
            AdminDatabaseBuildResult result = await builder.BuildAsync(
                new AdminDatabaseBuildRequest(
                    [sourcePbf],
                    workingDirectory,
                    outputPath,
                    IntermediateStorageMode.Auto,
                    16 * 1024 * 1024,
                    256 * 1024 * 1024),
                TestContext.Current.CancellationToken);

            Assert.Equal(Path.GetFullPath(outputPath), result.DatabasePath);
            Assert.Equal(1, result.AdminCount);
            Assert.Equal(0, result.AccessOverrideCount);
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

        await using SqliteCommand adminCommand = connection.CreateCommand();
        adminCommand.CommandText =
            """
            SELECT rowid, admin_level, iso_code, parent_admin, name, name_en,
                   drive_on_right, allow_intersection_names, default_language,
                   supported_languages, geom
            FROM admins
            ORDER BY rowid
            """;
        await using SqliteDataReader reader = await adminCommand.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(1L, reader.GetInt64(0));
        Assert.Equal(2L, reader.GetInt64(1));
        Assert.Equal("MC", reader.GetString(2));
        Assert.True(reader.IsDBNull(3));
        Assert.Equal("Monaco", reader.GetString(4));
        Assert.True(reader.IsDBNull(5));
        Assert.Equal(1L, reader.GetInt64(6));
        Assert.Equal(0L, reader.GetInt64(7));
        Assert.Equal("fr", reader.GetString(8));
        Assert.True(reader.IsDBNull(9));

        byte[] geometryBlob = (byte[])reader[10];
        GaiaGeoReader gaiaReader = new();
        var geometry = gaiaReader.Read(geometryBlob);
        geometry.Normalize();
        byte[] canonicalWkb = new WKBWriter(
            ByteOrder.LittleEndian,
            handleSRID: true).Write(geometry);

        Assert.Equal("MultiPolygon", geometry.GeometryType);
        Assert.Equal(4326, geometry.SRID);
        Assert.Equal(1, geometry.NumGeometries);
        Assert.Equal(520, geometry.NumPoints);
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
            WHERE f_table_name = 'admins' AND f_geometry_column = 'geom'
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
        indexCommand.CommandText = "SELECT COUNT(*) FROM idx_admins_geom";
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
