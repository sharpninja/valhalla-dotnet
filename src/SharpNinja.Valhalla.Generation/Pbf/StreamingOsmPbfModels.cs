namespace SharpNinja.Valhalla.Generation.Pbf;

public enum OsmEntityKind
{
    Node = 0,
    Way = 1,
    Relation = 2,
}

public enum OsmPbfCompressionKind
{
    Raw = 0,
    Zlib = 1,
    Lz4 = 2,
}

public enum StreamingOsmPbfFailureCode
{
    InvalidConfiguration = 0,
    TruncatedInput = 1,
    OversizedBlobHeader = 2,
    OversizedCompressedBlob = 3,
    OversizedUncompressedBlob = 4,
    MalformedProtocolBuffer = 5,
    UnsupportedCompression = 6,
    InvalidStringTableReference = 7,
    EntityLimitExceeded = 8,
    RelationMemberLimitExceeded = 9,
    DecompressionFailed = 10,
}

public sealed class StreamingOsmPbfException : IOException
{
    public StreamingOsmPbfException(
        StreamingOsmPbfFailureCode failureCode,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        FailureCode = failureCode;
    }

    public StreamingOsmPbfFailureCode FailureCode { get; }
}

public sealed record StreamingOsmPbfReaderOptions(
    int MaximumBlobHeaderBytes = 64 * 1024,
    int MaximumCompressedBlobBytes = 32 * 1024 * 1024,
    int MaximumUncompressedBlobBytes = 32 * 1024 * 1024,
    int MaximumStringTableEntries = 4_000_000,
    int MaximumEntityMessagesPerBlock = 8_000_000,
    int MaximumRelationMembers = 4_000_000,
    int CancellationCheckInterval = 4096);

public readonly record struct OsmEntityOrdinal(
    int FileOrdinal,
    long BlockOrdinal,
    int EntityOrdinal);

public readonly record struct OsmTag(string Key, string Value);

public readonly record struct OsmRelationMemberEntity(
    ulong Id,
    SharpNinja.Valhalla.Mjolnir.OsmMemberType Type,
    string Role);

public sealed record OsmNodeEntity(
    OsmEntityOrdinal Ordinal,
    ulong Id,
    double Latitude,
    double Longitude,
    IReadOnlyList<OsmTag> Tags);

public sealed record OsmWayEntity(
    OsmEntityOrdinal Ordinal,
    ulong Id,
    IReadOnlyList<ulong> NodeReferences,
    IReadOnlyList<OsmTag> Tags);

public sealed record OsmRelationEntity(
    OsmEntityOrdinal Ordinal,
    ulong Id,
    IReadOnlyList<OsmRelationMemberEntity> Members,
    IReadOnlyList<OsmTag> Tags);

public sealed record StreamingOsmPbfBlockReceipt(
    int FileOrdinal,
    long BlockOrdinal,
    string BlobType,
    OsmPbfCompressionKind Compression,
    int CompressedBytes,
    int UncompressedBytes,
    int DecompressionCount);

public sealed record StreamingOsmPbfReadMetrics(
    long BytesRead,
    int FileBlockCount,
    int DataBlockCount,
    int DecompressionCount,
    int DecodedNodeCount,
    int DecodedWayCount,
    int DecodedRelationCount,
    int SkippedNodeCount,
    int SkippedWayCount,
    int SkippedRelationCount,
    int MaterializedTagCount,
    int MaterializedTagDictionaryCount,
    IReadOnlyList<StreamingOsmPbfBlockReceipt> BlockReceipts);

public sealed record StreamingOsmPbfReadResult(StreamingOsmPbfReadMetrics Metrics);

public interface IStreamingOsmEntitySink
{
    bool ShouldRetain(OsmEntityKind kind);

    void AddNode(scoped in OsmNodeView node);

    void AddWay(scoped in OsmWayView way);

    void AddRelation(scoped in OsmRelationView relation);
}

public readonly ref struct OsmTagView
{
    private readonly ReadOnlySpan<uint> keys;
    private readonly ReadOnlySpan<uint> values;
    private readonly OsmStringTableView strings;

    internal OsmTagView(
        ReadOnlySpan<uint> keys,
        ReadOnlySpan<uint> values,
        OsmStringTableView strings)
    {
        this.keys = keys;
        this.values = values;
        this.strings = strings;
    }

    public int Count => Math.Min(keys.Length, values.Length);

    public OsmTag this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            strings.RecordMaterializedTag();
            return new OsmTag(
                strings.Decode(checked((int)keys[index])),
                strings.Decode(checked((int)values[index])));
        }
    }

    public OsmTag[] Materialize()
    {
        var result = new OsmTag[Count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = this[index];
        }

        return result;
    }
}

public readonly ref struct OsmNodeView
{
    internal OsmNodeView(
        OsmEntityOrdinal ordinal,
        ulong id,
        double latitude,
        double longitude,
        OsmTagView tags)
    {
        Ordinal = ordinal;
        Id = id;
        Latitude = latitude;
        Longitude = longitude;
        Tags = tags;
    }

    public OsmEntityOrdinal Ordinal { get; }

    public ulong Id { get; }

    public double Latitude { get; }

    public double Longitude { get; }

    public OsmTagView Tags { get; }
}

