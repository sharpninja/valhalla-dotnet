namespace SharpNinja.Valhalla.Traffic.Routing;

public sealed record GraphTileLaneTopologyIndexOptions(
    int MaximumGraphSignatures = 8,
    int MaximumDirectedEdgesPerGraph = 4_096,
    int MaximumTiles = 512,
    int MaximumTransitionContexts = 4_096,
    int MaximumConcurrentBuilds = 8,
    int MaximumOverlaySnapshots = 256)
{
    public static GraphTileLaneTopologyIndexOptions Default { get; } = new();

    internal void Validate()
    {
        ValidatePositive(MaximumGraphSignatures, nameof(MaximumGraphSignatures));
        ValidatePositive(
            MaximumDirectedEdgesPerGraph,
            nameof(MaximumDirectedEdgesPerGraph));
        ValidatePositive(MaximumTiles, nameof(MaximumTiles));
        ValidatePositive(
            MaximumTransitionContexts,
            nameof(MaximumTransitionContexts));
        ValidatePositive(
            MaximumConcurrentBuilds,
            nameof(MaximumConcurrentBuilds));
        ValidatePositive(
            MaximumOverlaySnapshots,
            nameof(MaximumOverlaySnapshots));
    }

    private static void ValidatePositive(int value, string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Cache and admission limits must be greater than zero.");
        }
    }
}
