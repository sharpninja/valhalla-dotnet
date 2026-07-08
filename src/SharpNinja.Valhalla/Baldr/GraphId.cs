// Faithful C# port of Valhalla baldr GraphId (valhalla @ 3.7.0).
// Source: valhalla/baldr/graphid.h (and the parts of src/baldr/graphid.cc that are routing-relevant).
// Identifier of a node or an edge within the tiled, hierarchical graph.
//
// Bit layout within the 64-bit value (matches C++ exactly):
//      3  bits for hierarchy level
//      22 bits for tile id
//      21 bits for id within the tile
// The remaining high bits are spare.
//
// PORT-NOTE: the C++ rapidjson json() method, the std::hash specialization, and the operator<<
//            stream output are omitted (json/rapidjson serialization is an excluded module).
//            The string-parsing constructor and to_string helper ARE ported as they are
//            routing-diagnostic helpers, not json serialization.

using System;
using System.Globalization;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Identifier of a node or an edge within the tiled, hierarchical graph. Includes the tile Id,
/// hierarchy level, and a unique identifier within the tile/level. Backed by a single 64-bit
/// value, exactly as in the C++ <c>struct GraphId</c> (8 bytes).
/// </summary>
public struct GraphId : IEquatable<GraphId>, IComparable<GraphId>
{
    /// <summary>Maximum of 8 (0-7) graph hierarchies are supported.</summary>
    public const uint MaxGraphHierarchy = 7;

    /// <summary>
    /// 46 bits are used for the non-spare part of a graph Id. Fill all of them.
    /// </summary>
    public const ulong InvalidGraphId = 0x3fffffffffffUL;

    /// <summary>Value used to increment an Id by 1.</summary>
    public const ulong IdIncrement = 1UL << 25;

    /// <summary>Single 64 bit value representing the graph id.</summary>
    public ulong Value;

    /// <summary>Default-equivalent factory producing an invalid GraphId (matches C++ default ctor).</summary>
    public static GraphId Invalid => new GraphId { Value = InvalidGraphId };

    /// <summary>
    /// Constructor from tileid, level, and id. Mirrors the C++ packing
    /// <c>level | (tileid &lt;&lt; 3) | (static_cast&lt;uint64_t&gt;(id) &lt;&lt; 25)</c>.
    /// </summary>
    public GraphId(uint tileid, uint level, uint id)
    {
        if (tileid > GraphConstants.MaxGraphTileId)
        {
            throw new InvalidOperationException("Tile id out of valid range");
        }

        if (level > MaxGraphHierarchy)
        {
            throw new InvalidOperationException("Level out of valid range");
        }

        if (id > GraphConstants.MaxGraphId)
        {
            throw new InvalidOperationException("Id out of valid range");
        }

        Value = level | ((ulong)tileid << 3) | ((ulong)id << 25);
    }

    /// <summary>Constructor from a raw 64-bit value (validates the packed fields).</summary>
    public GraphId(ulong value)
    {
        Value = value;
        if (Tileid() > GraphConstants.MaxGraphTileId)
        {
            throw new InvalidOperationException("Tile id out of valid range");
        }

        if (Level() > MaxGraphHierarchy)
        {
            throw new InvalidOperationException("Level out of valid range");
        }

        if (Id() > GraphConstants.MaxGraphId)
        {
            throw new InvalidOperationException("Id out of valid range");
        }
    }

    /// <summary>Constructor from a string of the form level/tile_id/id.</summary>
    public GraphId(string value)
    {
        string[] parts = value.Split('/');
        if (parts.Length != 3)
        {
            throw new InvalidOperationException("Tile string format does not match level/tile/id");
        }

        uint level = uint.Parse(parts[0], CultureInfo.InvariantCulture);
        uint tileid = uint.Parse(parts[1], CultureInfo.InvariantCulture);
        uint id = uint.Parse(parts[2], CultureInfo.InvariantCulture);
        this = new GraphId(tileid, level, id);
    }

