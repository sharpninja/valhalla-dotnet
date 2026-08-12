// Faithful C# port of Valhalla baldr ComplexRestriction (complexrestriction.h) @ 3.7.0.
// Source: valhalla/baldr/complexrestriction.h
//
// A complex restriction is a restriction that either: (1) has via ways, (2) applies to specific
// travel modes, or (3) has specific time periods. This class is the fixed-size portion of the
// complex restriction; on disk a list of GraphIds (the vias) follows immediately after the struct.
//
// EXACT LAYOUT (three consecutive uint64 words => 24 bytes total):
//   word 0 (from graph id + begin date-time bits, LSB first):
//     bits  0..45 (46 bits) : from_graphid_
//     bit  46     ( 1 bit)  : has_dt_         (have date-time info)
//     bits 47..51 ( 5 bits) : begin_day_dow_  (begin day or dow enum)
//     bits 52..55 ( 4 bits) : begin_month_
//     bits 56..58 ( 3 bits) : begin_week_
//     bits 59..63 ( 5 bits) : begin_hrs_
//   word 1 (to graph id + end date-time bits, LSB first):
//     bits  0..45 (46 bits) : to_graphid_
//     bit  46     ( 1 bit)  : dt_type_        (YMD = 0 or nth dow = 1)
//     bits 47..51 ( 5 bits) : end_day_dow_
//     bits 52..55 ( 4 bits) : end_month_
//     bits 56..58 ( 3 bits) : end_week_
//     bits 59..63 ( 5 bits) : end_hrs_
//   word 2 (restriction data, LSB first):
//     bits  0..3  ( 4 bits) : type_           (RestrictionType)
//     bits  4..15 (12 bits) : modes_
//     bits 16..20 ( 5 bits) : via_count_
//     bits 21..27 ( 7 bits) : dow_            (day-of-week mask, e.g. Mo-Fr = 62)
//     bits 28..33 ( 6 bits) : begin_mins_
//     bits 34..39 ( 6 bits) : end_mins_
//     bits 40..46 ( 7 bits) : probability_    (used for probable restrictions, 0-100)
//     bits 47..63 (17 bits) : spare_
// Total size: 24 bytes (the C++ test asserts sizeof(ComplexRestriction) == 24).
//
// PORT-NOTE: the C++ WalkVias template reads GraphIds out of the bytes immediately following the
//            struct on the memory-mapped tile (reinterpret_cast<GraphId*>(this + 1)). In this port
//            the vias are passed in explicitly (the reader supplies the GraphId span that follows
//            the struct), since C# does not do raw pointer arithmetic off a managed struct.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>Whether <see cref="ComplexRestriction.WalkVias"/> should keep or stop walking.</summary>
public enum WalkingVia
{
    /// <summary>Continue walking the via list.</summary>
    KeepWalking,

    /// <summary>Stop walking the via list early.</summary>
    StopWalking,
}

