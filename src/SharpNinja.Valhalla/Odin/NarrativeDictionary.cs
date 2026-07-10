// Faithful C# port of Valhalla odin NarrativeDictionary
// (valhalla/odin/narrative_dictionary.h + src/odin/narrative_dictionary.cc) @ 3.7.0.
//
// The upstream code models one localized instruction file as a class with ~60 typed "subset"
// members, each a boost ptree-loaded struct carrying a phrases map plus a handful of optional
// arrays/strings (cardinal_directions, relative_directions, ordinal_values, empty_street_name_labels,
// metric_lengths, us_customary_lengths, ferry_label, station_label, object_labels,
// empty_transit_name_labels, transit_stop_count_labels).
//
// PORT-NOTE: C++ expresses the per-subset field set through an inheritance hierarchy (StartSubset :
// PhraseSet, etc.). Here a single NarrativeSubset carries the union of all possible optional fields;
// only the fields a given subset actually needs are populated from its JSON node (exactly the fields
// upstream reads for that subset). The NarrativeBuilder reads named fields off named subsets, so the
// behavior is identical while the model is flat and simpler. The locale JSON files are verbatim
// copies of upstream locales/*.json, embedded as resources (see the .csproj).

using System.Collections.Generic;
using System.Globalization;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// One instruction subset from a localized narrative file: a sparse phrases map (keyed "0".."N")
/// plus the optional arrays/strings that subset carries. Faithful port of the upstream PhraseSet
/// hierarchy, flattened into a single union type (see file header).
/// </summary>
public sealed class NarrativeSubset
{
    /// <summary>Sparse phrase templates keyed by string id ("0", "1", ... - not necessarily dense).</summary>
    public IReadOnlyDictionary<string, string> Phrases { get; init; } = EmptyPhrases;

