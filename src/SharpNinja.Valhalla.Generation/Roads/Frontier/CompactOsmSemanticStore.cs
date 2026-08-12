using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using SharpNinja.Valhalla.Generation.Storage;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

internal sealed record CompactOsmSemanticStoreOptions(
    string WorkingDirectory,
    IntermediateStorageMode StorageMode,
    long MemoryBudgetBytes,
    long ScratchDiskBudgetBytes,
    int SegmentSizeBytes = 64 * 1024 * 1024);

internal sealed class CompactOsmSemanticStore : IDisposable
{
    private const int StorePartitionCount = 18;
    private const int ReservedStorePartitions = 10;
    private readonly IntermediateSequenceStore<GenerationNodeRecord> nodes;
    private readonly IntermediateSequenceStore<GenerationWayRecord> ways;
    private readonly IntermediateSequenceStore<GenerationWayNodeReference> wayNodeReferences;
    private readonly IntermediateSequenceStore<GenerationRelationRecord> relations;
    private readonly IntermediateSequenceStore<GenerationRelationMemberRecord> relationMembers;
    private readonly IntermediateSequenceStore<GenerationRestrictionRecord> restrictions;
    private readonly IntermediateSequenceStore<GenerationRestrictionViaRecord> restrictionVias;
    private readonly IntermediateSequenceStore<NodeIncidenceRecord> incidenceInput;
    private readonly CompactOsmMetadataStore metadata;
    private readonly NodeIncidenceIndex incidenceIndex;
    private bool disposed;

    private CompactOsmSemanticStore(
        IntermediateSequenceStore<GenerationNodeRecord> nodes,
        IntermediateSequenceStore<GenerationWayRecord> ways,
        IntermediateSequenceStore<GenerationWayNodeReference> wayNodeReferences,
        IntermediateSequenceStore<GenerationRelationRecord> relations,
        IntermediateSequenceStore<GenerationRelationMemberRecord> relationMembers,
        IntermediateSequenceStore<GenerationRestrictionRecord> restrictions,
        IntermediateSequenceStore<GenerationRestrictionViaRecord> restrictionVias,
        IntermediateSequenceStore<NodeIncidenceRecord> incidenceInput,
        CompactOsmMetadataStore metadata,
        NodeIncidenceIndex incidenceIndex)
    {
        this.nodes = nodes;
        this.ways = ways;
        this.wayNodeReferences = wayNodeReferences;
        this.relations = relations;
        this.relationMembers = relationMembers;
        this.restrictions = restrictions;
        this.restrictionVias = restrictionVias;
        this.incidenceInput = incidenceInput;
        this.metadata = metadata;
        this.incidenceIndex = incidenceIndex;
    }

