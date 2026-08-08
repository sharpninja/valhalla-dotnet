using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using SharpNinja.Valhalla.Generation.Pbf;
using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Admin;

public sealed class ManagedAdminDatabaseBuilder : IAdminDatabaseBuilder
{
    private const int SpatialReferenceId = 4326;
    private const int ScratchCheckInterval = 4096;
    private static readonly GeometryFactory GeometryFactory =
        new(new PrecisionModel(), SpatialReferenceId);

    public async ValueTask<AdminDatabaseBuildResult> BuildAsync(
        AdminDatabaseBuildRequest request,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(request);

        string workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        string outputPath = Path.GetFullPath(request.OutputPath);
        string outputDirectory = Path.GetDirectoryName(outputPath)
            ?? throw Failure(
                AdminDatabaseFailureCode.InvalidConfiguration,
                "The admin database output path has no parent directory.");
        Directory.CreateDirectory(workingDirectory);
        Directory.CreateDirectory(outputDirectory);

        string buildId = Guid.NewGuid().ToString("N");
        string stagingPath = Path.Combine(workingDirectory, $"admins-{buildId}.staging.sqlite");
        string temporaryOutputPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{buildId}.tmp");
        List<AdminDatabaseDiagnostic> diagnostics = [];
        long scratchHighWater = 0;

        try
        {
            SQLitePCL.Batteries_V2.Init();
            StreamingOsmPbfReadResult pbfResult;
            List<AdminFeature> features;
            await using (AdminStagingStore staging = new(
                stagingPath,
                request.ScratchDiskBudgetBytes))
            {
                StreamingOsmPbfReader reader = new();
                pbfResult = await reader.ReadAsync(
                    request.OsmPbfPaths,
                    staging,
                    cancellationToken).ConfigureAwait(false);
                staging.Complete();
                scratchHighWater = Math.Max(scratchHighWater, staging.ScratchHighWaterBytes);
                features = BuildFeatures(staging, diagnostics, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            ApplyParentRelationships(features, cancellationToken);
            DatabaseWriteReceipt writeReceipt = await WriteDatabaseAsync(
                temporaryOutputPath,
                features,
                request.ScratchDiskBudgetBytes,
                cancellationToken).ConfigureAwait(false);
            scratchHighWater = Math.Max(
                scratchHighWater,
                checked(new FileInfo(stagingPath).Length + writeReceipt.BytesWritten));
            EnsureScratchBudget(scratchHighWater, request.ScratchDiskBudgetBytes);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryOutputPath, outputPath, overwrite: true);
            byte[] outputBytes = await File.ReadAllBytesAsync(
                outputPath,
                cancellationToken).ConfigureAwait(false);

            return new AdminDatabaseBuildResult(
                outputPath,
                features.Count,
                writeReceipt.AccessOverrideCount,
                writeReceipt.SpatialIndexCount,
                Convert.ToHexString(SHA256.HashData(outputBytes)),
                outputBytes.LongLength,
                scratchHighWater,
                pbfResult.Metrics,
                diagnostics);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (AdminDatabaseBuildException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Failure(
                AdminDatabaseFailureCode.DatabaseWriteFailed,
                "Managed admin database generation failed.",
                exception);
        }
        finally
        {
            DeleteIfExists(temporaryOutputPath);
            DeleteIfExists(stagingPath);
        }
    }

    private static List<AdminFeature> BuildFeatures(
        AdminStagingStore staging,
        List<AdminDatabaseDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        List<AdminFeature> features = [];
        foreach (StagedAdminRelation relation in staging.ReadAdminRelations())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryTransformAdmin(relation, out TransformedAdmin admin))
            {
                continue;
            }

            if (!TryBuildGeometry(
                    staging,
                    relation,
                    cancellationToken,
                    out MultiPolygon geometry,
                    out string? failureMessage))
            {
                diagnostics.Add(new AdminDatabaseDiagnostic(
                    failureMessage?.Contains("missing", StringComparison.Ordinal) == true
                        ? AdminDatabaseDiagnosticCode.IncompleteBoundary
                        : AdminDatabaseDiagnosticCode.DegenerateBoundary,
                    failureMessage ?? "The administrative boundary could not be assembled.",
                    relation.Id));
                continue;
            }

            features.Add(new AdminFeature(
                relation.Id,
                admin.AdminLevel,
                admin.IsoCode,
                admin.Name,
                admin.NameEnglish,
                admin.DriveOnRight,
                admin.AllowIntersectionNames,
                admin.DefaultLanguage,
                SupportedLanguages: null,
                geometry,
                ParentRowId: null));
        }

        return features;
    }

