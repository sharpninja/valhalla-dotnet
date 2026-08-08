using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Transit;

internal sealed class TransitTimeZoneResolver : IDisposable
{
    private static int sqliteInitialized;

    private readonly SqliteConnection? _connection;
    private readonly GaiaGeoReader _geometryReader = new();

    private TransitTimeZoneResolver(SqliteConnection? connection)
    {
        _connection = connection;
    }

    public static TransitTimeZoneResolver Open(string? databasePath)
    {
        if (string.IsNullOrEmpty(databasePath))
        {
            return new TransitTimeZoneResolver(null);
        }

        EnsureSqliteInitialized();
        try
        {
            var connection = new SqliteConnection(
                $"Data Source={databasePath};Mode=ReadOnly;Cache=Private;Pooling=False");
            connection.Open();
            return new TransitTimeZoneResolver(connection);
        }
        catch (Exception exception) when (
            exception is SqliteException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidConfiguration,
                "The transit timezone database could not be opened.",
                exception);
        }
    }

    public uint Resolve(string explicitTimeZoneId, PointLL coordinate)
    {
        if (!string.IsNullOrWhiteSpace(explicitTimeZoneId))
        {
            return ResolveTimeZoneId(explicitTimeZoneId);
        }

        if (_connection is null)
        {
            return 0;
        }

        using SqliteCommand command = _connection.CreateCommand();
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
        var point = new Point(coordinate.Lng, coordinate.Lat)
        {
            SRID = 4326,
        };

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string timeZoneId = reader.GetString(0);
            byte[] geometryBlob = (byte[])reader[1];
            Geometry boundary = _geometryReader.Read(geometryBlob);
            if (boundary.Covers(point))
            {
                return ResolveTimeZoneId(timeZoneId);
            }
        }

        return 0;
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }

    private static uint ResolveTimeZoneId(string timeZoneId)
    {
        if (ValhallaTimeZoneIndex.TryGetIndex(timeZoneId, out uint index))
        {
            return index;
        }

        throw new TransitTileBuildException(
            TransitTileBuildFailureCode.InvalidValue,
            $"Timezone {timeZoneId} is not present in the Valhalla 3.8.3 timezone index.");
    }

    private static void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref sqliteInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }
}
