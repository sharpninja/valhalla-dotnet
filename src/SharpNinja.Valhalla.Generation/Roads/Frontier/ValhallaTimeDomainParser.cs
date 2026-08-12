using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Generation.Roads.Frontier;

/// <summary>
/// Generation-owned access to the shared Valhalla 3.8.3 conditional-time parser.
/// </summary>
internal static class ValhallaTimeDomainParser
{
    internal static IReadOnlyList<ulong> Parse(string expression) =>
        OsmConditionalTimeDomainParser.Parse(expression);
}

