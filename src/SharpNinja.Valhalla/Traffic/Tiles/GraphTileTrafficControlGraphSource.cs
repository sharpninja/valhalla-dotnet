using System.Globalization;
using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Traffic.Tiles;

/// <summary>
/// Reads traffic-control flags directly from Valhalla graph tiles. The scan is
/// UI-agnostic and cancellable; <see cref="ValhallaTrafficControlIndex"/> owns
/// signature-based snapshot caching.
/// </summary>
public sealed class GraphTileTrafficControlGraphSource : IValhallaTrafficControlGraphSource
{
    public Task<IReadOnlyList<TrafficControlGraphEdge>> ReadAsync(
        ValhallaGraphTrafficContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GraphTileDirectory is null)
        {
            throw new ArgumentException(
                "GraphTileDirectory is required to read graph traffic controls.",
                nameof(context));
        }

        if (!Directory.Exists(context.GraphTileDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Valhalla graph tile directory '{context.GraphTileDirectory}' does not exist.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run<IReadOnlyList<TrafficControlGraphEdge>>(
            () => ReadCore(context.GraphTileDirectory, cancellationToken),
            cancellationToken);
    }

    private static IReadOnlyList<TrafficControlGraphEdge> ReadCore(
        string tileDirectory,
        CancellationToken cancellationToken)
    {
        var controls = new List<TrafficControlGraphEdge>();
        foreach (string file in Directory
                     .EnumerateFiles(tileDirectory, "*.gph", SearchOption.AllDirectories)
                     .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseTileId(tileDirectory, file, out uint tileId, out uint level))
            {
                continue;
            }

            GraphTile? tile = GraphTile.Create(tileDirectory, new GraphId(tileId, level, 0));
            if (tile is null)
            {
                continue;
            }

            for (uint nodeIndex = 0; nodeIndex < tile.Header().Nodecount(); nodeIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                NodeInfo node = tile.Node((int)nodeIndex);
                ulong fromNodeId = new GraphId(tileId, level, nodeIndex).Value;
                for (uint localEdgeIndex = 0; localEdgeIndex < node.EdgeCount; localEdgeIndex++)
                {
                    DirectedEdge edge = tile.DirectedEdge((int)(node.EdgeIndex + localEdgeIndex));
                    if (!edge.TrafficSignal && !edge.StopSign && !edge.YieldSign)
                    {
                        continue;
                    }

                    ulong directedEdgeId =
                        new GraphId(tileId, level, node.EdgeIndex + localEdgeIndex).Value;
                    controls.Add(new TrafficControlGraphEdge(
                        directedEdgeId,
                        fromNodeId,
                        edge.EndNode.Value,
                        edge.TrafficSignal,
                        edge.StopSign,
                        edge.YieldSign));
                }
            }
        }

        return Array.AsReadOnly(controls.ToArray());
    }

    private static bool TryParseTileId(
        string tileDirectory,
        string file,
        out uint tileId,
        out uint level)
    {
        tileId = 0;
        level = 0;
        string relativePath = Path.GetRelativePath(tileDirectory, file).Replace('\\', '/');
        string[] parts = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 ||
            !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out level) ||
            level > GraphId.MaxGraphHierarchy)
        {
            return false;
        }

        string digits = string.Concat(
            parts.Skip(1).Select(static part => Path.GetFileNameWithoutExtension(part)));
        return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out tileId);
    }
}