    public IReadOnlyList<string> CardinalDirections { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> RelativeDirections { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> OrdinalValues { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> EmptyStreetNameLabels { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> MetricLengths { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> UsCustomaryLengths { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> EmptyTransitNameLabels { get; init; } = System.Array.Empty<string>();
    public IReadOnlyList<string> ObjectLabels { get; init; } = System.Array.Empty<string>();

    public string FerryLabel { get; init; } = string.Empty;
    public string StationLabel { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> TransitStopCountLabels { get; init; } = EmptyPhrases;

    private static readonly IReadOnlyDictionary<string, string> EmptyPhrases =
        new Dictionary<string, string>(0);

    /// <summary>
    /// Returns the phrase template for the specified integer phrase id. Mirrors upstream
    /// <c>phrases.at(std::to_string(phrase_id))</c>. Throws <see cref="KeyNotFoundException"/> when the
    /// id is absent - a programming error in phrase-id selection, exactly as upstream <c>.at()</c> would.
    /// </summary>
    public string GetPhrase(int phraseId)
        => Phrases[phraseId.ToString(CultureInfo.InvariantCulture)];

    /// <summary>Returns the phrase template for the specified string phrase id.</summary>
    public string GetPhrase(string phraseId) => Phrases[phraseId];
}

/// <summary>
/// Stores the localized narrative instructions for one language tag. Faithful port of
/// <c>valhalla::odin::NarrativeDictionary</c>; each property corresponds to one upstream subset member.
/// </summary>
public sealed class NarrativeDictionary
{
    public NarrativeDictionary(string languageTag, string posixLocale)
    {
        LanguageTag = languageTag;
        PosixLocale = posixLocale;
    }

    /// <summary>The BCP-47 language tag this dictionary was loaded for (e.g. "en-US").</summary>
    public string LanguageTag { get; }

    /// <summary>The POSIX locale string from the language file (e.g. "en_US.UTF-8").</summary>
    public string PosixLocale { get; }

    // Start
    public NarrativeSubset StartSubset { get; init; } = new();
    public NarrativeSubset StartVerbalSubset { get; init; } = new();

    // Destination
    public NarrativeSubset DestinationSubset { get; init; } = new();
    public NarrativeSubset DestinationVerbalAlertSubset { get; init; } = new();
    public NarrativeSubset DestinationVerbalSubset { get; init; } = new();

    // Becomes
    public NarrativeSubset BecomesSubset { get; init; } = new();
    public NarrativeSubset BecomesVerbalSubset { get; init; } = new();

    // Continue
    public NarrativeSubset ContinueSubset { get; init; } = new();
    public NarrativeSubset ContinueVerbalAlertSubset { get; init; } = new();
    public NarrativeSubset ContinueVerbalSubset { get; init; } = new();

    // Bear
    public NarrativeSubset BearSubset { get; init; } = new();
    public NarrativeSubset BearVerbalSubset { get; init; } = new();

    // Turn
    public NarrativeSubset TurnSubset { get; init; } = new();
    public NarrativeSubset TurnVerbalSubset { get; init; } = new();

    // Sharp
    public NarrativeSubset SharpSubset { get; init; } = new();
    public NarrativeSubset SharpVerbalSubset { get; init; } = new();

    // Uturn
    public NarrativeSubset UturnSubset { get; init; } = new();
    public NarrativeSubset UturnVerbalSubset { get; init; } = new();

    // RampStraight
    public NarrativeSubset RampStraightSubset { get; init; } = new();
    public NarrativeSubset RampStraightVerbalSubset { get; init; } = new();

    // Ramp
    public NarrativeSubset RampSubset { get; init; } = new();
    public NarrativeSubset RampVerbalSubset { get; init; } = new();

    // Exit
    public NarrativeSubset ExitSubset { get; init; } = new();
    public NarrativeSubset ExitVerbalSubset { get; init; } = new();
    public NarrativeSubset ExitVisualSubset { get; init; } = new();

    // Keep
    public NarrativeSubset KeepSubset { get; init; } = new();
    public NarrativeSubset KeepVerbalSubset { get; init; } = new();

    // KeepToStayOn
    public NarrativeSubset KeepToStayOnSubset { get; init; } = new();
    public NarrativeSubset KeepToStayOnVerbalSubset { get; init; } = new();

    // Merge
    public NarrativeSubset MergeSubset { get; init; } = new();
    public NarrativeSubset MergeVerbalSubset { get; init; } = new();

    // EnterRoundabout
    public NarrativeSubset EnterRoundaboutSubset { get; init; } = new();
    public NarrativeSubset EnterRoundaboutVerbalSubset { get; init; } = new();

    // ExitRoundabout
    public NarrativeSubset ExitRoundaboutSubset { get; init; } = new();
    public NarrativeSubset ExitRoundaboutVerbalSubset { get; init; } = new();

    // EnterFerry
    public NarrativeSubset EnterFerrySubset { get; init; } = new();
    public NarrativeSubset EnterFerryVerbalSubset { get; init; } = new();

    // TransitConnectionStart
    public NarrativeSubset TransitConnectionStartSubset { get; init; } = new();
    public NarrativeSubset TransitConnectionStartVerbalSubset { get; init; } = new();

    // TransitConnectionTransfer
    public NarrativeSubset TransitConnectionTransferSubset { get; init; } = new();
    public NarrativeSubset TransitConnectionTransferVerbalSubset { get; init; } = new();

    // TransitConnectionDestination
    public NarrativeSubset TransitConnectionDestinationSubset { get; init; } = new();
    public NarrativeSubset TransitConnectionDestinationVerbalSubset { get; init; } = new();

    // Depart
    public NarrativeSubset DepartSubset { get; init; } = new();
    public NarrativeSubset DepartVerbalSubset { get; init; } = new();

    // Arrive
    public NarrativeSubset ArriveSubset { get; init; } = new();
    public NarrativeSubset ArriveVerbalSubset { get; init; } = new();

    // Transit
    public NarrativeSubset TransitSubset { get; init; } = new();
    public NarrativeSubset TransitVerbalSubset { get; init; } = new();

    // TransitRemainOn
    public NarrativeSubset TransitRemainOnSubset { get; init; } = new();
    public NarrativeSubset TransitRemainOnVerbalSubset { get; init; } = new();

    // TransitTransfer
    public NarrativeSubset TransitTransferSubset { get; init; } = new();
    public NarrativeSubset TransitTransferVerbalSubset { get; init; } = new();

    // Post transition verbal
    public NarrativeSubset PostTransitionVerbalSubset { get; init; } = new();

    // Post transition transit verbal
    public NarrativeSubset PostTransitionTransitVerbalSubset { get; init; } = new();

    // Verbal multi-cue
    public NarrativeSubset VerbalMultiCueSubset { get; init; } = new();

    // Approach verbal alert
    public NarrativeSubset ApproachVerbalAlertSubset { get; init; } = new();

    // Pass
    public NarrativeSubset PassSubset { get; init; } = new();

    // Indoor / level change
    public NarrativeSubset ElevatorSubset { get; init; } = new();
    public NarrativeSubset StepsSubset { get; init; } = new();
    public NarrativeSubset EscalatorSubset { get; init; } = new();
    public NarrativeSubset LevelChangeSubset { get; init; } = new();
    public NarrativeSubset EnterBuildingSubset { get; init; } = new();
    public NarrativeSubset ExitBuildingSubset { get; init; } = new();

    // Park vehicle
    public NarrativeSubset ParkVehicleSubset { get; init; } = new();
}
