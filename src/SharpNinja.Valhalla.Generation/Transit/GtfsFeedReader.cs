using System.IO.Compression;
using System.Text;

namespace SharpNinja.Valhalla.Generation.Transit;

internal sealed class GtfsFeedReader
{
    private static readonly string[] RequiredFiles =
    [
        "agency.txt",
        "stops.txt",
        "routes.txt",
        "trips.txt",
        "stop_times.txt",
    ];

    private static readonly string[] OptionalFiles =
    [
        "calendar.txt",
        "calendar_dates.txt",
        "frequencies.txt",
        "shapes.txt",
        "transfers.txt",
    ];

    public async ValueTask<GtfsFeedData> ReadAsync(
        string feedPath,
        long memoryBudgetBytes,
        CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(feedPath);
        if (Directory.Exists(fullPath))
        {
            RejectReparsePoint(fullPath, nameof(feedPath));
            return await ReadDirectoryAsync(fullPath, memoryBudgetBytes, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(fullPath) ||
            !string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidConfiguration,
                $"GTFS feed must be a directory or .zip file: {feedPath}");
        }

        RejectReparsePoint(fullPath, nameof(feedPath));
        return await ReadArchiveAsync(fullPath, memoryBudgetBytes, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<GtfsFeedData> ReadDirectoryAsync(
        string path,
        long memoryBudgetBytes,
        CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, GtfsTable>(StringComparer.OrdinalIgnoreCase);
        long consumedBytes = 0;
        foreach (string fileName in RequiredFiles.Concat(OptionalFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string filePath = Path.Combine(path, fileName);
            if (!File.Exists(filePath))
            {
                if (RequiredFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    throw MissingFile(fileName);
                }

                continue;
            }

            RejectReparsePoint(filePath, fileName);
            long length = new FileInfo(filePath).Length;
            consumedBytes = checked(consumedBytes + length);
            EnforceBudget(consumedBytes, memoryBudgetBytes);
            await using FileStream stream = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            tables[fileName] = await ReadTableAsync(stream, fileName, cancellationToken).ConfigureAwait(false);
        }

        return new GtfsFeedData(Path.GetFileName(path), tables);
    }

    private static async ValueTask<GtfsFeedData> ReadArchiveAsync(
        string path,
        long memoryBudgetBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Contains('/') || entry.FullName.Contains('\\'))
            {
                continue;
            }

            if (!RequiredFiles.Contains(entry.Name, StringComparer.OrdinalIgnoreCase) &&
                !OptionalFiles.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!entries.TryAdd(entry.Name, entry))
            {
                throw new TransitTileBuildException(
                    TransitTileBuildFailureCode.InvalidCsv,
                    $"GTFS archive contains duplicate table {entry.Name}");
            }

            expandedBytes = checked(expandedBytes + entry.Length);
            EnforceBudget(expandedBytes, memoryBudgetBytes);
        }

        var tables = new Dictionary<string, GtfsTable>(StringComparer.OrdinalIgnoreCase);
        foreach (string fileName in RequiredFiles.Concat(OptionalFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!entries.TryGetValue(fileName, out ZipArchiveEntry? entry))
            {
                if (RequiredFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase))
                {
                    throw MissingFile(fileName);
                }

                continue;
            }

            await using Stream entryStream = entry.Open();
            tables[fileName] = await ReadTableAsync(entryStream, fileName, cancellationToken).ConfigureAwait(false);
        }

        return new GtfsFeedData(Path.GetFileNameWithoutExtension(path), tables);
    }

    private static async ValueTask<GtfsTable> ReadTableAsync(
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 64 * 1024,
            leaveOpen: true);
        IReadOnlyList<string[]> records;
        try
        {
            records = await ReadRecordsAsync(reader, cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidCsv,
                $"{fileName} is not valid UTF-8",
                exception);
        }

