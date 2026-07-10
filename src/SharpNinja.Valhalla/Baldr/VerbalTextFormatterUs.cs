// Faithful C# port of Valhalla baldr VerbalTextFormatterUs
// (valhalla/baldr/verbal_text_formatter_us.h + src/baldr/verbal_text_formatter_us.cc) @ 3.7.0.
// Source: valhalla/baldr/verbal_text_formatter_us.h, src/baldr/verbal_text_formatter_us.cc
//
// The US-specific verbal text formatter expands route-number and highway text for a text-to-speech
// engine (e.g. "US 322" -> "U.S. 3 22", "I 95 South" -> "Interstate 95 South"). The transformation
// order and the regex/replacement pairs mirror the C++ exactly.
//
// PORT-NOTE: the C++ regexes use ECMAScript syntax with POSIX classes ([[:alpha:]]) which are ported
// to explicit [A-Za-z] ranges here; capture-group back-references ($3) are written in the .NET
// unambiguous ${3} form. std::regex_constants::icase maps to RegexOptions.IgnoreCase.

using System.Text.RegularExpressions;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// The US-specific verbal text formatter. Faithful port of
/// <c>valhalla::baldr::VerbalTextFormatterUs</c>.
/// </summary>
public class VerbalTextFormatterUs : VerbalTextFormatter
{
    private const RegexOptions IcaseCompiled = RegexOptions.IgnoreCase | RegexOptions.Compiled;

    private static readonly Regex UsNumberSplitRegex = new(@"(\D*)(\d+)(st|nd|rd|th)?(\D*)", IcaseCompiled);

    private static readonly Regex InterstateRegex = new(@"(\bI)([ -])(H)?(\d{1,3})", IcaseCompiled);
    private const string InterstateOutPattern = "Interstate ${3}${4}";

    private static readonly Regex UsHighwayRegex = new(@"(\bUS)([ -])(Highway )?(\d{1,3})", IcaseCompiled);
    private const string UsHighwayOutPattern = "U.S. ${3}${4}";

    private static readonly Regex LeadingOhRegex = new(@"( )(0)([1-9])", RegexOptions.Compiled);
    private const string LeadingOhOutPattern = "${1}o${3}";

    private static readonly (Regex Regex, string Replacement)[] ThousandFindReplace =
    {
        (new Regex(@"(^|\D)([1-9]{1,2})(000$)", RegexOptions.Compiled), "${1}${2} thousand"),
        (new Regex(@"(^|\D)([1-9]{1,2})(000th)", IcaseCompiled), "${1}${2} thousandth"),
        (new Regex(@"(^|\D)([1-9]{1,2})(000)( |-)", RegexOptions.Compiled), "${1}${2} thousand "),
        (new Regex(@"(^|\D)([1-9]{1,2})(000)(\D)", RegexOptions.Compiled), "${1}${2} thousand ${4}"),
    };

    private static readonly (Regex Regex, string Replacement)[] HundredFindReplace =
    {
        (new Regex(@"(^|\D)([1-9]{1,2})(00$)", RegexOptions.Compiled), "${1}${2} hundred"),
        (new Regex(@"(^|\D)([1-9]{1,2})(00th)", IcaseCompiled), "${1}${2} hundredth"),
        (new Regex(@"(^|\D)([1-9]{1,2})(00)( |-)", RegexOptions.Compiled), "${1}${2} hundred "),
        (new Regex(@"(^|\D)([1-9]{1,2})(00)(\D)", RegexOptions.Compiled), "${1}${2} hundred ${4}"),
    };

