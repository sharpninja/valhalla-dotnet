// Faithful C# port of the ConditionalSpeedLimit coverage implied by Valhalla's
// conditional_speed_limit.h. The C++ file carries a single compile-time guard:
//   static_assert(sizeof(ConditionalSpeedLimit) == 8, "invalid ConditionalSpeedLimit struct size");
// There is no dedicated gtest .cc for it, so this suite asserts the size guard plus the union
// overlay semantics: the speed_ field (bits 54-61) shares the same 8-byte word as the embedded
// TimeDomain, so writing one must not corrupt the meaningful TimeDomain bits (which live in bits
// 0-53) and vice versa.

using System.Runtime.CompilerServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class ConditionalSpeedLimitTests
{
    [Fact]
    public void SizeofIsEightBytes()
        => Assert.Equal(8, Unsafe.SizeOf<ConditionalSpeedLimit>());

    [Fact]
    public void SpeedRoundTrips()
    {
        var csl = default(ConditionalSpeedLimit);
        csl.Speed = 90;
        Assert.Equal((byte)90, csl.Speed);
    }

    [Fact]
    public void TimeDomainRoundTrips()
    {
        // Mo-Fr 06:00-11:00 packed value (from the timeparsing constants); its meaningful bits all
        // live below bit 54, so they coexist with the speed field.
        var td = new TimeDomain(23622321788);
        var csl = default(ConditionalSpeedLimit);
        csl.TimeDomain = td;
        Assert.Equal(td, csl.TimeDomain);
        Assert.Equal(62u, csl.TimeDomain.Dow);
        Assert.Equal(6u, csl.TimeDomain.BeginHrs);
        Assert.Equal(11u, csl.TimeDomain.EndHrs);
    }

    [Fact]
    public void SpeedAndTimeDomainCoexist()
    {
        // Set a TimeDomain whose meaningful fields occupy bits 0-53, then overlay a speed in bits
        // 54-61. Reading the TimeDomain fields back must be unaffected by the speed, and the speed
        // must be readable independently.
        var td = new TimeDomain(40802435968); // Sa 03:30-19:00

        var csl = new ConditionalSpeedLimit(td.TdValue);
        csl.Speed = 110;

        Assert.Equal((byte)110, csl.Speed);

        TimeDomain back = csl.TimeDomain;
        Assert.Equal(64u, back.Dow);
        Assert.Equal(3u, back.BeginHrs);
        Assert.Equal(30u, back.BeginMins);
        Assert.Equal(19u, back.EndHrs);
    }

    [Fact]
    public void RawValueConstructorReadsBackBits()
    {
        // speed_ at bits 54-61: a speed of 100 (0x64) shifted to bit 54.
        ulong raw = 100UL << 54;
        var csl = new ConditionalSpeedLimit(raw);
        Assert.Equal((byte)100, csl.Speed);
        Assert.Equal(raw, csl.Value);
    }
}
