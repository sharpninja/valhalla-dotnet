// Faithful C# port of Valhalla odin Sign + Signs
// (valhalla/odin/sign.h + valhalla/odin/signs.h + src/odin/signs.cc) @ 3.7.0.
// Source: valhalla/odin/sign.h, valhalla/odin/signs.h, src/odin/signs.cc
//
// Public members are PascalCase. Sorting, counting, trimming, and string-building algorithms mirror
// the C++ exactly (including the guide branch/toward round/truncate split).
//
// PORT-NOTE: The optional VerbalTextFormatter / MarkupFormatter parameters on the Get*String /
// ListToString methods are ported (A2). The verbal narrative path passes a formatter so sign text is
// expanded for text-to-speech; the WRITTEN path passes none (verbal_formatter == nullptr branch) and
// uses the raw sign text. The pronunciation field on Sign is carried (structural data) and is only
// consulted when the MarkupFormatter has phoneme markup enabled (disabled by default).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// A single odin sign element (text + is-route-number + consecutive count + optional
/// pronunciation). Faithful port of <c>valhalla::odin::Sign</c> (odin/sign.h).
/// </summary>
public sealed class OdinSign : IEquatable<OdinSign>
{
    private string _text;
    private bool _isRouteNumber;
    private uint _consecutiveCount;
    private Pronunciation? _pronunciation;

    /// <summary>
    /// Constructor. Faithful port of
    /// <c>Sign(const std::string&amp;, const bool, const std::optional&lt;Pronunciation&gt;&amp;)</c>.
    /// </summary>
    /// <param name="text">Text string.</param>
    /// <param name="isRouteNumber">Whether the sign element is a reference route number.</param>
    /// <param name="pronunciation">The pronunciation of this sign (optional).</param>
    public OdinSign(string text, bool isRouteNumber, Pronunciation? pronunciation = null)
    {
        _text = text;
        _isRouteNumber = isRouteNumber;
        _consecutiveCount = 0;
        _pronunciation = pronunciation;
    }

    /// <summary>Returns the sign text. Faithful port of <c>text()</c>.</summary>
    public string Text() => _text;

    /// <summary>
    /// Returns true if the sign element is a reference route number such as "I 81 South".
    /// Faithful port of <c>is_route_number()</c>.
    /// </summary>
    public bool IsRouteNumber() => _isRouteNumber;

    /// <summary>
    /// Returns the frequency of this sign within a set of consecutive signs. Faithful port of
    /// <c>consecutive_count()</c>.
    /// </summary>
    public uint ConsecutiveCount() => _consecutiveCount;

    /// <summary>
    /// Sets the frequency of this sign within a set of consecutive signs. Faithful port of
    /// <c>set_consecutive_count()</c>.
    /// </summary>
    public void SetConsecutiveCount(uint consecutiveCount) => _consecutiveCount = consecutiveCount;

    /// <summary>Returns the pronunciation of this sign. Faithful port of <c>pronunciation()</c>.</summary>
    public Pronunciation? GetPronunciation() => _pronunciation;

    /// <summary>Equality - mirrors C++ <c>operator==</c> (text, is_route_number, consecutive_count).</summary>
    public bool Equals(OdinSign? other)
    {
        if (other is null)
        {
            return false;
        }

        // C++ operator== compares text_, is_route_number_, and consecutive_count_.
        return _text == other._text
               && _isRouteNumber == other._isRouteNumber
               && _consecutiveCount == other._consecutiveCount;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as OdinSign);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_text, _isRouteNumber, _consecutiveCount);
}

/// <summary>
/// The collection of odin sign lists associated with a maneuver (exit / guide / junction). Faithful
/// port of <c>valhalla::odin::Signs</c> (odin/signs.h, src/odin/signs.cc).
/// </summary>
public sealed class Signs : IEquatable<Signs>
{
    // The number of guide sign types (i.e. branch and toward). Faithful port of kNumberOfGuideSignTypes.
    private const uint NumberOfGuideSignTypes = 2;

