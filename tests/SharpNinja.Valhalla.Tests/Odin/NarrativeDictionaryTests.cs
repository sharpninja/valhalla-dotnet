// Tests for the Odin narrative dictionary + loader (A0). Oracle: the verbatim upstream
// locales/*.json (valhalla 3.7.0), embedded as resources. These assert the loader parses every shipped
// locale, maps the typed subset fields, falls back to en-US for unknown tags, and reproduces exact
// upstream phrase templates (drift guard).

using SharpNinja.Valhalla.Odin;

namespace SharpNinja.Valhalla.Tests.Odin;

public class NarrativeDictionaryTests
{
    // The 34 locale tags shipped verbatim from upstream valhalla locales/*.json.
    public static readonly string[] AllTags =
    {
        "ar-SA", "bg-BG", "ca-ES", "cs-CZ", "da-DK", "de-DE", "el-GR", "en-AU", "en-GB",
        "en-US-x-pirate", "en-US", "es-ES", "et-EE", "fi-FI", "fr-FR", "hi-IN", "hu-HU",
        "it-IT", "ja-JP", "ko-KR", "mn-MN", "nb-NO", "nl-NL", "pl-PL", "pt-BR", "pt-PT",
        "ro-RO", "ru-RU", "sk-SK", "sl-SI", "sv-SE", "tr-TR", "uk-UA", "vi-VN",
    };

    public static TheoryData<string> EveryTag()
    {
        var data = new TheoryData<string>();
        foreach (var tag in AllTags)
        {
            data.Add(tag);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryTag))]
    public void Load_EveryShippedLocale_DoesNotThrow_AndReportsTagAndPosixLocale(string tag)
    {
        var dict = NarrativeDictionaryLoader.Get(tag);

        Assert.Equal(tag, dict.LanguageTag);
        Assert.False(string.IsNullOrWhiteSpace(dict.PosixLocale));

        // Every locale must at least carry the start phrases and cardinal directions - the minimum a
        // depart instruction needs.
        Assert.NotEmpty(dict.StartSubset.Phrases);
        Assert.Equal(8, dict.StartSubset.CardinalDirections.Count);
    }

    [Fact]
    public void Load_EnUs_ReportsExpectedPosixLocale()
    {
        var dict = NarrativeDictionaryLoader.Get("en-US");

        Assert.Equal("en-US", dict.LanguageTag);
        Assert.Equal("en_US.UTF-8", dict.PosixLocale);
    }

    [Fact]
    public void Load_UnknownLanguage_FallsBackToEnUs()
    {
        var dict = NarrativeDictionaryLoader.Get("zz-ZZ");

        Assert.Equal("en-US", dict.LanguageTag);
        Assert.Equal("en_US.UTF-8", dict.PosixLocale);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_NullOrEmptyLanguage_FallsBackToEnUs(string? language)
    {
        var dict = NarrativeDictionaryLoader.Get(language);
        Assert.Equal("en-US", dict.LanguageTag);
    }

    [Fact]
    public void EnUs_CardinalDirections_MatchUpstreamOrder()
    {
        var start = NarrativeDictionaryLoader.Get("en-US").StartSubset;

        Assert.Equal(
            new[] { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" },
            start.CardinalDirections);
    }

    [Fact]
    public void EnUs_OrdinalValues_ContainsFirstThroughTenth()
    {
        var roundabout = NarrativeDictionaryLoader.Get("en-US").EnterRoundaboutSubset;

        Assert.Equal(
            new[] { "1st", "2nd", "3rd", "4th", "5th", "6th", "7th", "8th", "9th", "10th" },
            roundabout.OrdinalValues);
    }

    [Fact]
    public void EnUs_RelativeDirections_TurnIsLeftRight_KeepIsLeftStraightRight()
    {
        var dict = NarrativeDictionaryLoader.Get("en-US");

        Assert.Equal(new[] { "left", "right" }, dict.TurnSubset.RelativeDirections);
        Assert.Equal(new[] { "left", "straight", "right" }, dict.KeepSubset.RelativeDirections);
    }

    [Fact]
    public void EnUs_EmptyStreetNameLabels_HasSevenEntries()
    {
        var start = NarrativeDictionaryLoader.Get("en-US").StartSubset;

        Assert.Equal(7, start.EmptyStreetNameLabels.Count);
        Assert.Equal("the walkway", start.EmptyStreetNameLabels[0]);
        Assert.Equal("the tunnel", start.EmptyStreetNameLabels[6]);
    }

    [Fact]
    public void EnUs_FerryLabel_IsFerry()
    {
        Assert.Equal("Ferry", NarrativeDictionaryLoader.Get("en-US").EnterFerrySubset.FerryLabel);
    }

    // Exact-template drift guard against the verbatim en-US.json.
    [Fact]
    public void EnUs_PhraseTemplates_MatchUpstream()
    {
        var dict = NarrativeDictionaryLoader.Get("en-US");

        Assert.Equal("Head <CARDINAL_DIRECTION>.", dict.StartSubset.GetPhrase(0));
        Assert.Equal("Drive <CARDINAL_DIRECTION> on <STREET_NAMES>.", dict.StartSubset.GetPhrase(5));
        Assert.Equal("Continue on <STREET_NAMES>.", dict.ContinueSubset.GetPhrase(1));
        Assert.Equal("Turn <RELATIVE_DIRECTION> onto <STREET_NAMES>.", dict.TurnSubset.GetPhrase(1));
        Assert.Equal("Make a sharp <RELATIVE_DIRECTION>.", dict.SharpSubset.GetPhrase(0));
        Assert.Equal(
            "Enter the roundabout and take the <ORDINAL_VALUE> exit.",
            dict.EnterRoundaboutSubset.GetPhrase(1));
        Assert.Equal("You have arrived at your destination.", dict.DestinationSubset.GetPhrase(0));
        Assert.Equal("Merge onto <STREET_NAMES>.", dict.MergeSubset.GetPhrase(2));
    }

    // The loader must tolerate keys it does not map (example_phrases inside every subset, top-level
    // aliases). Proven by en-US loading and by every shipped locale loading above; asserted directly
    // here for the sparse exit phrase map (which skips ids 9, 11, 13, ...).
    [Fact]
    public void EnUs_SparseExitPhraseMap_LoadsPresentIdsAndOmitsAbsent()
    {
        var exit = NarrativeDictionaryLoader.Get("en-US").ExitSubset;

        Assert.True(exit.Phrases.ContainsKey("8"));
        Assert.False(exit.Phrases.ContainsKey("9"));
        Assert.Equal("Take the <NAME_SIGN> exit on the <RELATIVE_DIRECTION>.", exit.GetPhrase(8));
    }
}
