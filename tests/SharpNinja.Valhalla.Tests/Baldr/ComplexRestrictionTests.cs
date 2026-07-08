// Faithful C# port of Valhalla's gtest suite test/complexrestriction.cc.
//
// The C++ test covers:
//   - Sizeof: sizeof(ComplexRestriction) == 24 (tile-compatibility guard).
//   - WalkViasBuilder: a mjolnir ComplexRestrictionBuilder refuses to WalkVias (throws). The
//     builder lives in the EXCLUDED mjolnir module, so that specific case is not ported; the
//     PORT-NOTE below records the omission and we instead test WalkVias on the read-side struct.
//   - WriteRead: drives a mjolnir ComplexRestrictionBuilder's set_* methods and reads them back
//     via the baldr getters. ComplexRestriction (baldr) is read-only over the packed words; the
//     builder is mjolnir. To preserve the round-trip coverage WITHOUT porting mjolnir, we pack the
//     three 64-bit words directly (exactly as the builder would on disk) and assert the baldr
//     getters decode every field identically. The via-count limit (kMaxViasPerRestriction) is also
//     verified.
//
// PORT-NOTE: mjolnir::ComplexRestrictionBuilder is part of the excluded tile BUILDER (mjolnir),
//            so the builder-specific WalkViasBuilder throw test and the set_*-based WriteRead are
//            re-expressed against the baldr read-side struct. The on-disk bit layout is identical,
//            so the field coverage is equivalent.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class ComplexRestrictionTests
{
    // Expected size is 24 bytes. Since there are still "spare" bits we want to alert if somehow any
    // change grows this structure size.
    private const int ComplexRestrictionExpectedSize = 24;

    [Fact]
    public void Sizeof()
        => Assert.Equal(ComplexRestrictionExpectedSize, Unsafe.SizeOf<ComplexRestriction>());

    [Fact]
    public void SizeofStructConstantMatchesType()
        => Assert.Equal(Unsafe.SizeOf<ComplexRestriction>(), ComplexRestriction.SizeOfStruct);

    [Fact]
    public void DefaultFromIdIsInvalid()
    {
        // EXPECT_FALSE(r.from_graphid().is_valid()) for the default-constructed restriction.
        ComplexRestriction r = ComplexRestriction.Create();
        Assert.False(r.FromGraphId().IsValid());
        Assert.False(r.ToGraphId().IsValid());
        Assert.Equal(0, r.ViaCount());
    }

    // Packs the three words exactly as the mjolnir builder would, then asserts each baldr getter.
    // Mirrors TEST(ComplexRestriction, WriteRead).
    [Fact]
    public void WriteRead_AllFieldsRoundTrip()
    {
        var from = new GraphId(1234, 1, 111);
        var to = new GraphId(2345, 1, 2222);

        // word 0: from_graphid_ | has_dt_ | begin_day_dow_ | begin_month_ | begin_week_ | begin_hrs_
        ulong word0 =
            (from.Value & 0x3FFFFFFFFFFF) |
            (1UL << 46) |          // has_dt_ = true
            (3UL << 47) |          // begin_day_dow_ = 3
            (7UL << 52) |          // begin_month_ = 7
            (4UL << 56) |          // begin_week_ = 4
            (5UL << 59);           // begin_hrs_ = 5

        // word 1: to_graphid_ | dt_type_ | end_day_dow_ | end_month_ | end_week_ | end_hrs_
        ulong word1 =
            (to.Value & 0x3FFFFFFFFFFF) |
            (1UL << 46) |          // dt_type_ = true
            (2UL << 47) |          // end_day_dow_ = 2
            (4UL << 52) |          // end_month_ = 4
            (5UL << 56) |          // end_week_ = 5
            (15UL << 59);          // end_hrs_ = 15

        // word 2: type_ | modes_ | via_count_ | dow_ | begin_mins_ | end_mins_ | probability_
        ulong word2 =
            ((ulong)RestrictionType.NoLeftTurn & 0xF) |
            (2224UL << 4) |        // modes_ = 2224
            (5UL << 16) |          // via_count_ = 5
            (53UL << 21) |         // dow_ = 53
            (5UL << 28) |          // begin_mins_ = 5
            (55UL << 34) |         // end_mins_ = 55
            (0UL << 40);           // probability_ = 0

        ComplexRestriction r = PackRaw(word0, word1, word2);

        Assert.Equal(from, r.FromGraphId());
        Assert.Equal(to, r.ToGraphId());
        Assert.Equal(5, r.ViaCount());
        Assert.Equal(RestrictionType.NoLeftTurn, r.Type());
        Assert.Equal((ushort)2224, r.Modes());
        Assert.True(r.HasDt());
        Assert.Equal(3, r.BeginDayDow());
        Assert.Equal(7, r.BeginMonth());
        Assert.Equal(4, r.BeginWeek());
        Assert.Equal(5, r.BeginHrs());
        Assert.True(r.DtType());
        Assert.Equal(2, r.EndDayDow());
        Assert.Equal(4, r.EndMonth());
        Assert.Equal(5, r.EndWeek());
        Assert.Equal(15, r.EndHrs());
        Assert.Equal(53, r.Dow());
        Assert.Equal(5, r.BeginMins());
        Assert.Equal(55, r.EndMins());
    }

    [Fact]
    public void ViaCount_Maxes_At_31()
    {
        // via_count_ is a 5-bit field. The builder caps at kMaxViasPerRestriction (31). Confirm the
        // baldr field cannot represent more than 31 (the 5-bit max).
        Assert.Equal(31, ComplexRestriction.MaxViasPerRestriction);

        ComplexRestriction r = PackRaw(0, 0, 31UL << 16);
        Assert.Equal(31, r.ViaCount());
    }

    [Fact]
    public void SizeOf_IncludesViaList()
    {
        ComplexRestriction r = PackRaw(0, 0, 5UL << 16); // via_count_ = 5
        Assert.Equal(24 + (5 * 8), r.SizeOf());
    }

    [Fact]
    public void WalkVias_VisitsEachViaInOrder()
    {
        ComplexRestriction r = PackRaw(0, 0, 3UL << 16); // via_count_ = 3
        var vias = new List<GraphId>
        {
            new GraphId(10, 0, 1),
            new GraphId(20, 0, 2),
            new GraphId(30, 0, 3),
        };

        var walked = new List<GraphId>();
        r.WalkVias(vias, via =>
        {
            walked.Add(via);
            return WalkingVia.KeepWalking;
        });

        Assert.Equal(vias, walked);
    }

    [Fact]
    public void WalkVias_StopsEarly()
    {
        ComplexRestriction r = PackRaw(0, 0, 3UL << 16); // via_count_ = 3
        var vias = new List<GraphId>
        {
            new GraphId(10, 0, 1),
            new GraphId(20, 0, 2),
            new GraphId(30, 0, 3),
        };

        var walked = new List<GraphId>();
        r.WalkVias(vias, via =>
        {
            walked.Add(via);
            return WalkingVia.StopWalking;
        });

        Assert.Single(walked);
        Assert.Equal(vias[0], walked[0]);
    }

    [Fact]
    public void WalkVias_NoViasDoesNothing()
    {
        ComplexRestriction r = ComplexRestriction.Create(); // via_count_ = 0
        int calls = 0;
        r.WalkVias(new List<GraphId>(), _ =>
        {
            calls++;
            return WalkingVia.KeepWalking;
        });

        Assert.Equal(0, calls);
    }

    // Helper that constructs a ComplexRestriction from raw 64-bit words via reflection-free
    // reinterpretation: the struct is exactly three contiguous ulongs, so we overlay the words.
    private static ComplexRestriction PackRaw(ulong word0, ulong word1, ulong word2)
    {
        Span<ulong> words = stackalloc ulong[3] { word0, word1, word2 };
        return Unsafe.ReadUnaligned<ComplexRestriction>(
            ref Unsafe.As<ulong, byte>(ref words[0]));
    }
}