    private readonly List<OdinSign> _exitNumberList = new();
    private readonly List<OdinSign> _exitBranchList = new();
    private readonly List<OdinSign> _exitTowardList = new();
    private readonly List<OdinSign> _exitNameList = new();
    private readonly List<OdinSign> _guideBranchList = new();
    private readonly List<OdinSign> _guideTowardList = new();
    private readonly List<OdinSign> _junctionNameList = new();

    /// <summary>Default constructor. Faithful port of <c>Signs()</c>.</summary>
    public Signs()
    {
    }

    /// <summary>
    /// Replaces this <see cref="Signs"/>'s lists with deep copies of <paramref name="other"/>'s.
    /// Faithful port of the C++ protobuf copy-assignment used by the maneuver builder
    /// (<c>*(mutable_signs()) = other.signs()</c>). Each <see cref="OdinSign"/> is copied so the two
    /// instances do not alias their elements.
    /// </summary>
    public void CopyFrom(Signs other)
    {
        ReplaceList(_exitNumberList, other._exitNumberList);
        ReplaceList(_exitBranchList, other._exitBranchList);
        ReplaceList(_exitTowardList, other._exitTowardList);
        ReplaceList(_exitNameList, other._exitNameList);
        ReplaceList(_guideBranchList, other._guideBranchList);
        ReplaceList(_guideTowardList, other._guideTowardList);
        ReplaceList(_junctionNameList, other._junctionNameList);
    }

    private static void ReplaceList(List<OdinSign> dest, List<OdinSign> source)
    {
        dest.Clear();
        foreach (OdinSign sign in source)
        {
            var copy = new OdinSign(sign.Text(), sign.IsRouteNumber(), sign.GetPronunciation());
            copy.SetConsecutiveCount(sign.ConsecutiveCount());
            dest.Add(copy);
        }
    }

    /// <summary>
    /// Sort signs by descending consecutive count order. Faithful port of <c>Sort()</c>.
    /// </summary>
    public static void Sort(List<OdinSign> signs)
    {
        // C++: std::sort with comparator (b.consecutive_count() < a.consecutive_count()).
        // Use a stable sort to mirror common implementations and keep ties in input order.
        var ordered = signs
            .Select((sign, index) => (sign, index))
            .OrderByDescending(t => t.sign.ConsecutiveCount())
            .ThenBy(t => t.index)
            .Select(t => t.sign)
            .ToList();
        signs.Clear();
        signs.AddRange(ordered);
    }

    /// <summary>
    /// Increment consecutive counts for matching prev/curr signs and sort both lists. Faithful port
    /// of <c>CountAndSort()</c>.
    /// </summary>
    public static void CountAndSort(List<OdinSign> prevSigns, List<OdinSign> currSigns)
    {
        // Increment count for consecutive exit signs
        foreach (OdinSign currSign in currSigns)
        {
            foreach (OdinSign prevSign in prevSigns)
            {
                if (currSign.Text() == prevSign.Text())
                {
                    currSign.SetConsecutiveCount(currSign.ConsecutiveCount() + 1);
                    prevSign.SetConsecutiveCount(currSign.ConsecutiveCount());
                }
            }
        }

        // Sort the previous and current exit signs by descending consecutive count
        Sort(prevSigns);
        Sort(currSigns);
    }

    /// <summary>
    /// Returns a trimmed copy of the supplied signs, optionally limited by max count and/or by
    /// consecutive count. Faithful port of <c>TrimSigns()</c>.
    /// </summary>
    public static List<OdinSign> TrimSigns(
        IReadOnlyList<OdinSign> signs,
        uint maxCount = 0,
        bool limitByConsecutiveCount = false)
    {
        var trimmedSigns = new List<OdinSign>();

        uint count = 0;
        uint consecutiveCount = 0;

        foreach (OdinSign sign in signs)
        {
            // If supplied, limit by max count
            if (maxCount > 0 && count == maxCount)
            {
                break;
            }

            // if requested, process consecutive exit counts
            if (limitByConsecutiveCount)
            {
                // Set consecutive count of first sign
                if (count == 0)
                {
                    consecutiveCount = sign.ConsecutiveCount();
                }

                // Limit if consecutive count does not match
                else if (sign.ConsecutiveCount() != consecutiveCount)
                {
                    break;
                }
            }

            trimmedSigns.Add(sign);
            ++count;
        }

        return trimmedSigns;
    }