    private static bool TryTransformAdmin(
        StagedAdminRelation relation,
        out TransformedAdmin admin)
    {
        admin = null!;
        IReadOnlyDictionary<string, string> tags = relation.Tags;
        if (!TryGet(tags, "type", out string? type)
            || !string.Equals(type, "boundary", StringComparison.Ordinal)
            || !TryGet(tags, "boundary", out string? boundary))
        {
            return false;
        }

        string? name = ValueOrNull(tags, "name");
        string? nameEnglish = ValueOrNull(tags, "name:en");
        string? defaultLanguage = ValueOrNull(tags, "default_language");
        bool isLinguisticBoundary =
            defaultLanguage is not null
            && (string.Equals(boundary, "political", StringComparison.Ordinal)
                || TryGet(tags, "political_division", out string? politicalDivision)
                && string.Equals(
                    politicalDivision,
                    "linguistic_community",
                    StringComparison.Ordinal));

        if (!int.TryParse(ValueOrNull(tags, "admin_level"), out int adminLevel))
        {
            if (!isLinguisticBoundary)
            {
                return false;
            }

            adminLevel = 15;
        }

        if (isLinguisticBoundary)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            admin = new TransformedAdmin(
                adminLevel,
                IsoCode: null,
                name,
                NormalizeEnglishName(name, nameEnglish),
                DriveOnRight: false,
                AllowIntersectionNames: false,
                defaultLanguage);
            return true;
        }

        if (!string.Equals(boundary, "administrative", StringComparison.Ordinal)
            && !string.Equals(boundary, "territorial", StringComparison.Ordinal))
        {
            return false;
        }

        if (adminLevel is not (2 or 3 or 4 or 6) || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (adminLevel == 3)
        {
            if (!IsRetainedLevelThree(name, nameEnglish))
            {
                return false;
            }

            if (defaultLanguage is null
                && !string.Equals(nameEnglish, "Hong Kong", StringComparison.Ordinal)
                && !string.Equals(name, "Metro Manila", StringComparison.Ordinal))
            {
                defaultLanguage = "fr";
            }
        }

        if (adminLevel == 6
            && !string.Equals(name, "District of Columbia", StringComparison.Ordinal))
        {
            return false;
        }

        if (adminLevel == 2
            && string.Equals(name, "France", StringComparison.Ordinal))
        {
            return false;
        }

        if (adminLevel == 2
            && (string.Equals(nameEnglish, "Abkhazia", StringComparison.Ordinal)
                || string.Equals(nameEnglish, "South Ossetia", StringComparison.Ordinal)))
        {
            adminLevel = 4;
        }

        if (string.Equals(name, "Metro Manila", StringComparison.Ordinal))
        {
            adminLevel = 4;
        }

        if (adminLevel == 3)
        {
            adminLevel = 2;
            if (string.Equals(nameEnglish, "Metropolitan France", StringComparison.Ordinal))
            {
                name = "France";
            }
        }

        if (adminLevel == 6)
        {
            adminLevel = 4;
        }

        string? isoCode = ResolveIsoCode(tags, adminLevel, name, nameEnglish);
        bool driveOnRight = !LeftDrivingAdminNames.Contains(name)
            && (nameEnglish is null || !LeftDrivingAdminNames.Contains(nameEnglish));
        bool allowIntersectionNames = IntersectionNameAdminNames.Contains(name)
            || nameEnglish is not null
            && IntersectionNameAdminNames.Contains(nameEnglish);

        admin = new TransformedAdmin(
            adminLevel,
            isoCode,
            name,
            NormalizeEnglishName(name, nameEnglish),
            driveOnRight,
            allowIntersectionNames,
            defaultLanguage);
        return true;
    }

    private static bool TryBuildGeometry(
        AdminStagingStore staging,
        StagedAdminRelation relation,
        CancellationToken cancellationToken,
        out MultiPolygon geometry,
        out string? failureMessage)
    {
        geometry = null!;
        failureMessage = null;
        List<IReadOnlyList<ulong>> outerSegments = [];
        List<IReadOnlyList<ulong>> innerSegments = [];

        foreach (StagedRelationMember member in relation.Members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (member.Type != OsmMemberType.Way)
            {
                continue;
            }

            IReadOnlyList<ulong>? nodeReferences = staging.ReadWayNodeReferences(member.Id);
            if (nodeReferences is null)
            {
                failureMessage = $"Administrative relation {relation.Id} has a missing way {member.Id}.";
                return false;
            }

            if (string.Equals(member.Role, "inner", StringComparison.Ordinal))
            {
                innerSegments.Add(nodeReferences);
            }
            else
            {
                outerSegments.Add(nodeReferences);
            }
        }

        if (!TryAssembleRings(outerSegments, out List<IReadOnlyList<ulong>> outerNodeRings)
            || outerNodeRings.Count == 0)
        {
            failureMessage = $"Administrative relation {relation.Id} has no complete outer ring.";
            return false;
        }

        if (!TryAssembleRings(innerSegments, out List<IReadOnlyList<ulong>> innerNodeRings))
        {
            failureMessage = $"Administrative relation {relation.Id} has an incomplete inner ring.";
            return false;
        }

        List<LinearRing> outerRings = [];
        foreach (IReadOnlyList<ulong> nodeRing in outerNodeRings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinearRing? ring = BuildLinearRing(staging, nodeRing);
            if (ring is null)
            {
                failureMessage = $"Administrative relation {relation.Id} has missing or degenerate outer nodes.";
                return false;
            }

            outerRings.Add(ring);
        }

        List<LinearRing> innerRings = [];
        foreach (IReadOnlyList<ulong> nodeRing in innerNodeRings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinearRing? ring = BuildLinearRing(staging, nodeRing);
            if (ring is null)
            {
                failureMessage = $"Administrative relation {relation.Id} has missing or degenerate inner nodes.";
                return false;
            }

            innerRings.Add(ring);
        }

        List<List<LinearRing>> holesByOuter = outerRings
            .Select(static _ => new List<LinearRing>())
            .ToList();
        for (int innerIndex = 0; innerIndex < innerRings.Count; innerIndex++)
        {
            LinearRing inner = innerRings[innerIndex];
            Polygon holePolygon = GeometryFactory.CreatePolygon(inner);
            Point representativePoint = holePolygon.InteriorPoint;
            int containingOuter = -1;
            double containingArea = double.MaxValue;
            for (int outerIndex = 0; outerIndex < outerRings.Count; outerIndex++)
            {
                Polygon outerPolygon = GeometryFactory.CreatePolygon(outerRings[outerIndex]);
                if (outerPolygon.Covers(representativePoint)
                    && outerPolygon.Area < containingArea)
                {
                    containingOuter = outerIndex;
                    containingArea = outerPolygon.Area;
                }
            }

            if (containingOuter < 0)
            {
                failureMessage = $"Administrative relation {relation.Id} has an uncontained inner ring.";
                return false;
            }

            holesByOuter[containingOuter].Add(inner);
        }

        Polygon[] polygons = new Polygon[outerRings.Count];
        for (int index = 0; index < polygons.Length; index++)
        {
            polygons[index] = GeometryFactory.CreatePolygon(
                outerRings[index],
                holesByOuter[index].ToArray());
            if (!polygons[index].IsValid || polygons[index].IsEmpty)
            {
                failureMessage = $"Administrative relation {relation.Id} produced an invalid polygon.";
                return false;
            }
        }

        geometry = GeometryFactory.CreateMultiPolygon(polygons);
        geometry.SRID = SpatialReferenceId;
        if (!geometry.IsValid || geometry.IsEmpty)
        {
            failureMessage = $"Administrative relation {relation.Id} produced an invalid multipolygon.";
            geometry = null!;
            return false;
        }

        return true;
    }

