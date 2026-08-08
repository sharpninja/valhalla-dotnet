// Faithful C# port of Valhalla mjolnir ComplexRestrictionBuilder
// (complexrestrictionbuilder.h + src/mjolnir/complexrestrictionbuilder.cc) @ 3.8.3 commit a60c7cb.
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/complexrestrictionbuilder.h
//   F:/github/valhalla/src/mjolnir/complexrestrictionbuilder.cc
//
// In C++ ComplexRestrictionBuilder derives from baldr::ComplexRestriction and adds a vector of via
// GraphIds plus methods to set the protected bit-fields and serialize the record. In C# the baldr
// ComplexRestriction is a value-type struct, so the builder is a class that *contains* a
// ComplexRestriction value (the fixed 24-byte part) and the via list, exposing the same setters and
// serialization. The serialized bytes are byte-identical to what the C++ operator<< writes (the
// three uint64 words followed by the via GraphIds), which is exactly what the ported Baldr
// ComplexRestrictionView reader parses.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Class to build complex restrictions. Faithful port of C++ <c>class ComplexRestrictionBuilder</c>
/// (derived from <see cref="ComplexRestriction"/>). Adds methods to set fields of the structure,
/// holds a list of via edge <see cref="GraphId"/>s, and serializes to the byte-compatible record.
/// </summary>
public sealed class ComplexRestrictionBuilder
{
    // The fixed-size portion of the complex restriction (24 bytes).
    private ComplexRestriction _restriction = ComplexRestriction.Create();

    // via list.
    private List<GraphId> _viaList = new();

    /// <summary>Default constructor. Mirrors <c>ComplexRestrictionBuilder() = default</c>.</summary>
    public ComplexRestrictionBuilder()
    {
    }

    /// <summary>
    /// Constructs a builder from an existing fixed-size restriction record (the deserialize path).
    /// Mirrors <c>ComplexRestrictionBuilder(const ComplexRestriction&amp; restriction)</c>.
    /// </summary>
    public ComplexRestrictionBuilder(ComplexRestriction restriction) => _restriction = restriction;

    /// <summary>Sets the from edge graph id. Faithful port of <c>set_from_id</c>.</summary>
    public void SetFromId(GraphId fromId) => _restriction.SetFromGraphId(fromId);

    /// <summary>Sets the to edge graph id. Faithful port of <c>set_to_id</c>.</summary>
    public void SetToId(GraphId toId) => _restriction.SetToGraphId(toId);

    /// <summary>
    /// Sets the vias for this restriction. Faithful port of <c>set_via_list</c>: if the list exceeds
    /// the max allowed it is ignored (a debug message in C++); the via count is then set to the
    /// (clamped) list size.
    /// </summary>
    public void SetViaList(IReadOnlyList<GraphId> viaList)
    {
        if (viaList.Count > ComplexRestriction.MaxViasPerRestriction)
        {
            // LOG_DEBUG in C++; keep the previous via_list_ unchanged.
        }
        else
        {
            _viaList = new List<GraphId>(viaList);
        }

        SetViaCount(_viaList.Count);
    }

    /// <summary>Sets the restriction type. Faithful port of <c>set_type</c>.</summary>
    public void SetType(RestrictionType type) => _restriction.SetType(type);

    /// <summary>Sets the access modes for the restriction. Faithful port of <c>set_modes</c>.</summary>
    public void SetModes(ushort modes) => _restriction.SetModes(modes);

    /// <summary>Sets the date-time flag for the restriction. Faithful port of <c>set_dt</c>.</summary>
    public void SetDt(bool dt) => _restriction.SetHasDt(dt);

    /// <summary>Sets the begin day or dow for the restriction. Faithful port of <c>set_begin_day_dow</c>.</summary>
    public void SetBeginDayDow(byte beginDayDow) => _restriction.SetBeginDayDow(beginDayDow);

    /// <summary>Sets the begin month for the restriction. Faithful port of <c>set_begin_month</c>.</summary>
    public void SetBeginMonth(byte beginMonth) => _restriction.SetBeginMonth(beginMonth);

    /// <summary>Sets the begin week for the restriction. Faithful port of <c>set_begin_week</c>.</summary>
    public void SetBeginWeek(byte beginWeek) => _restriction.SetBeginWeek(beginWeek);

    /// <summary>Sets the begin hours for the restriction. Faithful port of <c>set_begin_hrs</c>.</summary>
    public void SetBeginHrs(byte beginHrs) => _restriction.SetBeginHrs(beginHrs);

    /// <summary>Sets the date-time restriction type. Faithful port of <c>set_dt_type</c>.</summary>
    public void SetDtType(bool type) => _restriction.SetDtType(type);

    /// <summary>Sets the end day or dow for the restriction. Faithful port of <c>set_end_day_dow</c>.</summary>
    public void SetEndDayDow(byte endDayDow) => _restriction.SetEndDayDow(endDayDow);

    /// <summary>Sets the end month for the restriction. Faithful port of <c>set_end_month</c>.</summary>
    public void SetEndMonth(byte endMonth) => _restriction.SetEndMonth(endMonth);

    /// <summary>Sets the end week for the restriction. Faithful port of <c>set_end_week</c>.</summary>
    public void SetEndWeek(byte endWeek) => _restriction.SetEndWeek(endWeek);