    /// <summary>Exit number sign list. Faithful port of <c>exit_number_list()</c>.</summary>
    public IReadOnlyList<OdinSign> ExitNumberList() => _exitNumberList;

    /// <summary>Mutable exit number sign list. Faithful port of <c>mutable_exit_number_list()</c>.</summary>
    public List<OdinSign> MutableExitNumberList() => _exitNumberList;

    /// <summary>Returns the exit number string. Faithful port of <c>GetExitNumberString()</c>.</summary>
    public string GetExitNumberString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
        => ListToString(_exitNumberList, maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);

    /// <summary>Exit branch sign list. Faithful port of <c>exit_branch_list()</c>.</summary>
    public IReadOnlyList<OdinSign> ExitBranchList() => _exitBranchList;

    /// <summary>Mutable exit branch sign list. Faithful port of <c>mutable_exit_branch_list()</c>.</summary>
    public List<OdinSign> MutableExitBranchList() => _exitBranchList;

    /// <summary>Returns the exit branch string. Faithful port of <c>GetExitBranchString()</c>.</summary>
    public string GetExitBranchString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
        => ListToString(_exitBranchList, maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);

    /// <summary>Exit toward sign list. Faithful port of <c>exit_toward_list()</c>.</summary>
    public IReadOnlyList<OdinSign> ExitTowardList() => _exitTowardList;

    /// <summary>Mutable exit toward sign list. Faithful port of <c>mutable_exit_toward_list()</c>.</summary>
    public List<OdinSign> MutableExitTowardList() => _exitTowardList;

    /// <summary>Returns the exit toward string. Faithful port of <c>GetExitTowardString()</c>.</summary>
    public string GetExitTowardString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
        => ListToString(_exitTowardList, maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);

    /// <summary>Exit name sign list. Faithful port of <c>exit_name_list()</c>.</summary>
    public IReadOnlyList<OdinSign> ExitNameList() => _exitNameList;

    /// <summary>Mutable exit name sign list. Faithful port of <c>mutable_exit_name_list()</c>.</summary>
    public List<OdinSign> MutableExitNameList() => _exitNameList;

    /// <summary>Returns the exit name string. Faithful port of <c>GetExitNameString()</c>.</summary>
    public string GetExitNameString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
        => ListToString(_exitNameList, maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);

    /// <summary>Guide branch sign list. Faithful port of <c>guide_branch_list()</c>.</summary>
    public IReadOnlyList<OdinSign> GuideBranchList() => _guideBranchList;

    /// <summary>Mutable guide branch sign list. Faithful port of <c>mutable_guide_branch_list()</c>.</summary>
    public List<OdinSign> MutableGuideBranchList() => _guideBranchList;

    /// <summary>Returns the guide branch string. Faithful port of <c>GetGuideBranchString()</c>.</summary>
    public string GetGuideBranchString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
        => ListToString(_guideBranchList, maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);

    /// <summary>Guide toward sign list. Faithful port of <c>guide_toward_list()</c>.</summary>
    public IReadOnlyList<OdinSign> GuideTowardList() => _guideTowardList;

    /// <summary>Mutable guide toward sign list. Faithful port of <c>mutable_guide_toward_list()</c>.</summary>
    public List<OdinSign> MutableGuideTowardList() => _guideTowardList;

    /// <summary>Returns the guide toward string. Faithful port of <c>GetGuideTowardString()</c>.</summary>
    public string GetGuideTowardString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
        => ListToString(_guideTowardList, maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);

