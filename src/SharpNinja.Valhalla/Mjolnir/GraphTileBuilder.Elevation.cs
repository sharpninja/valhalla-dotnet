using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Elevation payload applied to one existing edge-info record during the Mjolnir elevation stage.
/// </summary>
public sealed record EdgeInfoElevationData(
    float MeanElevation,
    IReadOnlyList<sbyte> EncodedElevation);

public sealed partial class GraphTileBuilder
{
    /// <summary>
    /// Rewrites elevation payloads and remaps every directed-edge edge-info offset after the
    /// variable-length encoded elevations change record sizes.
    /// </summary>
    /// <remarks>
    /// This is the managed equivalent of the offset-remap portion of Valhalla 3.8.3
    /// <c>ElevationBuilder::Build</c>. The builder must have been constructed from an existing
    /// <c>GraphTile</c>.
    /// </remarks>
    public int ApplyElevationData(
        IReadOnlyDictionary<uint, EdgeInfoElevationData> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (_sourceTile is null)
        {
            throw new InvalidOperationException(
                "Elevation data can only be applied to a deserialized graph tile");
        }

        uint[] originalOffsets = _directedEdgesBuilder
            .Select(edge => checked((uint)edge.EdgeInfoOffset))
            .Distinct()
            .Order()
            .ToArray();
        if (originalOffsets.Length != _edgeinfoList.Count)
        {
            throw new InvalidDataException(
                "Directed-edge and edge-info record counts are inconsistent");
        }

        var remappedOffsets = new Dictionary<uint, uint>(originalOffsets.Length);
        uint nextOffset = 0;
        int applied = 0;
        for (int index = 0; index < originalOffsets.Length; index++)
        {
            uint originalOffset = originalOffsets[index];
            EdgeInfoBuilder edgeInfo = _edgeinfoList[index];
            if (updates.TryGetValue(originalOffset, out EdgeInfoElevationData? update))
            {
                edgeInfo.SetMeanElevation(update.MeanElevation);
                edgeInfo.SetEncodedElevation(update.EncodedElevation);
                applied++;
            }

            remappedOffsets.Add(originalOffset, nextOffset);
            nextOffset = checked(nextOffset + (uint)edgeInfo.SizeOf());
        }

        for (int index = 0; index < _directedEdgesBuilder.Count; index++)
        {
            DirectedEdge edge = _directedEdgesBuilder[index];
            uint originalOffset = checked((uint)edge.EdgeInfoOffset);
            edge.SetEdgeInfoOffset(remappedOffsets[originalOffset]);
            _directedEdgesBuilder[index] = edge;
        }

        _edgeInfoOffset = nextOffset;
        _headerBuilder.SetHasElevation(true);
        return applied;
    }
}
