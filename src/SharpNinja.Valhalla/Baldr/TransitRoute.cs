// Faithful packed C# port of Valhalla 3.8.3 baldr TransitRoute.
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Transit route metadata stored as two colors and four packed 64-bit text-reference words.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TransitRoute
{
    private const ulong NameOffsetMask = (1ul << 24) - 1;

    private readonly uint routeColor_;
    private readonly uint routeTextColor_;
    private readonly ulong word0_;
    private readonly ulong word1_;
    private readonly ulong word2_;
    private readonly ulong word3_;

    /// <summary>Constructs a transit route record.</summary>
    public TransitRoute(
        TransitType routeType,
        uint oneStopOffset,
        uint operatedByOneStopIdOffset,
        uint operatedByNameOffset,
        uint operatedByWebsiteOffset,
        uint routeColor,
        uint routeTextColor,
        uint shortNameOffset,
        uint longNameOffset,
        uint descriptionOffset)
    {
        ValidateOffset(oneStopOffset, nameof(oneStopOffset));
        ValidateOffset(operatedByOneStopIdOffset, nameof(operatedByOneStopIdOffset));
        ValidateOffset(operatedByNameOffset, nameof(operatedByNameOffset));
        ValidateOffset(operatedByWebsiteOffset, nameof(operatedByWebsiteOffset));
        ValidateOffset(shortNameOffset, nameof(shortNameOffset));
        ValidateOffset(longNameOffset, nameof(longNameOffset));
        ValidateOffset(descriptionOffset, nameof(descriptionOffset));

        routeColor_ = routeColor;
        routeTextColor_ = routeTextColor;
        word0_ = (ulong)routeType | ((ulong)oneStopOffset << 8);
        word1_ = operatedByOneStopIdOffset | ((ulong)operatedByNameOffset << 24);
        word2_ = operatedByWebsiteOffset | ((ulong)shortNameOffset << 24);
        word3_ = longNameOffset | ((ulong)descriptionOffset << 24);
    }

    /// <summary>Gets the internal Valhalla transit mode.</summary>
    public TransitType RouteType => (TransitType)(word0_ & 0xFF);

    /// <summary>Gets the route OneStop identifier text-list offset.</summary>
    public uint OneStopOffset => (uint)((word0_ >> 8) & NameOffsetMask);

    /// <summary>Gets the operator OneStop identifier text-list offset.</summary>
    public uint OperatedByOneStopIdOffset => (uint)(word1_ & NameOffsetMask);

    /// <summary>Gets the operator name text-list offset.</summary>
    public uint OperatedByNameOffset => (uint)((word1_ >> 24) & NameOffsetMask);

    /// <summary>Gets the operator website text-list offset.</summary>
    public uint OperatedByWebsiteOffset => (uint)(word2_ & NameOffsetMask);

    /// <summary>Gets the route color.</summary>
    public uint RouteColor => routeColor_;

    /// <summary>Gets the route text color.</summary>
    public uint RouteTextColor => routeTextColor_;

    /// <summary>Gets the route short-name text-list offset.</summary>
    public uint ShortNameOffset => (uint)((word2_ >> 24) & NameOffsetMask);

    /// <summary>Gets the route long-name text-list offset.</summary>
    public uint LongNameOffset => (uint)(word3_ & NameOffsetMask);

    /// <summary>Gets the route description text-list offset.</summary>
    public uint DescriptionOffset => (uint)((word3_ >> 24) & NameOffsetMask);

    private static void ValidateOffset(uint value, string parameterName)
    {
        if (value > GraphConstants.MaxNameOffset)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
