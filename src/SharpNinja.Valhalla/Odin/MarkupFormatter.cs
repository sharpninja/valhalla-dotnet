// Faithful C# port of Valhalla odin MarkupFormatter
// (valhalla/odin/markup_formatter.h + src/odin/markup_formatter.cc) @ 3.7.0.
// Source: valhalla/odin/markup_formatter.h, src/odin/markup_formatter.cc
//
// Produces the optional phoneme (TTS pronunciation) markup that wraps a street-name or sign string
// when markup is enabled and a pronunciation is present. In the ported driving verbal path markup is
// DISABLED by default (matching the upstream default config value odin.markup_formatter.markup_enabled
// = false), so FormatPhonemeElement returns null and the verbal formatter falls through to the plain
// text. The phoneme-format substitution is ported faithfully so that enabling markup + supplying a
// Pronunciation produces the same string upstream would; no locale in this slice enables it.

using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Formats the optional phoneme (text-to-speech pronunciation) markup element for street names and
/// signs. Faithful port of <c>valhalla::odin::MarkupFormatter</c>.
/// </summary>
public sealed class MarkupFormatter
{
    // Phoneme markup tags (markup_formatter.cc).
    private const string QuotesTag = "<QUOTES>";
    private const string PhoneticAlphabetTag = "<PHONETIC_ALPHABET>";
    private const string TextualStringTag = "<TEXTUAL_STRING>";
    private const string VerbalStringTag = "<VERBAL_STRING>";

    private const string SingleQuotes = "'";
    private const string DoubleQuotes = "\"";

    private static readonly IReadOnlyDictionary<PronunciationAlphabet, string> AlphabetStrings =
        new Dictionary<PronunciationAlphabet, string>
        {
            [PronunciationAlphabet.Ipa] = "ipa",
            [PronunciationAlphabet.Katakana] = "katakana",
            [PronunciationAlphabet.Jeita] = "jeita",
            [PronunciationAlphabet.NtSampa] = "nt-sampa",
        };

    private bool _markupEnabled;
    private readonly string _phonemeFormat;

    /// <summary>
    /// Constructor. Faithful port of <c>MarkupFormatter(const boost::property_tree::ptree&amp;)</c>;
    /// the upstream config keys default markup to disabled and the phoneme format to empty.
    /// </summary>
    /// <param name="markupEnabled">Whether markup is enabled (config <c>odin.markup_formatter.markup_enabled</c>).</param>
    /// <param name="phonemeFormat">The phoneme format template (config <c>odin.markup_formatter.phoneme_format</c>).</param>
    public MarkupFormatter(bool markupEnabled = false, string phonemeFormat = "")
    {
        _markupEnabled = markupEnabled;
        _phonemeFormat = phonemeFormat;
    }

    /// <summary>Returns true if markup is enabled. Faithful port of <c>markup_enabled()</c>.</summary>
    public bool MarkupEnabled() => _markupEnabled;

    /// <summary>Sets the markup enabled flag. Faithful port of <c>set_markup_enabled()</c>.</summary>
    public void SetMarkupEnabled(bool markupEnabled) => _markupEnabled = markupEnabled;

    /// <summary>
    /// Returns the street name with phoneme markup if it exists, otherwise null. Faithful port of
    /// <c>FormatPhonemeElement(const std::unique_ptr&lt;baldr::StreetName&gt;&amp;)</c>.
    /// </summary>
    public string? FormatPhonemeElement(StreetName streetName)
    {
        if (MarkupEnabled())
        {
            Pronunciation? pronunciation = streetName.GetPronunciation();
            if (pronunciation.HasValue)
            {
                string phonemeMarkupString = FormatPhonemeElement(streetName.Value, pronunciation.Value);
                return phonemeMarkupString.Length == 0 ? null : phonemeMarkupString;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the sign with phoneme markup if it exists, otherwise null. Faithful port of
    /// <c>FormatPhonemeElement(const Sign&amp;)</c>.
    /// </summary>
    public string? FormatPhonemeElement(OdinSign sign)
    {
        if (MarkupEnabled())
        {
            Pronunciation? pronunciation = sign.GetPronunciation();
            if (pronunciation.HasValue)
            {
                string phonemeMarkupString = FormatPhonemeElement(sign.Text(), pronunciation.Value);
                if (phonemeMarkupString.Length != 0)
                {
                    return phonemeMarkupString;
                }
            }
        }

        return null;
    }

    // Faithful port of the protected FormatPhonemeElement(textual_string, pronunciation).
    private string FormatPhonemeElement(string textualString, Pronunciation pronunciation)
    {
        string phonemeMarkupString = _phonemeFormat;

        phonemeMarkupString = FormatQuotes(phonemeMarkupString, pronunciation.Alphabet);

        phonemeMarkupString = phonemeMarkupString.Replace(PhoneticAlphabetTag, AlphabetToString(pronunciation.Alphabet));
        phonemeMarkupString = phonemeMarkupString.Replace(TextualStringTag, textualString);
        phonemeMarkupString = phonemeMarkupString.Replace(VerbalStringTag, pronunciation.Value);

        return phonemeMarkupString;
    }

    // Faithful port of UseSingleQuotes (only nt-sampa uses single quotes).
    private static bool UseSingleQuotes(PronunciationAlphabet alphabet) => alphabet == PronunciationAlphabet.NtSampa;

    // Faithful port of FormatQuotes.
    private static string FormatQuotes(string markupString, PronunciationAlphabet alphabet)
        => markupString.Replace(QuotesTag, UseSingleQuotes(alphabet) ? SingleQuotes : DoubleQuotes);

    private static string AlphabetToString(PronunciationAlphabet alphabet)
        => AlphabetStrings.TryGetValue(alphabet, out string? value)
            ? value
            : throw new System.InvalidOperationException("Missing value in Pronunciation alphabet enum to string");
}
