// Faithful C# port of Valhalla baldr graphtileptr.h (valhalla @ 3.7.0).
// Source: F:/github/valhalla/valhalla/baldr/graphtileptr.h
//
// In C++ this header just type-aliases a reference-counted pointer to a const
// GraphTile (either std::shared_ptr<const GraphTile> when thread-safe ref-counting
// is enabled, or boost::intrusive_ptr<const GraphTile> otherwise). The ubiquitous
// alias is `graph_tile_ptr`.
//
// PORT-NOTE: In C# the GC and ordinary reference semantics replace both shared_ptr
// and boost::intrusive_ptr; there is no need to reproduce the ref-counting machinery.
// `GraphTilePtr` is therefore a plain nullable reference alias to the (forthcoming)
// GraphTile class. The two build-time variants (ENABLE_THREAD_SAFE_TILE_REF_COUNT)
// collapse to the same managed reference type. The GraphTile class itself is part of a
// later baldr port slice; this alias is provided now so dependent signatures can refer
// to it without re-introducing the C++ pointer types.

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Managed equivalent of the C++ <c>graph_tile_ptr</c> alias (a ref-counted pointer to a
/// const <c>GraphTile</c>). Use <c>GraphTilePtr?</c> for a possibly-null tile reference.
/// </summary>
/// <remarks>
/// PORT-NOTE: This is a placeholder marker interface. The C++ alias points at
/// <c>const GraphTile</c>; the concrete <c>GraphTile</c> type belongs to a later slice of
/// the baldr port. The alias is intentionally minimal so the on-disk tile layout types
/// (GraphTileHeader, etc.) ported here do not depend on the full tile reader.
/// </remarks>
public interface IGraphTilePtr
{
}