    private static readonly (Regex Regex, string Replacement)[] StateRoutes =
    {
        (new Regex(@"(\bSR)([ -])?(\d{1,4})", IcaseCompiled), "State Route ${3}"),
        (new Regex(@"(\bSH)([ -])?(\d{1,4})", IcaseCompiled), "State Highway ${3}"),
        (new Regex(@"(\bCA)([ -])(\d{1,3})", IcaseCompiled), "California ${3}"),
        (new Regex(@"(\bTX)([ -])(\d{1,3})", IcaseCompiled), "Texas ${3}"),
        (new Regex(@"(\bFL)([ -])(A)?(\d{1,3})", IcaseCompiled), "Florida ${3}${4}"),
        (new Regex(@"(\bNY)([ -])(\d{1,3})", IcaseCompiled), "New York ${3}"),
        (new Regex(@"(\bIL)([ -])(\d{1,3})", IcaseCompiled), "Illinois ${3}"),
        (new Regex(@"(\bPA)([ -])(\d{1,3})", IcaseCompiled), "Pennsylvania ${3}"),
        (new Regex(@"(\bOH)([ -])(\d{1,3})", IcaseCompiled), "Ohio ${3}"),
        (new Regex(@"(\bGA)([ -])(\d{1,3})", IcaseCompiled), "Georgia ${3}"),
        (new Regex(@"(\bNC)([ -])(\d{1,3})", IcaseCompiled), "North Carolina ${3}"),
        (new Regex(@"(\bM)([ -])(\d{1,3})", IcaseCompiled), "Michigan ${3}"),
        (new Regex(@"(\bNJ)([ -])(\d{1,3})", IcaseCompiled), "New Jersey ${3}"),
        (new Regex(@"(\bVA)([ -])(\d{1,3})", IcaseCompiled), "Virginia ${3}"),
        (new Regex(@"(\bWA)([ -])(\d{1,3})", IcaseCompiled), "Washington ${3}"),
        (new Regex(@"(\bMA)([ -])(\d{1,3})", IcaseCompiled), "Massachusetts ${3}"),
        (new Regex(@"(\bAZ)([ -])(\d{1,3})", IcaseCompiled), "Arizona ${3}"),
        (new Regex(@"(\bIN)([ -])(\d{1,3})", IcaseCompiled), "Indiana ${3}"),
        (new Regex(@"(\bTN)([ -])(\d{1,3})", IcaseCompiled), "Tennessee ${3}"),
        (new Regex(@"(\bMO)([ -])(\d{1,3})", IcaseCompiled), "Missouri ${3}"),
        (new Regex(@"(\bMO)([ -])([A-Za-z]{1,2}\b)", IcaseCompiled), "Missouri ${3}"),
        (new Regex(@"(\bMD)([ -])(\d{1,3})", IcaseCompiled), "Maryland ${3}"),
        (new Regex(@"(\bWI)([ -])(\d{1,3})", IcaseCompiled), "Wisconsin ${3}"),
        (new Regex(@"(\bMN)([ -])(\d{1,3})", IcaseCompiled), "Minnesota ${3}"),
        (new Regex(@"(\bAL)([ -])(\d{1,3})", IcaseCompiled), "Alabama ${3}"),
        (new Regex(@"(\bSC)([ -])(\d{1,3})", IcaseCompiled), "South Carolina ${3}"),
        (new Regex(@"(\bLA)([ -])(\d{1,4})", IcaseCompiled), "Louisiana ${3}"),
        (new Regex(@"(\bKY)([ -])(\d{1,4})", IcaseCompiled), "Kentucky ${3}"),
        (new Regex(@"(\bOR)([ -])(\d{1,3})", IcaseCompiled), "Oregon ${3}"),
        (new Regex(@"(\bOK)([ -])(\d{1,3})", IcaseCompiled), "Oklahoma ${3}"),
        (new Regex(@"(\bCT)([ -])(\d{1,3})", IcaseCompiled), "Connecticut ${3}"),
        (new Regex(@"(\bIA)([ -])(\d{1,3})", IcaseCompiled), "Iowa ${3}"),
        (new Regex(@"(\bMS)([ -])(\d{1,3})", IcaseCompiled), "Mississippi ${3}"),
        (new Regex(@"(\bAR)([ -])(\d{1,3})", IcaseCompiled), "Arkansas ${3}"),
        (new Regex(@"(\bUT)([ -])(\d{1,3})", IcaseCompiled), "Utah ${3}"),
        (new Regex(@"(\bKS)([ -])(\d{1,3})", IcaseCompiled), "Kansas ${3}"),
        (new Regex(@"(\bNV)([ -])(\d{1,3})", IcaseCompiled), "Nevada ${3}"),
        (new Regex(@"(\bNM)([ -])(\d{1,4})", IcaseCompiled), "New Mexico ${3}"),
        (new Regex(@"(\bNE)([ -])(\d{1,3})", IcaseCompiled), "Nebraska ${3}"),
        (new Regex(@"(\bWV)([ -])(\d{1,3})", IcaseCompiled), "West Virginia ${3}"),
        (new Regex(@"(\bID)([ -])(\d{1,3})", IcaseCompiled), "Idaho ${3}"),
        (new Regex(@"(\bHI)([ -])(\d{1,4})", IcaseCompiled), "Hawaii ${3}"),
        (new Regex(@"(\bME)([ -])(\d{1,3})", IcaseCompiled), "Maine ${3}"),
        (new Regex(@"(\bNH)([ -])(\d{1,3})", IcaseCompiled), "New Hampshire ${3}"),
        (new Regex(@"(\bRI)([ -])(\d{1,3})", IcaseCompiled), "Rhode Island ${3}"),
        (new Regex(@"(\bMT)([ -])(\d{1,3})", IcaseCompiled), "Montana ${3}"),
        (new Regex(@"(\bDE)([ -])(\d{1,3})", IcaseCompiled), "Delaware ${3}"),
        (new Regex(@"(\bSD)([ -])(\d{1,4})", IcaseCompiled), "South Dakota ${3}"),
        (new Regex(@"(\bND)([ -])(\d{1,4})", IcaseCompiled), "North Dakota ${3}"),
        (new Regex(@"(\bAK)([ -])(\d{1,3})", IcaseCompiled), "Alaska ${3}"),
        (new Regex(@"(\bDC)([ -])(\d{1,3})", IcaseCompiled), "D C ${3}"),
        (new Regex(@"(\bVT)([ -])(\d{1,3})", IcaseCompiled), "Vermont ${3}"),
        (new Regex(@"(\bWY)([ -])(\d{1,3})", IcaseCompiled), "Wyoming ${3}"),
    };

