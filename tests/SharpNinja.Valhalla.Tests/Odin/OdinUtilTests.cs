// Faithful C# port of the locale-independent cases of Valhalla's gtest suite test/util_odin.cc.
// The test_get_locales / test_time / test_date / test_supported_locales cases exercise the
// narrativebuilder locale machinery, which is DEFERRED for this structural port (see OdinUtil.cs
// PORT-NOTE). The test_streetname_string_check case (GetWordCount / StrlenUtf8) is locale-independent
// and is ported here in full.

using SharpNinja.Valhalla.Odin;

namespace SharpNinja.Valhalla.Tests.Odin;

public class OdinUtilTests
{
    private static void TryGetWordCount(string streetName, int expectedWordCount)
        => Assert.Equal(expectedWordCount, OdinUtil.GetWordCount(streetName));

    private static void TryGetStrlenUtf8(string streetName, int expectedStrlen)
        => Assert.Equal(expectedStrlen, OdinUtil.StrlenUtf8(streetName));

    [Fact]
    public void TestStreetnameStringCheck()
    {
        string streetName = "Carretera de Santa Agnès de Malanyanes al Coll";
        TryGetWordCount(streetName, 8);
        TryGetStrlenUtf8(streetName, 46);

        streetName = "Calle de la Virgen de la Cabeza";
        TryGetWordCount(streetName, 7);
        TryGetStrlenUtf8(streetName, 31);

        streetName = "Calle del Arroyo de Pozuelo";
        TryGetWordCount(streetName, 5);
        TryGetStrlenUtf8(streetName, 27);

        streetName = "Avenue du Duc de Dantzig";
        TryGetWordCount(streetName, 5);
        TryGetStrlenUtf8(streetName, 24);

        streetName = "Богданова вулиця";
        TryGetWordCount(streetName, 2);
        TryGetStrlenUtf8(streetName, 16);

        streetName = "Щепкіна вулиця/Schepkina Street";
        TryGetWordCount(streetName, 4);
        TryGetStrlenUtf8(streetName, 31);

        streetName = "Набережна Заводська вулиця";
        TryGetWordCount(streetName, 3);
        TryGetStrlenUtf8(streetName, 26);

        streetName = "BV-5105";
        TryGetWordCount(streetName, 2);
        TryGetStrlenUtf8(streetName, 7);

        streetName = "East Van Buren Street";
        TryGetWordCount(streetName, 4);
        TryGetStrlenUtf8(streetName, 21);

        streetName = "246/玉川通り/一般国道246号/Tamagawa-dori";
        TryGetWordCount(streetName, 5);
        TryGetStrlenUtf8(streetName, 31);

        streetName = "三田3丁目";
        TryGetWordCount(streetName, 1);
        TryGetStrlenUtf8(streetName, 5);
    }

    // IsSimilarTurnDegree is exercised indirectly by EnhancedTripPathTests but kept here as a direct
    // unit check of the OdinUtil helper.
    [Fact]
    public void TestIsSimilarTurnDegree()
    {
        // Right direction, within default 40-degree threshold.
        Assert.True(OdinUtil.IsSimilarTurnDegree(0, 30, true));
        Assert.False(OdinUtil.IsSimilarTurnDegree(0, 50, true));

        // Left direction (delta measured the other way).
        Assert.True(OdinUtil.IsSimilarTurnDegree(30, 0, false));
        Assert.False(OdinUtil.IsSimilarTurnDegree(50, 0, false));

        // Wraparound across 0/360.
        Assert.True(OdinUtil.IsSimilarTurnDegree(350, 10, true));
        Assert.True(OdinUtil.IsSimilarTurnDegree(10, 350, false));
    }
}
