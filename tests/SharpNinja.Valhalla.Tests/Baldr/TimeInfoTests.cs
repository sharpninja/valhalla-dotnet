// Faithful C# port of the routing-relevant TimeInfo behavior from Valhalla's time_info.h.
//
// time_info.h has no dedicated gtest .cc of its own; it is exercised indirectly by astar.cc /
// matrix.cc (full routing integration tests that require built tiles, GraphReader, the protobuf
// Location type and the DateTime timezone database). Those integration paths are EXCLUDED from the
// port (protobuf + curler/HTTP tile fetch + datetime tz db). This suite therefore unit-tests the
// pure offset arithmetic of forward()/reverse(), invalid(), day_seconds() and equality, which are
// the routing-critical pieces, with hand-computed expected values that follow the C++ math exactly.

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class TimeInfoTests
{
    private static TimeInfo MakeValid(
        ulong localTime = 1_000_000,
        ulong secondOfWeek = 100_000,
        ulong secondsFromNow = 50,
        bool negativeSecondsFromNow = false,
        ulong timezoneIndex = 1)
        => new TimeInfo
        {
            Valid = true,
            TimezoneIndex = timezoneIndex,
            LocalTime = localTime,
            SecondOfWeek = secondOfWeek,
            SecondsFromNow = secondsFromNow,
            NegativeSecondsFromNow = negativeSecondsFromNow,
        };

    [Fact]
    public void Invalid_HasExpectedDefaults()
    {
        TimeInfo ti = TimeInfo.Invalid();
        Assert.False(ti.Valid);
        Assert.Equal(0UL, ti.TimezoneIndex);
        Assert.Equal(0UL, ti.LocalTime);
        Assert.Equal(GraphConstants.InvalidSecondsOfWeek, ti.SecondOfWeek);
        Assert.Equal(0UL, ti.SecondsFromNow);
        Assert.False(ti.NegativeSecondsFromNow);
    }

    [Fact]
    public void Forward_OnInvalid_ReturnsSelf()
    {
        TimeInfo ti = TimeInfo.Invalid();
        TimeInfo res = ti.Forward(3600, 5);
        Assert.Equal(ti, res);
    }

    [Fact]
    public void Reverse_OnInvalid_ReturnsSelf()
    {
        TimeInfo ti = TimeInfo.Invalid();
        TimeInfo res = ti.Reverse(3600, 5);
        Assert.Equal(ti, res);
    }

    [Fact]
    public void Forward_SameTimezone_OffsetsLocalTimeAndWeekAndNow()
    {
        TimeInfo ti = MakeValid(timezoneIndex: 7);
        TimeInfo res = ti.Forward(3600, 7);

        Assert.True(res.Valid);
        Assert.Equal(7UL, res.TimezoneIndex);
        Assert.Equal(1_003_600UL, res.LocalTime);
        Assert.Equal(103_600UL, res.SecondOfWeek);
        // sign = +1 (not negative); sfn = 50*1 + 3600 = 3650.
        Assert.Equal(3650UL, res.SecondsFromNow);
        Assert.False(res.NegativeSecondsFromNow);
    }

    [Fact]
    public void Forward_WrapsWeekSecondPastEnd()
    {
        // second_of_week near the end of the week wraps by subtracting kSecondsPerWeek.
        TimeInfo ti = MakeValid(secondOfWeek: Constants.SecondsPerWeek - 10, timezoneIndex: 3);
        TimeInfo res = ti.Forward(3600, 3);
        // 604790 + 3600 = 608390 > 604800 -> 608390 - 604800 = 3590.
        Assert.Equal(3590UL, res.SecondOfWeek);
    }

    [Fact]
    public void Reverse_SameTimezone_OffsetsBackward()
    {
        TimeInfo ti = MakeValid(localTime: 1_000_000, secondOfWeek: 100_000, secondsFromNow: 50, timezoneIndex: 2);
        TimeInfo res = ti.Reverse(3600, 2);

        Assert.Equal(996_400UL, res.LocalTime);
        Assert.Equal(96_400UL, res.SecondOfWeek);
        // sign = +1; sfn = 50*1 - 3600 = -3550 -> magnitude 3550, negative true.
        Assert.Equal(3550UL, res.SecondsFromNow);
        Assert.True(res.NegativeSecondsFromNow);
    }

    [Fact]
    public void Reverse_WrapsWeekSecondPastBeginning()
    {
        TimeInfo ti = MakeValid(secondOfWeek: 100, timezoneIndex: 4);
        TimeInfo res = ti.Reverse(3600, 4);
        // 100 - 3600 = -3500 < 0 -> -3500 + 604800 = 601300.
        Assert.Equal(601_300UL, res.SecondOfWeek);
    }

    [Fact]
    public void Forward_NegativeSecondsFromNow_SignHandled()
    {
        // negative_seconds_from_now == true -> sign = -1; sfn = 100*(-1) + 3600 = 3500, positive.
        TimeInfo ti = MakeValid(secondsFromNow: 100, negativeSecondsFromNow: true, timezoneIndex: 1);
        TimeInfo res = ti.Forward(3600, 1);
        Assert.Equal(3500UL, res.SecondsFromNow);
        Assert.False(res.NegativeSecondsFromNow);
    }

    [Fact]
    public void Forward_AppliesTimezoneDiffOnTzChange()
    {
        // When the next timezone differs, the supplied tz-diff delegate adjusts second_of_week.
        TimeInfo ti = MakeValid(secondOfWeek: 100_000, timezoneIndex: 1);
        TimeInfo res = ti.Forward(0, 2, (lt, from, to) => 3600); // +1h tz diff

        Assert.Equal(2UL, res.TimezoneIndex);
        Assert.Equal(103_600UL, res.SecondOfWeek);
    }

    [Fact]
    public void Reverse_AppliesTimezoneDiffOnTzChange()
    {
        TimeInfo ti = MakeValid(secondOfWeek: 100_000, timezoneIndex: 1);
        TimeInfo res = ti.Reverse(0, 2, (lt, from, to) => -3600); // -1h tz diff

        Assert.Equal(2UL, res.TimezoneIndex);
        Assert.Equal(96_400UL, res.SecondOfWeek);
    }

    [Fact]
    public void Forward_NoTimezoneDiffDelegate_TreatsAsNoChange()
    {
        // No delegate supplied: tz change contributes 0 (single-timezone behavior).
        TimeInfo ti = MakeValid(secondOfWeek: 100_000, timezoneIndex: 1);
        TimeInfo res = ti.Forward(0, 9, timezoneDiff: null);
        Assert.Equal(9UL, res.TimezoneIndex);
        Assert.Equal(100_000UL, res.SecondOfWeek);
    }

    [Fact]
    public void DaySeconds_IsSecondOfWeekModuloDay()
    {
        // second_of_week = 1 day + 1234 seconds -> day_seconds == 1234.
        TimeInfo ti = MakeValid(secondOfWeek: Constants.SecondsPerDay + 1234);
        Assert.Equal(1234u, ti.DaySeconds());
    }

    [Fact]
    public void Equality_ComparesRoutingFields()
    {
        TimeInfo a = MakeValid();
        TimeInfo b = MakeValid();
        Assert.True(a == b);
        Assert.False(a != b);

        TimeInfo c = MakeValid(localTime: 999);
        Assert.True(a != c);
    }
}
