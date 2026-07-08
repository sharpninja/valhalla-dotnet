// Faithful C# port of the TimeDomain coverage in Valhalla's gtest suite test/timeparsing.cc.
//
// The C++ test drives mjolnir::get_time_range(condition) (an OSM opening_hours string parser)
// and then decomposes the resulting packed uint64 TimeDomain values, asserting both the raw
// td_value and the individual bit-fields. mjolnir::get_time_range is part of the EXCLUDED tile
// BUILDER (mjolnir), so it is NOT ported here. Instead, this port takes the EXACT expected
// packed td_value constants that test/timeparsing.cc hard-codes and verifies that our TimeDomain
// bit-layout decodes them into precisely the same fields the C++ test asserts. This is the
// strongest available check of tile-blob bit-layout fidelity: the on-disk 64-bit word produced by
// the C++ builder must decode identically in C#.
//
// Each row below is (description, td_value, type, dow, beginMonth, beginDay, beginWeek, beginHrs,
// beginMins, endMonth, endDay, endWeek, endHrs, endMins) taken verbatim from the C++ TEST cases
// (the DateTimePoint there is {month, day, week, hour, minute}).

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class TimeDomainTests
{
    private static void AssertDecodes(
        ulong tdValue,
        uint type,
        uint dow,
        uint beginMonth,
        uint beginDay,
        uint beginWeek,
        uint beginHrs,
        uint beginMins,
        uint endMonth,
        uint endDay,
        uint endWeek,
        uint endHrs,
        uint endMins)
    {
        var res = new TimeDomain(tdValue);

        Assert.Equal(tdValue, res.TdValue);
        Assert.Equal(type, res.Type);
        Assert.Equal(dow, res.Dow);
        Assert.Equal(beginMonth, res.BeginMonth);
        Assert.Equal(beginDay, res.BeginDayDow);
        Assert.Equal(beginWeek, res.BeginWeek);
        Assert.Equal(beginHrs, res.BeginHrs);
        Assert.Equal(beginMins, res.BeginMins);
        Assert.Equal(endMonth, res.EndMonth);
        Assert.Equal(endDay, res.EndDayDow);
        Assert.Equal(endWeek, res.EndWeek);
        Assert.Equal(endHrs, res.EndHrs);
        Assert.Equal(endMins, res.EndMins);
    }

    // TEST(TimeParsing, TestConditionalRestrictions): "Mo-Fr 06:00-11:00,17:00-19:00"
    [Fact]
    public void MoFr_0600_1100_And_1700_1900()
    {
        AssertDecodes(23622321788, 0, 62, 0, 0, 0, 6, 0, 0, 0, 0, 11, 0);
        AssertDecodes(40802193788, 0, 62, 0, 0, 0, 17, 0, 0, 0, 0, 19, 0);
    }

    // "Sa 03:30-19:00"
    [Fact]
    public void Sa_0330_1900()
        => AssertDecodes(40802435968, 0, 64, 0, 0, 0, 3, 30, 0, 0, 0, 19, 0);

    // "Mo,We,Th,Fr 12:00-18:00"
    [Fact]
    public void MoWeThFr_1200_1800()
        => AssertDecodes(38654708852, 0, 58, 0, 0, 0, 12, 0, 0, 0, 0, 18, 0);

    // "Sa-Su 12:00-17:00"
    [Fact]
    public void SaSu_1200_1700()
        => AssertDecodes(36507225218, 0, 65, 0, 0, 0, 12, 0, 0, 0, 0, 17, 0);

    // "July 23-Aug 21 Sa 14:00-20:00"
    [Fact]
    public void July23_Aug21_Sa_1400_2000()
        => AssertDecodes(1512971146104448, 0, 64, 7, 23, 0, 14, 0, 8, 21, 0, 20, 0);

    // "JUL 23-jUl 28 Fr,PH 10:00-20:00"
    [Fact]
    public void Jul23_Jul28_Fr_1000_2000()
        => AssertDecodes(2001154308835904, 0, 32, 7, 23, 0, 10, 0, 7, 28, 0, 20, 0);

    // "Apr-Sep Sa 10:00-13:00"
    [Fact]
    public void AprSep_Sa_1000_1300()
        => AssertDecodes(39610337987200, 0, 64, 4, 0, 0, 10, 0, 9, 0, 0, 13, 0);

    // "06:00-11:00,17:00-19:45" (no dow, plain time ranges)
    [Fact]
    public void Plain_0600_1100_And_1700_1945()
    {
        AssertDecodes(23622321664, 0, 0, 0, 0, 0, 6, 0, 0, 0, 0, 11, 0);
        AssertDecodes(3133178646784, 0, 0, 0, 0, 0, 17, 0, 0, 0, 0, 19, 45);
    }

    // "Oct 16-Nov 15: 09:00-17:30"
    [Fact]
    public void Oct16_Nov15_0900_1730()
        => AssertDecodes(1106007905274112, 0, 0, 10, 16, 0, 9, 0, 11, 15, 0, 17, 30);

    // "Nov 16-Feb 15: 09:00-16:30" (spans end of year)
    [Fact]
    public void Nov16_Feb15_0900_1630()
        => AssertDecodes(1066423339714816, 0, 0, 11, 16, 0, 9, 0, 2, 15, 0, 16, 30);

    // "th 07:00-08:30"
    [Fact]
    public void Th_0700_0830()
        => AssertDecodes(2078764173088, 0, 16, 0, 0, 0, 7, 0, 0, 0, 0, 8, 30);

    // "th-friday 06:00-09:30"
    [Fact]
    public void ThFr_0600_0930()
        => AssertDecodes(2080911656544, 0, 48, 0, 0, 0, 6, 0, 0, 0, 0, 9, 30);

    // "May 15 09:00-11:30"
    [Fact]
    public void May15_0900_1130()
        => AssertDecodes(1079606730295552, 0, 0, 5, 15, 0, 9, 0, 5, 15, 0, 11, 30);

    // "monday-friday 7:00-9:30,13:00-15:00"
    [Fact]
    public void MoFr_0700_0930_And_1300_1500()
    {
        AssertDecodes(2080911656828, 0, 62, 0, 0, 0, 7, 0, 0, 0, 0, 9, 30);
        AssertDecodes(32212258172, 0, 62, 0, 0, 0, 13, 0, 0, 0, 0, 15, 0);
    }

    // ranges without time
    // "Mon-Friday"
    [Fact]
    public void MonFriday_NoTime()
        => AssertDecodes(124, 0, 62, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    // "Mo,Wed"
    [Fact]
    public void MoWed_NoTime()
        => AssertDecodes(20, 0, 10, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    // "March-May"
    [Fact]
    public void MarchMay_NoTime()
        => AssertDecodes(21990234128384, 0, 0, 3, 0, 0, 0, 0, 5, 0, 0, 0, 0);

    // "March 18-April 30"
    [Fact]
    public void March18_April30_NoTime()
        => AssertDecodes(2128654663942144, 0, 0, 3, 18, 0, 0, 0, 4, 30, 0, 0, 0);

    // TEST(TimeParsing, TestConditionalMaxspeed): "(Mo, We, Th, Sa 07:00-15:00)" dow == 0b01011010 (90)
    [Fact]
    public void MoWeThSa_0700_1500_DowMask()
    {
        // From the C++ test: dow == 0b01011010 == 90 for Mo, We, Th, Sa.
        var td = new TimeDomain();
        td.SetDow(0b01011010);
        td.SetBeginHrs(7);
        td.SetEndHrs(15);
        Assert.Equal(90u, td.Dow);
        Assert.Equal(7u, td.BeginHrs);
        Assert.Equal(15u, td.EndHrs);
    }

    // ----- Bit-field setter round-trips and bounds (timedomain.h getters/setters) -----

    [Fact]
    public void DefaultConstructorIsZero()
    {
        var td = new TimeDomain();
        Assert.Equal(0UL, td.TdValue);
    }

    [Fact]
    public void SetType_RoundTrips()
    {
        var td = new TimeDomain();
        td.SetType(true);
        Assert.Equal(TimeDomain.NthDow, td.Type);
        td.SetType(false);
        Assert.Equal(TimeDomain.Ymd, (uint)td.Type);
    }

    [Fact]
    public void SetBeginHrs_24WrapsToZero()
    {
        var td = new TimeDomain();
        td.SetBeginHrs(24);
        Assert.Equal(0u, td.BeginHrs);
    }

    [Fact]
    public void SetBeginMins_60WrapsToZero()
    {
        var td = new TimeDomain();
        td.SetBeginMins(60);
        Assert.Equal(0u, td.BeginMins);
    }

    [Fact]
    public void SetEndHrs_24WrapsToZero()
    {
        var td = new TimeDomain();
        td.SetEndHrs(24);
        Assert.Equal(0u, td.EndHrs);
    }

    [Fact]
    public void SetEndMins_60WrapsToZero()
    {
        var td = new TimeDomain();
        td.SetEndMins(60);
        Assert.Equal(0u, td.EndMins);
    }

    [Fact]
    public void SetDow_OverMaxThrows()
    {
        var td = new TimeDomain();
        Assert.Throws<InvalidOperationException>(() => td.SetDow(128));
    }

    [Fact]
    public void SetBeginMonth_OverMaxThrows()
    {
        var td = new TimeDomain();
        Assert.Throws<InvalidOperationException>(() => td.SetBeginMonth(13));
    }

    [Fact]
    public void SetBeginWeek_OverMaxThrows()
    {
        var td = new TimeDomain();
        Assert.Throws<InvalidOperationException>(() => td.SetBeginWeek(6));
    }

    [Fact]
    public void SetBeginDayDow_YmdOverMaxDayThrows()
    {
        var td = new TimeDomain();
        td.SetType(false); // kYMD
        Assert.Throws<InvalidOperationException>(() => td.SetBeginDayDow(32));
    }

    [Fact]
    public void SetBeginDayDow_NthDowOverMaxDowThrows()
    {
        var td = new TimeDomain();
        td.SetType(true); // kNthDow
        Assert.Throws<InvalidOperationException>(() => td.SetBeginDayDow(8));
    }

    [Fact]
    public void EqualityOperators()
    {
        var a = new TimeDomain(23622321788);
        var b = new TimeDomain(23622321788);
        var c = new TimeDomain(40802193788);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a != c);
        Assert.Equal((ulong)a, (ulong)b);
    }

    // ----- to_string() (src/baldr/timedomain.cc) -----

    [Fact]
    public void ToString_MoFr_0600_1100()
    {
        // Mo-Fr 06:00-11:00 (td_value from the C++ timeparsing test).
        var td = new TimeDomain(23622321788);
        Assert.Equal("Mo-Fr 06:00-11:00", td.ToString());
    }

    [Fact]
    public void ToString_Sa_0330_1900()
    {
        var td = new TimeDomain(40802435968);
        Assert.Equal("Sa 03:30-19:00", td.ToString());
    }

    [Fact]
    public void ToString_July23_Aug21_Sa_1400_2000()
    {
        var td = new TimeDomain(1512971146104448);
        Assert.Equal("Jul 23-Aug 21 Sa 14:00-20:00", td.ToString());
    }

    [Fact]
    public void ToString_MoWeThFr_1200_1800()
    {
        // dow mask 58 == Mo,We,Th,Fr -> "Mo,We-Fr".
        var td = new TimeDomain(38654708852);
        Assert.Equal("Mo,We-Fr 12:00-18:00", td.ToString());
    }

    [Fact]
    public void ToString_Plain_TimeOnly()
    {
        // 06:00-11:00, no dow / no date.
        var td = new TimeDomain(23622321664);
        Assert.Equal("06:00-11:00", td.ToString());
    }
}