    /// <summary>Gets the tile Id.</summary>
    public uint Tileid() => (uint)((Value & 0x1fffff8UL) >> 3);

    /// <summary>Gets the hierarchy level.</summary>
    public uint Level() => (uint)(Value & 0x7UL);

    /// <summary>Gets the identifier within the hierarchy level.</summary>
    public uint Id() => (uint)((Value & 0x3ffffe000000UL) >> 25);

    /// <summary>Set the Id portion of the GraphId.</summary>
    public void SetId(uint id) => Value = (Value & 0x1ffffffUL) | (((ulong)id & 0x1fffffUL) << 25);

    /// <summary>Returns true if the id is valid.</summary>
    public bool IsValid() => Value != InvalidGraphId;

    /// <summary>Returns a GraphId omitting the id of the object within the level.</summary>
    public GraphId TileBase() => new GraphId(Value & 0x1ffffffUL);

    /// <summary>Returns a value indicating the tile (level and tile id) of the graph Id.</summary>
    public uint TileValue() => (uint)(Value & 0x1ffffffUL);

    /// <summary>Post-increment equivalent: advances this id by one and returns the prior value.</summary>
    public GraphId PostIncrement()
    {
        GraphId t = this;
        Value += IdIncrement;
        return t;
    }

    /// <summary>Pre-increment equivalent: advances this id by one and returns it.</summary>
    public GraphId PreIncrement()
    {
        Value += IdIncrement;
        return this;
    }

    /// <summary>Advances the id by an offset (operator+ in C++).</summary>
    public static GraphId operator +(GraphId lhs, ulong offset)
        => new GraphId(lhs.Tileid(), lhs.Level(), (uint)(lhs.Id() + offset));

    /// <summary>cache-friendly comparison: compares by level, then tileid, then id.</summary>
    public static bool CacheComparator(GraphId a, GraphId b)
    {
        if (a.Level() != b.Level())
        {
            return a.Level() < b.Level();
        }

        if (a.Tileid() != b.Tileid())
        {
            return a.Tileid() < b.Tileid();
        }

        return a.Id() < b.Id();
    }

    /// <inheritdoc/>
    public int CompareTo(GraphId other) => Value.CompareTo(other.Value);

    /// <inheritdoc/>
    public bool Equals(GraphId other) => Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is GraphId other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // Simplified version of murmur3 hash for 64 bit (matches the C++ std::hash specialization).
        ulong v = Value;
        v ^= v >> 33;
        v *= 0xff51afd7ed558ccdUL;
        v ^= v >> 33;
        v *= 0xc4ceb9fe1a85ec53UL;
        v ^= v >> 33;
        return (int)v;
    }

    /// <summary>Operator EqualTo.</summary>
    public static bool operator ==(GraphId lhs, GraphId rhs) => lhs.Value == rhs.Value;

    /// <summary>Operator not equal.</summary>
    public static bool operator !=(GraphId lhs, GraphId rhs) => lhs.Value != rhs.Value;

    /// <summary>Less than operator for sorting.</summary>
    public static bool operator <(GraphId lhs, GraphId rhs) => lhs.Value < rhs.Value;

    /// <summary>Greater than operator for sorting.</summary>
    public static bool operator >(GraphId lhs, GraphId rhs) => lhs.Value > rhs.Value;

    /// <summary>Less than or equal operator.</summary>
    public static bool operator <=(GraphId lhs, GraphId rhs) => lhs.Value <= rhs.Value;

    /// <summary>Greater than or equal operator.</summary>
    public static bool operator >=(GraphId lhs, GraphId rhs) => lhs.Value >= rhs.Value;

    /// <summary>cast operator to the raw 64-bit value.</summary>
    public static explicit operator ulong(GraphId id) => id.Value;

    /// <inheritdoc/>
    public override string ToString()
        => Level().ToString(CultureInfo.InvariantCulture) + "/" +
           Tileid().ToString(CultureInfo.InvariantCulture) + "/" +
           Id().ToString(CultureInfo.InvariantCulture);
}