public readonly ref struct OsmWayView
{
    private readonly ReadOnlySpan<ulong> nodeReferences;

    internal OsmWayView(
        OsmEntityOrdinal ordinal,
        ulong id,
        ReadOnlySpan<ulong> nodeReferences,
        OsmTagView tags)
    {
        Ordinal = ordinal;
        Id = id;
        this.nodeReferences = nodeReferences;
        Tags = tags;
    }

    public OsmEntityOrdinal Ordinal { get; }

    public ulong Id { get; }

    public ReadOnlySpan<ulong> NodeReferences => nodeReferences;

    public OsmTagView Tags { get; }
}

public readonly ref struct OsmRelationView
{
    private readonly ReadOnlySpan<long> memberIds;
    private readonly ReadOnlySpan<int> memberTypes;
    private readonly ReadOnlySpan<int> roleStringIds;
    private readonly OsmStringTableView strings;

    internal OsmRelationView(
        OsmEntityOrdinal ordinal,
        ulong id,
        ReadOnlySpan<long> memberIds,
        ReadOnlySpan<int> memberTypes,
        ReadOnlySpan<int> roleStringIds,
        OsmStringTableView strings,
        OsmTagView tags)
    {
        Ordinal = ordinal;
        Id = id;
        this.memberIds = memberIds;
        this.memberTypes = memberTypes;
        this.roleStringIds = roleStringIds;
        this.strings = strings;
        Tags = tags;
    }

    public OsmEntityOrdinal Ordinal { get; }

    public ulong Id { get; }

    public int MemberCount => Math.Min(
        memberIds.Length,
        Math.Min(memberTypes.Length, roleStringIds.Length));

    public OsmTagView Tags { get; }

    public OsmRelationMemberEntity GetMember(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, MemberCount);
        return new OsmRelationMemberEntity(
            checked((ulong)memberIds[index]),
            (SharpNinja.Valhalla.Mjolnir.OsmMemberType)memberTypes[index],
            strings.Decode(roleStringIds[index]));
    }

    public OsmRelationMemberEntity[] MaterializeMembers()
    {
        var result = new OsmRelationMemberEntity[MemberCount];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = GetMember(index);
        }

        return result;
    }
}

public sealed class InMemoryOsmEntityStore : IStreamingOsmEntitySink
{
    private readonly Func<OsmEntityKind, bool> retentionPolicy;

    public InMemoryOsmEntityStore(Func<OsmEntityKind, bool>? retentionPolicy = null)
    {
        this.retentionPolicy = retentionPolicy ?? (_ => true);
    }

    public List<OsmNodeEntity> Nodes { get; } = [];

    public List<OsmWayEntity> Ways { get; } = [];

    public List<OsmRelationEntity> Relations { get; } = [];

    public bool ShouldRetain(OsmEntityKind kind) => retentionPolicy(kind);

    public void AddNode(scoped in OsmNodeView node)
    {
        Nodes.Add(new OsmNodeEntity(
            node.Ordinal,
            node.Id,
            node.Latitude,
            node.Longitude,
            node.Tags.Materialize()));
    }

    public void AddWay(scoped in OsmWayView way)
    {
        Ways.Add(new OsmWayEntity(
            way.Ordinal,
            way.Id,
            way.NodeReferences.ToArray(),
            way.Tags.Materialize()));
    }

    public void AddRelation(scoped in OsmRelationView relation)
    {
        Relations.Add(new OsmRelationEntity(
            relation.Ordinal,
            relation.Id,
            relation.MaterializeMembers(),
            relation.Tags.Materialize()));
    }
}

internal readonly ref struct OsmStringTableView
{
    private readonly ReadOnlySpan<byte> data;
    private readonly ReadOnlySpan<int> offsets;
    private readonly ReadOnlySpan<int> lengths;
    private readonly StreamingOsmPbfMetricsAccumulator metrics;

    public OsmStringTableView(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<int> offsets,
        ReadOnlySpan<int> lengths,
        StreamingOsmPbfMetricsAccumulator metrics)
    {
        this.data = data;
        this.offsets = offsets;
        this.lengths = lengths;
        this.metrics = metrics;
    }

    public int Count => offsets.Length;

    public string Decode(int index)
    {
        if (index < 0 || index >= Count)
        {
            throw new StreamingOsmPbfException(
                StreamingOsmPbfFailureCode.InvalidStringTableReference,
                "PBF protocol error: string-table reference is outside the current block.");
        }

        return System.Text.Encoding.UTF8.GetString(data.Slice(offsets[index], lengths[index]));
    }

    public void RecordMaterializedTag() => metrics.MaterializedTagCount++;
}

internal sealed class StreamingOsmPbfMetricsAccumulator
{
    public long BytesRead { get; set; }
    public int FileBlockCount { get; set; }
    public int DataBlockCount { get; set; }
    public int DecompressionCount { get; set; }
    public int DecodedNodeCount { get; set; }
    public int DecodedWayCount { get; set; }
    public int DecodedRelationCount { get; set; }
    public int SkippedNodeCount { get; set; }
    public int SkippedWayCount { get; set; }
    public int SkippedRelationCount { get; set; }
    public int MaterializedTagCount { get; set; }
    public List<StreamingOsmPbfBlockReceipt> BlockReceipts { get; } = [];

    public StreamingOsmPbfReadMetrics Snapshot() =>
        new(
            BytesRead,
            FileBlockCount,
            DataBlockCount,
            DecompressionCount,
            DecodedNodeCount,
            DecodedWayCount,
            DecodedRelationCount,
            SkippedNodeCount,
            SkippedWayCount,
            SkippedRelationCount,
            MaterializedTagCount,
            0,
            BlockReceipts.ToArray());
}