    /// <summary>Sets the end hours for the restriction. Faithful port of <c>set_end_hrs</c>.</summary>
    public void SetEndHrs(byte endHrs) => _restriction.SetEndHrs(endHrs);

    /// <summary>Sets the dow mask for the restriction. Faithful port of <c>set_dow</c>.</summary>
    public void SetDow(byte dow) => _restriction.SetDow(dow);

    /// <summary>Sets the begin minutes for the restriction. Faithful port of <c>set_begin_mins</c>.</summary>
    public void SetBeginMins(byte beginMins) => _restriction.SetBeginMins(beginMins);

    /// <summary>Sets the end minutes for the restriction. Faithful port of <c>set_end_mins</c>.</summary>
    public void SetEndMins(byte endMins) => _restriction.SetEndMins(endMins);

    /// <summary>Sets the probability (percentage 0-100). Faithful port of <c>set_probability</c>.</summary>
    public void SetProbability(byte probability) => _restriction.SetProbability(probability);

    /// <summary>Gets the to edge graph id. Faithful port of <c>to_graphid()</c>.</summary>
    public GraphId ToGraphId() => _restriction.ToGraphId();

    /// <summary>Gets the from edge graph id. Faithful port of <c>from_graphid()</c>.</summary>
    public GraphId FromGraphId() => _restriction.FromGraphId();

    /// <summary>Gets the number of vias. Faithful port of <c>via_count()</c>.</summary>
    public byte ViaCount() => _restriction.ViaCount();

    /// <summary>Gets the access modes mask of the restriction. Faithful port of <c>modes()</c>.</summary>
    public ushort Modes() => _restriction.Modes();

    /// <summary>Gets the via list (the GraphIds that follow the struct on disk).</summary>
    public IReadOnlyList<GraphId> ViaList() => _viaList;

    /// <summary>
    /// Gets the size, in bytes, of this complex restriction once serialized: the fixed 24-byte
    /// structure plus the via edge id list (8 bytes each). Mirrors <c>ComplexRestriction::SizeOf()</c>.
    /// </summary>
    public int SizeOf() => ComplexRestriction.SizeOfStruct + (ViaCount() * ComplexRestriction.SizeOfGraphId);

    // Set the number of vias. Faithful port of the protected set_via_count (clamps to the max).
    private void SetViaCount(int count)
    {
        byte clamped = count > ComplexRestriction.MaxViasPerRestriction
            ? (byte)ComplexRestriction.MaxViasPerRestriction
            : (byte)count;
        _restriction.SetViaCount(clamped);
    }

    /// <summary>
    /// Serializes the restriction to the stream. Faithful port of the C++ <c>operator&lt;&lt;</c>:
    /// writes the fixed part (3 * uint64) and then the via GraphIds (clamped to the max).
    /// </summary>
    public void Serialize(Stream output)
    {
        uint viaCount = (uint)_viaList.Count;
        if (viaCount > ComplexRestriction.MaxViasPerRestriction)
        {
            viaCount = ComplexRestriction.MaxViasPerRestriction;
        }

        (ulong word0, ulong word1, ulong word2) = _restriction.RawWords();
        WriteUInt64(output, word0);
        WriteUInt64(output, word1);
        WriteUInt64(output, word2);

        for (uint i = 0; i < viaCount; i++)
        {
            WriteUInt64(output, _viaList[(int)i].Value);
        }
    }

    /// <summary>
    /// Overloaded equality - used to ensure no dups in tiles. Faithful port of the C++
    /// <c>operator==</c>: compares from/to/type/modes/has_dt/probability, the date-time fields when
    /// has_dt is set, and the via list.
    /// </summary>
    public bool Equals(ComplexRestrictionBuilder other)
    {
        if (other is null)
        {
            return false;
        }

        ComplexRestriction a = _restriction;
        ComplexRestriction b = other._restriction;

        if (a.FromGraphId().Value != b.FromGraphId().Value || a.ToGraphId().Value != b.ToGraphId().Value ||
            a.Type() != b.Type() || a.Modes() != b.Modes() || a.HasDt() != b.HasDt() ||
            a.Probability() != b.Probability())
        {
            return false;
        }

        if (a.HasDt() && (a.BeginDayDow() != b.BeginDayDow() || a.BeginHrs() != b.BeginHrs() ||
                          a.BeginMins() != b.BeginMins() || a.BeginMonth() != b.BeginMonth() ||
                          a.BeginWeek() != b.BeginWeek() || a.Dow() != b.Dow() ||
                          a.DtType() != b.DtType() || a.EndDayDow() != b.EndDayDow() ||
                          a.EndHrs() != b.EndHrs() || a.EndMins() != b.EndMins() ||
                          a.EndMonth() != b.EndMonth() || a.EndWeek() != b.EndWeek()))
        {
            return false;
        }

        if (_viaList.Count != other._viaList.Count)
        {
            return false;
        }

        for (int i = 0; i < _viaList.Count; i++)
        {
            if (_viaList[i].Value != other._viaList[i].Value)
            {
                return false;
            }
        }

        return true;
    }

    private static void WriteUInt64(Stream output, ulong value)
    {
        Span<byte> buf = stackalloc byte[sizeof(ulong)];
        MemoryMarshal.Write(buf, in value);
        output.Write(buf);
    }
}
