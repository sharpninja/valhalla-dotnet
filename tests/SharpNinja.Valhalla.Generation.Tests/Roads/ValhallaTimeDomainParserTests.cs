using Xunit;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Roads.Frontier;

namespace SharpNinja.Valhalla.Generation.Tests.Roads;

public sealed class ValhallaTimeDomainParserTests
{
    public static TheoryData<string, ulong[]> OfficialValhalla383Cases =>
        new()
        {
            {
                "Mo-Fr 06:00-11:00,17:00-19:00",
                [23622321788UL, 40802193788UL]
            },
            {
                "Sa 03:30-19:00",
                [40802435968UL]
            },
            {
                "Mo,We,Th,Fr 12:00-18:00",
                [38654708852UL]
            },
            {
                "Sa-Su 12:00-17:00",
                [36507225218UL]
            },
            {
                "July 23-Aug 21 Sa 14:00-20:00",
                [1512971146104448UL]
            },
            {
                "Apr-Sep Mo-Fr 09:00-13:00,14:00-18:00",
                [39610337986940UL, 39621075406460UL]
            },
        };

    [Theory]
    [MemberData(nameof(OfficialValhalla383Cases))]
    public void Parse_OfficialValhalla383VectorMatchesExactWords(
        string expression,
        ulong[] expected)
    {
        Assert.Equal(expected, ValhallaTimeDomainParser.Parse(expression));
    }

    [Theory]
    [InlineData("Oct 16-Nov 15: 09:00-17:30", 1106007905274112UL)]
    [InlineData("Nov 16-Feb 15: 09:00-16:30", 1066423339714816UL)]
    public void Parse_OfficialColonSeparatedDateRangeMatchesExactWord(
        string expression,
        ulong expected)
    {
        Assert.Equal([expected], ValhallaTimeDomainParser.Parse(expression));
    }

    [Fact]
    public void Parse_OvernightWeekdayRangePreservesOfficialFields()
    {
        TimeDomain domain = new(
            Assert.Single(ValhallaTimeDomainParser.Parse("Mo-Fr 19:00-07:00")));

        Assert.Equal(TimeDomain.Ymd, domain.Type);
        Assert.Equal((byte)62, domain.Dow);
        Assert.Equal((byte)19, domain.BeginHrs);
        Assert.Equal((byte)0, domain.BeginMins);
        Assert.Equal((byte)7, domain.EndHrs);
        Assert.Equal((byte)0, domain.EndMins);
    }

    [Fact]
    public void Parse_MonthRangePreservesOfficialFields()
    {
        TimeDomain domain = new(
            Assert.Single(ValhallaTimeDomainParser.Parse("Jun-Aug")));

        Assert.Equal(TimeDomain.Ymd, domain.Type);
        Assert.Equal((byte)6, domain.BeginMonth);
        Assert.Equal((byte)8, domain.EndMonth);
    }

    [Fact]
    public void Parse_DayOfMonthRangeWithWholeDayTimePreservesOfficialFields()
    {
        TimeDomain domain = new(
            Assert.Single(
                ValhallaTimeDomainParser.Parse(
                    "Apr 15-Oct 15 00:00-24:00")));

        Assert.Equal(TimeDomain.Ymd, domain.Type);
        Assert.Equal((byte)4, domain.BeginMonth);
        Assert.Equal((byte)15, domain.BeginDayDow);
        Assert.Equal((byte)10, domain.EndMonth);
        Assert.Equal((byte)15, domain.EndDayDow);
        Assert.Equal((byte)0, domain.BeginHrs);
        Assert.Equal((byte)0, domain.EndHrs);
    }

    [Theory]
    [InlineData("summer")]
    [InlineData("winter")]
    [InlineData("PH off")]
    [InlineData("SH")]
    [InlineData("not-a-time-domain")]
    public void Parse_UnsupportedOrMalformedExpressionFailsClosed(
        string expression)
    {
        Assert.Empty(ValhallaTimeDomainParser.Parse(expression));
    }

    [Fact]
    public void Parse_PreservesOfficialFieldSemantics()
    {
        IReadOnlyList<ulong> values =
            ValhallaTimeDomainParser.Parse(
                "Dec Su[-1]-Mar 3 Sat 15:00-17:00");

        TimeDomain domain = new(Assert.Single(values));
        Assert.Equal(TimeDomain.NthDow, domain.Type);
        Assert.Equal((byte)64, domain.Dow);
        Assert.Equal((byte)12, domain.BeginMonth);
        Assert.Equal((byte)1, domain.BeginDayDow);
        Assert.Equal((byte)5, domain.BeginWeek);
        Assert.Equal((byte)15, domain.BeginHrs);
        Assert.Equal((byte)3, domain.EndMonth);
        Assert.Equal((byte)3, domain.EndDayDow);
        Assert.Equal((byte)0, domain.EndWeek);
        Assert.Equal((byte)17, domain.EndHrs);
    }
}
