using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;

namespace SharpNinja.Valhalla.Generation.TimeZones;

internal sealed record TimeZoneBoundaryRecord(
    int SourceRecordNumber,
    string TimeZoneId,
    MultiPolygon Geometry,
    long SourceBytesRead);

internal static class ShapefileTimeZoneBoundaryReader
{
    private const int ShapefileHeaderLength = 100;
    private const int ShapefileFileCode = 9994;
    private const int ShapefileVersion = 1000;
    private const int NullShape = 0;
    private const int PolygonShape = 5;
    private const int PolygonZShape = 15;
    private const int PolygonMShape = 25;
    private const int MaximumRecordBytes = 256 * 1024 * 1024;
    private static readonly GeometryFactory GeometryFactory =
        new(new PrecisionModel(), 4326);

    public static async IAsyncEnumerable<TimeZoneBoundaryRecord> ReadAsync(
        string shapefilePath,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string shapePath = Path.GetFullPath(shapefilePath);
        string basePath = Path.Combine(
            Path.GetDirectoryName(shapePath)!,
            Path.GetFileNameWithoutExtension(shapePath));
        string dbfPath = basePath + ".dbf";
        string projectionPath = basePath + ".prj";

        await ValidateProjectionAsync(
            projectionPath,
            cancellationToken).ConfigureAwait(false);

        await using FileStream shapeStream = new(
            shapePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using FileStream dbfStream = new(
            dbfPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        ShapefileHeader shapeHeader = await ReadShapefileHeaderAsync(
            shapeStream,
            cancellationToken).ConfigureAwait(false);
        DbfHeader dbfHeader = await ReadDbfHeaderAsync(
            dbfStream,
            cancellationToken).ConfigureAwait(false);

        if (shapeHeader.DeclaredFileLengthBytes != shapeStream.Length)
        {
            throw InvalidShapefile(
                $"The SHP header declares {shapeHeader.DeclaredFileLengthBytes} bytes, "
                + $"but the file contains {shapeStream.Length} bytes.");
        }

        byte[] dbfRecord = ArrayPool<byte>.Shared.Rent(dbfHeader.RecordLength);
        try
        {
            for (int sourceIndex = 0; sourceIndex < dbfHeader.RecordCount; sourceIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await dbfStream.ReadExactlyAsync(
                    dbfRecord.AsMemory(0, dbfHeader.RecordLength),
                    cancellationToken).ConfigureAwait(false);

                ShapefileRecord shapeRecord = await ReadRecordAsync(
                    shapeStream,
                    sourceIndex + 1,
                    cancellationToken).ConfigureAwait(false);

                if (dbfRecord[0] == (byte)'*')
                {
                    continue;
                }

                string timeZoneId = Encoding.ASCII.GetString(
                    dbfRecord,
                    dbfHeader.TimeZoneFieldOffset,
                    dbfHeader.TimeZoneFieldLength).Trim();
                if (string.IsNullOrWhiteSpace(timeZoneId))
                {
                    throw InvalidShapefile(
                        $"DBF record {sourceIndex + 1} has an empty TZID.");
                }

                if (shapeRecord.ShapeType == NullShape)
                {
                    continue;
                }

                MultiPolygon geometry = ParsePolygon(
                    shapeRecord.Content.Span,
                    shapeRecord.ShapeType,
                    sourceIndex + 1);
                yield return new TimeZoneBoundaryRecord(
                    sourceIndex + 1,
                    timeZoneId,
                    geometry,
                    shapeRecord.Content.Length + 8L + dbfHeader.RecordLength);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dbfRecord);
        }

        if (shapeStream.Position != shapeStream.Length)
        {
            throw InvalidShapefile(
                "The SHP and DBF record counts do not match.");
        }
    }

    private static async ValueTask ValidateProjectionAsync(
        string projectionPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(projectionPath))
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.InvalidConfiguration,
                $"The timezone projection file does not exist: {projectionPath}");
        }

