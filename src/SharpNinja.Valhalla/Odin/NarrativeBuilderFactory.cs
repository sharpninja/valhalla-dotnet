// Faithful C# port of Valhalla odin NarrativeBuilderFactory
// (valhalla/odin/narrative_builder_factory.h + src/odin/narrative_builder_factory.cc) @ 3.7.0.
// Source: valhalla/odin/narrative_builder_factory.h, src/odin/narrative_builder_factory.cc
//
// Creates the language-specific narrative builder for a locale. Upstream selects a per-language
// subclass keyed off the resolved dictionary's language tag ("cs-CZ" -> NarrativeBuilder_csCZ,
// "hi-IN" -> _hiIN, "it-IT" -> _itIT, "ru-RU" -> _ruRU); every other tag returns the base
// NarrativeBuilder. This port keys off options.Language (the BCP-47 tag the caller pairs with the
// resolved dictionary), matched case-insensitively.

using System;

using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Creates a <see cref="NarrativeBuilder"/> for the given options, trip path, and dictionary.
/// Faithful port of <c>valhalla::odin::NarrativeBuilderFactory::Create</c>.
/// </summary>
public static class NarrativeBuilderFactory
{
    /// <summary>
    /// Returns the narrative builder for the specified options and dictionary, selecting the per-locale
    /// grammar subclass by <see cref="Options.Language"/>. Faithful port of
    /// <c>NarrativeBuilderFactory::Create(const Options&amp;, const EnhancedTripLeg*, const MarkupFormatter&amp;)</c>.
    /// </summary>
    /// <param name="options">The directions options (units, language).</param>
    /// <param name="tripPath">The enhanced trip path (may be null; used for destination lookups).</param>
    /// <param name="dictionary">The localized narrative dictionary.</param>
    /// <returns>The per-locale <see cref="NarrativeBuilder"/> subclass, or the base builder.</returns>
    public static NarrativeBuilder Create(Options options, EnhancedTripLeg? tripPath, NarrativeDictionary dictionary)
    {
        // If a NarrativeBuilder is derived with specific code for a particular language then add logic
        // here and return the derived NarrativeBuilder; otherwise return the base NarrativeBuilder.
        string language = options.Language ?? string.Empty;

        if (string.Equals(language, "cs-CZ", StringComparison.OrdinalIgnoreCase))
        {
            return new NarrativeBuilder_csCZ(options, tripPath, dictionary);
        }

        if (string.Equals(language, "hi-IN", StringComparison.OrdinalIgnoreCase))
        {
            return new NarrativeBuilder_hiIN(options, tripPath, dictionary);
        }

        if (string.Equals(language, "it-IT", StringComparison.OrdinalIgnoreCase))
        {
            return new NarrativeBuilder_itIT(options, tripPath, dictionary);
        }

        if (string.Equals(language, "ru-RU", StringComparison.OrdinalIgnoreCase))
        {
            return new NarrativeBuilder_ruRU(options, tripPath, dictionary);
        }

        return new NarrativeBuilder(options, tripPath, dictionary);
    }
}