    /// <summary>
    /// Returns the merged guide string (branch then toward, split by round/truncate when both
    /// exist). Faithful port of <c>GetGuideString()</c>.
    /// </summary>
    public string GetGuideString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
    {
        string guideString = string.Empty;

        // If both branch and toward exist
        // and either unlimited max count or max count is greater than 1
        // then process guide sign info splitting between branch and toward signs
        if (HasGuideBranch() && HasGuideToward() && (maxCount == 0 || maxCount > 1))
        {
            // Round using floating point division
            string guideBranch = GetGuideBranchString(
                (uint)Math.Round((float)maxCount / NumberOfGuideSignTypes, MidpointRounding.AwayFromZero),
                limitByConsecutiveCount,
                delim,
                verbalFormatter,
                markupFormatter);

            // Truncate using integer division
            string guideToward = GetGuideTowardString(
                maxCount / NumberOfGuideSignTypes,
                limitByConsecutiveCount,
                delim,
                verbalFormatter,
                markupFormatter);
            guideString = guideBranch + delim + guideToward;
        }
        else if (HasGuideBranch())
        {
            guideString = GetGuideBranchString(maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);
        }
        else if (HasGuideToward())
        {
            guideString = GetGuideTowardString(maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);
        }

        return guideString;
    }

    /// <summary>
    /// Returns a new merged list of guide signs (branch then toward, split by round/truncate when
    /// both exist). Faithful port of <c>GetGuideSigns()</c>.
    /// </summary>
    public List<OdinSign> GetGuideSigns(uint maxCount = 0, bool limitByConsecutiveCount = false)
    {
        // If both branch and toward exist
        // and either unlimited max count or max count is greater than 1
        // then process guide sign info splitting between branch and toward signs
        if (HasGuideBranch() && HasGuideToward() && maxCount != 1)
        {
            // Round using floating point division
            List<OdinSign> guideBranch = TrimSigns(
                _guideBranchList,
                (uint)Math.Round((float)maxCount / NumberOfGuideSignTypes, MidpointRounding.AwayFromZero),
                limitByConsecutiveCount);

            // Truncate using integer division
            List<OdinSign> guideToward = TrimSigns(
                _guideTowardList,
                maxCount / NumberOfGuideSignTypes,
                limitByConsecutiveCount);

            var guideSigns = new List<OdinSign>(guideBranch.Count + guideToward.Count);
            guideSigns.AddRange(guideBranch);
            guideSigns.AddRange(guideToward);
            return guideSigns;
        }

        if (HasGuideBranch())
        {
            return TrimSigns(_guideBranchList, maxCount, limitByConsecutiveCount);
        }

        if (HasGuideToward())
        {
            return TrimSigns(_guideTowardList, maxCount, limitByConsecutiveCount);
        }

        return new List<OdinSign>();
    }

    /// <summary>Junction name sign list. Faithful port of <c>junction_name_list()</c>.</summary>
    public IReadOnlyList<OdinSign> JunctionNameList() => _junctionNameList;

    /// <summary>Mutable junction name sign list. Faithful port of <c>mutable_junction_name_list()</c>.</summary>
    public List<OdinSign> MutableJunctionNameList() => _junctionNameList;

    /// <summary>Returns the junction name string. Faithful port of <c>GetJunctionNameString()</c>.</summary>
    public string GetJunctionNameString(uint maxCount = 0, bool limitByConsecutiveCount = false, string delim = "/",
        VerbalTextFormatter? verbalFormatter = null, MarkupFormatter? markupFormatter = null)
        => ListToString(_junctionNameList, maxCount, limitByConsecutiveCount, delim, verbalFormatter, markupFormatter);

    /// <summary>True if any exit sign exists. Faithful port of <c>HasExit()</c>.</summary>
    public bool HasExit() => HasExitNumber() || HasExitBranch() || HasExitToward() || HasExitName();

    /// <summary>True if an exit number sign exists. Faithful port of <c>HasExitNumber()</c>.</summary>
    public bool HasExitNumber() => _exitNumberList.Count > 0;

    /// <summary>True if an exit branch sign exists. Faithful port of <c>HasExitBranch()</c>.</summary>
    public bool HasExitBranch() => _exitBranchList.Count > 0;

    /// <summary>True if an exit toward sign exists. Faithful port of <c>HasExitToward()</c>.</summary>
    public bool HasExitToward() => _exitTowardList.Count > 0;

