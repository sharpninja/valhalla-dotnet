using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

internal interface IRestrictionTileMutation
{
    GraphTileHeader Header();

    NodeInfo NodeBuilder(int index);

    DirectedEdge DirectedEdgeBuilder(int index);

    void SetDirectedEdgeBuilder(int index, DirectedEdge edge);

    ulong EdgeInfoWayId(DirectedEdge edge);

    void AddForwardComplexRestriction(ComplexRestrictionBuilder restriction);

    void AddReverseComplexRestriction(ComplexRestrictionBuilder restriction);

    void StoreTileData(string tileDirectory, CancellationToken cancellationToken);
}

internal sealed class GraphTileBuilderRestrictionTileMutation
    : IRestrictionTileMutation
{
    private readonly GraphTileBuilder builder;

    internal GraphTileBuilderRestrictionTileMutation(GraphTile tile)
    {
        builder = new GraphTileBuilder(tile);
    }

    public GraphTileHeader Header() => builder.Header();

    public NodeInfo NodeBuilder(int index) => builder.NodeBuilder(index);

    public DirectedEdge DirectedEdgeBuilder(int index) =>
        builder.DirectedEdgeBuilder(index);

    public void SetDirectedEdgeBuilder(int index, DirectedEdge edge) =>
        builder.SetDirectedEdgeBuilder(index, edge);

    public ulong EdgeInfoWayId(DirectedEdge edge) =>
        builder.EdgeInfoFor(edge).WayId;

    public void AddForwardComplexRestriction(
        ComplexRestrictionBuilder restriction) =>
        builder.AddForwardComplexRestriction(restriction);

    public void AddReverseComplexRestriction(
        ComplexRestrictionBuilder restriction) =>
        builder.AddReverseComplexRestriction(restriction);

    public void StoreTileData(
        string tileDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        builder.StoreTileData(tileDirectory);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
