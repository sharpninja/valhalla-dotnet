// Faithful C# port of Valhalla baldr VerbalTextFormatter
// (valhalla/baldr/verbal_text_formatter.h + src/baldr/verbal_text_formatter.cc) @ 3.7.0.
// Source: valhalla/baldr/verbal_text_formatter.h, src/baldr/verbal_text_formatter.cc
//
// The generic verbal text formatter prepares strings for a text-to-speech engine. The base class
// Format() returns the text unchanged (the generic path); the US subclass (VerbalTextFormatterUs)
// performs the numeric/street expansion. When a MarkupFormatter is supplied and produces a phoneme
// markup string it is used instead; otherwise the plain formatted text is returned.
//
// PORT-NOTE: The base FormNumberSplitTts / ProcessNumberSplitMatch are ported for completeness (they
// are the fallback used by non-US locales). The digit-grouping insert operates on ASCII digits, so
// the byte-index arithmetic in the C++ maps directly to char indices here.

using System.Text.RegularExpressions;

using SharpNinja.Valhalla.Odin;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// The generic verbal text formatter that prepares strings for use with a text-to-speech engine.
/// Faithful port of <c>valhalla::baldr::VerbalTextFormatter</c>.
/// </summary>
public class VerbalTextFormatter
{
    // Regular expression to find numbers (verbal_text_formatter.cc). ECMAScript groups map to .NET.
    private static readonly Regex NumberSplitRegex = new(@"(\D*)(\d+)(\D*)", RegexOptions.Compiled);

    /// <summary>The country code (retained for special-case logic). Faithful port of <c>country_code_</c>.</summary>
    protected readonly string CountryCode;

    /// <summary>The state code (retained for special-case logic). Faithful port of <c>state_code_</c>.</summary>
    protected readonly string StateCode;

    /// <summary>Constructor. Faithful port of <c>VerbalTextFormatter(country_code, state_code)</c>.</summary>
    public VerbalTextFormatter(string countryCode, string stateCode)
    {
        CountryCode = countryCode;
        StateCode = stateCode;
    }

    /// <summary>
    /// Returns a text-to-speech formatted string for the specified street name, using the phoneme
    /// markup when the markup formatter produces one. Faithful port of
    /// <c>Format(const std::unique_ptr&lt;baldr::StreetName&gt;&amp;, const odin::MarkupFormatter*)</c>.
    /// </summary>
    public string Format(StreetName streetName, MarkupFormatter? markupFormatter = null)
    {
        if (markupFormatter != null)
        {
            string? phonemeMarkupString = markupFormatter.FormatPhonemeElement(streetName);
            if (phonemeMarkupString != null)
            {
                return phonemeMarkupString;
            }
        }

        return Format(streetName.Value);
    }

    /// <summary>
    /// Returns a text-to-speech formatted string for the specified sign, using the phoneme markup
    /// when the markup formatter produces one. Faithful port of
    /// <c>Format(const odin::Sign&amp;, const odin::MarkupFormatter*)</c>.
    /// </summary>
    public string Format(OdinSign sign, MarkupFormatter? markupFormatter = null)
    {
        if (markupFormatter != null)
        {
            string? phonemeMarkupString = markupFormatter.FormatPhonemeElement(sign);
            if (phonemeMarkupString != null)
            {
                return phonemeMarkupString;
            }
        }

        return Format(sign.Text());
    }

    /// <summary>
    /// Returns a text-to-speech formatted string for the specified text. The base implementation
    /// returns the text unchanged. Faithful port of <c>Format(const std::string&amp;)</c>.
    /// </summary>
    public virtual string Format(string text) => text;

    /// <summary>Faithful port of the base <c>ProcessNumberSplitMatch</c>.</summary>
    protected virtual string ProcessNumberSplitMatch(Match m)
    {
        var tts = new System.Text.StringBuilder();
        if (m.Groups[1].Success)
        {
            tts.Append(m.Groups[1].Value);
        }

        string num = m.Groups[2].Value;
        num = InsertNumberSpaces(num);
        tts.Append(num);

        if (m.Groups[3].Success)
        {
            tts.Append(m.Groups[3].Value);
        }

        return tts.ToString();
    }

    /// <summary>Faithful port of the base <c>FormNumberSplitTts</c>.</summary>
    protected virtual string FormNumberSplitTts(string source)
    {
        var tts = new System.Text.StringBuilder();
        foreach (Match m in NumberSplitRegex.Matches(source))
        {
            tts.Append(ProcessNumberSplitMatch(m));
        }

        return tts.Length == 0 ? source : tts.ToString();
    }

    /// <summary>
    /// Inserts spaces into a run of digits to split it for TTS (e.g. "322" -&gt; "3 22"). Faithful
    /// port of the digit-grouping insert loop used by both the base and US number-split processors.
    /// </summary>
    protected static string InsertNumberSpaces(string num)
    {
        const int step = 2;
        const char space = ' ';
        var chars = new System.Collections.Generic.List<char>(num);
        for (int i = (chars.Count % 2 == 0) ? step : (step - 1); i < chars.Count; i += step + 1)
        {
            chars.Insert(i, space);
        }

        return new string(chars.ToArray());
    }
}
