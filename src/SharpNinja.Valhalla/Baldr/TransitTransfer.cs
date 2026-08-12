// Faithful packed C# port of Valhalla 3.8.3 baldr TransitTransfer.
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>Transit transfer information between two tile-local stops.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct TransitTransfer : IComparable<TransitTransfer>
{
    private readonly uint fromStopId_;
    private readonly uint toStopId_;
    private readonly uint word2_;

    /// <summary>Constructs a transit transfer record.</summary>
    public TransitTransfer(
        uint fromStopId,
        uint toStopId,
        TransferType type,
        uint minimumTime)
    {
        fromStopId_ = fromStopId;
        toStopId_ = toStopId;
        word2_ = (uint)type | (Math.Min(minimumTime, GraphConstants.MaxTransferTime) << 4);
    }

    /// <summary>Gets the source tile-local stop index.</summary>
    public uint FromStopId => fromStopId_;

    /// <summary>Gets the destination tile-local stop index.</summary>
    public uint ToStopId => toStopId_;

    /// <summary>Gets the transfer behavior.</summary>
    public TransferType Type => (TransferType)(word2_ & 0xF);

    /// <summary>Gets the minimum transfer time in seconds.</summary>
    public uint MinimumTime => (word2_ >> 4) & 0xFFFF;

    /// <inheritdoc />
    public int CompareTo(TransitTransfer other)
    {
        int comparison = FromStopId.CompareTo(other.FromStopId);
        return comparison != 0 ? comparison : ToStopId.CompareTo(other.ToStopId);
    }
}
