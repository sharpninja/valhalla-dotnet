using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace SharpNinja.Valhalla.Generation.TimeZones;

public sealed class ManagedTimeZoneDatabaseBuilder : ITimeZoneDatabaseBuilder
{
    private static int sqliteInitialized;

    public async ValueTask<TimeZoneDatabaseBuildResult> BuildAsync(
        TimeZoneDatabaseBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidatedRequest validated = Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSqliteInitialized();

        Directory.CreateDirectory(validated.WorkingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(validated.OutputPath)!);
        string temporaryPath = Path.Combine(
            validated.WorkingDirectory,
            $"tz-world-{Guid.NewGuid():N}.sqlite.tmp");
        long scratchHighWater = 0;
        int timeZoneCount = 0;
        List<TimeZoneDatabaseDiagnostic> diagnostics = [];

        try
        {
            await using SqliteConnection connection = new(
                $"Data Source={temporaryPath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await CreateSchemaAsync(
                connection,
                cancellationToken).ConfigureAwait(false);
            scratchHighWater = await ObserveScratchAsync(
                connection,
                validated.ScratchDiskBudgetBytes,
                scratchHighWater,
                cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = (SqliteTransaction)await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            await using SqliteCommand insertTimeZone = CreateTimeZoneInsert(
                connection,
                transaction);
            await using SqliteCommand insertIndex = CreateSpatialIndexInsert(
                connection,
                transaction);
            GaiaGeoWriter geometryWriter = new()
            {
                HandleOrdinates = Ordinates.XY,
            };

            await foreach (TimeZoneBoundaryRecord boundary in
                ShapefileTimeZoneBoundaryReader.ReadAsync(
                    validated.SourceShapefilePath,
                    cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                timeZoneCount++;
                byte[] geometryBlob = geometryWriter.Write(boundary.Geometry);
                insertTimeZone.Parameters["$pk_uid"].Value = timeZoneCount;
                insertTimeZone.Parameters["$tzid"].Value = boundary.TimeZoneId;
                insertTimeZone.Parameters["$geom"].Value = geometryBlob;
                await insertTimeZone.ExecuteNonQueryAsync(
                    cancellationToken).ConfigureAwait(false);

                Envelope envelope = boundary.Geometry.EnvelopeInternal;
                insertIndex.Parameters["$pkid"].Value = timeZoneCount;
                insertIndex.Parameters["$xmin"].Value = envelope.MinX;
                insertIndex.Parameters["$xmax"].Value = envelope.MaxX;
                insertIndex.Parameters["$ymin"].Value = envelope.MinY;
                insertIndex.Parameters["$ymax"].Value = envelope.MaxY;
                await insertIndex.ExecuteNonQueryAsync(
                    cancellationToken).ConfigureAwait(false);

                scratchHighWater = await ObserveScratchAsync(
                    connection,
                    validated.ScratchDiskBudgetBytes,
                    scratchHighWater,
                    cancellationToken).ConfigureAwait(false);
            }

            if (timeZoneCount == 0)
            {
                throw new TimeZoneDatabaseBuildException(
                    TimeZoneDatabaseFailureCode.InvalidBoundaryGeometry,
                    "The timezone source contains no usable polygon boundaries.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteNonQueryAsync(
                connection,
                "VACUUM; ANALYZE;",
                cancellationToken).ConfigureAwait(false);
            scratchHighWater = await ObserveScratchAsync(
                connection,
                validated.ScratchDiskBudgetBytes,
                scratchHighWater,
                cancellationToken).ConfigureAwait(false);
            await connection.CloseAsync().ConfigureAwait(false);

            long bytesWritten = new FileInfo(temporaryPath).Length;
            EnsureScratchBudget(
                bytesWritten,
                validated.ScratchDiskBudgetBytes);
            scratchHighWater = Math.Max(scratchHighWater, bytesWritten);
            string sha256 = ComputeSha256(temporaryPath);
            cancellationToken.ThrowIfCancellationRequested();

            File.Move(
                temporaryPath,
                validated.OutputPath,
                overwrite: true);

            return new TimeZoneDatabaseBuildResult(
                validated.OutputPath,
                validated.SourceVersion,
                timeZoneCount,
                timeZoneCount,
                sha256,
                bytesWritten,
                scratchHighWater,
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (TimeZoneDatabaseBuildException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or SqliteException
                or InvalidOperationException)
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.DatabaseWriteFailed,
                "The timezone database could not be built.",
                exception);
        }
        finally
        {
            DeleteIfExists(temporaryPath);
            DeleteIfExists(temporaryPath + "-journal");
            DeleteIfExists(temporaryPath + "-wal");
            DeleteIfExists(temporaryPath + "-shm");
        }
    }

    private static ValidatedRequest Validate(
        TimeZoneDatabaseBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceShapefilePath)
            || string.IsNullOrWhiteSpace(request.SourceVersion)
            || string.IsNullOrWhiteSpace(request.WorkingDirectory)
            || string.IsNullOrWhiteSpace(request.OutputPath)
            || request.ScratchDiskBudgetBytes <= 0)
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.InvalidConfiguration,
                "Timezone source, source version, working directory, output path, "
                + "and a positive scratch-disk budget are required.");
        }

