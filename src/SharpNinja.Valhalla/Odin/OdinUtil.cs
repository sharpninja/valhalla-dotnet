// Faithful C# port of Valhalla odin util (valhalla/odin/util.h + src/odin/util.cc) @ 3.7.0.
// Source: valhalla/odin/util.h, src/odin/util.cc
//
// Public members are PascalCase. Algorithm boundaries / thresholds mirror the C++ exactly.
//
// PORT-NOTE (DEFER): The localization machinery in odin/util.cc - get_localized_time,
// get_localized_date, get_locales, get_locales_json, parse_string_into_locale, Bcp47Locale, and the
// NarrativeDictionary loader (load_narrative_locals) - belongs to the narrativebuilder /
// narrative_dictionary prose family, which is explicitly DEFERRED for this structural port. Those
// functions produce localized prose text, not maneuver structure, so they are intentionally omitted.
// turn_lane_direction (OSRM modifier strings) is likewise prose-shaped and omitted.
//
// What is ported here are the structure-relevant, locale-independent helpers used by the maneuver
// builder and the enhanced trip path: GetQuotedString, IsSimilarTurnDegree, GetWordCount, and
// StrlenUtf8.

using System.Text;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// odin utility helpers. Faithful port of the locale-independent functions in
/// <c>valhalla::odin</c> (odin/util.h).
/// </summary>
public static class OdinUtil
{
    /// <summary>Limit by consecutive count flag default. Faithful port of <c>kLimitByConseuctiveCount</c>.</summary>
    public const bool LimitByConsecutiveCount = true;

    /// <summary>Maximum number of sign elements. Faithful port of <c>kElementMaxCount</c>.</summary>
    public const uint ElementMaxCount = 4;

    /// <summary>Maximum number of verbal alert sign elements. Faithful port of <c>kVerbalAlertElementMaxCount</c>.</summary>
    public const uint VerbalAlertElementMaxCount = 1;

    /// <summary>Maximum number of verbal pre-transition sign elements. Faithful port of <c>kVerbalPreElementMaxCount</c>.</summary>
    public const uint VerbalPreElementMaxCount = 2;

    /// <summary>Maximum number of verbal post-transition sign elements. Faithful port of <c>kVerbalPostElementMaxCount</c>.</summary>
    public const uint VerbalPostElementMaxCount = 2;

    /// <summary>Verbal delimiter (", "). Faithful port of <c>kVerbalDelim</c>.</summary>
    public const string VerbalDelim = ", ";

    /// <summary>
    /// Returns the specified item surrounded with quotes. Faithful port of <c>GetQuotedString</c>.
    /// </summary>
    /// <param name="item">The text to surround with quotes.</param>
    /// <returns>The specified item surrounded with quotes.</returns>
    public static string GetQuotedString(string item) => "\"" + item + "\"";

    /// <summary>
    /// Returns true if the intersecting turn degree is within the threshold of the path turn degree
    /// in the specified direction. Faithful port of <c>IsSimilarTurnDegree</c>.
    /// </summary>
    /// <param name="pathTurnDegree">The path turn degree.</param>
    /// <param name="intersectingTurnDegree">The intersecting edge turn degree.</param>
    /// <param name="isRight">Whether to measure the delta in the right (clockwise) direction.</param>
    /// <param name="turnDegreeThreshold">The maximum allowed delta (default 40).</param>
    /// <returns>True if the turn degrees are similar within the threshold.</returns>
    public static bool IsSimilarTurnDegree(
        uint pathTurnDegree,
        uint intersectingTurnDegree,
        bool isRight,
        uint turnDegreeThreshold = 40)
    {
        uint turnDegreeDelta;
        if (isRight)
        {
            turnDegreeDelta = ((intersectingTurnDegree - pathTurnDegree) + 360) % 360;
        }
        else
        {
            turnDegreeDelta = ((pathTurnDegree - intersectingTurnDegree) + 360) % 360;
        }

        return turnDegreeDelta <= turnDegreeThreshold;
    }

    /// <summary>
    /// Returns the number of words in the specified street name. Words are separated by spaces, any
    /// whitespace, or punctuation. Faithful port of <c>get_word_count</c>.
    /// </summary>
    /// <param name="streetName">The street name to count words in.</param>
    /// <returns>The number of words.</returns>
    public static int GetWordCount(string streetName)
    {
        int wordCount = 0;
        int pos = 0;
        int end = streetName.Length;

        while (pos != end)
        {
            // Skip over space, white space, and punctuation
            while (pos != end && (streetName[pos] == ' ' || IsSpace(streetName[pos]) || IsPunct(streetName[pos])))
            {
                ++pos;
            }

            // Word found - increment
            wordCount += pos != end ? 1 : 0;

            // Skip over letters in word
            while (pos != end && streetName[pos] != ' ' && !IsSpace(streetName[pos]) && !IsPunct(streetName[pos]))
            {
                ++pos;
            }
        }

        return wordCount;
    }

    /// <summary>
    /// Returns the number of UTF-8 code points (characters) in the specified string. Faithful port
    /// of <c>strlen_utf8</c> - counts bytes that are not UTF-8 continuation bytes (0x80..0xBF).
    /// </summary>
    /// <param name="str">The string to measure.</param>
    /// <returns>The number of UTF-8 characters.</returns>
    public static int StrlenUtf8(string str)
    {
        int length = 0;
        foreach (byte c in Encoding.UTF8.GetBytes(str))
        {
            if ((c & 0xC0) != 0x80)
            {
                ++length;
            }
        }

        return length;
    }

    // Faithful port of std::isspace for the ASCII "C" locale (the locale get_word_count operates in).
    private static bool IsSpace(char c)
        => c == ' ' || c == '\t' || c == '\n' || c == '\v' || c == '\f' || c == '\r';

    // Faithful port of std::ispunct for the ASCII "C" locale: printable, not alphanumeric, not space.
    private static bool IsPunct(char c)
    {
        if (c > 0x7F)
        {
            // Non-ASCII bytes are not punctuation under the "C" locale.
            return false;
        }

        bool isPrintable = c > ' ' && c < 0x7F;
        bool isAlnum = (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        return isPrintable && !isAlnum;
    }
}
