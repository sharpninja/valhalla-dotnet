namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Identifies the canonical semantic replay pass requested by <see cref="PbfGraphParser"/>.
/// </summary>
public enum OsmPbfEntityPass
{
    Ways = 0,
    Nodes = 1,
    Relations = 2,
}

/// <summary>
/// Replays normalized OSM entities without requiring the graph parser to reopen or decode the
/// original PBF. Implementations preserve source-file boundaries and canonical entity order.
/// </summary>
public interface IOsmPbfEntitySource
{
    /// <summary>Gets the number of source files represented by this entity source.</summary>
    int FileCount { get; }

    /// <summary>
    /// Replays one entity kind for one source file into <paramref name="visitor"/>.
    /// Implementations must honor cancellation during long replays.
    /// </summary>
    void VisitFile(
        int fileOrdinal,
        OsmPbfEntityPass pass,
        IOsmPbfVisitor visitor,
        CancellationToken cancellationToken);
}

internal sealed class FileOsmPbfEntitySource : IOsmPbfEntitySource
{
    private readonly IReadOnlyList<string> pbfPaths;

    public FileOsmPbfEntitySource(IReadOnlyList<string> pbfPaths)
    {
        this.pbfPaths = pbfPaths ?? throw new ArgumentNullException(nameof(pbfPaths));
    }

    public int FileCount => pbfPaths.Count;

    public void VisitFile(
        int fileOrdinal,
        OsmPbfEntityPass pass,
        IOsmPbfVisitor visitor,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(fileOrdinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(fileOrdinal, pbfPaths.Count);
        ArgumentNullException.ThrowIfNull(visitor);
        cancellationToken.ThrowIfCancellationRequested();

        new OsmPbfReader(new FilteringVisitor(pass, visitor, cancellationToken)).Parse(
            pbfPaths[fileOrdinal]);
    }

    private sealed class FilteringVisitor : IOsmPbfVisitor
    {
        private readonly OsmPbfEntityPass pass;
        private readonly IOsmPbfVisitor inner;
        private readonly CancellationToken cancellationToken;
        private int entityCount;

        public FilteringVisitor(
            OsmPbfEntityPass pass,
            IOsmPbfVisitor inner,
            CancellationToken cancellationToken)
        {
            this.pass = pass;
            this.inner = inner;
            this.cancellationToken = cancellationToken;
        }

        public void Header(
            double? minLat,
            double? minLon,
            double? maxLat,
            double? maxLon,
            IReadOnlyList<string> requiredFeatures) =>
            inner.Header(minLat, minLon, maxLat, maxLon, requiredFeatures);

        public void Node(
            ulong id,
            double lat,
            double lon,
            IReadOnlyDictionary<string, string> tags)
        {
            CheckCancellation();
            if (pass == OsmPbfEntityPass.Nodes)
            {
                inner.Node(id, lat, lon, tags);
            }
        }

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
            CheckCancellation();
            if (pass == OsmPbfEntityPass.Ways)
            {
                inner.Way(id, nodeRefs, tags);
            }
        }

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags)
        {
            CheckCancellation();
            if (pass == OsmPbfEntityPass.Relations)
            {
                inner.Relation(id, members, tags);
            }
        }

        private void CheckCancellation()
        {
            entityCount++;
            if ((entityCount & 4095) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }
}
