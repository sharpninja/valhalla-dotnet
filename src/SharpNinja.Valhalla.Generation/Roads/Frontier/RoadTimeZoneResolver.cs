using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed class RoadTimeZoneResolver : IDisposable
{
    private static int sqliteInitialized;
    private readonly SqliteConnection? connection;
    private readonly GaiaGeoReader geometryReader = new();

    private RoadTimeZoneResolver(SqliteConnection? connection)
    {
        this.connection = connection;
    }

    internal static RoadTimeZoneResolver Open(string? databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            return new RoadTimeZoneResolver(null);
        }

        string fullPath = Path.GetFullPath(databasePath);
        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"The road timezone database does not exist: {fullPath}");
        }

        EnsureSqliteInitialized();
        try
        {
            var opened = new SqliteConnection(
                $"Data Source={fullPath};Mode=ReadOnly;Cache=Private;Pooling=False");
            opened.Open();
            return new RoadTimeZoneResolver(opened);
        }
        catch (Exception exception) when (
            exception is SqliteException or InvalidOperationException or IOException)
        {
            throw new InvalidDataException(
                "The road timezone database could not be opened.",
                exception);
        }
    }

    internal uint Resolve(PointLL coordinate)
    {
        if (connection is null)
        {
            return 0;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT t.tzid, t.geom
            FROM idx_tz_world_geom AS i
            INNER JOIN tz_world AS t ON t.pk_uid = i.pkid
            WHERE i.xmin <= $longitude AND i.xmax >= $longitude
              AND i.ymin <= $latitude AND i.ymax >= $latitude
            ORDER BY t.pk_uid
            """;
        command.Parameters.AddWithValue("$longitude", coordinate.Lng);
        command.Parameters.AddWithValue("$latitude", coordinate.Lat);
        var point = new Point(coordinate.Lng, coordinate.Lat) { SRID = 4326 };

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string timeZoneId = reader.GetString(0);
            Geometry boundary = geometryReader.Read((byte[])reader[1]);
            if (!boundary.Covers(point))
            {
                continue;
            }

            if (ValhallaTimeZoneCatalog.TryGetIndex(timeZoneId, out uint index))
            {
                return index;
            }

            throw new InvalidDataException(
                $"Timezone {timeZoneId} is not present in the Valhalla 3.8.3 catalog.");
        }

        throw new InvalidDataException(
            $"No timezone polygon covers road node {coordinate.Lat},{coordinate.Lng}.");
    }

    public void Dispose() => connection?.Dispose();

    private static void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref sqliteInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }
}