    internal long CurrentMemoryBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                nodes.State.CurrentMemoryBytes +
                ways.State.CurrentMemoryBytes +
                wayNodeReferences.State.CurrentMemoryBytes +
                relations.State.CurrentMemoryBytes +
                relationMembers.State.CurrentMemoryBytes +
                restrictions.State.CurrentMemoryBytes +
                restrictionVias.State.CurrentMemoryBytes +
                incidenceInput.State.CurrentMemoryBytes +
                metadata.CurrentMemoryBytes +
                incidenceIndex.CurrentMemoryBytes);
        }
    }

    internal long PeakMemoryBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                nodes.State.PeakMemoryBytes +
                ways.State.PeakMemoryBytes +
                wayNodeReferences.State.PeakMemoryBytes +
                relations.State.PeakMemoryBytes +
                relationMembers.State.PeakMemoryBytes +
                restrictions.State.PeakMemoryBytes +
                restrictionVias.State.PeakMemoryBytes +
                incidenceInput.State.PeakMemoryBytes +
                metadata.PeakMemoryBytes +
                incidenceIndex.PeakMemoryBytes);
        }
    }

    internal long CurrentScratchBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                nodes.State.CurrentScratchBytes +
                ways.State.CurrentScratchBytes +
                wayNodeReferences.State.CurrentScratchBytes +
                relations.State.CurrentScratchBytes +
                relationMembers.State.CurrentScratchBytes +
                restrictions.State.CurrentScratchBytes +
                restrictionVias.State.CurrentScratchBytes +
                incidenceInput.State.CurrentScratchBytes +
                metadata.CurrentScratchBytes +
                incidenceIndex.CurrentScratchBytes);
        }
    }

    internal long ScratchHighWaterMarkBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return checked(
                nodes.State.ScratchHighWaterMarkBytes +
                ways.State.ScratchHighWaterMarkBytes +
                wayNodeReferences.State.ScratchHighWaterMarkBytes +
                relations.State.ScratchHighWaterMarkBytes +
                relationMembers.State.ScratchHighWaterMarkBytes +
                restrictions.State.ScratchHighWaterMarkBytes +
                restrictionVias.State.ScratchHighWaterMarkBytes +
                incidenceInput.State.ScratchHighWaterMarkBytes +
                metadata.ScratchHighWaterMarkBytes +
                incidenceIndex.ScratchHighWaterMarkBytes);
        }
    }


    internal long NodeCount => ReadCount(nodes);

    internal long WayCount => ReadCount(ways);

    internal long WayNodeReferenceCount => ReadCount(wayNodeReferences);

    internal long RelationCount => ReadCount(relations);

    internal long RelationMemberCount => ReadCount(relationMembers);

    internal long RestrictionCount => ReadCount(restrictions);

    internal long RestrictionViaCount => ReadCount(restrictionVias);

    internal long IncidenceCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return incidenceIndex.IncidenceCount;
        }
    }

    internal long IncidenceSummaryCount
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return incidenceIndex.SummaryCount;
        }
    }

    internal GenerationNodeRecord ReadNode(long ordinal) => Read(nodes, ordinal);

    internal GenerationWayRecord ReadWay(long ordinal) => Read(ways, ordinal);

    internal GenerationWayNodeReference ReadWayNodeReference(long ordinal) =>
        Read(wayNodeReferences, ordinal);

    internal GenerationRelationRecord ReadRelation(long ordinal) => Read(relations, ordinal);

    internal GenerationRelationMemberRecord ReadRelationMember(long ordinal) =>
        Read(relationMembers, ordinal);

    internal GenerationRestrictionRecord ReadRestriction(long ordinal) =>
        Read(restrictions, ordinal);

    internal GenerationRestrictionViaRecord ReadRestrictionVia(long ordinal) =>
        Read(restrictionVias, ordinal);

    internal IReadOnlyDictionary<string, string> ReadTags(long tagReference)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return metadata.ReadTags(tagReference);
    }

    internal string ReadRole(long roleReference)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return metadata.ReadString(roleReference);
    }

    internal bool TryFindIncidenceSummary(
        long osmNodeId,
        out NodeIncidenceSummary summary)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return incidenceIndex.TryFindSummary(osmNodeId, out summary);
    }

    internal static async ValueTask<CompactOsmSemanticStore> BuildAsync(
        IOsmPbfEntitySource source,
        CompactOsmSemanticStoreOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ValidateOptions(options);

        string root = Path.GetFullPath(options.WorkingDirectory);
        Directory.CreateDirectory(root);
        long storeMemoryBudget = options.MemoryBudgetBytes / StorePartitionCount;
        long storeScratchBudget = options.ScratchDiskBudgetBytes / StorePartitionCount;
        long indexMemoryBudget = checked(
            options.MemoryBudgetBytes -
            (storeMemoryBudget * ReservedStorePartitions));
        long indexScratchBudget = checked(
            options.ScratchDiskBudgetBytes -
            (storeScratchBudget * ReservedStorePartitions));

        IntermediateSequenceStore<GenerationNodeRecord>? nodes = null;
        IntermediateSequenceStore<GenerationWayRecord>? ways = null;
        IntermediateSequenceStore<GenerationWayNodeReference>? wayNodeReferences = null;
        IntermediateSequenceStore<GenerationRelationRecord>? relations = null;
        IntermediateSequenceStore<GenerationRelationMemberRecord>? relationMembers = null;
        IntermediateSequenceStore<GenerationRestrictionRecord>? restrictions = null;
        IntermediateSequenceStore<GenerationRestrictionViaRecord>? restrictionVias = null;
        IntermediateSequenceStore<NodeIncidenceRecord>? incidences = null;
        CompactOsmMetadataStore? metadata = null;
        NodeIncidenceIndex? incidenceIndex = null;
        try
        {
            nodes = CreateStore<GenerationNodeRecord>(
                root,
                "canonical-nodes",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            ways = CreateStore<GenerationWayRecord>(
                root,
                "canonical-ways",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            wayNodeReferences = CreateStore<GenerationWayNodeReference>(
                root,
                "canonical-way-nodes",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            relations = CreateStore<GenerationRelationRecord>(
                root,
                "canonical-relations",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            relationMembers = CreateStore<GenerationRelationMemberRecord>(
                root,
                "canonical-relation-members",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            restrictions = CreateStore<GenerationRestrictionRecord>(
                root,
                "canonical-restrictions",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            restrictionVias = CreateStore<GenerationRestrictionViaRecord>(
                root,
                "canonical-restriction-vias",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            incidences = CreateStore<NodeIncidenceRecord>(
                root,
                "node-incidence-input",
                options,
                storeMemoryBudget,
                storeScratchBudget);
            metadata = new CompactOsmMetadataStore(
                new IntermediateBlobStoreOptions(
                    root,
                    "canonical-metadata",
                    options.StorageMode,
                    storeMemoryBudget,
                    storeScratchBudget,
                    options.SegmentSizeBytes),
                storeMemoryBudget);

            var context = new BuildContext(
                ways,
                wayNodeReferences,
                relations,
                relationMembers,
                restrictions,
                restrictionVias,
                incidences,
                metadata);
            VisitPass(
                source,
                OsmPbfEntityPass.Ways,
                static (state, fileOrdinal) => new WayVisitor(state, fileOrdinal),
                context,
                cancellationToken);
            VisitPass(
                source,
                OsmPbfEntityPass.Relations,
                static (state, fileOrdinal) => new RelationVisitor(state, fileOrdinal),
                context,
                cancellationToken);

            await ways.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await wayNodeReferences.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await relations.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await relationMembers.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await restrictions.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await restrictionVias.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await incidences.CompleteAsync(cancellationToken).ConfigureAwait(false);

            incidenceIndex = await NodeIncidenceIndex.BuildAsync(
                    incidences,
                    new NodeIncidenceIndexOptions(
                        root,
                        options.StorageMode,
                        indexMemoryBudget,
                        indexScratchBudget,
                        options.SegmentSizeBytes),
                    cancellationToken)
                .ConfigureAwait(false);

            context.AttachNodeOutput(nodes, incidenceIndex);
            VisitPass(
                source,
                OsmPbfEntityPass.Nodes,
                static (state, _) => new NodeVisitor(state),
                context,
                cancellationToken);

            await nodes.CompleteAsync(cancellationToken).ConfigureAwait(false);
            await metadata.CompleteAsync(cancellationToken).ConfigureAwait(false);

            var result = new CompactOsmSemanticStore(
                nodes,
                ways,
                wayNodeReferences,
                relations,
                relationMembers,
                restrictions,
                restrictionVias,
                incidences,
                metadata,
                incidenceIndex);
            nodes = null;
            ways = null;
            wayNodeReferences = null;
            relations = null;
            relationMembers = null;
            restrictions = null;
            restrictionVias = null;
            incidences = null;
            metadata = null;
            incidenceIndex = null;
            return result;
        }
        catch
        {
            incidenceIndex?.Dispose();
            metadata?.Dispose();
            incidences?.Dispose();
            restrictionVias?.Dispose();
            restrictions?.Dispose();
            relationMembers?.Dispose();
            relations?.Dispose();
            wayNodeReferences?.Dispose();
            ways?.Dispose();
            nodes?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        incidenceIndex.Dispose();
        metadata.Dispose();
        incidenceInput.Dispose();
        restrictionVias.Dispose();
        restrictions.Dispose();
        relationMembers.Dispose();
        relations.Dispose();
        wayNodeReferences.Dispose();
        ways.Dispose();
        nodes.Dispose();
        disposed = true;
    }

    private static void VisitPass(
        IOsmPbfEntitySource source,
        OsmPbfEntityPass pass,
        Func<BuildContext, int, IOsmPbfVisitor> createVisitor,
        BuildContext context,
        CancellationToken cancellationToken)
    {
        for (var fileOrdinal = 0; fileOrdinal < source.FileCount; fileOrdinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            source.VisitFile(
                fileOrdinal,
                pass,
                createVisitor(context, fileOrdinal),
                cancellationToken);
        }
    }

    private static IntermediateSequenceStore<T> CreateStore<T>(
        string root,
        string name,
        CompactOsmSemanticStoreOptions options,
        long memoryBudget,
        long scratchBudget)
        where T : unmanaged =>
        new(
            new IntermediateSequenceStoreOptions(
                root,
                name,
                options.StorageMode,
                memoryBudget,
                scratchBudget,
                options.SegmentSizeBytes));

    private static long ReadCount<T>(IntermediateSequenceStore<T> store)
        where T : unmanaged =>
        store.State.RecordCount;

    private static T Read<T>(IntermediateSequenceStore<T> store, long ordinal)
        where T : unmanaged =>
        store.Read(ordinal);

    private static void ValidateOptions(CompactOsmSemanticStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MemoryBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.ScratchDiskBudgetBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.SegmentSizeBytes);

        int largestRecord = new[]
        {
            Unsafe.SizeOf<GenerationNodeRecord>(),
            Unsafe.SizeOf<GenerationWayRecord>(),
            Unsafe.SizeOf<GenerationWayNodeReference>(),
            Unsafe.SizeOf<GenerationRelationRecord>(),
            Unsafe.SizeOf<GenerationRelationMemberRecord>(),
            Unsafe.SizeOf<GenerationRestrictionRecord>(),
            Unsafe.SizeOf<GenerationRestrictionViaRecord>(),
            Unsafe.SizeOf<NodeIncidenceRecord>(),
        }.Max();
        if (options.MemoryBudgetBytes / StorePartitionCount < largestRecord)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The compact OSM semantic-store memory budget cannot fit one record per store.");
        }

        if (options.ScratchDiskBudgetBytes / StorePartitionCount < largestRecord)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The compact OSM semantic-store scratch budget cannot fit one record per store.");
        }
    }

    private sealed class BuildContext(
        IntermediateSequenceStore<GenerationWayRecord> ways,
        IntermediateSequenceStore<GenerationWayNodeReference> wayNodeReferences,
        IntermediateSequenceStore<GenerationRelationRecord> relations,
        IntermediateSequenceStore<GenerationRelationMemberRecord> relationMembers,
        IntermediateSequenceStore<GenerationRestrictionRecord> restrictions,
        IntermediateSequenceStore<GenerationRestrictionViaRecord> restrictionVias,
        IntermediateSequenceStore<NodeIncidenceRecord> incidences,
        CompactOsmMetadataStore metadata)
    {
        private readonly IReadOnlyDictionary<string, string> emptyNodeTags =
            OsmNodeSemanticTransformer.CreateEmptyTransformedTags();
        private long wayOrdinal;
        private long wayNodeOrdinal;
        private long relationOrdinal;
        private long relationMemberOrdinal;
        private long restrictionOrdinal;
        private long restrictionViaOrdinal;
        private long incidenceOrdinal;
        private long? emptyNodeTagReference;
        private IntermediateSequenceStore<GenerationNodeRecord>? nodes;
        private NodeIncidenceIndex? incidenceIndex;

        internal void AttachNodeOutput(
            IntermediateSequenceStore<GenerationNodeRecord> nodeOutput,
            NodeIncidenceIndex index)
        {
            nodes = nodeOutput;
            incidenceIndex = index;
        }

        internal void AddWay(
            int fileOrdinal,
            ulong osmWayId,
            ReadOnlySpan<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> rawTags)
        {
            if (!OsmWaySemanticTransformer.TryTransform(
                    nodeRefs,
                    rawTags,
                    out IReadOnlyDictionary<string, string>? tags))
            {
                return;
            }

            long wayId = checked((long)osmWayId);
            long nodeOffset = wayNodeOrdinal;
            long wayCanonicalOrdinal = wayOrdinal++;
            long tagReference = metadata.AppendTags(tags);
            ways.Append(
                new GenerationWayRecord(
                    wayId,
                    nodeOffset,
                    nodeRefs.Length,
                    tagReference,
                    wayCanonicalOrdinal));

            for (var nodeOrdinal = 0; nodeOrdinal < nodeRefs.Length; nodeOrdinal++)
            {
                long nodeId = checked((long)nodeRefs[nodeOrdinal]);
                long nodeCanonicalOrdinal = wayNodeOrdinal++;
                wayNodeReferences.Append(
                    new GenerationWayNodeReference(
                        nodeId,
                        wayId,
                        nodeOrdinal,
                        nodeCanonicalOrdinal));

                NodeIncidenceRole roles = nodeOrdinal switch
                {
                    0 => NodeIncidenceRole.WayStart,
                    _ when nodeOrdinal == nodeRefs.Length - 1 => NodeIncidenceRole.WayEnd,
                    _ => NodeIncidenceRole.WayIntermediate,
                };
                incidences.Append(
                    new NodeIncidenceRecord(
                        nodeId,
                        wayId,
                        fileOrdinal,
                        nodeOrdinal,
                        roles,
                        incidenceOrdinal++));
            }
        }

        internal void AddRelation(
            int fileOrdinal,
            ulong osmRelationId,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> rawTags)
        {
            if (rawTags.Count == 0 ||
                !OsmRelationSemanticTransformer.TryNormalizeRestrictionTags(
                    rawTags,
                    out IReadOnlyDictionary<string, string> tags))
            {
                return;
            }

            long relationId = checked((long)osmRelationId);
            long memberOffset = relationMemberOrdinal;
            long relationCanonicalOrdinal = relationOrdinal++;
            long tagReference = metadata.AppendTags(tags);
            relations.Append(
                new GenerationRelationRecord(
                    relationId,
                    memberOffset,
                    members.Count,
                    tagReference,
                    relationCanonicalOrdinal));

            bool restriction = IsRestriction(tags);
            if (restriction &&
                TryGetRestrictionStructure(
                    members,
                    out long fromWayId,
                    out long toWayId,
                    out int viaCount))
            {
                long viaOffset = restrictionViaOrdinal;
                var viaOrdinal = 0;
                foreach (OsmRelationMember member in members)
                {
                    if (!string.Equals(member.Role, "via", StringComparison.Ordinal) ||
                        member.Type is not (OsmMemberType.Node or OsmMemberType.Way))
                    {
                        continue;
                    }

                    restrictionVias.Append(
                        new GenerationRestrictionViaRecord(
                            relationId,
                            checked((long)member.Id),
                            member.Type,
                            viaOrdinal++,
                            restrictionViaOrdinal++));
                }

                restrictions.Append(
                    new GenerationRestrictionRecord(
                        relationId,
                        fromWayId,
                        toWayId,
                        viaOffset,
                        viaCount,
                        tagReference,
                        restrictionOrdinal++));
            }

            for (var memberOrdinal = 0; memberOrdinal < members.Count; memberOrdinal++)
            {
                OsmRelationMember member = members[memberOrdinal];
                long memberId = checked((long)member.Id);
                long memberCanonicalOrdinal = relationMemberOrdinal++;
                long roleReference = metadata.AppendString(member.Role);
                relationMembers.Append(
                    new GenerationRelationMemberRecord(
                        relationId,
                        memberId,
                        member.Type,
                        roleReference,
                        memberOrdinal,
                        memberCanonicalOrdinal));

                if (member.Type != OsmMemberType.Node)
                {
                    continue;
                }

                NodeIncidenceRole roles = NodeIncidenceRole.RelationMember;
                if (restriction &&
                    string.Equals(member.Role, "via", StringComparison.Ordinal))
                {
                    roles |= NodeIncidenceRole.RestrictionViaNode;
                }

                incidences.Append(
                    new NodeIncidenceRecord(
                        memberId,
                        relationId,
                        fileOrdinal,
                        memberOrdinal,
                        roles,
                        incidenceOrdinal++));
            }
        }

        internal void AddNode(
            ulong osmNodeId,
            double latitude,
            double longitude,
            IReadOnlyDictionary<string, string> rawTags,
            ref long summaryOrdinal,
            ref bool summaryInitialized)
        {
            long nodeId = checked((long)osmNodeId);
            if (incidenceIndex is null || nodes is null)
            {
                throw new InvalidOperationException(
                    "The compact OSM node output is not initialized.");
            }

            if (!summaryInitialized)
            {
                summaryOrdinal = incidenceIndex.FindSummaryOrdinalAtOrAfter(nodeId);
                summaryInitialized = true;
            }

            while (summaryOrdinal < incidenceIndex.SummaryCount &&
                   incidenceIndex.ReadSummary(summaryOrdinal).OsmNodeId < nodeId)
            {
                summaryOrdinal++;
            }

            if (summaryOrdinal >= incidenceIndex.SummaryCount ||
                incidenceIndex.ReadSummary(summaryOrdinal).OsmNodeId != nodeId)
            {
                return;
            }

            IReadOnlyDictionary<string, string> tags =
                OsmNodeSemanticTransformer.Transform(rawTags, emptyNodeTags);
            long tagReference;
            if (rawTags.Count == 0)
            {
                emptyNodeTagReference ??= metadata.AppendTags(tags);
                tagReference = emptyNodeTagReference.Value;
            }
            else
            {
                tagReference = metadata.AppendTags(tags);
            }

            nodes.Append(
                new GenerationNodeRecord(
                    nodeId,
                    ToE7(latitude, nameof(latitude)),
                    ToE7(longitude, nameof(longitude)),
                    GetNodeFlags(rawTags, tags),
                    tagReference));
        }

        private static bool TryGetRestrictionStructure(
            IReadOnlyList<OsmRelationMember> members,
            out long fromWayId,
            out long toWayId,
            out int viaCount)
        {
            fromWayId = 0;
            toWayId = 0;
            viaCount = 0;
            OsmMemberType? viaType = null;

            foreach (OsmRelationMember member in members)
            {
                if (string.Equals(member.Role, "from", StringComparison.Ordinal) &&
                    member.Type == OsmMemberType.Way)
                {
                    fromWayId = checked((long)member.Id);
                    continue;
                }

                if (string.Equals(member.Role, "to", StringComparison.Ordinal) &&
                    member.Type == OsmMemberType.Way)
                {
                    if (toWayId == 0)
                    {
                        toWayId = checked((long)member.Id);
                    }

                    continue;
                }

                if (!string.Equals(member.Role, "via", StringComparison.Ordinal) ||
                    member.Type is not (OsmMemberType.Node or OsmMemberType.Way))
                {
                    continue;
                }

                if ((viaType.HasValue && viaType.Value != member.Type) ||
                    (member.Type == OsmMemberType.Node && viaCount != 0))
                {
                    return false;
                }

                viaType = member.Type;
                viaCount++;
                if (viaCount > OSMRestriction.MaxViasPerRestriction)
                {
                    return false;
                }
            }

            return fromWayId != 0 && toWayId != 0 && viaCount != 0;
        }

        private static bool IsRestriction(IReadOnlyDictionary<string, string> tags)
        {
            foreach (string key in tags.Keys)
            {
                if (key.Equals("restriction", StringComparison.Ordinal) ||
                    key.StartsWith("restriction:", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private static int ToE7(double value, string parameterName)
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(parameterName);
            }

            return checked(
                (int)Math.Round(
                    value * 10_000_000d,
                    MidpointRounding.AwayFromZero));
        }

        private static NodeSemanticFlags GetNodeFlags(
            IReadOnlyDictionary<string, string> rawTags,
            IReadOnlyDictionary<string, string> tags)
        {
            NodeSemanticFlags flags = NodeSemanticFlags.None;
            if (tags.TryGetValue("highway", out string? highway))
            {
                flags |= highway switch
                {
                    "traffic_signals" => NodeSemanticFlags.TrafficSignal,
                    "stop" => NodeSemanticFlags.StopSign,
                    "give_way" => NodeSemanticFlags.YieldSign,
                    _ => NodeSemanticFlags.None,
                };
            }

            if (tags.TryGetValue("barrier", out string? barrier) &&
                barrier.Length != 0)
            {
                flags |= NodeSemanticFlags.Barrier;
                if (barrier is "gate" or "lift_gate" or "swing_gate")
                {
                    flags |= NodeSemanticFlags.Gate;
                }
            }

            if (HasAccessTransition(rawTags) ||
                (tags.TryGetValue("access_mask", out string? accessMask) &&
                 accessMask != "2047"))
            {
                flags |= NodeSemanticFlags.AccessTransition;
            }

            return flags;
        }

        private static bool HasAccessTransition(IReadOnlyDictionary<string, string> tags) =>
            tags.ContainsKey("access") ||
            tags.ContainsKey("vehicle") ||
            tags.ContainsKey("motor_vehicle") ||
            tags.ContainsKey("motorcar") ||
            tags.ContainsKey("hgv") ||
            tags.ContainsKey("bus") ||
            tags.ContainsKey("bicycle") ||
            tags.ContainsKey("foot");
    }

    private sealed class WayVisitor(BuildContext context, int fileOrdinal)
        : IOsmPbfSpanVisitor
    {
        public void Header(
            double? minLat,
            double? minLon,
            double? maxLat,
            double? maxLon,
            IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(
            ulong id,
            double lat,
            double lon,
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
            if (nodeRefs is ulong[] array)
            {
                context.AddWay(fileOrdinal, id, array, tags);
                return;
            }

            if (nodeRefs is List<ulong> list)
            {
                context.AddWay(fileOrdinal, id, CollectionsMarshal.AsSpan(list), tags);
                return;
            }

            context.AddWay(fileOrdinal, id, nodeRefs.ToArray(), tags);
        }

        public void Way(
            ulong id,
            ReadOnlySpan<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags) =>
            context.AddWay(fileOrdinal, id, nodeRefs, tags);

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags)
        {
        }
    }

    private sealed class RelationVisitor(BuildContext context, int fileOrdinal)
        : IOsmPbfVisitor
    {
        public void Header(
            double? minLat,
            double? minLon,
            double? maxLat,
            double? maxLon,
            IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(
            ulong id,
            double lat,
            double lon,
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags) =>
            context.AddRelation(fileOrdinal, id, members, tags);
    }

    private sealed class NodeVisitor(BuildContext context) : IOsmPbfVisitor
    {
        private long summaryOrdinal;
        private ulong lastNodeId;
        private bool hasLastNode;
        private bool summaryInitialized;

        public void Header(
            double? minLat,
            double? minLon,
            double? maxLat,
            double? maxLon,
            IReadOnlyList<string> requiredFeatures)
        {
        }

        public void Node(
            ulong id,
            double lat,
            double lon,
            IReadOnlyDictionary<string, string> tags)
        {
            if (hasLastNode && id < lastNodeId)
            {
                throw new InvalidDataException(
                    "The compact OSM node pass requires canonical node-id order per source file.");
            }

            lastNodeId = id;
            hasLastNode = true;
            context.AddNode(
                id,
                lat,
                lon,
                tags,
                ref summaryOrdinal,
                ref summaryInitialized);
        }

        public void Way(
            ulong id,
            IReadOnlyList<ulong> nodeRefs,
            IReadOnlyDictionary<string, string> tags)
        {
        }

        public void Relation(
            ulong id,
            IReadOnlyList<OsmRelationMember> members,
            IReadOnlyDictionary<string, string> tags)
        {
        }
    }
}

internal sealed class CompactOsmMetadataStore : IDisposable
{
    private const byte TagDictionaryKind = 1;
    private const byte StringKind = 2;
    private readonly IntermediateBlobStore store;
    private readonly int maximumPayloadBytes;
    private bool disposed;

    internal CompactOsmMetadataStore(
        IntermediateBlobStoreOptions options,
        long maximumPayloadBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumPayloadBytes);
        store = new IntermediateBlobStore(options);
        this.maximumPayloadBytes = checked((int)Math.Min(maximumPayloadBytes, int.MaxValue));
    }

    internal long CurrentMemoryBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return store.State.CurrentMemoryBytes;
        }
    }

    internal long PeakMemoryBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return store.State.PeakMemoryBytes;
        }
    }

    internal long CurrentScratchBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return store.State.CurrentScratchBytes;
        }
    }

    internal long ScratchHighWaterMarkBytes
    {
        get
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return store.State.ScratchHighWaterMarkBytes;
        }
    }


    internal long AppendTags(IReadOnlyDictionary<string, string> tags)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count > maximumPayloadBytes / 8)
        {
            throw new ValhallaGenerationResourceLimitException(
                "The transformed OSM tag count exceeds the metadata payload budget.");
        }

        KeyValuePair<string, string>[] entries =
            ArrayPool<KeyValuePair<string, string>>.Shared.Rent(Math.Max(1, tags.Count));
        try
        {
            var index = 0;
            foreach (KeyValuePair<string, string> tag in tags)
            {
                entries[index++] = tag;
            }

            Array.Sort(
                entries,
                0,
                tags.Count,
                KeyValuePairOrdinalComparer.Instance);

            int payloadLength = checked(1 + sizeof(int));
            for (var entryIndex = 0; entryIndex < tags.Count; entryIndex++)
            {
                payloadLength = checked(
                    payloadLength +
                    sizeof(int) +
                    Encoding.UTF8.GetByteCount(entries[entryIndex].Key) +
                    sizeof(int) +
                    Encoding.UTF8.GetByteCount(entries[entryIndex].Value));
            }

            return AppendPayload(
                TagDictionaryKind,
                payloadLength,
                span =>
                {
                    int offset = 1;
                    BinaryPrimitives.WriteInt32LittleEndian(
                        span.Slice(offset, sizeof(int)),
                        tags.Count);
                    offset += sizeof(int);
                    for (var entryIndex = 0; entryIndex < tags.Count; entryIndex++)
                    {
                        offset = WriteString(span, offset, entries[entryIndex].Key);
                        offset = WriteString(span, offset, entries[entryIndex].Value);
                    }
                });
        }
        finally
        {
            Array.Clear(entries, 0, tags.Count);
            ArrayPool<KeyValuePair<string, string>>.Shared.Return(entries);
        }
    }

    internal long AppendString(string value)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(value);
        int payloadLength = checked(
            1 +
            sizeof(int) +
            Encoding.UTF8.GetByteCount(value));
        return AppendPayload(
            StringKind,
            payloadLength,
            span => WriteString(span, 1, value));
    }

    internal IReadOnlyDictionary<string, string> ReadTags(long reference)
    {
        byte[] payload = ReadPayload(reference, TagDictionaryKind);
        ReadOnlySpan<byte> span = payload;
        int offset = 1;
        int count = ReadInt32(span, ref offset);
        if (count < 0 || count > payload.Length / 8)
        {
            throw new InvalidDataException("The compact OSM tag count is invalid.");
        }

        var tags = new Dictionary<string, string>(count, StringComparer.Ordinal);
        for (var index = 0; index < count; index++)
        {
            string key = ReadString(span, ref offset);
            string value = ReadString(span, ref offset);
            if (!tags.TryAdd(key, value))
            {
                throw new InvalidDataException(
                    $"The compact OSM metadata contains duplicate tag '{key}'.");
            }
        }

        if (offset != span.Length)
        {
            throw new InvalidDataException("The compact OSM tag payload has trailing bytes.");
        }

        return tags;
    }

    internal string ReadString(long reference)
    {
        byte[] payload = ReadPayload(reference, StringKind);
        ReadOnlySpan<byte> span = payload;
        int offset = 1;
        string value = ReadString(span, ref offset);
        if (offset != span.Length)
        {
            throw new InvalidDataException("The compact OSM string payload has trailing bytes.");
        }

        return value;
    }

    internal ValueTask<IntermediateBlobManifest> CompleteAsync(
        CancellationToken cancellationToken) =>
        store.CompleteAsync(cancellationToken);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        store.Dispose();
        disposed = true;
    }

    private long AppendPayload(
        byte kind,
        int payloadLength,
        Action<Span<byte>> writePayload)
    {
        if (payloadLength <= 0 || payloadLength > maximumPayloadBytes)
        {
            throw new ValhallaGenerationResourceLimitException(
                $"The compact OSM metadata payload requires {payloadLength} bytes, " +
                $"exceeding its {maximumPayloadBytes}-byte budget.");
        }

        int recordLength = checked(sizeof(int) + payloadLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(recordLength);
        try
        {
            Span<byte> span = buffer.AsSpan(0, recordLength);
            BinaryPrimitives.WriteInt32LittleEndian(span, payloadLength);
            Span<byte> payload = span[sizeof(int)..];
            payload[0] = kind;
            writePayload(payload);
            return store.Append(span).Offset;
        }
        finally
        {
            Array.Clear(buffer, 0, recordLength);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private byte[] ReadPayload(long reference, byte expectedKind)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (reference < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reference));
        }

        Span<byte> header = stackalloc byte[sizeof(int)];
        store.ReadRange(reference, header);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (payloadLength <= 0 || payloadLength > maximumPayloadBytes)
        {
            throw new InvalidDataException(
                $"The compact OSM metadata payload length {payloadLength} is invalid.");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        store.ReadRange(reference + sizeof(int), payload);
        if (payload[0] != expectedKind)
        {
            throw new InvalidDataException(
                $"Compact OSM metadata kind {payload[0]} does not match expected kind {expectedKind}.");
        }

        return payload;
    }

    private static int WriteString(Span<byte> destination, int offset, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        BinaryPrimitives.WriteInt32LittleEndian(
            destination.Slice(offset, sizeof(int)),
            byteCount);
        offset += sizeof(int);
        int written = Encoding.UTF8.GetBytes(value, destination[offset..]);
        return checked(offset + written);
    }

    private static int ReadInt32(ReadOnlySpan<byte> source, ref int offset)
    {
        if (offset < 0 || source.Length - offset < sizeof(int))
        {
            throw new InvalidDataException("The compact OSM metadata payload is truncated.");
        }

        int value = BinaryPrimitives.ReadInt32LittleEndian(
            source.Slice(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static string ReadString(ReadOnlySpan<byte> source, ref int offset)
    {
        int byteCount = ReadInt32(source, ref offset);
        if (byteCount < 0 || source.Length - offset < byteCount)
        {
            throw new InvalidDataException("The compact OSM string payload is truncated.");
        }

        string value = Encoding.UTF8.GetString(source.Slice(offset, byteCount));
        offset += byteCount;
        return value;
    }

    private sealed class KeyValuePairOrdinalComparer
        : IComparer<KeyValuePair<string, string>>
    {
        internal static KeyValuePairOrdinalComparer Instance { get; } = new();

        public int Compare(
            KeyValuePair<string, string> left,
            KeyValuePair<string, string> right)
        {
            int keyComparison = StringComparer.Ordinal.Compare(left.Key, right.Key);
            return keyComparison != 0
                ? keyComparison
                : StringComparer.Ordinal.Compare(left.Value, right.Value);
        }
    }
}
