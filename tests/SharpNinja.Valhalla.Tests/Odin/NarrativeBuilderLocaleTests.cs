// Tests for the per-locale NarrativeBuilder grammar subclasses (slice A4):
//   - NarrativeBuilder_csCZ / _hiIN / _ruRU GetPluralCategory (CLDR plural rules),
//   - NarrativeBuilder_itIT FormArticulatedPrepositions (" su il " -> " sul ", " su la " -> " sulla "),
//   - NarrativeBuilderFactory subclass selection by BCP-47 tag (case-insensitive),
//   - a smoke pass building a simple Start maneuver through every embedded locale.
//
// The plural / factory seams are driven through the public factory + the public virtual
// GetPluralCategory, so a base-only build fails these at runtime (clean RED). The articulated
// preposition hook is protected (faithful to upstream), so it is exercised through reflection on the
// factory-built builder; the base no-op leaves the string unchanged while the it-IT override rewrites.

using System.Collections.Generic;
using System.Reflection;

using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Odin;

public class NarrativeBuilderLocaleTests
{
    // Builds the factory-selected narrative builder for the supplied BCP-47 tag, using the locale
    // dictionary the loader resolves for that tag (exactly the production pairing).
    private static NarrativeBuilder Builder(string language)
        => NarrativeBuilderFactory.Create(
            new Options { Language = language }, null, NarrativeDictionaryLoader.Get(language));

    // Invokes the protected virtual FormArticulatedPrepositions(ref string) on the builder's runtime
    // type (base no-op or it-IT override) and returns the rewritten instruction.
    private static string Articulate(NarrativeBuilder builder, string instruction)
    {
        MethodInfo method = typeof(NarrativeBuilder).GetMethod(
            "FormArticulatedPrepositions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] args = { instruction };
        method.Invoke(builder, args);
        return (string)args[0]!;
    }

    // ---- GetPluralCategory: cs-CZ (count==1 -> one; 1<count<5 -> few; else other) -----------------

    [Fact]
    public void CsCz_Plural_One() => Assert.Equal("one", Builder("cs-CZ").GetPluralCategory(1));

    [Fact]
    public void CsCz_Plural_Few() => Assert.Equal("few", Builder("cs-CZ").GetPluralCategory(3));

    [Fact]
    public void CsCz_Plural_Other() => Assert.Equal("other", Builder("cs-CZ").GetPluralCategory(7));

    // ---- GetPluralCategory: hi-IN (always other) --------------------------------------------------

    [Fact]
    public void HiIn_Plural_Other_1() => Assert.Equal("other", Builder("hi-IN").GetPluralCategory(1));

    [Fact]
    public void HiIn_Plural_Other_2() => Assert.Equal("other", Builder("hi-IN").GetPluralCategory(2));

    [Fact]
    public void HiIn_Plural_Other_5() => Assert.Equal("other", Builder("hi-IN").GetPluralCategory(5));

    // ---- GetPluralCategory: ru-RU (Russian CLDR rule) ---------------------------------------------

    [Fact]
    public void RuRu_Plural_One_1() => Assert.Equal("one", Builder("ru-RU").GetPluralCategory(1));

    [Fact]
    public void RuRu_Plural_Other_11() => Assert.Equal("other", Builder("ru-RU").GetPluralCategory(11));

    [Fact]
    public void RuRu_Plural_Few_2() => Assert.Equal("few", Builder("ru-RU").GetPluralCategory(2));

    [Fact]
    public void RuRu_Plural_Other_12() => Assert.Equal("other", Builder("ru-RU").GetPluralCategory(12));

    [Fact]
    public void RuRu_Plural_Few_22() => Assert.Equal("few", Builder("ru-RU").GetPluralCategory(22));

    [Fact]
    public void RuRu_Plural_Other_5() => Assert.Equal("other", Builder("ru-RU").GetPluralCategory(5));

    // ---- it-IT articulated prepositions -----------------------------------------------------------

    [Fact]
    public void ItIt_Articulated_SuIl() => Assert.Equal(" sul ", Articulate(Builder("it-IT"), " su il "));

    [Fact]
    public void ItIt_Articulated_SuLa() => Assert.Equal(" sulla ", Articulate(Builder("it-IT"), " su la "));

    [Fact]
    public void ItIt_Articulated_Embedded()
        => Assert.Equal("Gira a destra sulla Via Roma.", Articulate(Builder("it-IT"), "Gira a destra su la Via Roma."));

    // The base (en-US) builder's hook is a genuine no-op: the string is returned unchanged.
    [Fact]
    public void Base_Articulated_NoOp() => Assert.Equal(" su il ", Articulate(Builder("en-US"), " su il "));

    // ---- Factory selection by BCP-47 tag ----------------------------------------------------------

    [Theory]
    [InlineData("cs-CZ", "NarrativeBuilder_csCZ")]
    [InlineData("hi-IN", "NarrativeBuilder_hiIN")]
    [InlineData("it-IT", "NarrativeBuilder_itIT")]
    [InlineData("ru-RU", "NarrativeBuilder_ruRU")]
    [InlineData("en-US", "NarrativeBuilder")]
    [InlineData("zz-ZZ", "NarrativeBuilder")]
    public void Factory_Selects_Subclass_By_Tag(string language, string expectedTypeName)
        => Assert.Equal(expectedTypeName, Builder(language).GetType().Name);

    [Fact]
    public void Factory_Selection_Is_Case_Insensitive()
        => Assert.Equal("NarrativeBuilder_itIT", Builder("IT-it").GetType().Name);

    // ---- Smoke: every embedded locale builds a non-empty Start instruction without throwing --------

    [Fact]
    public void AllLocales_BuildSimpleStart_YieldNonEmptyInstruction()
    {
        foreach (string tag in NarrativeDictionaryLoader.AvailableLanguageTags)
        {
            var maneuver = new Maneuver();
            maneuver.SetType(DirectionsLegManeuverType.Start);
            maneuver.SetTravelMode(TravelMode.Drive);
            maneuver.SetBeginCardinalDirection(DirectionsLegManeuverCardinalDirection.East);
            maneuver.SetStreetNames(new[] { ("Main Street", false) });

            var list = new LinkedList<Maneuver>();
            list.AddLast(maneuver);

            NarrativeBuilderFactory.Create(new Options { Language = tag }, null, NarrativeDictionaryLoader.Get(tag))
                .Build(list);

            Assert.False(string.IsNullOrEmpty(maneuver.Instruction()), $"Empty instruction for locale '{tag}'.");
        }
    }
}