        string sourcePath = Path.GetFullPath(request.SourceShapefilePath);
        if (!sourcePath.EndsWith(".shp", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(sourcePath))
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.InvalidConfiguration,
                $"The timezone SHP file does not exist: {sourcePath}");
        }

        string basePath = Path.Combine(
            Path.GetDirectoryName(sourcePath)!,
            Path.GetFileNameWithoutExtension(sourcePath));
        foreach (string extension in new[] { ".dbf", ".prj" })
        {
            string companion = basePath + extension;
            if (!File.Exists(companion))
            {
                throw new TimeZoneDatabaseBuildException(
                    TimeZoneDatabaseFailureCode.InvalidConfiguration,
                    $"The timezone shapefile companion does not exist: {companion}");
            }
        }

        string workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        string outputPath = Path.GetFullPath(request.OutputPath);
        if (Directory.Exists(outputPath))
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.InvalidConfiguration,
                "The timezone database output path names a directory.");
        }

        string workingRoot = Path.GetPathRoot(workingDirectory)!;
        string outputRoot = Path.GetPathRoot(outputPath)!;
        if (!workingRoot.Equals(
            outputRoot,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.InvalidConfiguration,
                "The working and output paths must share a filesystem for atomic promotion.");
        }

        if (sourcePath.Equals(
            outputPath,
            StringComparison.OrdinalIgnoreCase))
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.InvalidConfiguration,
                "The timezone source and output paths must differ.");
        }

        return new ValidatedRequest(
            sourcePath,
            request.SourceVersion.Trim(),
            workingDirectory,
            outputPath,
            request.ScratchDiskBudgetBytes);
    }

    private static async ValueTask CreateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await ExecuteNonQueryAsync(
            connection,
            """
            PRAGMA page_size=4096;
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            PRAGMA temp_store=MEMORY;
            PRAGMA auto_vacuum=NONE;
            CREATE TABLE tz_world (
                pk_uid INTEGER PRIMARY KEY AUTOINCREMENT,
                tzid TEXT,
                geom MULTIPOLYGON NOT NULL);
            CREATE TABLE geometry_columns (
                f_table_name TEXT NOT NULL,
                f_geometry_column TEXT NOT NULL,
                geometry_type INTEGER NOT NULL,
                coord_dimension INTEGER NOT NULL,
                srid INTEGER NOT NULL,
                spatial_index_enabled INTEGER NOT NULL,
                PRIMARY KEY (f_table_name, f_geometry_column));
            INSERT INTO geometry_columns (
                f_table_name, f_geometry_column, geometry_type,
                coord_dimension, srid, spatial_index_enabled)
            VALUES ('tz_world', 'geom', 6, 2, 4326, 1);
            CREATE VIRTUAL TABLE idx_tz_world_geom USING rtree(
                pkid, xmin, xmax, ymin, ymax);
            CREATE INDEX idx_tz_world_tzid ON tz_world (tzid);
            """,
            cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateTimeZoneInsert(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO tz_world (pk_uid, tzid, geom)
            VALUES ($pk_uid, $tzid, $geom)
            """;
        AddParameter(command, "$pk_uid");
        AddParameter(command, "$tzid");
        AddParameter(command, "$geom");
        return command;
    }

    private static SqliteCommand CreateSpatialIndexInsert(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO idx_tz_world_geom (pkid, xmin, xmax, ymin, ymax)
            VALUES ($pkid, $xmin, $xmax, $ymin, $ymax)
            """;
        AddParameter(command, "$pkid");
        AddParameter(command, "$xmin");
        AddParameter(command, "$xmax");
        AddParameter(command, "$ymin");
        AddParameter(command, "$ymax");
        return command;
    }

    private static async ValueTask<long> ObserveScratchAsync(
        SqliteConnection connection,
        long scratchDiskBudgetBytes,
        long highWaterBytes,
        CancellationToken cancellationToken)
    {
        long pageCount = await ExecuteScalarInt64Async(
            connection,
            "PRAGMA page_count;",
            cancellationToken).ConfigureAwait(false);
        long pageSize = await ExecuteScalarInt64Async(
            connection,
            "PRAGMA page_size;",
            cancellationToken).ConfigureAwait(false);
        long currentBytes = checked(pageCount * pageSize);
        EnsureScratchBudget(
            currentBytes,
            scratchDiskBudgetBytes);
        return Math.Max(highWaterBytes, currentBytes);
    }

    private static void EnsureScratchBudget(
        long currentBytes,
        long scratchDiskBudgetBytes)
    {
        if (currentBytes > scratchDiskBudgetBytes)
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.ScratchDiskBudgetExceeded,
                $"Timezone generation requires {currentBytes} scratch bytes, "
                + $"exceeding the configured {scratchDiskBudgetBytes} bytes.");
        }
    }

    private static async ValueTask ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(
            cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<long> ExecuteScalarInt64Async(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? result = await command.ExecuteScalarAsync(
            cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(
            result,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AddParameter(
        SqliteCommand command,
        string name)
        => command.Parameters.Add(new SqliteParameter(name, null));

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref sqliteInitialized, 1) == 0)
        {
            SQLitePCL.Batteries_V2.Init();
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed record ValidatedRequest(
        string SourceShapefilePath,
        string SourceVersion,
        string WorkingDirectory,
        string OutputPath,
        long ScratchDiskBudgetBytes);
}