        string projection = await File.ReadAllTextAsync(
            projectionPath,
            cancellationToken).ConfigureAwait(false);
        if (!projection.Contains("WGS_1984", StringComparison.OrdinalIgnoreCase)
            || !projection.Contains("UNIT[\"Degree\"", StringComparison.OrdinalIgnoreCase))
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.UnsupportedProjection,
                "Timezone boundaries must use WGS 84 longitude/latitude coordinates.");
        }
    }

    private static async ValueTask<ShapefileHeader> ReadShapefileHeaderAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[ShapefileHeaderLength];
        try
        {
            await stream.ReadExactlyAsync(
                header,
                cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidShapefile(
                "The SHP header is truncated.",
                exception);
        }

        int fileCode = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(0, 4));
        int fileLengthWords = BinaryPrimitives.ReadInt32BigEndian(
            header.AsSpan(24, 4));
        int version = BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(28, 4));
        int shapeType = BinaryPrimitives.ReadInt32LittleEndian(
            header.AsSpan(32, 4));

        if (fileCode != ShapefileFileCode
            || version != ShapefileVersion
            || fileLengthWords < ShapefileHeaderLength / 2
            || !IsPolygonShape(shapeType))
        {
            throw InvalidShapefile(
                $"Unsupported SHP header: fileCode={fileCode}, "
                + $"version={version}, shapeType={shapeType}.");
        }

        return new ShapefileHeader(fileLengthWords * 2L, shapeType);
    }

    private static async ValueTask<DbfHeader> ReadDbfHeaderAsync(
        FileStream stream,
        CancellationToken cancellationToken)
    {
        byte[] prefix = new byte[32];
        try
        {
            await stream.ReadExactlyAsync(
                prefix,
                cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidShapefile(
                "The DBF header is truncated.",
                exception);
        }

        int recordCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            prefix.AsSpan(4, 4)));
        int headerLength = BinaryPrimitives.ReadUInt16LittleEndian(
            prefix.AsSpan(8, 2));
        int recordLength = BinaryPrimitives.ReadUInt16LittleEndian(
            prefix.AsSpan(10, 2));
        if (recordCount < 0
            || headerLength < 33
            || recordLength < 2
            || headerLength > stream.Length)
        {
            throw InvalidShapefile(
                "The DBF header contains invalid record dimensions.");
        }

        int descriptorBytes = headerLength - 32;
        byte[] descriptors = new byte[descriptorBytes];
        try
        {
            await stream.ReadExactlyAsync(
                descriptors,
                cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidShapefile(
                "The DBF field descriptors are truncated.",
                exception);
        }

        int fieldOffset = 1;
        int timeZoneFieldOffset = -1;
        int timeZoneFieldLength = 0;
        for (int offset = 0; offset + 1 < descriptors.Length; offset += 32)
        {
            if (descriptors[offset] == 0x0D)
            {
                break;
            }

            int nameLength = Array.IndexOf(
                descriptors,
                (byte)0,
                offset,
                Math.Min(11, descriptors.Length - offset));
            if (nameLength < 0)
            {
                nameLength = offset + Math.Min(11, descriptors.Length - offset);
            }

            string name = Encoding.ASCII.GetString(
                descriptors,
                offset,
                nameLength - offset);
            int length = descriptors[offset + 16];
            if (length <= 0 || fieldOffset + length > recordLength)
            {
                throw InvalidShapefile(
                    $"DBF field '{name}' exceeds the declared record length.");
            }

            if (name.Equals("TZID", StringComparison.OrdinalIgnoreCase))
            {
                if (descriptors[offset + 11] != (byte)'C')
                {
                    throw InvalidShapefile(
                        "The DBF TZID field must be a character field.");
                }

                timeZoneFieldOffset = fieldOffset;
                timeZoneFieldLength = length;
            }

            fieldOffset += length;
        }

        if (timeZoneFieldOffset < 0)
        {
            throw InvalidShapefile(
                "The DBF file does not contain a TZID field.");
        }

        long expectedLength = headerLength + ((long)recordCount * recordLength);
        if (stream.Length < expectedLength)
        {
            throw InvalidShapefile(
                "The DBF record data is truncated.");
        }

        return new DbfHeader(
            recordCount,
            recordLength,
            timeZoneFieldOffset,
            timeZoneFieldLength);
    }

    private static async ValueTask<ShapefileRecord> ReadRecordAsync(
        FileStream stream,
        int expectedRecordNumber,
        CancellationToken cancellationToken)
    {
        byte[] recordHeader = new byte[8];
        try
        {
            await stream.ReadExactlyAsync(
                recordHeader,
                cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException exception)
        {
            throw InvalidShapefile(
                "The SHP record data is truncated.",
                exception);
        }

        int recordNumber = BinaryPrimitives.ReadInt32BigEndian(
            recordHeader.AsSpan(0, 4));
        int contentLengthWords = BinaryPrimitives.ReadInt32BigEndian(
            recordHeader.AsSpan(4, 4));
        long contentLengthLong = contentLengthWords * 2L;
        if (recordNumber != expectedRecordNumber
            || contentLengthWords < 2
            || contentLengthLong > MaximumRecordBytes
            || contentLengthLong > stream.Length - stream.Position)
        {
            throw InvalidShapefile(
                $"Invalid SHP record {recordNumber}; expected {expectedRecordNumber}.");
        }

        int contentLength = checked((int)contentLengthLong);
        byte[] content = ArrayPool<byte>.Shared.Rent(contentLength);
        try
        {
            await stream.ReadExactlyAsync(
                content.AsMemory(0, contentLength),
                cancellationToken).ConfigureAwait(false);

            int shapeType = BinaryPrimitives.ReadInt32LittleEndian(
                content.AsSpan(0, 4));
            if (shapeType != NullShape && !IsPolygonShape(shapeType))
            {
                throw InvalidShapefile(
                    $"SHP record {recordNumber} has unsupported shape type {shapeType}.");
            }

            return new ShapefileRecord(
                shapeType,
                content,
                contentLength);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(content);
            throw;
        }
    }

    private static MultiPolygon ParsePolygon(
        ReadOnlySpan<byte> content,
        int shapeType,
        int sourceRecordNumber)
    {
        if (!IsPolygonShape(shapeType) || content.Length < 44)
        {
            throw InvalidShapefile(
                $"SHP record {sourceRecordNumber} is not a complete polygon.");
        }

        int partCount = BinaryPrimitives.ReadInt32LittleEndian(
            content.Slice(36, 4));
        int pointCount = BinaryPrimitives.ReadInt32LittleEndian(
            content.Slice(40, 4));
        long requiredLength = 44L + (partCount * 4L) + (pointCount * 16L);
        if (partCount <= 0
            || pointCount <= 0
            || partCount > pointCount
            || requiredLength > content.Length)
        {
            throw InvalidShapefile(
                $"SHP record {sourceRecordNumber} has invalid polygon dimensions.");
        }

        int[] partOffsets = new int[partCount + 1];
        int partOffset = 44;
        for (int index = 0; index < partCount; index++)
        {
            int pointOffset = BinaryPrimitives.ReadInt32LittleEndian(
                content.Slice(partOffset + (index * 4), 4));
            if ((index == 0 && pointOffset != 0)
                || pointOffset < 0
                || pointOffset >= pointCount
                || (index > 0 && pointOffset <= partOffsets[index - 1]))
            {
                throw InvalidShapefile(
                    $"SHP record {sourceRecordNumber} has invalid part offsets.");
            }

            partOffsets[index] = pointOffset;
        }

        partOffsets[partCount] = pointCount;
        int pointsOffset = 44 + (partCount * 4);
        List<LinearRing> shells = [];
        List<LinearRing> holes = [];
        for (int partIndex = 0; partIndex < partCount; partIndex++)
        {
            int start = partOffsets[partIndex];
            int count = partOffsets[partIndex + 1] - start;
            if (count < 4)
            {
                throw InvalidShapefile(
                    $"SHP record {sourceRecordNumber} contains a degenerate ring.");
            }

            Coordinate[] coordinates = new Coordinate[count];
            for (int coordinateIndex = 0; coordinateIndex < count; coordinateIndex++)
            {
                int coordinateOffset =
                    pointsOffset + ((start + coordinateIndex) * 16);
                double x = BinaryPrimitives.ReadDoubleLittleEndian(
                    content.Slice(coordinateOffset, 8));
                double y = BinaryPrimitives.ReadDoubleLittleEndian(
                    content.Slice(coordinateOffset + 8, 8));
                if (!double.IsFinite(x)
                    || !double.IsFinite(y)
                    || x is < -180 or > 180
                    || y is < -90 or > 90)
                {
                    throw InvalidShapefile(
                        $"SHP record {sourceRecordNumber} has an invalid coordinate.");
                }

                coordinates[coordinateIndex] = new Coordinate(x, y);
            }

            if (!coordinates[0].Equals2D(coordinates[^1]))
            {
                throw InvalidShapefile(
                    $"SHP record {sourceRecordNumber} contains an open ring.");
            }

            LinearRing ring;
            try
            {
                ring = GeometryFactory.CreateLinearRing(coordinates);
            }
            catch (ArgumentException exception)
            {
                throw InvalidShapefile(
                    $"SHP record {sourceRecordNumber} contains an invalid ring.",
                    exception);
            }

            if (Orientation.IsCCW(coordinates))
            {
                holes.Add(ring);
            }
            else
            {
                shells.Add(ring);
            }
        }

        if (shells.Count == 0)
        {
            throw InvalidShapefile(
                $"SHP record {sourceRecordNumber} contains no exterior ring.");
        }

        List<List<LinearRing>> holesByShell = shells
            .Select(_ => new List<LinearRing>())
            .ToList();
        foreach (LinearRing hole in holes)
        {
            int containingShell = FindContainingShell(shells, hole);
            if (containingShell < 0)
            {
                throw InvalidShapefile(
                    $"SHP record {sourceRecordNumber} contains an uncontained hole.");
            }

            holesByShell[containingShell].Add(hole);
        }

        Polygon[] polygons = new Polygon[shells.Count];
        for (int index = 0; index < shells.Count; index++)
        {
            polygons[index] = GeometryFactory.CreatePolygon(
                shells[index],
                [.. holesByShell[index]]);
        }

        MultiPolygon geometry = GeometryFactory.CreateMultiPolygon(polygons);
        if (geometry.IsEmpty || !geometry.IsValid)
        {
            throw new TimeZoneDatabaseBuildException(
                TimeZoneDatabaseFailureCode.InvalidBoundaryGeometry,
                $"SHP record {sourceRecordNumber} produced invalid polygon geometry.");
        }

        return geometry;
    }

    private static int FindContainingShell(
        IReadOnlyList<LinearRing> shells,
        LinearRing hole)
    {
        Point sample = GeometryFactory.CreatePoint(hole.GetCoordinateN(0));
        int selectedIndex = -1;
        double selectedArea = double.PositiveInfinity;
        for (int index = 0; index < shells.Count; index++)
        {
            LinearRing shell = shells[index];
            if (!shell.EnvelopeInternal.Contains(hole.EnvelopeInternal))
            {
                continue;
            }

            Polygon polygon = GeometryFactory.CreatePolygon(shell);
            if (polygon.Covers(sample) && polygon.Area < selectedArea)
            {
                selectedArea = polygon.Area;
                selectedIndex = index;
            }
        }

        return selectedIndex;
    }

    private static bool IsPolygonShape(int shapeType)
        => shapeType is PolygonShape or PolygonZShape or PolygonMShape;

    private static TimeZoneDatabaseBuildException InvalidShapefile(
        string message,
        Exception? innerException = null)
        => new(
            TimeZoneDatabaseFailureCode.InvalidShapefile,
            message,
            innerException);

    private sealed record ShapefileHeader(
        long DeclaredFileLengthBytes,
        int ShapeType);

    private sealed record DbfHeader(
        int RecordCount,
        int RecordLength,
        int TimeZoneFieldOffset,
        int TimeZoneFieldLength);

    private sealed class ShapefileRecord : IDisposable
    {
        private byte[]? content;

        public ShapefileRecord(
            int shapeType,
            byte[] content,
            int contentLength)
        {
            ShapeType = shapeType;
            this.content = content;
            Content = content.AsMemory(0, contentLength);
        }

        public int ShapeType { get; }

        public ReadOnlyMemory<byte> Content { get; }

        public void Dispose()
        {
            byte[]? rented = Interlocked.Exchange(
                ref content,
                null);
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }
}