        if (records.Count == 0)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidCsv,
                $"{fileName} has no header row");
        }

        string[] header = records[0];
        if (header.Length == 0 || header.Any(string.IsNullOrWhiteSpace))
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidCsv,
                $"{fileName} has an invalid header row");
        }

        var indexes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < header.Length; index++)
        {
            string name = header[index].TrimStart('﻿');
            if (!indexes.TryAdd(name, index))
            {
                throw new TransitTileBuildException(
                    TransitTileBuildFailureCode.InvalidCsv,
                    $"{fileName} contains duplicate column {name}");
            }
        }

        string[][] rows = records.Skip(1)
            .Where(row => row.Any(value => value.Length != 0))
            .ToArray();
        if (rows.Any(row => row.Length != header.Length))
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidCsv,
                $"{fileName} contains a row with the wrong field count");
        }

        return new GtfsTable(fileName, indexes, rows);
    }

    private static async ValueTask<IReadOnlyList<string[]>> ReadRecordsAsync(
        TextReader reader,
        CancellationToken cancellationToken)
    {
        var records = new List<string[]>();
        var record = new List<string>();
        var field = new StringBuilder();
        var buffer = new char[16 * 1024];
        bool quoted = false;
        bool quoteClosed = false;
        bool pendingQuote = false;

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            for (int index = 0; index < read; index++)
            {
                char character = buffer[index];
                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (character == '"')
                    {
                        field.Append('"');
                        continue;
                    }

                    quoted = false;
                    quoteClosed = true;
                }

                if (quoted)
                {
                    if (character == '"')
                    {
                        if (index + 1 < read)
                        {
                            if (buffer[index + 1] == '"')
                            {
                                field.Append('"');
                                index++;
                            }
                            else
                            {
                                quoted = false;
                                quoteClosed = true;
                            }
                        }
                        else
                        {
                            pendingQuote = true;
                        }
                    }
                    else
                    {
                        field.Append(character);
                    }

                    continue;
                }

                if (quoteClosed && character != ',' && character != '\r' && character != '\n')
                {
                    throw new TransitTileBuildException(
                        TransitTileBuildFailureCode.InvalidCsv,
                        "Unexpected character after a closing quote");
                }

                if (character == '"' && field.Length == 0)
                {
                    quoted = true;
                    quoteClosed = false;
                }
                else if (character == ',')
                {
                    record.Add(field.ToString());
                    field.Clear();
                    quoteClosed = false;
                }
                else if (character == '\n')
                {
                    record.Add(field.ToString());
                    field.Clear();
                    quoteClosed = false;
                    records.Add(record.ToArray());
                    record.Clear();
                }
                else if (character != '\r')
                {
                    field.Append(character);
                }
            }
        }

        if (pendingQuote)
        {
            quoted = false;
            quoteClosed = true;
        }

        if (quoted)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidCsv,
                "CSV contains an unterminated quoted field");
        }

        if (field.Length != 0 || record.Count != 0 || quoteClosed)
        {
            record.Add(field.ToString());
            records.Add(record.ToArray());
        }

        return records;
    }


    private static TransitTileBuildException MissingFile(string fileName)
        => new(
            TransitTileBuildFailureCode.MissingRequiredFile,
            $"GTFS feed is missing required file {fileName}");

    private static void EnforceBudget(long bytes, long memoryBudgetBytes)
    {
        if (bytes > memoryBudgetBytes)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.ResourceExhausted,
                $"GTFS expanded input exceeds memory budget of {memoryBudgetBytes} bytes");
        }
    }

    private static void RejectReparsePoint(string path, string parameterName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.UnsafePath,
                $"{parameterName} cannot be a symbolic link or reparse point");
        }
    }
}

internal sealed record GtfsFeedData(
    string Prefix,
    IReadOnlyDictionary<string, GtfsTable> Tables)
{
    public GtfsTable Required(string fileName)
        => Tables.TryGetValue(fileName, out GtfsTable? table)
            ? table
            : throw new TransitTileBuildException(
                TransitTileBuildFailureCode.MissingRequiredFile,
                $"GTFS feed is missing required file {fileName}");

    public GtfsTable? Optional(string fileName)
        => Tables.GetValueOrDefault(fileName);
}

internal sealed class GtfsTable
{
    private readonly IReadOnlyDictionary<string, int> _indexes;

    public GtfsTable(
        string fileName,
        IReadOnlyDictionary<string, int> indexes,
        IReadOnlyList<string[]> rows)
    {
        FileName = fileName;
        _indexes = indexes;
        Rows = rows;
    }

    public string FileName { get; }

    public IReadOnlyList<string[]> Rows { get; }

    public string Required(string[] row, string column)
    {
        string value = Optional(row, column);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.InvalidValue,
                $"{FileName}.{column} is required");
        }

        return value;
    }

    public string Optional(string[] row, string column)
        => _indexes.TryGetValue(column, out int index) ? row[index] : string.Empty;

    public void RequireColumns(params string[] columns)
    {
        foreach (string column in columns)
        {
            if (!_indexes.ContainsKey(column))
            {
                throw new TransitTileBuildException(
                    TransitTileBuildFailureCode.InvalidCsv,
                    $"{FileName} is missing required column {column}");
            }
        }
    }
}
