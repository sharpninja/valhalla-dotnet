using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Sif;

using Xunit;

namespace SharpNinja.Valhalla.Tests.Baldr;

public sealed class ConditionalTimeDomainEvaluatorTests
{
    public static TheoryData<ulong, string, bool> Official383Matrix =>
        new()
        {
            { 23622321788UL, "2018-04-17T05:00", false },
            { 23622321788UL, "2018-04-17T06:00", true },
            { 23622321788UL, "2018-04-17T11:00", true },
            { 23622321788UL, "2018-04-17T11:11", false },
            { 40802435968UL, "2018-04-17T11:11", false },
            { 40802435968UL, "2018-04-21T03:00", false },
            { 40802435968UL, "2018-04-21T11:11", true },
            { 36507225218UL, "2018-04-27T13:00", false },
            { 36507225218UL, "2018-04-21T13:00", true },
            { 39610337986940UL, "2018-04-27T13:00", true },
            { 39610337986940UL, "2018-02-27T12:00", false },
            { 39610337986940UL, "2018-09-30T13:00", false },
            { 1106007905274112UL, "2018-10-10T11:00", false },
            { 1106007905274112UL, "2018-10-16T11:00", true },
            { 23622321664UL, "2018-10-16T05:59", false },
            { 23622321664UL, "2018-10-16T06:04", true },
            { 35184375234560UL, "2024-05-31T21:00", false },
            { 35184375234560UL, "2024-06-01T00:01", true },
            { 35184375234560UL, "2024-08-31T23:59", true },
            { 35184375234560UL, "2024-09-01T00:01", false },
        };

    [Theory]
    [MemberData(nameof(Official383Matrix))]
    public void IsActive_Official383NewYorkMatrixMatches(
        ulong word,
        string localIso,
        bool expected)
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        DateTime local = DateTime.SpecifyKind(
            DateTime.Parse(localIso, System.Globalization.CultureInfo.InvariantCulture),
            DateTimeKind.Unspecified);
        DateTimeOffset instant = new(local, zone.GetUtcOffset(local));

        Assert.Equal(
            expected,
            ConditionalTimeDomainEvaluator.IsActive(
                word,
                checked((ulong)instant.ToUnixTimeSeconds()),
                110));
    }

    [Fact]
    public void IsActive_InvalidTimeOrZoneFailsClosed()
    {
        Assert.False(ConditionalTimeDomainEvaluator.IsActive(23622321788UL, 0, 110));
        Assert.False(ConditionalTimeDomainEvaluator.IsActive(23622321788UL, 1, uint.MaxValue));
    }

    [Fact]
    public void ComplexRestriction_RoundTripsExactTimeDomainWord()
    {
        const ulong word = 39610337986940UL;
        TimeDomain domain = new(word);
        var restriction = default(ComplexRestriction);
        restriction.SetHasDt(true);
        restriction.SetDtType(domain.Type != 0);
        restriction.SetDow(domain.Dow);
        restriction.SetBeginHrs(domain.BeginHrs);
        restriction.SetBeginMins(domain.BeginMins);
        restriction.SetBeginMonth(domain.BeginMonth);
        restriction.SetBeginDayDow(domain.BeginDayDow);
        restriction.SetBeginWeek(domain.BeginWeek);
        restriction.SetEndHrs(domain.EndHrs);
        restriction.SetEndMins(domain.EndMins);
        restriction.SetEndMonth(domain.EndMonth);
        restriction.SetEndDayDow(domain.EndDayDow);
        restriction.SetEndWeek(domain.EndWeek);

        Assert.Equal(word, restriction.ToTimeDomain());
    }

    [Fact]
    public void DynamicCost_UsesRuntimeEvaluatorInsteadOfThrowing()
    {
        TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        DateTime local = new(2018, 4, 17, 6, 0, 0, DateTimeKind.Unspecified);
        DateTimeOffset instant = new(local, zone.GetUtcOffset(local));

        Assert.True(
            DynamicCost.IsConditionalActive(
                23622321788UL,
                checked((ulong)instant.ToUnixTimeSeconds()),
                110));
    }
}
