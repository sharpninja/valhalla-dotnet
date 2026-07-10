// Faithful C# port of the per-locale NarrativeBuilder grammar subclasses
// (valhalla/odin/narrativebuilder.h 668-760 + src/odin/narrativebuilder.cc 4908-4943) @ 3.7.0.
// Source: valhalla/odin/narrativebuilder.{h,cc}.
//
// These are the only language-specialized builders upstream ships: three override GetPluralCategory
// with the CLDR plural rules for their language, and NarrativeBuilder_itIT enables the articulated
// preposition post-processing (see NarrativeBuilder.FormArticulatedPrepositions). Everything else keeps
// the base NarrativeBuilder behavior; the factory selects the subclass by BCP-47 tag.
//
// PORT-NOTE: upstream GetPluralCategory / FormArticulatedPrepositions are protected; the base
// GetPluralCategory is public virtual in this port (A2), so the overrides are public override. The
// subclasses are internal (the public surface is the base NarrativeBuilder + the factory);
// boost::replace_all maps to string.Replace.

using System.Collections.Generic;

using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Czech (cs-CZ) narrative builder. Faithful port of <c>NarrativeBuilder_csCZ</c>: overrides the
/// plural category with the Czech CLDR rule.
/// </summary>
internal sealed class NarrativeBuilder_csCZ : NarrativeBuilder
{
    /// <summary>Constructor. Faithful port of the <c>NarrativeBuilder_csCZ</c> constructor.</summary>
    public NarrativeBuilder_csCZ(
        Options options,
        EnhancedTripLeg? tripPath,
        NarrativeDictionary dictionary,
        MarkupFormatter? markupFormatter = null)
        : base(options, tripPath, dictionary, markupFormatter)
    {
    }

    /// <summary>
    /// Faithful port of <c>NarrativeBuilder_csCZ::GetPluralCategory</c>: count==1 -> "one",
    /// 1 &lt; count &lt; 5 -> "few", otherwise "other".
    /// </summary>
    public override string GetPluralCategory(int count)
    {
        if (count == 1)
        {
            return PluralCategoryOneKey;
        }
        else if ((count > 1) && (count < 5))
        {
            return PluralCategoryFewKey;
        }

        return PluralCategoryOtherKey;
    }
}

/// <summary>
/// Hindi (hi-IN) narrative builder. Faithful port of <c>NarrativeBuilder_hiIN</c>: the plural category
/// is always "other".
/// </summary>
internal sealed class NarrativeBuilder_hiIN : NarrativeBuilder
{
    /// <summary>Constructor. Faithful port of the <c>NarrativeBuilder_hiIN</c> constructor.</summary>
    public NarrativeBuilder_hiIN(
        Options options,
        EnhancedTripLeg? tripPath,
        NarrativeDictionary dictionary,
        MarkupFormatter? markupFormatter = null)
        : base(options, tripPath, dictionary, markupFormatter)
    {
    }

    /// <summary>Faithful port of <c>NarrativeBuilder_hiIN::GetPluralCategory</c>: always "other".</summary>
    public override string GetPluralCategory(int count) => PluralCategoryOtherKey;
}

/// <summary>
/// Italian (it-IT) narrative builder. Faithful port of <c>NarrativeBuilder_itIT</c>: enables the
/// articulated preposition post-processing and overrides <see cref="FormArticulatedPrepositions"/>.
/// </summary>
internal sealed class NarrativeBuilder_itIT : NarrativeBuilder
{
    // Faithful port of NarrativeBuilder_itIT::articulated_prepositions_ (a simple preposition + a
    // definite article combined into the Italian articulated form).
    private static readonly IReadOnlyDictionary<string, string> ArticulatedPrepositions =
        new Dictionary<string, string>
        {
            { " su il ", " sul " },
            { " su la ", " sulla " },
        };

    /// <summary>
    /// Constructor. Faithful port of the <c>NarrativeBuilder_itIT</c> constructor: enables articulated
    /// prepositions for Italian.
    /// </summary>
    public NarrativeBuilder_itIT(
        Options options,
        EnhancedTripLeg? tripPath,
        NarrativeDictionary dictionary,
        MarkupFormatter? markupFormatter = null)
        : base(options, tripPath, dictionary, markupFormatter)
    {
        // Enable articulated prepositions for Italian.
        _articulatedPrepositionEnabled = true;
    }

    /// <summary>
    /// Faithful port of <c>NarrativeBuilder_itIT::FormArticulatedPrepositions</c>: replaces each simple
    /// preposition + article pair with its articulated form (boost::replace_all -> string.Replace).
    /// </summary>
    protected override void FormArticulatedPrepositions(ref string instruction)
    {
        foreach (KeyValuePair<string, string> item in ArticulatedPrepositions)
        {
            instruction = instruction.Replace(item.Key, item.Value);
        }
    }
}

/// <summary>
/// Russian (ru-RU) narrative builder. Faithful port of <c>NarrativeBuilder_ruRU</c>: overrides the
/// plural category with the Russian CLDR rule.
/// </summary>
internal sealed class NarrativeBuilder_ruRU : NarrativeBuilder
{
    /// <summary>Constructor. Faithful port of the <c>NarrativeBuilder_ruRU</c> constructor.</summary>
    public NarrativeBuilder_ruRU(
        Options options,
        EnhancedTripLeg? tripPath,
        NarrativeDictionary dictionary,
        MarkupFormatter? markupFormatter = null)
        : base(options, tripPath, dictionary, markupFormatter)
    {
    }

    /// <summary>
    /// Faithful port of <c>NarrativeBuilder_ruRU::GetPluralCategory</c> (Russian CLDR rule):
    /// rem10==1 &amp;&amp; rem100!=11 -> "one"; rem10 in 2..4 &amp;&amp; rem100 not in 12..14 -> "few";
    /// otherwise "other".
    /// </summary>
    public override string GetPluralCategory(int count)
    {
        int rem10 = count % 10;
        int rem100 = count % 100;

        // http://www.unicode.org/cldr/charts/29/supplemental/language_plural_rules.html#ru
        if (rem10 == 1 && rem100 != 11)
        {
            return PluralCategoryOneKey;
        }
        else if ((rem10 > 1 && rem10 < 5) && !(rem100 > 11 && rem100 < 15))
        {
            return PluralCategoryFewKey;
        }

        return PluralCategoryOtherKey;
    }
}