    private static LinearRing? BuildLinearRing(
        AdminStagingStore staging,
        IReadOnlyList<ulong> nodeIds)
    {
        List<Coordinate> coordinates = new(nodeIds.Count);
        Coordinate? previous = null;
        foreach (ulong nodeId in nodeIds)
        {
            Coordinate? coordinate = staging.ReadNodeCoordinate(nodeId);
            if (coordinate is null)
            {
                return null;
            }

            if (previous is null || !previous.Equals2D(coordinate))
            {
                coordinates.Add(coordinate);
                previous = coordinate;
            }
        }

        if (coordinates.Count < 4)
        {
            return null;
        }

        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.Add(coordinates[0].Copy());
        }

        return coordinates.Count >= 4
            ? GeometryFactory.CreateLinearRing(coordinates.ToArray())
            : null;
    }

    private static bool TryAssembleRings(
        IReadOnlyList<IReadOnlyList<ulong>> segments,
        out List<IReadOnlyList<ulong>> rings)
    {
        rings = [];
        bool[] used = new bool[segments.Count];
        for (int seedIndex = 0; seedIndex < segments.Count; seedIndex++)
        {
            if (used[seedIndex] || segments[seedIndex].Count < 2)
            {
                continue;
            }

            List<ulong> chain = [.. segments[seedIndex]];
            used[seedIndex] = true;
            while (chain[0] != chain[^1])
            {
                bool connected = false;
                for (int candidateIndex = 0; candidateIndex < segments.Count; candidateIndex++)
                {
                    if (used[candidateIndex] || segments[candidateIndex].Count < 2)
                    {
                        continue;
                    }

                    IReadOnlyList<ulong> candidate = segments[candidateIndex];
                    if (candidate[0] == chain[^1])
                    {
                        AppendForward(chain, candidate);
                    }
                    else if (candidate[^1] == chain[^1])
                    {
                        AppendReverse(chain, candidate);
                    }
                    else if (candidate[^1] == chain[0])
                    {
                        PrependForward(chain, candidate);
                    }
                    else if (candidate[0] == chain[0])
                    {
                        PrependReverse(chain, candidate);
                    }
                    else
                    {
                        continue;
                    }

                    used[candidateIndex] = true;
                    connected = true;
                    break;
                }

                if (!connected)
                {
                    return false;
                }
            }

            rings.Add(chain);
        }

        return used.All(static value => value);
    }

    private static void AppendForward(List<ulong> chain, IReadOnlyList<ulong> candidate)
    {
        for (int index = 1; index < candidate.Count; index++)
        {
            chain.Add(candidate[index]);
        }
    }

    private static void AppendReverse(List<ulong> chain, IReadOnlyList<ulong> candidate)
    {
        for (int index = candidate.Count - 2; index >= 0; index--)
        {
            chain.Add(candidate[index]);
        }
    }

    private static void PrependForward(List<ulong> chain, IReadOnlyList<ulong> candidate)
    {
        for (int index = candidate.Count - 2; index >= 0; index--)
        {
            chain.Insert(0, candidate[index]);
        }
    }

    private static void PrependReverse(List<ulong> chain, IReadOnlyList<ulong> candidate)
    {
        for (int index = 1; index < candidate.Count; index++)
        {
            chain.Insert(0, candidate[index]);
        }
    }

    private static MultiPolygon ApplyUpstreamWktPrecision(MultiPolygon geometry)
    {
        Polygon[] polygons = new Polygon[geometry.NumGeometries];
        for (int polygonIndex = 0; polygonIndex < polygons.Length; polygonIndex++)
        {
            Polygon polygon = (Polygon)geometry[polygonIndex];
            LinearRing shell = ApplyUpstreamWktPrecision(polygon.Shell);
            LinearRing[] holes = new LinearRing[polygon.NumInteriorRings];
            for (int holeIndex = 0; holeIndex < holes.Length; holeIndex++)
            {
                holes[holeIndex] = ApplyUpstreamWktPrecision(
                    (LinearRing)polygon.GetInteriorRingN(holeIndex));
            }

            polygons[polygonIndex] = GeometryFactory.CreatePolygon(shell, holes);
        }

        MultiPolygon precise = GeometryFactory.CreateMultiPolygon(polygons);
        precise.SRID = SpatialReferenceId;
        return precise;
    }

    private static LinearRing ApplyUpstreamWktPrecision(LinearRing ring)
    {
        Coordinate[] coordinates = ring.Coordinates;
        for (int index = 0; index < coordinates.Length; index++)
        {
            Coordinate coordinate = coordinates[index];
            coordinates[index] = new Coordinate(
                ApplyUpstreamWktPrecision(coordinate.X),
                ApplyUpstreamWktPrecision(coordinate.Y));
        }

        return GeometryFactory.CreateLinearRing(coordinates);
    }

    private static double ApplyUpstreamWktPrecision(double value) =>
        double.Parse(
            value.ToString("G7", CultureInfo.InvariantCulture),
            NumberStyles.Float,
            CultureInfo.InvariantCulture);

    private static void ApplyParentRelationships(
        List<AdminFeature> features,
        CancellationToken cancellationToken)
    {
        for (int childIndex = 0; childIndex < features.Count; childIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdminFeature child = features[childIndex];
            int? parentRowId = null;
            int parentLevel = int.MinValue;
            Point point = child.Geometry.InteriorPoint;
            for (int parentIndex = 0; parentIndex < features.Count; parentIndex++)
            {
                if (childIndex == parentIndex)
                {
                    continue;
                }

                AdminFeature parent = features[parentIndex];
                if (parent.AdminLevel >= child.AdminLevel
                    || parent.AdminLevel < parentLevel
                    || !parent.Geometry.Covers(point))
                {
                    continue;
                }

                parentRowId = parentIndex + 1;
                parentLevel = parent.AdminLevel;
            }

            features[childIndex] = child with { ParentRowId = parentRowId };
        }
    }

    private static async ValueTask<DatabaseWriteReceipt> WriteDatabaseAsync(
        string databasePath,
        IReadOnlyList<AdminFeature> features,
        long scratchDiskBudgetBytes,
        CancellationToken cancellationToken)
    {
        DeleteIfExists(databasePath);
        await using SqliteConnection connection = new(
            $"Data Source={databasePath};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteNonQueryAsync(
            connection,
            """
            PRAGMA page_size=4096;
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            PRAGMA temp_store=MEMORY;
            PRAGMA auto_vacuum=NONE;
            CREATE TABLE admins (
                admin_level INTEGER NOT NULL,
                iso_code TEXT,
                parent_admin INTEGER,
                name TEXT NOT NULL,
                name_en TEXT,
                drive_on_right INTEGER NULL,
                allow_intersection_names INTEGER NULL,
                default_language TEXT,
                supported_languages TEXT,
                geom MULTIPOLYGON NOT NULL);
            CREATE TABLE admin_access (
                admin_id INTEGER NOT NULL,
                iso_code TEXT,
                trunk INTEGER DEFAULT NULL,
                trunk_link INTEGER DEFAULT NULL,
                track INTEGER DEFAULT NULL,
                footway INTEGER DEFAULT NULL,
                pedestrian INTEGER DEFAULT NULL,
                bridleway INTEGER DEFAULT NULL,
                cycleway INTEGER DEFAULT NULL,
                path INTEGER DEFAULT NULL,
                motorroad INTEGER DEFAULT NULL);
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
            VALUES ('admins', 'geom', 6, 2, 4326, 1);
            CREATE VIRTUAL TABLE idx_admins_geom USING rtree(
                pkid, xmin, xmax, ymin, ymax);
            CREATE INDEX IdxLevel ON admins (admin_level);
            CREATE INDEX IdxDriveOnRight ON admins (drive_on_right);
            CREATE INDEX IdxAllowIntersectionNames ON admins (allow_intersection_names);
            """,
            cancellationToken).ConfigureAwait(false);

        await using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand adminCommand = connection.CreateCommand();
        adminCommand.Transaction = transaction;
        adminCommand.CommandText =
            """
            INSERT INTO admins (
                admin_level, iso_code, parent_admin, name, name_en,
                drive_on_right, allow_intersection_names,
                default_language, supported_languages, geom)
            VALUES (
                $admin_level, $iso_code, $parent_admin, $name, $name_en,
                $drive_on_right, $allow_intersection_names,
                $default_language, $supported_languages, $geom)
            """;
        AddParameter(adminCommand, "$admin_level");
        AddParameter(adminCommand, "$iso_code");
        AddParameter(adminCommand, "$parent_admin");
        AddParameter(adminCommand, "$name");
        AddParameter(adminCommand, "$name_en");
        AddParameter(adminCommand, "$drive_on_right");
        AddParameter(adminCommand, "$allow_intersection_names");
        AddParameter(adminCommand, "$default_language");
        AddParameter(adminCommand, "$supported_languages");
        AddParameter(adminCommand, "$geom");

        await using SqliteCommand indexCommand = connection.CreateCommand();
        indexCommand.Transaction = transaction;
        indexCommand.CommandText =
            """
            INSERT INTO idx_admins_geom (pkid, xmin, xmax, ymin, ymax)
            VALUES ($pkid, $xmin, $xmax, $ymin, $ymax)
            """;
        AddParameter(indexCommand, "$pkid");
        AddParameter(indexCommand, "$xmin");
        AddParameter(indexCommand, "$xmax");
        AddParameter(indexCommand, "$ymin");
        AddParameter(indexCommand, "$ymax");

        GaiaGeoWriter geometryWriter = new() { HandleOrdinates = Ordinates.XY };
        for (int index = 0; index < features.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdminFeature feature = features[index];
            MultiPolygon geometry = ApplyUpstreamWktPrecision(feature.Geometry);
            adminCommand.Parameters["$admin_level"].Value = feature.AdminLevel;
            adminCommand.Parameters["$iso_code"].Value = DbValue(feature.IsoCode);
            adminCommand.Parameters["$parent_admin"].Value = DbValue(feature.ParentRowId);
            adminCommand.Parameters["$name"].Value = feature.Name;
            adminCommand.Parameters["$name_en"].Value = DbValue(feature.NameEnglish);
            adminCommand.Parameters["$drive_on_right"].Value = feature.DriveOnRight ? 1 : 0;
            adminCommand.Parameters["$allow_intersection_names"].Value =
                feature.AllowIntersectionNames ? 1 : 0;
            adminCommand.Parameters["$default_language"].Value =
                DbValue(feature.DefaultLanguage);
            adminCommand.Parameters["$supported_languages"].Value =
                DbValue(feature.SupportedLanguages);
            adminCommand.Parameters["$geom"].Value = geometryWriter.Write(geometry);
            await adminCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            Envelope envelope = geometry.EnvelopeInternal;
            indexCommand.Parameters["$pkid"].Value = index + 1;
            indexCommand.Parameters["$xmin"].Value = envelope.MinX;
            indexCommand.Parameters["$xmax"].Value = envelope.MaxX;
            indexCommand.Parameters["$ymin"].Value = envelope.MinY;
            indexCommand.Parameters["$ymax"].Value = envelope.MaxY;
            await indexCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            EnsureScratchBudget(
                new FileInfo(databasePath).Length,
                scratchDiskBudgetBytes);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ExecuteNonQueryAsync(
            connection,
            "PRAGMA optimize;",
            cancellationToken).ConfigureAwait(false);
        await connection.CloseAsync().ConfigureAwait(false);

        long bytesWritten = new FileInfo(databasePath).Length;
        EnsureScratchBudget(bytesWritten, scratchDiskBudgetBytes);
        return new DatabaseWriteReceipt(
            bytesWritten,
            AccessOverrideCount: 0,
            SpatialIndexCount: features.Count);
    }

    private static async ValueTask ExecuteNonQueryAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddParameter(SqliteCommand command, string name)
    {
        command.Parameters.Add(new SqliteParameter(name, DBNull.Value));
    }

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static string? ResolveIsoCode(
        IReadOnlyDictionary<string, string> tags,
        int adminLevel,
        string name,
        string? nameEnglish)
    {
        if (adminLevel == 2)
        {
            string? isoCode = ValueOrNull(tags, "ISO3166-1:alpha2")
                ?? ValueOrNull(tags, "ISO3166-1");
            if (isoCode is null
                && string.Equals(name, "British Sovereign Base Areas", StringComparison.Ordinal))
            {
                isoCode = "GB";
            }

            if (isoCode is null
                && string.Equals(name, "France", StringComparison.Ordinal)
                && string.Equals(nameEnglish, "Metropolitan France", StringComparison.Ordinal))
            {
                isoCode = "FR";
            }

            return isoCode;
        }

        if (adminLevel == 4)
        {
            string? iso3166 = ValueOrNull(tags, "ISO3166-2");
            if (iso3166 is null)
            {
                return null;
            }

            int separatorIndex = iso3166.IndexOf('-', StringComparison.Ordinal);
            if (separatorIndex == 2 && iso3166.Length is 5 or 6)
            {
                return iso3166[3..];
            }

            if (separatorIndex < 0 && iso3166.Length is 2 or 3)
            {
                return iso3166;
            }

            if (separatorIndex < 0 && iso3166.Length is 4 or 5)
            {
                return iso3166[2..];
            }
        }

        return null;
    }

    private static string? NormalizeEnglishName(string name, string? englishName) =>
        string.Equals(name, englishName, StringComparison.Ordinal)
            ? null
            : englishName;

    private static bool IsRetainedLevelThree(string name, string? englishName) =>
        RetainedFrenchLevelThreeAdminNames.Contains(name)
        || string.Equals(englishName, "Metropolitan France", StringComparison.Ordinal)
        || string.Equals(englishName, "Hong Kong", StringComparison.Ordinal)
        || string.Equals(name, "Metro Manila", StringComparison.Ordinal);

    private static bool IsAdminCandidate(IReadOnlyDictionary<string, string> tags)
    {
        if (!TryGet(tags, "type", out string? type)
            || !string.Equals(type, "boundary", StringComparison.Ordinal)
            || !TryGet(tags, "boundary", out string? boundary))
        {
            return false;
        }

        if ((string.Equals(boundary, "administrative", StringComparison.Ordinal)
                || string.Equals(boundary, "territorial", StringComparison.Ordinal))
            && TryGet(tags, "admin_level", out string? level)
            && level is "2" or "3" or "4" or "6")
        {
            return true;
        }

        return ValueOrNull(tags, "default_language") is not null
            && (string.Equals(boundary, "political", StringComparison.Ordinal)
                || string.Equals(
                    ValueOrNull(tags, "political_division"),
                    "linguistic_community",
                    StringComparison.Ordinal));
    }

    private static bool TryGet(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string? value)
    {
        if (values.TryGetValue(key, out string? found)
            && !string.IsNullOrWhiteSpace(found))
        {
            value = found;
            return true;
        }

        value = null;
        return false;
    }

    private static string? ValueOrNull(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        TryGet(values, key, out string? value) ? value : null;

    private static void ValidateRequest(AdminDatabaseBuildRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.OsmPbfPaths);
        if (request.OsmPbfPaths.Count == 0
            || request.OsmPbfPaths.Any(string.IsNullOrWhiteSpace)
            || request.OsmPbfPaths.Any(path => !File.Exists(path)))
        {
            throw Failure(
                AdminDatabaseFailureCode.InvalidConfiguration,
                "At least one existing OSM PBF input is required.");
        }

        if (string.IsNullOrWhiteSpace(request.WorkingDirectory)
            || string.IsNullOrWhiteSpace(request.OutputPath)
            || request.MemoryBudgetBytes <= 0
            || request.ScratchDiskBudgetBytes <= 0)
        {
            throw Failure(
                AdminDatabaseFailureCode.InvalidConfiguration,
                "Working/output paths and positive memory/scratch budgets are required.");
        }

        if (!Enum.IsDefined(request.StorageMode))
        {
            throw Failure(
                AdminDatabaseFailureCode.InvalidConfiguration,
                "The intermediate storage mode is unsupported.");
        }
    }

    private static void EnsureScratchBudget(long bytes, long budget)
    {
        if (bytes > budget)
        {
            throw Failure(
                AdminDatabaseFailureCode.ScratchDiskBudgetExceeded,
                $"Admin generation scratch usage {bytes} exceeds the configured budget {budget}.");
        }
    }

    private static AdminDatabaseBuildException Failure(
        AdminDatabaseFailureCode code,
        string message,
        Exception? innerException = null) =>
        new(code, message, innerException);

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static readonly HashSet<string> RetainedFrenchLevelThreeAdminNames =
        new(StringComparer.Ordinal)
        {
            "Guyane",
            "Guadeloupe",
            "La Réunion",
            "Martinique",
            "Mayotte",
            "Saint-Pierre-et-Miquelon",
            "Saint-Barthélemy",
            "Saint-Martin (France)",
            "Polynésie Française",
            "Wallis-et-Futuna",
            "Nouvelle-Calédonie",
            "Île de Clipperton",
            "Terres australes et antarctiques françaises",
        };

    private static readonly HashSet<string> IntersectionNameAdminNames =
        new(StringComparer.Ordinal)
        {
            "Japan",
            "North Korea",
            "South Korea",
            "Nicaragua",
        };

    private static readonly HashSet<string> LeftDrivingAdminNames =
        new(StringComparer.Ordinal)
        {
            "Anguilla",
            "Antigua and Barbuda",
            "Australia",
            "Bangladesh",
            "Barbados",
            "Bermuda",
            "Bhutan",
            "Botswana",
            "British Virgin Islands",
            "Brunei Darussalam",
            "Cayman Islands",
            "Cook Islands",
            "Cyprus",
            "Dominica",
            "England",
            "Falkland Islands",
            "Grenada",
            "Guernsey",
            "Guyana",
            "Hong Kong",
            "India",
            "Indonesia",
            "Ireland",
            "Isle of Man",
            "Jamaica",
            "Japan",
            "Jersey",
            "Kenya",
            "Kiribati",
            "Lesotho",
            "Macao",
            "Malawi",
            "Malaysia",
            "Maldives",
            "Malta",
            "Mauritius",
            "Moçambique",
            "Montserrat",
            "Namibia",
            "Naoero",
            "Nepal",
            "New Zealand",
            "Niue",
            "Northern Ireland",
            "Pakistan",
            "Papua Niugini",
            "Pitcairn Islands",
            "Republic of Ireland",
            "Saint Helena, Ascension and Tristan da Cunha",
            "Saint Kitts and Nevis",
            "Saint Lucia",
            "Saint Vincent and the Grenadines",
            "Samoa",
            "Sesel",
            "Singapore",
            "Solomon Islands",
            "Soomaaliya",
            "South Africa",
            "Sri Lanka",
            "Suriname",
            "Alba / Scotland",
            "Swatini",
            "Tanzania",
            "Thailand",
            "The Bahamas",
            "Tokelau",
            "Tonga",
            "Trinidad and Tobago",
            "Turks and Caicos Islands",
            "Tuvalu",
            "Uganda",
            "United Kingdom",
            "United States Virgin Islands",
            "Viti",
            "Cymru / Wales",
            "Zambia",
            "Zimbabwe",
        };

    private sealed record TransformedAdmin(
        int AdminLevel,
        string? IsoCode,
        string Name,
        string? NameEnglish,
        bool DriveOnRight,
        bool AllowIntersectionNames,
        string? DefaultLanguage);

    private sealed record AdminFeature(
        ulong RelationId,
        int AdminLevel,
        string? IsoCode,
        string Name,
        string? NameEnglish,
        bool DriveOnRight,
        bool AllowIntersectionNames,
        string? DefaultLanguage,
        string? SupportedLanguages,
        MultiPolygon Geometry,
        int? ParentRowId);

    private sealed record DatabaseWriteReceipt(
        long BytesWritten,
        int AccessOverrideCount,
        int SpatialIndexCount);

    private sealed record StagedRelationMember(
        ulong Id,
        OsmMemberType Type,
        string Role);

    private sealed record StagedAdminRelation(
        ulong Id,
        OsmEntityOrdinal Ordinal,
        IReadOnlyDictionary<string, string> Tags,
        IReadOnlyList<StagedRelationMember> Members);

    private sealed class AdminStagingStore : IStreamingOsmEntitySink, IAsyncDisposable
    {
        private readonly string path;
        private readonly long scratchDiskBudgetBytes;
        private readonly SqliteConnection connection;
        private readonly SqliteTransaction transaction;
        private readonly SqliteCommand nodeCommand;
        private readonly SqliteCommand wayCommand;
        private readonly SqliteCommand relationCommand;
        private long operationCount;
        private bool completed;

        public AdminStagingStore(string path, long scratchDiskBudgetBytes)
        {
            this.path = path;
            this.scratchDiskBudgetBytes = scratchDiskBudgetBytes;
            DeleteIfExists(path);
            connection = new SqliteConnection(
                $"Data Source={path};Mode=ReadWriteCreate;Cache=Private;Pooling=False");
            connection.Open();
            using (SqliteCommand schema = connection.CreateCommand())
            {
                schema.CommandText =
                    """
                    PRAGMA page_size=4096;
                    PRAGMA journal_mode=OFF;
                    PRAGMA synchronous=OFF;
                    PRAGMA temp_store=MEMORY;
                    CREATE TABLE nodes (
                        id INTEGER PRIMARY KEY,
                        latitude REAL NOT NULL,
                        longitude REAL NOT NULL) WITHOUT ROWID;
                    CREATE TABLE ways (
                        id INTEGER PRIMARY KEY,
                        node_references BLOB NOT NULL) WITHOUT ROWID;
                    CREATE TABLE admin_relations (
                        id INTEGER PRIMARY KEY,
                        file_ordinal INTEGER NOT NULL,
                        block_ordinal INTEGER NOT NULL,
                        entity_ordinal INTEGER NOT NULL,
                        tags BLOB NOT NULL,
                        members BLOB NOT NULL) WITHOUT ROWID;
                    CREATE INDEX admin_relation_order ON admin_relations (
                        file_ordinal, block_ordinal, entity_ordinal, id);
                    """;
                schema.ExecuteNonQuery();
            }

            transaction = connection.BeginTransaction();

            nodeCommand = connection.CreateCommand();
            nodeCommand.Transaction = transaction;
            nodeCommand.CommandText =
                "INSERT OR REPLACE INTO nodes (id, latitude, longitude) VALUES ($id, $lat, $lon)";
            AddParameter(nodeCommand, "$id");
            AddParameter(nodeCommand, "$lat");
            AddParameter(nodeCommand, "$lon");

            wayCommand = connection.CreateCommand();
            wayCommand.Transaction = transaction;
            wayCommand.CommandText =
                "INSERT OR REPLACE INTO ways (id, node_references) VALUES ($id, $refs)";
            AddParameter(wayCommand, "$id");
            AddParameter(wayCommand, "$refs");

            relationCommand = connection.CreateCommand();
            relationCommand.Transaction = transaction;
            relationCommand.CommandText =
                """
                INSERT OR REPLACE INTO admin_relations (
                    id, file_ordinal, block_ordinal, entity_ordinal, tags, members)
                VALUES ($id, $file, $block, $entity, $tags, $members)
                """;
            AddParameter(relationCommand, "$id");
            AddParameter(relationCommand, "$file");
            AddParameter(relationCommand, "$block");
            AddParameter(relationCommand, "$entity");
            AddParameter(relationCommand, "$tags");
            AddParameter(relationCommand, "$members");
        }

        public long ScratchHighWaterBytes { get; private set; }

        public bool ShouldRetain(OsmEntityKind kind) => true;

        public void AddNode(scoped in OsmNodeView node)
        {
            nodeCommand.Parameters["$id"].Value = checked((long)node.Id);
            nodeCommand.Parameters["$lat"].Value = node.Latitude;
            nodeCommand.Parameters["$lon"].Value = node.Longitude;
            nodeCommand.ExecuteNonQuery();
            CheckScratchBudget();
        }

        public void AddWay(scoped in OsmWayView way)
        {
            byte[] references = new byte[checked(way.NodeReferences.Length * sizeof(ulong))];
            for (int index = 0; index < way.NodeReferences.Length; index++)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    references.AsSpan(index * sizeof(ulong), sizeof(ulong)),
                    way.NodeReferences[index]);
            }

            wayCommand.Parameters["$id"].Value = checked((long)way.Id);
            wayCommand.Parameters["$refs"].Value = references;
            wayCommand.ExecuteNonQuery();
            CheckScratchBudget();
        }

        public void AddRelation(scoped in OsmRelationView relation)
        {
            SortedDictionary<string, string> tags = new(StringComparer.Ordinal);
            for (int index = 0; index < relation.Tags.Count; index++)
            {
                OsmTag tag = relation.Tags[index];
                tags[tag.Key] = tag.Value;
            }

            if (!IsAdminCandidate(tags))
            {
                return;
            }

            StagedRelationMember[] members = new StagedRelationMember[relation.MemberCount];
            for (int index = 0; index < members.Length; index++)
            {
                OsmRelationMemberEntity member = relation.GetMember(index);
                members[index] = new StagedRelationMember(
                    member.Id,
                    member.Type,
                    member.Role);
            }

            relationCommand.Parameters["$id"].Value = checked((long)relation.Id);
            relationCommand.Parameters["$file"].Value = relation.Ordinal.FileOrdinal;
            relationCommand.Parameters["$block"].Value = relation.Ordinal.BlockOrdinal;
            relationCommand.Parameters["$entity"].Value = relation.Ordinal.EntityOrdinal;
            relationCommand.Parameters["$tags"].Value =
                JsonSerializer.SerializeToUtf8Bytes(tags);
            relationCommand.Parameters["$members"].Value =
                JsonSerializer.SerializeToUtf8Bytes(members);
            relationCommand.ExecuteNonQuery();
            CheckScratchBudget();
        }

        public void Complete()
        {
            if (completed)
            {
                return;
            }

            transaction.Commit();
            completed = true;
            CheckScratchBudget(force: true);
        }

        public IEnumerable<StagedAdminRelation> ReadAdminRelations()
        {
            EnsureCompleted();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, file_ordinal, block_ordinal, entity_ordinal, tags, members
                FROM admin_relations
                ORDER BY file_ordinal, block_ordinal, entity_ordinal, id
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                SortedDictionary<string, string>? tags =
                    JsonSerializer.Deserialize<SortedDictionary<string, string>>(
                        (byte[])reader[4]);
                StagedRelationMember[]? members =
                    JsonSerializer.Deserialize<StagedRelationMember[]>((byte[])reader[5]);
                yield return new StagedAdminRelation(
                    checked((ulong)reader.GetInt64(0)),
                    new OsmEntityOrdinal(
                        reader.GetInt32(1),
                        reader.GetInt64(2),
                        reader.GetInt32(3)),
                    tags ?? new SortedDictionary<string, string>(StringComparer.Ordinal),
                    members ?? []);
            }
        }

        public IReadOnlyList<ulong>? ReadWayNodeReferences(ulong wayId)
        {
            EnsureCompleted();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT node_references FROM ways WHERE id = $id";
            command.Parameters.AddWithValue("$id", checked((long)wayId));
            object? value = command.ExecuteScalar();
            if (value is not byte[] bytes)
            {
                return null;
            }

            ulong[] references = new ulong[bytes.Length / sizeof(ulong)];
            for (int index = 0; index < references.Length; index++)
            {
                references[index] = BinaryPrimitives.ReadUInt64LittleEndian(
                    bytes.AsSpan(index * sizeof(ulong), sizeof(ulong)));
            }

            return references;
        }

        public Coordinate? ReadNodeCoordinate(ulong nodeId)
        {
            EnsureCompleted();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT longitude, latitude FROM nodes WHERE id = $id";
            command.Parameters.AddWithValue("$id", checked((long)nodeId));
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read()
                ? new Coordinate(reader.GetDouble(0), reader.GetDouble(1))
                : null;
        }

        public async ValueTask DisposeAsync()
        {
            nodeCommand.Dispose();
            wayCommand.Dispose();
            relationCommand.Dispose();
            transaction.Dispose();
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        private void EnsureCompleted()
        {
            if (!completed)
            {
                throw new InvalidOperationException(
                    "The admin staging store must be completed before it can be read.");
            }
        }

        private void CheckScratchBudget(bool force = false)
        {
            operationCount++;
            if (!force && operationCount % ScratchCheckInterval != 0)
            {
                return;
            }

            long length = File.Exists(path) ? new FileInfo(path).Length : 0;
            ScratchHighWaterBytes = Math.Max(ScratchHighWaterBytes, length);
            EnsureScratchBudget(ScratchHighWaterBytes, scratchDiskBudgetBytes);
        }
    }
}