/// <summary>
/// Information held for each complex access restriction. Faithful port of C++
/// <c>class ComplexRestriction</c>. This is the fixed-size (24 byte) portion; a list of via
/// <see cref="GraphId"/>s follows immediately after the structure on disk.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: laid out as three consecutive little-endian 64-bit words (24 bytes total),
/// matching the C++ struct exactly so a tile byte buffer parses identically. See the file header
/// for the full bit map.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct ComplexRestriction
{
    /// <summary>Maximum number of vias per restriction. Mirrors C++ <c>kMaxViasPerRestriction</c>.</summary>
    public const int MaxViasPerRestriction = 31;

    // word 0: from graph id + begin date-time bits
    private ulong _word0;

    // word 1: to graph id + end date-time bits
    private ulong _word1;

    // word 2: restriction data
    private ulong _word2;

    // word 0 fields
    private const int FromGraphIdShift = 0;
    private const ulong GraphIdMask = 0x3FFFFFFFFFFF; // 46 bits
    private const int HasDtShift = 46;
    private const ulong OneBitMask = 0x1;
    private const int BeginDayDowShift = 47;
    private const ulong DayDowMask = 0x1F; // 5 bits
    private const int BeginMonthShift = 52;
    private const ulong MonthMask = 0xF; // 4 bits
    private const int BeginWeekShift = 56;
    private const ulong WeekMask = 0x7; // 3 bits
    private const int BeginHrsShift = 59;
    private const ulong HrsMask = 0x1F; // 5 bits

    // word 1 fields
    private const int ToGraphIdShift = 0;
    private const int DtTypeShift = 46;
    private const int EndDayDowShift = 47;
    private const int EndMonthShift = 52;
    private const int EndWeekShift = 56;
    private const int EndHrsShift = 59;

    // word 2 fields
    private const int TypeShift = 0;
    private const ulong TypeMask = 0xF; // 4 bits
    private const int ModesShift = 4;
    private const ulong ModesMask = 0xFFF; // 12 bits
    private const int ViaCountShift = 16;
    private const ulong ViaCountMask = 0x1F; // 5 bits
    private const int DowShift = 21;
    private const ulong DowMask = 0x7F; // 7 bits
    private const int BeginMinsShift = 28;
    private const ulong MinsMask = 0x3F; // 6 bits
    private const int EndMinsShift = 34;
    private const int ProbabilityShift = 40;
    private const ulong ProbabilityMask = 0x7F; // 7 bits

    /// <summary>
    /// Default-equivalent factory. Mirrors the C++ default ctor: from/to graph ids set to the
    /// invalid graph id, has_dt/type/modes/via_count set to 0.
    /// </summary>
    public static ComplexRestriction Create()
    {
        var cr = default(ComplexRestriction);
        cr.SetWord0(FromGraphIdShift, GraphIdMask, GraphId.InvalidGraphId);
        cr.SetWord1(ToGraphIdShift, GraphIdMask, GraphId.InvalidGraphId);

        // has_dt_, type_, modes_, via_count_ already 0 from default initialization.
        return cr;
    }

    private readonly ulong GetWord0(int shift, ulong mask) => (_word0 >> shift) & mask;

    private void SetWord0(int shift, ulong mask, ulong v) => _word0 = (_word0 & ~(mask << shift)) | ((v & mask) << shift);

    private readonly ulong GetWord1(int shift, ulong mask) => (_word1 >> shift) & mask;

    private void SetWord1(int shift, ulong mask, ulong v) => _word1 = (_word1 & ~(mask << shift)) | ((v & mask) << shift);

    private readonly ulong GetWord2(int shift, ulong mask) => (_word2 >> shift) & mask;

    private void SetWord2(int shift, ulong mask, ulong v) => _word2 = (_word2 & ~(mask << shift)) | ((v & mask) << shift);

    // ------------------------------------------------------------------
    // Setters (used by the mjolnir ComplexRestrictionBuilder, which in C++ is a derived class that
    // writes the protected bit-fields directly). The bit positions match the file-header layout.
    // ------------------------------------------------------------------

    /// <summary>Sets the from edge graph id (46 bits). Mirrors <c>set_from_id</c>.</summary>
    public void SetFromGraphId(GraphId fromId) => SetWord0(FromGraphIdShift, GraphIdMask, fromId.Value);

    /// <summary>Sets the to edge graph id (46 bits). Mirrors <c>set_to_id</c>.</summary>
    public void SetToGraphId(GraphId toId) => SetWord1(ToGraphIdShift, GraphIdMask, toId.Value);

    /// <summary>Sets the number of vias (5 bits). Mirrors <c>set_via_count</c>.</summary>
    public void SetViaCount(byte count) => SetWord2(ViaCountShift, ViaCountMask, count);

    /// <summary>Sets the restriction type (4 bits). Mirrors <c>set_type</c>.</summary>
    public void SetType(RestrictionType type) => SetWord2(TypeShift, TypeMask, (byte)type);

    /// <summary>Sets the access modes mask (12 bits). Mirrors <c>set_modes</c>.</summary>
    public void SetModes(ushort modes) => SetWord2(ModesShift, ModesMask, modes);

    /// <summary>Sets the date-time flag (1 bit). Mirrors <c>set_dt</c>.</summary>
    public void SetHasDt(bool dt) => SetWord0(HasDtShift, OneBitMask, dt ? 1UL : 0UL);

    /// <summary>Sets the begin day or dow (5 bits). Mirrors <c>set_begin_day_dow</c>.</summary>
    public void SetBeginDayDow(byte v) => SetWord0(BeginDayDowShift, DayDowMask, v);

    /// <summary>Sets the begin month (4 bits). Mirrors <c>set_begin_month</c>.</summary>
    public void SetBeginMonth(byte v) => SetWord0(BeginMonthShift, MonthMask, v);

    /// <summary>Sets the begin week (3 bits). Mirrors <c>set_begin_week</c>.</summary>
    public void SetBeginWeek(byte v) => SetWord0(BeginWeekShift, WeekMask, v);

    /// <summary>Sets the begin hours (5 bits). Mirrors <c>set_begin_hrs</c>.</summary>
    public void SetBeginHrs(byte v) => SetWord0(BeginHrsShift, HrsMask, v);

    /// <summary>Sets the date-time restriction type (YMD = 0 or nth dow = 1; 1 bit). Mirrors <c>set_dt_type</c>.</summary>
    public void SetDtType(bool v) => SetWord1(DtTypeShift, OneBitMask, v ? 1UL : 0UL);

    /// <summary>Sets the end day or dow (5 bits). Mirrors <c>set_end_day_dow</c>.</summary>
    public void SetEndDayDow(byte v) => SetWord1(EndDayDowShift, DayDowMask, v);

    /// <summary>Sets the end month (4 bits). Mirrors <c>set_end_month</c>.</summary>
    public void SetEndMonth(byte v) => SetWord1(EndMonthShift, MonthMask, v);

    /// <summary>Sets the end week (3 bits). Mirrors <c>set_end_week</c>.</summary>
    public void SetEndWeek(byte v) => SetWord1(EndWeekShift, WeekMask, v);

    /// <summary>Sets the end hours (5 bits). Mirrors <c>set_end_hrs</c>.</summary>
    public void SetEndHrs(byte v) => SetWord1(EndHrsShift, HrsMask, v);

    /// <summary>Sets the dow mask (7 bits). Mirrors <c>set_dow</c>.</summary>
    public void SetDow(byte v) => SetWord2(DowShift, DowMask, v);

    /// <summary>Sets the begin minutes (6 bits). Mirrors <c>set_begin_mins</c>.</summary>
    public void SetBeginMins(byte v) => SetWord2(BeginMinsShift, MinsMask, v);

    /// <summary>Sets the end minutes (6 bits). Mirrors <c>set_end_mins</c>.</summary>
    public void SetEndMins(byte v) => SetWord2(EndMinsShift, MinsMask, v);

    /// <summary>Sets the probability percentage (7 bits, 0-100). Mirrors <c>set_probability</c>.</summary>
    public void SetProbability(byte v) => SetWord2(ProbabilityShift, ProbabilityMask, v);

    /// <summary>
    /// Gets the three raw 64-bit words (word0/word1/word2) that make up the fixed-size structure, in
    /// the exact on-disk little-endian order. Used by the writer to emit the 24-byte record.
    /// </summary>
    public readonly (ulong Word0, ulong Word1, ulong Word2) RawWords() => (_word0, _word1, _word2);

    /// <summary>Constructs a ComplexRestriction from the three raw 64-bit words (the on-disk record).</summary>
    public static ComplexRestriction FromRawWords(ulong word0, ulong word1, ulong word2)
    {
        var cr = default(ComplexRestriction);
        cr._word0 = word0;
        cr._word1 = word1;
        cr._word2 = word2;
        return cr;
    }

    /// <summary>Gets the restriction's from graph id.</summary>
    public readonly GraphId FromGraphId() => new GraphId(GetWord0(FromGraphIdShift, GraphIdMask));

    /// <summary>Gets the restriction's to graph id.</summary>
    public readonly GraphId ToGraphId() => new GraphId(GetWord1(ToGraphIdShift, GraphIdMask));

    /// <summary>Gets the number of vias.</summary>
    public readonly byte ViaCount() => (byte)GetWord2(ViaCountShift, ViaCountMask);

    /// <summary>Gets the restriction type.</summary>
    public readonly RestrictionType Type() => (RestrictionType)GetWord2(TypeShift, TypeMask);

    /// <summary>Gets the modes impacted by the restriction (access mode mask).</summary>
    public readonly ushort Modes() => (ushort)GetWord2(ModesShift, ModesMask);

    /// <summary>Gets the date-time flag (whether there is date-time info for this restriction).</summary>
    public readonly bool HasDt() => GetWord0(HasDtShift, OneBitMask) != 0;

    /// <summary>Gets the begin day or dow for the restriction.</summary>
    public readonly byte BeginDayDow() => (byte)GetWord0(BeginDayDowShift, DayDowMask);

    /// <summary>Gets the begin month for the restriction.</summary>
    public readonly byte BeginMonth() => (byte)GetWord0(BeginMonthShift, MonthMask);

    /// <summary>Gets the begin week for the restriction.</summary>
    public readonly byte BeginWeek() => (byte)GetWord0(BeginWeekShift, WeekMask);

    /// <summary>Gets the begin hours for the restriction.</summary>
    public readonly byte BeginHrs() => (byte)GetWord0(BeginHrsShift, HrsMask);

    /// <summary>Gets the date-time restriction type (YMD = 0 or nth dow = 1).</summary>
    public readonly bool DtType() => GetWord1(DtTypeShift, OneBitMask) != 0;

    /// <summary>Gets the end day or dow for the restriction.</summary>
    public readonly byte EndDayDow() => (byte)GetWord1(EndDayDowShift, DayDowMask);

    /// <summary>Gets the end month for the restriction.</summary>
    public readonly byte EndMonth() => (byte)GetWord1(EndMonthShift, MonthMask);

    /// <summary>Gets the end week for the restriction.</summary>
    public readonly byte EndWeek() => (byte)GetWord1(EndWeekShift, WeekMask);

    /// <summary>Gets the end hours for the restriction.</summary>
    public readonly byte EndHrs() => (byte)GetWord1(EndHrsShift, HrsMask);

    /// <summary>Gets the dow mask: indicates days of week to apply the restriction (e.g. Mo-Fr = 62).</summary>
    public readonly byte Dow() => (byte)GetWord2(DowShift, DowMask);

    /// <summary>Gets the begin minutes for the restriction.</summary>
    public readonly byte BeginMins() => (byte)GetWord2(BeginMinsShift, MinsMask);

    /// <summary>Gets the end minutes for the restriction.</summary>
    public readonly byte EndMins() => (byte)GetWord2(EndMinsShift, MinsMask);

    /// <summary>Gets the probability (percentage 0-100) for the restriction.</summary>
    public readonly byte Probability() => (byte)GetWord2(ProbabilityShift, ProbabilityMask);

    /// <summary>Reconstructs the exact packed conditional time-domain word.</summary>
    public readonly ulong ToTimeDomain()
    {
        var domain = new TimeDomain();
        domain.SetType(DtType());
        domain.SetDow(Dow());
        domain.SetBeginHrs(BeginHrs());
        domain.SetBeginMins(BeginMins());
        domain.SetBeginMonth(BeginMonth());
        domain.SetBeginDayDow(BeginDayDow());
        domain.SetBeginWeek(BeginWeek());
        domain.SetEndHrs(EndHrs());
        domain.SetEndMins(EndMins());
        domain.SetEndMonth(EndMonth());
        domain.SetEndDayDow(EndDayDow());
        domain.SetEndWeek(EndWeek());
        return domain.TdValue;
    }

    /// <summary>
    /// Gets the size, in bytes, of this complex restriction. Includes the fixed-size structure
    /// (24 bytes) plus the via edge id list (8 bytes each) that immediately follows on disk.
    /// Mirrors C++ <c>SizeOf()</c>.
    /// </summary>
    public readonly int SizeOf() => SizeOfStruct + (ViaCount() * SizeOfGraphId);

    /// <summary>Size of the fixed-size struct in bytes (matches C++ <c>sizeof(ComplexRestriction)</c>).</summary>
    public const int SizeOfStruct = 24;

    /// <summary>Size of a single via <see cref="GraphId"/> in bytes (matches C++ <c>sizeof(GraphId)</c>).</summary>
    public const int SizeOfGraphId = 8;

    /// <summary>
    /// Walks the vias of the restriction and calls <paramref name="callback"/> for each.
    /// The <paramref name="vias"/> are the GraphIds that follow the struct on disk (the reader
    /// supplies them; see the file PORT-NOTE). Stops early if the callback returns
    /// <see cref="WalkingVia.StopWalking"/>. Faithful port of C++ <c>WalkVias</c>.
    /// </summary>
    public readonly void WalkVias(IReadOnlyList<GraphId> vias, Func<GraphId, WalkingVia> callback)
    {
        if (ViaCount() > 0)
        {
            for (uint i = 0; i < ViaCount(); i++)
            {
                if (callback(vias[(int)i]) == WalkingVia.StopWalking)
                {
                    break;
                }
            }
        }
    }
}