    /// <summary>True if an exit name sign exists. Faithful port of <c>HasExitName()</c>.</summary>
    public bool HasExitName() => _exitNameList.Count > 0;

    /// <summary>True if any guide sign exists. Faithful port of <c>HasGuide()</c>.</summary>
    public bool HasGuide() => HasGuideBranch() || HasGuideToward();

    /// <summary>True if a guide branch sign exists. Faithful port of <c>HasGuideBranch()</c>.</summary>
    public bool HasGuideBranch() => _guideBranchList.Count > 0;

    /// <summary>True if a guide toward sign exists. Faithful port of <c>HasGuideToward()</c>.</summary>
    public bool HasGuideToward() => _guideTowardList.Count > 0;

    /// <summary>True if a junction name sign exists. Faithful port of <c>HasJunctionName()</c>.</summary>
    public bool HasJunctionName() => _junctionNameList.Count > 0;

    /// <summary>Returns a debug string. Faithful port of <c>ToString()</c>.</summary>
    public override string ToString()
    {
        var signsString = new StringBuilder();

        signsString.Append("exit_numbers=").Append(GetExitNumberString());
        signsString.Append(" | exit_onto_streets=").Append(GetExitBranchString());
        signsString.Append(" | exit_toward_locations=").Append(GetExitTowardString());
        signsString.Append(" | exit_names=").Append(GetExitNameString());
        signsString.Append(" | guide_onto_streets=").Append(GetGuideBranchString());
        signsString.Append(" | guide_toward_locations=").Append(GetGuideTowardString());
        signsString.Append(" | junction_names=").Append(GetJunctionNameString());

        return signsString.ToString();
    }

    /// <summary>Equality - mirrors C++ <c>operator==</c> (all seven lists element-wise).</summary>
    public bool Equals(Signs? rhs)
    {
        if (rhs is null)
        {
            return false;
        }

        return _exitNumberList.SequenceEqual(rhs._exitNumberList)
               && _exitBranchList.SequenceEqual(rhs._exitBranchList)
               && _exitTowardList.SequenceEqual(rhs._exitTowardList)
               && _exitNameList.SequenceEqual(rhs._exitNameList)
               && _guideBranchList.SequenceEqual(rhs._guideBranchList)
               && _guideTowardList.SequenceEqual(rhs._guideTowardList)
               && _junctionNameList.SequenceEqual(rhs._junctionNameList);
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Signs);

    /// <inheritdoc/>
    public override int GetHashCode() => _exitNumberList.Count ^ _exitBranchList.Count;

    // Faithful port of ListToString(). When a verbal formatter is supplied (the verbal narrative
    // path) each sign is run through it (optionally applying markup); otherwise the raw sign.Text()
    // is used (the written path).
    private static string ListToString(
        IReadOnlyList<OdinSign> signs,
        uint maxCount,
        bool limitByConsecutiveCount,
        string delim,
        VerbalTextFormatter? verbalFormatter = null,
        MarkupFormatter? markupFormatter = null)
    {
        var signString = new StringBuilder();
        uint count = 0;

        // C++ initializes consecutive_count to -1 (unsigned wraparound); it is only read after being
        // set when count == 0, so the initial value is irrelevant to behavior.
        uint consecutiveCount = 0;

        foreach (OdinSign sign in signs)
        {
            // If supplied, limit by max count
            if (maxCount > 0 && count == maxCount)
            {
                break;
            }

            // if requested, process consecutive exit counts
            if (limitByConsecutiveCount)
            {
                // Set consecutive count of first sign
                if (count == 0)
                {
                    consecutiveCount = sign.ConsecutiveCount();
                }

                // Limit if consecutive count does not match
                else if (sign.ConsecutiveCount() != consecutiveCount)
                {
                    break;
                }
            }

            // Add delimiter
            if (signString.Length != 0)
            {
                signString.Append(delim);
            }

            // Concatenate exit text (verbally formatted if a formatter is supplied) and update count
            signString.Append(verbalFormatter != null ? verbalFormatter.Format(sign, markupFormatter) : sign.Text());
            ++count;
        }

        return signString.ToString();
    }
}
