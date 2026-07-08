// Faithful C# port of Valhalla mjolnir UniqueNames.
// Source: valhalla/mjolnir/uniquenames.h @ 3.7.0
//
// Holds a list of unique names/strings and the indices into them. Index 0 is always
// the empty string (a dummy is inserted on construction so index 0 is never a real
// name). Used by OSMData for both node_names and name_offset_map.

using System.Collections.Generic;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Class to hold a list of unique names and indexes to them. Faithful port of the C++
/// <c>class UniqueNames</c> from <c>valhalla/mjolnir/uniquenames.h</c>.
/// </summary>
public sealed class UniqueNames
{
    // Map of name -> index.
    private readonly Dictionary<string, uint> _names = new();

    // List of names in index order (indexes_[i] is the name with index i).
    private readonly List<string> _indexes = new();

    /// <summary>Constructs the list, inserting the empty string so index 0 is never used.</summary>
    public UniqueNames()
    {
        // Insert dummy so index 0 is never used.
        Index(string.Empty);
    }

    /// <summary>
    /// Gets an index for the specified name. If the name is not already used it is added
    /// to the name map. Faithful to <c>UniqueNames::index</c>.
    /// </summary>
    public uint Index(string name)
    {
        if (_names.TryGetValue(name, out uint existing))
        {
            return existing;
        }

        uint index = (uint)_indexes.Count;
        _names[name] = index;
        _indexes.Add(name);
        return index;
    }

    /// <summary>
    /// Gets a name given an index. Returns the empty string (index 0) if the index is out
    /// of range. Faithful to <c>UniqueNames::name</c>.
    /// </summary>
    public string Name(uint index) =>
        index < (uint)_indexes.Count ? _indexes[(int)index] : _indexes[0];

    /// <summary>Clears the names and indexes.</summary>
    public void Clear()
    {
        _names.Clear();
        _indexes.Clear();
    }

    /// <summary>
    /// Gets the number of unique names. Since a blank name is added as the first unique
    /// name this returns the map size minus 1 (faithful to <c>UniqueNames::Size</c>).
    /// </summary>
    public int Size() => _names.Count - 1;
}