    private static readonly (Regex Regex, string Replacement)[] CountyRoutes =
    {
        (new Regex(@"(\bCR)(\d{1,4})([A-Za-z]{1,2})?\b", IcaseCompiled), "County Route ${2}${3}"),
        (new Regex(@"(\bCR)([ -])([A-Za-z]{1,2})?(\d{1,4})([A-Za-z]{1,2})?\b", IcaseCompiled), "County Route ${3}${4}${5}"),
        (new Regex(@"(\bCR)([ -])([A-Za-z]{1,2})\b", IcaseCompiled), "County Route ${3}"),
        (new Regex(@"(\bC R)(\d{1,4})([A-Za-z]{1,2})?\b", IcaseCompiled), "County Route ${2}${3}"),
        (new Regex(@"(\bC R)([ -])([A-Za-z]{1,2})?(\d{1,4})([A-Za-z]{1,2})?\b", IcaseCompiled), "County Route ${3}${4}${5}"),
        (new Regex(@"(\bC R)([ -])([A-Za-z]{1,2})\b", IcaseCompiled), "County Route ${3}"),
        (new Regex(@"(\bCO)([ -])?(\d{1,4})([A-Za-z]{1,2})?\b", IcaseCompiled), "County Road ${3}${4}"),
    };

    /// <summary>Constructor. Faithful port of <c>VerbalTextFormatterUs(country_code, state_code)</c>.</summary>
    public VerbalTextFormatterUs(string countryCode, string stateCode)
        : base(countryCode, stateCode)
    {
    }

    /// <summary>
    /// Returns a US text-to-speech formatted string for the specified text. Faithful port of the US
    /// <c>Format(const std::string&amp;)</c> transformation pipeline.
    /// </summary>
    public override string Format(string text)
    {
        string verbalText = text;

        verbalText = FormInterstateTts(verbalText);
        verbalText = FormUsHighwayTts(verbalText);
        verbalText = ProcessStatesTts(verbalText);
        verbalText = ProcessCountysTts(verbalText);

        verbalText = ProcessThousandTts(verbalText);
        verbalText = ProcessHundredTts(verbalText);
        verbalText = FormNumberSplitTts(verbalText);
        verbalText = FormLeadingOhTts(verbalText);

        return verbalText;
    }

    /// <summary>Faithful port of the US <c>ProcessNumberSplitMatch</c> (does not split when a st/nd/rd/th suffix follows).</summary>
    protected override string ProcessNumberSplitMatch(Match m)
    {
        var tts = new System.Text.StringBuilder();
        if (m.Groups[1].Success)
        {
            tts.Append(m.Groups[1].Value);
        }

        // If the source number has st/nd/rd/th appended to it then do not split it.
        if (m.Groups[3].Success)
        {
            tts.Append(m.Groups[2].Value);
            tts.Append(m.Groups[3].Value);
        }
        else
        {
            tts.Append(InsertNumberSpaces(m.Groups[2].Value));
        }

        if (m.Groups[4].Success)
        {
            tts.Append(m.Groups[4].Value);
        }

        return tts.ToString();
    }

    /// <summary>Faithful port of the US <c>FormNumberSplitTts</c>.</summary>
    protected override string FormNumberSplitTts(string source)
    {
        var tts = new System.Text.StringBuilder();
        foreach (Match m in UsNumberSplitRegex.Matches(source))
        {
            tts.Append(ProcessNumberSplitMatch(m));
        }

        return tts.Length == 0 ? source : tts.ToString();
    }

    private static string FormInterstateTts(string source) => InterstateRegex.Replace(source, InterstateOutPattern);

    private static string FormUsHighwayTts(string source) => UsHighwayRegex.Replace(source, UsHighwayOutPattern);

    private static string ProcessStatesTts(string source)
    {
        foreach ((Regex regex, string replacement) in StateRoutes)
        {
            string tts = regex.Replace(source, replacement);
            if (tts != source)
            {
                return tts;
            }
        }

        return source;
    }

    private static string ProcessCountysTts(string source)
    {
        foreach ((Regex regex, string replacement) in CountyRoutes)
        {
            string tts = regex.Replace(source, replacement);
            if (tts != source)
            {
                return tts;
            }
        }

        return source;
    }

    private static string ProcessThousandTts(string source)
    {
        string tts = source;
        foreach ((Regex regex, string replacement) in ThousandFindReplace)
        {
            tts = regex.Replace(tts, replacement);
        }

        return tts;
    }

    private static string ProcessHundredTts(string source)
    {
        string tts = source;
        foreach ((Regex regex, string replacement) in HundredFindReplace)
        {
            tts = regex.Replace(tts, replacement);
        }

        return tts;
    }

    private static string FormLeadingOhTts(string source) => LeadingOhRegex.Replace(source, LeadingOhOutPattern);
}
