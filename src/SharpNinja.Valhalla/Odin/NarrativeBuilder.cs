// Faithful C# port of Valhalla odin NarrativeBuilder maneuver-instruction path
// (valhalla/odin/narrativebuilder.h + src/odin/narrativebuilder.cc) @ 3.7.0.
// Source: valhalla/odin/narrativebuilder.{h,cc}, valhalla/odin/narrative_dictionary.h (phrase tags).
//
// SCOPE: this slice ports the WRITTEN maneuver.set_instruction(FormXInstruction(...)) path (A1) AND
// the VERBAL narrative strings (A2) for the DRIVING maneuver families, en-US, end to end:
//   - the verbal formers (FormVerbalStart / Continue / AlertContinue / Turn / AlertTurn / Uturn /
//     AlertUturn / RampStraight / Ramp / Exit / Keep / KeepToStayOn / Merge / EnterRoundabout /
//     ExitRoundabout / EnterFerry with their alert + succinct variants, Becomes, Destination),
//   - FormVerbalPostTransitionInstruction + FormLength / FormMetricLength / FormUsCustomaryLength,
//   - FormVerbalMultiCue (the post-pass + IsVerbalMultiCuePossible / IsWithinVerbalMultiCueBounds),
//   - GetPluralCategory (base) + FormVerbalAlertApproachInstruction.
// Build() sets the verbal succinct / transition-alert / pre / post instructions for each driving type
// exactly as src/odin/narrativebuilder.cc Build() (lines 52-554), then runs FormVerbalMultiCue.
// A3 adds the INDOOR / level-change / pass maneuver families that the ported engine can produce:
// FormElevatorInstruction / FormStepsInstruction / FormEscalatorInstruction / FormEnterBuildingInstruction
// / FormExitBuildingInstruction / FormGenericLevelChangeInstruction / FormParkVehicleInstruction /
// FormPassInstruction, with their Build() case wiring (see narrativebuilder.cc Build() 467-543).
// STILL DEFERRED to later slices: the TRANSIT maneuver families (see the DEFER PORT-NOTE in Build())
// and the per-locale grammar subclasses / articulated prepositions (A4). Bike-share (A1) is complete:
// the FormBssManeuverType prefix rides on every maneuver instruction (see the end of Build()).
//
// PORT-NOTE (DEFER): FormDestinationInstruction / FormVerbal[Alert]DestinationInstruction read
// trip_path_->GetDestination().name()/.street() upstream. The ported EnhancedTripLeg does not carry
// proto Location objects (see EnhancedTripPath.cs header), so the destination name/street degrade to
// empty exactly as an empty upstream location would - phrase_id stays at the lower (no-destination)
// value. Side-of-street relative direction is still honored via the maneuver type.
//
// PORT-NOTE: the verbal path threads maneuver.VerbalFormatter() (a baldr::VerbalTextFormatter, e.g.
// VerbalTextFormatterUs) plus the MarkupFormatter through FormStreetNames and Signs.Get*String so
// route numbers expand for TTS (e.g. "US 322" -> "U.S. 3 22"). The WRITTEN path passes no formatter
// (verbal_formatter == nullptr) and uses the raw value. Markup (phoneme) formatting is disabled by
// default (upstream config default), so MarkupFormatter.FormatPhonemeElement returns null and the
// plain formatted text is used - a documented no-op for the driving verbal path (see MarkupFormatter).
// boost::replace_all maps to string.Replace; phrases.at(id) maps to NarrativeSubset.GetPhrase(id).
//
// PORT-NOTE: upstream reads option_roundabout_exits from a hard-coded `true` (with a TODO to source
// it from options_); this port keeps the same hard-coded `true` to match the oracle byte-for-byte.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Builds the localized WRITTEN maneuver instructions for a maneuver list. Faithful port of
/// <c>valhalla::odin::NarrativeBuilder</c> (written driving path - see file header).
/// </summary>
public class NarrativeBuilder
{
    // Phrase tags (narrative_dictionary.h).
    private const string CardinalDirectionTag = "<CARDINAL_DIRECTION>";
    private const string RelativeDirectionTag = "<RELATIVE_DIRECTION>";
    private const string OrdinalValueTag = "<ORDINAL_VALUE>";
    private const string StreetNamesTag = "<STREET_NAMES>";
    private const string PreviousStreetNamesTag = "<PREVIOUS_STREET_NAMES>";
    private const string BeginStreetNamesTag = "<BEGIN_STREET_NAMES>";
    private const string CrossStreetNamesTag = "<CROSS_STREET_NAMES>";
    private const string RoundaboutExitStreetNamesTag = "<ROUNDABOUT_EXIT_STREET_NAMES>";
    private const string RoundaboutExitBeginStreetNamesTag = "<ROUNDABOUT_EXIT_BEGIN_STREET_NAMES>";
    private const string DestinationTag = "<DESTINATION>";
    private const string NumberSignTag = "<NUMBER_SIGN>";
    private const string BranchSignTag = "<BRANCH_SIGN>";
    private const string TowardSignTag = "<TOWARD_SIGN>";
    private const string NameSignTag = "<NAME_SIGN>";
    private const string JunctionNameTag = "<JUNCTION_NAME>";
    private const string FerryLabelTag = "<FERRY_LABEL>";
    private const string LengthTag = "<LENGTH>";
    private const string CurrentVerbalCueTag = "<CURRENT_VERBAL_CUE>";
    private const string NextVerbalCueTag = "<NEXT_VERBAL_CUE>";
    private const string KilometersTag = "<KILOMETERS>";
    private const string MetersTag = "<METERS>";
    private const string MilesTag = "<MILES>";
    private const string TenthsOfMilesTag = "<TENTHS_OF_MILE>";
    private const string FeetTag = "<FEET>";
    private const string LevelTag = "<LEVEL>";
    private const string ObjectLabelTag = "<OBJECT_LABEL>";

    // Metric length indexes (narrative_dictionary.h).
    private const int KilometersIndex = 0;
    private const int OneKilometerIndex = 1;
    private const int MetersIndex = 2;
    private const int SmallMetersIndex = 3;

    // US customary length indexes (narrative_dictionary.h).
    private const int MilesIndex = 0;
    private const int OneMileIndex = 1;
    private const int HalfMileIndex = 2;
    private const int QuarterMileIndex = 3;
    private const int FeetIndex = 4;
    private const int SmallFeetIndex = 5;

    // Plural category keys (narrative_dictionary.h). Protected so the per-locale subclasses
    // (NarrativeBuilder_csCZ / _hiIN / _ruRU) share them in their GetPluralCategory overrides.
    protected const string PluralCategoryOneKey = "one";
    protected const string PluralCategoryFewKey = "few";
    protected const string PluralCategoryOtherKey = "other";

    // Verbal multi-cue thresholds / minimum ramp length (narrativebuilder.cc anonymous namespace).
    private const int VerbalMultiCueTimeThreshold = 13;
    private const int VerbalMultiCueTimeStartManeuverThreshold = VerbalMultiCueTimeThreshold * 3;
    private const float VerbalPostMinimumRampLength = 2.0f; // Kilometers
    private const float VerbalAlertMergePriorManeuverMinimumLength = VerbalPostMinimumRampLength;

    // Empty street-name label indexes (narrative_dictionary.h).
    private const int WalkwayIndex = 0;
    private const int CyclewayIndex = 1;
    private const int MountainBikeTrailIndex = 2;
    private const int PedestrianCrossingIndex = 3;
    private const int StepsIndex = 4;
    private const int BridgeIndex = 5;
    private const int TunnelIndex = 6;

    // Pass object-label indexes (narrative_dictionary.h) - into pass_subset.object_labels.
    private const int GateIndex = 0;
    private const int BollardIndex = 1;
    private const int StreetIntersectionIndex = 2;

    // The written path uses max_count 0 and delim "/" (narrativebuilder.h defaults).
    private const uint WrittenElementMaxCount = 4; // kElementMaxCount (util.h)
    private const string WrittenDelim = "/";

    // Lower and upper bounds for roundabout_exit_count (narrativebuilder.cc).
    private const uint RoundaboutExitCountLowerBound = 1;
    private const uint RoundaboutExitCountUpperBound = 10;

    /// <summary>The directions options (units, language). Faithful port of <c>options_</c>.</summary>
    protected readonly Options Options;

    /// <summary>The enhanced trip path (may be null). Faithful port of <c>trip_path_</c>.</summary>
    protected readonly EnhancedTripLeg? TripPath;

    /// <summary>The localized narrative dictionary. Faithful port of <c>dictionary_</c>.</summary>
    protected readonly NarrativeDictionary Dictionary;

    /// <summary>The markup (phoneme / TTS) formatter. Faithful port of <c>markup_formatter_</c>.</summary>
    protected readonly MarkupFormatter MarkupFormatter;

    /// <summary>
    /// When true, each built instruction is post-processed by <see cref="FormArticulatedPrepositions"/>
    /// (e.g. it-IT combines " su il " -> " sul "). Faithful port of <c>articulated_preposition_enabled_</c>
    /// (default false); per-locale subclasses set it in their constructor.
    /// </summary>
    protected bool _articulatedPrepositionEnabled;

    /// <summary>Constructor. Faithful port of the <c>NarrativeBuilder</c> constructor.</summary>
    public NarrativeBuilder(
        Options options,
        EnhancedTripLeg? tripPath,
        NarrativeDictionary dictionary,
        MarkupFormatter? markupFormatter = null)
    {
        Options = options;
        TripPath = tripPath;
        Dictionary = dictionary;
        MarkupFormatter = markupFormatter ?? new MarkupFormatter();
    }

    /// <summary>
    /// Dispatches each maneuver to its written FormXInstruction method (via
    /// <see cref="Maneuver.SetInstruction"/>) and, for the driving families, sets the verbal
    /// succinct / transition-alert / pre-transition / post-transition instructions exactly as
    /// upstream <c>NarrativeBuilder::Build()</c> does, then runs the verbal multi-cue post-pass. The
    /// out-of-scope maneuver families (transit / pedestrian / indoor) still leave their prose empty.
    /// </summary>
    public virtual void Build(LinkedList<Maneuver> maneuvers)
    {
        Maneuver? prevManeuver = null;
        foreach (Maneuver maneuver in maneuvers)
        {
            switch (maneuver.Type())
            {
                case DirectionsLegManeuverType.StartRight:
                case DirectionsLegManeuverType.Start:
                case DirectionsLegManeuverType.StartLeft:
                case DirectionsLegManeuverType.FerryExit:
                case DirectionsLegManeuverType.PostTransitConnectionDestination:
                    maneuver.SetInstruction(FormStartInstruction(maneuver));
                    maneuver.SetVerbalSuccinctTransitionInstruction(FormVerbalSuccinctStartTransitionInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalStartInstruction(maneuver));
                    maneuver.SetVerbalPostTransitionInstruction(
                        FormVerbalPostTransitionInstruction(maneuver, maneuver.HasBeginStreetNames()));
                    break;

                case DirectionsLegManeuverType.DestinationRight:
                case DirectionsLegManeuverType.Destination:
                case DirectionsLegManeuverType.DestinationLeft:
                    maneuver.SetInstruction(FormDestinationInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertDestinationInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalDestinationInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.Becomes:
                    if (prevManeuver != null)
                    {
                        maneuver.SetInstruction(FormBecomesInstruction(maneuver, prevManeuver));
                        maneuver.SetVerbalPreTransitionInstruction(FormVerbalBecomesInstruction(maneuver, prevManeuver));
                    }

                    maneuver.SetVerbalPostTransitionInstruction(
                        FormVerbalPostTransitionInstruction(maneuver, maneuver.HasBeginStreetNames()));
                    break;

                case DirectionsLegManeuverType.SlightRight:
                case DirectionsLegManeuverType.SlightLeft:
                case DirectionsLegManeuverType.Right:
                case DirectionsLegManeuverType.SharpRight:
                case DirectionsLegManeuverType.SharpLeft:
                case DirectionsLegManeuverType.Left:
                    maneuver.SetInstruction(FormTurnInstruction(maneuver));
                    maneuver.SetVerbalSuccinctTransitionInstruction(FormVerbalSuccinctTurnTransitionInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertTurnInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalTurnInstruction(maneuver));
                    maneuver.SetVerbalPostTransitionInstruction(
                        FormVerbalPostTransitionInstruction(maneuver, maneuver.HasBeginStreetNames()));
                    break;

                case DirectionsLegManeuverType.UturnRight:
                case DirectionsLegManeuverType.UturnLeft:
                    maneuver.SetInstruction(FormUturnInstruction(maneuver));
                    maneuver.SetVerbalSuccinctTransitionInstruction(FormVerbalSuccinctUturnTransitionInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertUturnInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalUturnInstruction(maneuver));
                    maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.RampStraight:
                    maneuver.SetInstruction(FormRampStraightInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertRampStraightInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalRampStraightInstruction(maneuver));
                    if (maneuver.Length() > VerbalPostMinimumRampLength || maneuver.ContainsObviousManeuver() ||
                        maneuver.HasCollapsedMergeManeuver())
                    {
                        maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    }

                    break;

                case DirectionsLegManeuverType.RampRight:
                case DirectionsLegManeuverType.RampLeft:
                    maneuver.SetInstruction(FormRampInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertRampInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalRampInstruction(maneuver));
                    if (maneuver.Length() > VerbalPostMinimumRampLength || maneuver.ContainsObviousManeuver() ||
                        maneuver.HasCollapsedMergeManeuver())
                    {
                        maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    }

                    break;

                case DirectionsLegManeuverType.ExitRight:
                case DirectionsLegManeuverType.ExitLeft:
                    maneuver.SetInstruction(FormExitInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertExitInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalExitInstruction(maneuver));
                    if (maneuver.Length() > VerbalPostMinimumRampLength || maneuver.ContainsObviousManeuver() ||
                        maneuver.HasCollapsedMergeManeuver())
                    {
                        maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    }

                    break;

                case DirectionsLegManeuverType.StayStraight:
                case DirectionsLegManeuverType.StayRight:
                case DirectionsLegManeuverType.StayLeft:
                    if (maneuver.ToStayOn())
                    {
                        maneuver.SetInstruction(FormKeepToStayOnInstruction(maneuver));
                        maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertKeepToStayOnInstruction(maneuver));
                        maneuver.SetVerbalPreTransitionInstruction(FormVerbalKeepToStayOnInstruction(maneuver));
                    }
                    else
                    {
                        maneuver.SetInstruction(FormKeepInstruction(maneuver));
                        maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertKeepInstruction(maneuver));
                        maneuver.SetVerbalPreTransitionInstruction(FormVerbalKeepInstruction(maneuver));
                    }

                    // For a ramp - only set verbal post if > min ramp length.
                    if (maneuver.Ramp() && !maneuver.HasCollapsedMergeManeuver())
                    {
                        if (maneuver.Length() > VerbalPostMinimumRampLength)
                        {
                            maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                        }
                    }
                    else
                    {
                        maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    }

                    break;

                case DirectionsLegManeuverType.Merge:
                case DirectionsLegManeuverType.MergeRight:
                case DirectionsLegManeuverType.MergeLeft:
                    maneuver.SetInstruction(FormMergeInstruction(maneuver));
                    maneuver.SetVerbalSuccinctTransitionInstruction(FormVerbalSuccinctMergeTransitionInstruction(maneuver));
                    if (prevManeuver != null &&
                        prevManeuver.Length() > VerbalAlertMergePriorManeuverMinimumLength)
                    {
                        maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertMergeInstruction(maneuver));
                    }

                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalMergeInstruction(maneuver));
                    maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.RoundaboutEnter:
                    maneuver.SetInstruction(FormEnterRoundaboutInstruction(maneuver));
                    maneuver.SetVerbalSuccinctTransitionInstruction(
                        FormVerbalSuccinctEnterRoundaboutTransitionInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertEnterRoundaboutInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalEnterRoundaboutInstruction(maneuver));
                    if (maneuver.HasCombinedEnterExitRoundabout())
                    {
                        maneuver.SetVerbalPostTransitionInstruction(
                            FormVerbalPostTransitionInstruction(maneuver, maneuver.HasRoundaboutExitBeginStreetNames()));
                    }

                    break;

                case DirectionsLegManeuverType.RoundaboutExit:
                    maneuver.SetInstruction(FormExitRoundaboutInstruction(maneuver));
                    maneuver.SetVerbalSuccinctTransitionInstruction(
                        FormVerbalSuccinctExitRoundaboutTransitionInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalExitRoundaboutInstruction(maneuver));
                    maneuver.SetVerbalPostTransitionInstruction(
                        FormVerbalPostTransitionInstruction(maneuver, maneuver.HasBeginStreetNames()));
                    break;

                case DirectionsLegManeuverType.FerryEnter:
                    maneuver.SetInstruction(FormEnterFerryInstruction(maneuver));
                    maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertEnterFerryInstruction(maneuver));
                    maneuver.SetVerbalPreTransitionInstruction(FormVerbalEnterFerryInstruction(maneuver));
                    maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.ElevatorEnter:
                {
                    string instr = FormElevatorInstruction(maneuver);
                    maneuver.SetInstruction(instr);

                    if (maneuver.HasNodeType() && maneuver.GetNodeType() == NodeType.Elevator)
                    {
                        maneuver.SetVerbalTransitionAlertInstruction(instr);
                        maneuver.SetVerbalPreTransitionInstruction(instr);
                        maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    }

                    break;
                }

                case DirectionsLegManeuverType.StepsEnter:
                {
                    string instr = FormStepsInstruction(maneuver);
                    maneuver.SetInstruction(instr);
                    maneuver.SetVerbalTransitionAlertInstruction(instr);
                    maneuver.SetVerbalPreTransitionInstruction(instr);
                    maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    break;
                }

                case DirectionsLegManeuverType.EscalatorEnter:
                    maneuver.SetInstruction(FormEscalatorInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.BuildingEnter:
                    maneuver.SetInstruction(FormEnterBuildingInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.BuildingExit:
                    maneuver.SetInstruction(FormExitBuildingInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.LevelChange:
                    maneuver.SetInstruction(FormGenericLevelChangeInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.ParkVehicle:
                    maneuver.SetInstruction(FormParkVehicleInstruction(maneuver));
                    break;

                case DirectionsLegManeuverType.Continue:
                default:
                    // PORT-NOTE (DEFER): the transit maneuver families (kTransit / kTransitRemainOn /
                    // kTransitTransfer / kTransitConnection* and the depart / arrive arms with their
                    // FormTransit* / FormDepart / FormArrive / FormVerbalPostTransitionTransit /
                    // FormTransitName formers) are NOT ported. Transit / multimodal ROUTING is an
                    // excluded upstream surface in this port (see RouteEngine.cs - multimodal_transit
                    // is not ported), so a transit maneuver can never appear in a route and the ported
                    // Maneuver / EnhancedTripLeg carry no transit route info to drive those formers.
                    // This is blocked-by-dependency on transit routing; revisit if transit routing is
                    // ever ported. The has_node_type() PASS arm and the plain Continue path below are
                    // in scope and produced faithfully.
                    if (maneuver.HasNodeType())
                    {
                        string instr = FormPassInstruction(maneuver);
                        maneuver.SetInstruction(instr);
                        maneuver.SetVerbalPreTransitionInstruction(instr);
                    }
                    else
                    {
                        maneuver.SetInstruction(FormContinueInstruction(maneuver));
                        maneuver.SetVerbalTransitionAlertInstruction(FormVerbalAlertContinueInstruction(maneuver));
                        maneuver.SetVerbalPreTransitionInstruction(FormVerbalContinueInstruction(maneuver));
                        maneuver.SetVerbalPostTransitionInstruction(FormVerbalPostTransitionInstruction(maneuver));
                    }

                    break;
            }

            // Prefix the bike-share maneuver text, exactly as upstream (empty for non-BSS maneuvers).
            maneuver.SetInstruction(FormBssManeuverType(maneuver.BssManeuverType()) + maneuver.Instruction());

            prevManeuver = maneuver;
        }

        // Iterate over maneuvers to form verbal multi-cue instructions.
        FormVerbalMultiCue(maneuvers);
    }

    /// <summary>Faithful port of <c>FormStartInstruction</c> (written).</summary>
    private string FormStartInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.StartSubset;

        string cardinalDirection = subset.CardinalDirections[(int)maneuver.BeginCardinalDirection()];

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        string beginStreetNames = FormStreetNames(maneuver, maneuver.BeginStreetNames());

        UpdateObviousManeuverStreetNames(maneuver, ref beginStreetNames, ref streetNames);

        int phraseId = 0;
        if (streetNames.Length != 0)
        {
            phraseId += 1;
        }

        if (beginStreetNames.Length != 0)
        {
            phraseId += 1;
        }

        if (maneuver.GetTravelMode() == TravelMode.Drive)
        {
            phraseId += 4;
        }
        else if (maneuver.GetTravelMode() == TravelMode.Pedestrian)
        {
            phraseId += 8;
        }
        else if (maneuver.GetTravelMode() == TravelMode.Bicycle)
        {
            phraseId += 16;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(CardinalDirectionTag, cardinalDirection);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(BeginStreetNamesTag, beginStreetNames);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormDestinationInstruction</c> (written; destination degraded - see header).</summary>
    private string FormDestinationInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.DestinationSubset;

        int phraseId = 0;

        // PORT-NOTE (DEFER): destination name/street are not available (no proto Location); they
        // degrade to empty, so the name/street phrase branch (+1) is never taken.
        string destination = string.Empty;

        string relativeDirection = string.Empty;
        if (maneuver.Type() == DirectionsLegManeuverType.DestinationLeft)
        {
            phraseId += 2;
            relativeDirection = subset.RelativeDirections[0];
        }
        else if (maneuver.Type() == DirectionsLegManeuverType.DestinationRight)
        {
            phraseId += 2;
            relativeDirection = subset.RelativeDirections[1];
        }

        string instruction = subset.GetPhrase(phraseId);
        if (phraseId > 0)
        {
            instruction = instruction.Replace(RelativeDirectionTag, relativeDirection);
            instruction = instruction.Replace(DestinationTag, destination);
        }

        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormBecomesInstruction</c> (written).</summary>
    private string FormBecomesInstruction(Maneuver maneuver, Maneuver prevManeuver)
    {
        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames());
        string prevStreetNames = FormStreetNames(prevManeuver, prevManeuver.StreetNames());

        string instruction = Dictionary.BecomesSubset.GetPhrase(0);
        instruction = instruction.Replace(PreviousStreetNamesTag, prevStreetNames);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormContinueInstruction</c> (written).</summary>
    private string FormContinueInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.ContinueSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 3;
            guideSign = maneuver.GetSigns().GetGuideString(WrittenElementMaxCount, false);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 2;
            junctionName = maneuver.GetSigns().GetJunctionNameString(WrittenElementMaxCount, false);
        }
        else if (streetNames.Length != 0)
        {
            phraseId = 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormElevatorInstruction</c> (written; indoor level-change family).</summary>
    private string FormElevatorInstruction(Maneuver maneuver)
    {
        // "0": "Take the elevator.", "1": "Take the elevator to <LEVEL>."
        int phraseId = 0;
        string endLevel = string.Empty;

        if (maneuver.EndLevelRef().Length != 0)
        {
            phraseId += 1;
            endLevel = maneuver.EndLevelRef();
        }

        string instruction = Dictionary.ElevatorSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(LevelTag, endLevel);
        return instruction;
    }

    /// <summary>Faithful port of <c>FormStepsInstruction</c> (written; indoor level-change family).</summary>
    private string FormStepsInstruction(Maneuver maneuver)
    {
        // "0": "Take the stairs.", "1": "Take the stairs to <LEVEL>."
        int phraseId = 0;
        string endLevel = string.Empty;

        if (maneuver.EndLevelRef().Length != 0)
        {
            phraseId += 1;
            endLevel = maneuver.EndLevelRef();
        }

        string instruction = Dictionary.StepsSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(LevelTag, endLevel);
        return instruction;
    }

    /// <summary>Faithful port of <c>FormEscalatorInstruction</c> (written; indoor level-change family).</summary>
    private string FormEscalatorInstruction(Maneuver maneuver)
    {
        // "0": "Take the escalator.", "1": "Take the escalator to <LEVEL>."
        int phraseId = 0;
        string endLevel = string.Empty;

        if (maneuver.EndLevelRef().Length != 0)
        {
            phraseId += 1;
            endLevel = maneuver.EndLevelRef();
        }

        string instruction = Dictionary.EscalatorSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(LevelTag, endLevel);
        return instruction;
    }

    /// <summary>Faithful port of <c>FormGenericLevelChangeInstruction</c> (written; always phrase 0).</summary>
    private string FormGenericLevelChangeInstruction(Maneuver maneuver)
    {
        // "0": "Continue to <LEVEL>."
        const int phraseId = 0;
        string endLevel = string.Empty;

        if (maneuver.EndLevelRef().Length != 0)
        {
            endLevel = maneuver.EndLevelRef();
        }

        string instruction = Dictionary.LevelChangeSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(LevelTag, endLevel);
        return instruction;
    }

    /// <summary>Faithful port of <c>FormParkVehicleInstruction</c> (written; always phrase 0).</summary>
    private string FormParkVehicleInstruction(Maneuver maneuver)
    {
        // "0": "Park your vehicle"
        const int phraseId = 0;
        return Dictionary.ParkVehicleSubset.GetPhrase(phraseId);
    }

    /// <summary>Faithful port of <c>FormEnterBuildingInstruction</c> (written; indoor family).</summary>
    private string FormEnterBuildingInstruction(Maneuver maneuver)
    {
        // "0": "Enter the building.", "1": "Enter the building, and continue on <STREET_NAMES>."
        NarrativeSubset subset = Dictionary.EnterBuildingSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        int phraseId = 0;
        if (streetNames.Length != 0)
        {
            phraseId += 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        return instruction;
    }

    /// <summary>Faithful port of <c>FormExitBuildingInstruction</c> (written; indoor family).</summary>
    private string FormExitBuildingInstruction(Maneuver maneuver)
    {
        // "0": "Exit the building.", "1": "Exit the building, and continue on <STREET_NAMES>."
        NarrativeSubset subset = Dictionary.ExitBuildingSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        int phraseId = 0;
        if (streetNames.Length != 0)
        {
            phraseId += 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        return instruction;
    }

    /// <summary>
    /// Faithful port of <c>FormPassInstruction</c> (written; the has_node_type() default/pass arm).
    /// </summary>
    private string FormPassInstruction(Maneuver maneuver)
    {
        // "0": "Pass <OBJECT_LABEL>.", "1": "Pass traffic signals on <OBJECT_LABEL>."
        NarrativeSubset subset = Dictionary.PassSubset;

        int phraseId = 0;
        string objectLabel = string.Empty;
        int dictionaryObjectIndex = StreetIntersectionIndex; // Upstream default (kGateIndex commented out).

        if (maneuver.HasNodeType())
        {
            switch (maneuver.GetNodeType())
            {
                case NodeType.Gate:
                    dictionaryObjectIndex = GateIndex;
                    break;
                case NodeType.Bollard:
                    dictionaryObjectIndex = BollardIndex;
                    break;
                case NodeType.StreetIntersection:
                    dictionaryObjectIndex = StreetIntersectionIndex;
                    if (maneuver.TrafficSignal())
                    {
                        phraseId = 1;
                    }

                    if (maneuver.HasCrossStreetNames())
                    {
                        objectLabel = FormStreetNames(maneuver, maneuver.CrossStreetNames());
                    }

                    break;
                default:
                    break;
            }

            if (objectLabel.Length == 0)
            {
                objectLabel = subset.ObjectLabels[dictionaryObjectIndex];
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(ObjectLabelTag, objectLabel);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormTurnInstruction</c> (written; bear / turn / sharp subset by type).</summary>
    private string FormTurnInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = maneuver.Type() switch
        {
            DirectionsLegManeuverType.SlightRight or DirectionsLegManeuverType.SlightLeft => Dictionary.BearSubset,
            DirectionsLegManeuverType.Right or DirectionsLegManeuverType.Left => Dictionary.TurnSubset,
            DirectionsLegManeuverType.SharpRight or DirectionsLegManeuverType.SharpLeft => Dictionary.SharpSubset,
            _ => throw new ValhallaException(230),
        };

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        string beginStreetNames = FormStreetNames(maneuver, maneuver.BeginStreetNames());

        UpdateObviousManeuverStreetNames(maneuver, ref beginStreetNames, ref streetNames);

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 5;
            guideSign = maneuver.GetSigns().GetGuideString(WrittenElementMaxCount, false);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 4;
            junctionName = maneuver.GetSigns().GetJunctionNameString(WrittenElementMaxCount, false);
        }
        else if (maneuver.ToStayOn())
        {
            phraseId = 3;
        }
        else if (beginStreetNames.Length != 0)
        {
            phraseId = 2;
        }
        else if (streetNames.Length != 0)
        {
            phraseId = 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(BeginStreetNamesTag, beginStreetNames);
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormUturnInstruction</c> (written).</summary>
    private string FormUturnInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.UturnSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        string crossStreetNames = FormStreetNames(maneuver, maneuver.CrossStreetNames());

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 7;
            guideSign = maneuver.GetSigns().GetGuideString(WrittenElementMaxCount, false);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 6;
            junctionName = maneuver.GetSigns().GetJunctionNameString(WrittenElementMaxCount, false);
        }
        else
        {
            if (streetNames.Length != 0)
            {
                phraseId += 1;
                if (maneuver.ToStayOn())
                {
                    phraseId += 1;
                }
            }

            if (crossStreetNames.Length != 0)
            {
                phraseId += 3;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(CrossStreetNamesTag, crossStreetNames);
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormRampStraightInstruction</c> (written).</summary>
    private string FormRampStraightInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.RampStraightSubset;

        int phraseId = 0;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if (maneuver.HasExitBranchSign())
        {
            phraseId += 1;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(WrittenElementMaxCount, false);
        }

        if (maneuver.HasExitTowardSign())
        {
            phraseId += 2;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(WrittenElementMaxCount, false);
        }

        if (maneuver.HasExitNameSign() && !maneuver.HasExitBranchSign() && !maneuver.HasExitTowardSign())
        {
            phraseId += 4;
            exitNameSign = maneuver.GetSigns().GetExitNameString(WrittenElementMaxCount, false);
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(BranchSignTag, exitBranchSign);
        instruction = instruction.Replace(TowardSignTag, exitTowardSign);
        instruction = instruction.Replace(NameSignTag, exitNameSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormRampInstruction</c> (written).</summary>
    private string FormRampInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.RampSubset;

        int phraseId = 0;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Right ||
            maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Left)
        {
            phraseId = 5;
        }
        else if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight && maneuver.DriveOnRight()) ||
                 (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft && !maneuver.DriveOnRight()))
        {
            phraseId = 10;
        }

        if (maneuver.HasExitBranchSign())
        {
            phraseId += 1;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(WrittenElementMaxCount, false);
        }

        if (maneuver.HasExitTowardSign())
        {
            phraseId += 2;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(WrittenElementMaxCount, false);
        }

        if (maneuver.HasExitNameSign() && !maneuver.HasExitBranchSign() && !maneuver.HasExitTowardSign())
        {
            phraseId += 4;
            exitNameSign = maneuver.GetSigns().GetExitNameString(WrittenElementMaxCount, false);
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(BranchSignTag, exitBranchSign);
        instruction = instruction.Replace(TowardSignTag, exitTowardSign);
        instruction = instruction.Replace(NameSignTag, exitNameSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormExitInstruction</c> (written).</summary>
    private string FormExitInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.ExitSubset;

        int phraseId = 0;
        string exitNumberSign = string.Empty;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if ((maneuver.Type() == DirectionsLegManeuverType.ExitRight && maneuver.DriveOnRight()) ||
            (maneuver.Type() == DirectionsLegManeuverType.ExitLeft && !maneuver.DriveOnRight()))
        {
            phraseId = 15;
        }

        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString();
        }

        if (maneuver.HasExitBranchSign())
        {
            phraseId += 2;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(WrittenElementMaxCount, false);
        }

        if (maneuver.HasExitTowardSign())
        {
            phraseId += 4;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(WrittenElementMaxCount, false);
        }

        if (maneuver.HasExitNameSign() && !maneuver.HasExitNumberSign())
        {
            phraseId += 8;
            exitNameSign = maneuver.GetSigns().GetExitNameString(WrittenElementMaxCount, false);
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(NumberSignTag, exitNumberSign);
        instruction = instruction.Replace(BranchSignTag, exitBranchSign);
        instruction = instruction.Replace(TowardSignTag, exitTowardSign);
        instruction = instruction.Replace(NameSignTag, exitNameSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormKeepInstruction</c> (written).</summary>
    private string FormKeepInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.KeepSubset;

        string streetNames = string.Empty;
        string exitNumberSign = string.Empty;
        string towardSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            if (maneuver.HasGuideBranchSign())
            {
                streetNames = maneuver.GetSigns().GetGuideBranchString(WrittenElementMaxCount, false);
            }

            if (maneuver.HasGuideTowardSign())
            {
                towardSign = maneuver.GetSigns().GetGuideTowardString(WrittenElementMaxCount, false);
            }
        }
        else
        {
            if (maneuver.Ramp() && maneuver.HasExitBranchSign())
            {
                streetNames = maneuver.GetSigns().GetExitBranchString(WrittenElementMaxCount, false);
            }
            else
            {
                streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
                    subset.EmptyStreetNameLabels, true, WrittenElementMaxCount);

                if (streetNames.Length == 0 && maneuver.HasExitBranchSign())
                {
                    streetNames = maneuver.GetSigns().GetExitBranchString(WrittenElementMaxCount, false);
                }
            }

            if (maneuver.HasExitTowardSign())
            {
                towardSign = maneuver.GetSigns().GetExitTowardString(WrittenElementMaxCount, false);
            }
        }

        int phraseId = 0;
        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString();
        }

        if (streetNames.Length != 0)
        {
            phraseId += 2;
        }

        if (towardSign.Length != 0)
        {
            phraseId += 4;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeThreeDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(NumberSignTag, exitNumberSign);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(TowardSignTag, towardSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormKeepToStayOnInstruction</c> (written).</summary>
    private string FormKeepToStayOnInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.KeepToStayOnSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true, WrittenElementMaxCount);

        string towardSign = string.Empty;
        if (maneuver.HasGuideTowardSign())
        {
            towardSign = maneuver.GetSigns().GetGuideTowardString(WrittenElementMaxCount, false);
        }
        else if (maneuver.HasExitTowardSign())
        {
            towardSign = maneuver.GetSigns().GetExitTowardString(WrittenElementMaxCount, false);
        }

        string exitNumberSign = string.Empty;
        int phraseId = 0;
        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString();
        }

        if (towardSign.Length != 0)
        {
            phraseId += 2;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeThreeDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(NumberSignTag, exitNumberSign);
        instruction = instruction.Replace(TowardSignTag, towardSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormMergeInstruction</c> (written).</summary>
    private string FormMergeInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.MergeSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        int phraseId = 0;
        string guideSign = string.Empty;

        if (streetNames.Length != 0)
        {
            phraseId = 2;
        }
        else if (maneuver.HasGuideSign())
        {
            phraseId = 4;
            guideSign = maneuver.GetSigns().GetGuideString(WrittenElementMaxCount, false);
        }

        string relativeDirection = string.Empty;
        if (maneuver.Type() == DirectionsLegManeuverType.MergeLeft ||
            maneuver.Type() == DirectionsLegManeuverType.MergeRight)
        {
            phraseId += 1;
            relativeDirection = FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections);
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDirection);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormEnterRoundaboutInstruction</c> (written).</summary>
    private string FormEnterRoundaboutInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.EnterRoundaboutSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames());

        string roundaboutExitStreetNames = string.Empty;
        string roundaboutExitBeginStreetNames = string.Empty;

        // PORT-NOTE: upstream hard-codes option_roundabout_exits = true (see file header).
        const bool optionRoundaboutExits = true;
        if (optionRoundaboutExits)
        {
            roundaboutExitStreetNames = maneuver.RoundaboutExitBeginStreetNames().Count == 0
                ? FormStreetNames(maneuver, maneuver.RoundaboutExitStreetNames())
                : FormStreetNames(maneuver, maneuver.RoundaboutExitBeginStreetNames());
        }
        else
        {
            // Unreachable while optionRoundaboutExits is hard-coded to true above (matching upstream's
            // current hard-coded option_roundabout_exits). Kept, not deleted, so the option_roundabout_exits
            // = false behavior is preserved verbatim for when that option is threaded through for real.
#pragma warning disable CS0162 // Unreachable code detected
            roundaboutExitStreetNames = FormStreetNames(maneuver, maneuver.RoundaboutExitStreetNames(),
                subset.EmptyStreetNameLabels, true);
            roundaboutExitBeginStreetNames = FormStreetNames(maneuver, maneuver.RoundaboutExitBeginStreetNames());
#pragma warning restore CS0162
        }

        int phraseId = 0;
        string guideSign = string.Empty;

        if (streetNames.Length != 0)
        {
            phraseId = 8;
        }

        string ordinalValue = string.Empty;
        if (maneuver.RoundaboutExitCount() >= RoundaboutExitCountLowerBound &&
            maneuver.RoundaboutExitCount() <= RoundaboutExitCountUpperBound)
        {
            phraseId += 1;
            ordinalValue = subset.OrdinalValues[(int)maneuver.RoundaboutExitCount() - 1];
        }
        else if (roundaboutExitStreetNames.Length != 0 || roundaboutExitBeginStreetNames.Length != 0 ||
                 maneuver.RoundaboutExitSigns().HasGuide())
        {
            phraseId += 4;
        }

        if (maneuver.RoundaboutExitSigns().HasGuide())
        {
            phraseId += 3;
            guideSign = maneuver.RoundaboutExitSigns().GetGuideString(WrittenElementMaxCount, false);
        }
        else
        {
            if (roundaboutExitStreetNames.Length != 0)
            {
                phraseId += 1;
            }

            if (roundaboutExitBeginStreetNames.Length != 0)
            {
                phraseId += 1;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(OrdinalValueTag, ordinalValue);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        instruction = instruction.Replace(RoundaboutExitStreetNamesTag, roundaboutExitStreetNames);
        instruction = instruction.Replace(RoundaboutExitBeginStreetNamesTag, roundaboutExitBeginStreetNames);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormExitRoundaboutInstruction</c> (written).</summary>
    private string FormExitRoundaboutInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.ExitRoundaboutSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        string beginStreetNames = FormStreetNames(maneuver, maneuver.BeginStreetNames(),
            subset.EmptyStreetNameLabels);

        UpdateObviousManeuverStreetNames(maneuver, ref beginStreetNames, ref streetNames);

        int phraseId = 0;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 3;
            guideSign = maneuver.GetSigns().GetGuideString(WrittenElementMaxCount, false);
        }
        else
        {
            if (streetNames.Length != 0)
            {
                phraseId += 1;
            }

            if (beginStreetNames.Length != 0)
            {
                phraseId += 1;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(BeginStreetNamesTag, beginStreetNames);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormEnterFerryInstruction</c> (written).</summary>
    private string FormEnterFerryInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.EnterFerrySubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(),
            subset.EmptyStreetNameLabels, true);

        string ferryLabel = subset.FerryLabel;

        int phraseId = 0;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 3;
            guideSign = maneuver.GetSigns().GetGuideString(WrittenElementMaxCount, false);
        }
        else if (streetNames.Length != 0)
        {
            phraseId = 1;
            if (!HasLabel(streetNames, ferryLabel))
            {
                phraseId = 2;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(FerryLabelTag, ferryLabel);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    // -------------------------------------------------------------------------------------------
    // Verbal formers (pre / alert / succinct) + post-transition length
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Returns the verbal alert approach instruction combining the specified distance and verbal cue.
    /// Faithful port of <c>FormVerbalAlertApproachInstruction</c>.
    /// </summary>
    public string FormVerbalAlertApproachInstruction(float distance, string verbalCue)
    {
        NarrativeSubset subset = Dictionary.ApproachVerbalAlertSubset;

        string instruction = subset.GetPhrase(0);
        instruction = instruction.Replace(LengthTag, FormLength(distance, subset.MetricLengths, subset.UsCustomaryLengths));
        instruction = instruction.Replace(CurrentVerbalCueTag, verbalCue);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalStartInstruction</c>.</summary>
    private string FormVerbalStartInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.StartVerbalSubset;

        string cardinalDirection = subset.CardinalDirections[(int)maneuver.BeginCardinalDirection()];

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string beginStreetNames = FormStreetNames(maneuver, maneuver.BeginStreetNames(), subset.EmptyStreetNameLabels,
            false, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        UpdateObviousManeuverStreetNames(maneuver, ref beginStreetNames, ref streetNames);

        int phraseId = 0;
        if (maneuver.GetTravelMode() == TravelMode.Drive)
        {
            phraseId += 5;
        }
        else if (maneuver.GetTravelMode() == TravelMode.Pedestrian)
        {
            phraseId += 10;
        }
        else if (maneuver.GetTravelMode() == TravelMode.Bicycle)
        {
            phraseId += 15;
        }

        if (streetNames.Length != 0)
        {
            phraseId += 2;
        }

        if (beginStreetNames.Length != 0)
        {
            phraseId += 2;
        }
        else if (maneuver.IncludeVerbalPreTransitionLength())
        {
            phraseId += 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(CardinalDirectionTag, cardinalDirection);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(BeginStreetNamesTag, beginStreetNames);
        instruction = instruction.Replace(LengthTag, FormLength(maneuver, subset.MetricLengths, subset.UsCustomaryLengths));
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertDestinationInstruction</c> (destination degraded - see A1 header).</summary>
    private string FormVerbalAlertDestinationInstruction(Maneuver maneuver)
    {
        int phraseId = 0;

        // PORT-NOTE (DEFER): no proto Location, so the destination name/street degrade to empty and
        // the name/street phrase branch (+1) is never taken - mirrors the written path.
        string destination = string.Empty;

        string relativeDirection = string.Empty;
        if (maneuver.Type() == DirectionsLegManeuverType.DestinationLeft)
        {
            phraseId += 2;
            relativeDirection = Dictionary.DestinationSubset.RelativeDirections[0];
        }
        else if (maneuver.Type() == DirectionsLegManeuverType.DestinationRight)
        {
            phraseId += 2;
            relativeDirection = Dictionary.DestinationSubset.RelativeDirections[1];
        }

        string instruction = Dictionary.DestinationVerbalAlertSubset.GetPhrase(phraseId);
        if (phraseId > 0)
        {
            instruction = instruction.Replace(RelativeDirectionTag, relativeDirection);
            instruction = instruction.Replace(DestinationTag, destination);
        }

        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalDestinationInstruction</c> (destination degraded - see A1 header).</summary>
    private string FormVerbalDestinationInstruction(Maneuver maneuver)
    {
        int phraseId = 0;

        // PORT-NOTE (DEFER): destination name/street degrade to empty (no proto Location).
        string destination = string.Empty;

        string relativeDirection = string.Empty;
        if (maneuver.Type() == DirectionsLegManeuverType.DestinationLeft)
        {
            phraseId += 2;
            relativeDirection = Dictionary.DestinationSubset.RelativeDirections[0];
        }
        else if (maneuver.Type() == DirectionsLegManeuverType.DestinationRight)
        {
            phraseId += 2;
            relativeDirection = Dictionary.DestinationSubset.RelativeDirections[1];
        }

        string instruction = Dictionary.DestinationVerbalSubset.GetPhrase(phraseId);
        if (phraseId > 0)
        {
            instruction = instruction.Replace(RelativeDirectionTag, relativeDirection);
            instruction = instruction.Replace(DestinationTag, destination);
        }

        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalBecomesInstruction</c>.</summary>
    private string FormVerbalBecomesInstruction(Maneuver maneuver, Maneuver prevManeuver)
    {
        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), null, false,
            OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, prevManeuver.VerbalFormatter());

        string prevStreetNames = FormStreetNames(prevManeuver, prevManeuver.StreetNames(), null, false,
            OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, prevManeuver.VerbalFormatter());

        string instruction = Dictionary.BecomesVerbalSubset.GetPhrase(0);
        instruction = instruction.Replace(PreviousStreetNamesTag, prevStreetNames);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertContinueInstruction</c>.</summary>
    private string FormVerbalAlertContinueInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.ContinueVerbalAlertSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalAlertElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 3;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 2;
            junctionName = maneuver.GetSigns().GetJunctionNameString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (streetNames.Length != 0)
        {
            phraseId = 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalContinueInstruction</c>.</summary>
    private string FormVerbalContinueInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.ContinueVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 6;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 4;
            junctionName = maneuver.GetSigns().GetJunctionNameString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (streetNames.Length != 0)
        {
            phraseId = 2;
        }

        if (maneuver.IncludeVerbalPreTransitionLength())
        {
            phraseId += 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(LengthTag, FormLength(maneuver, subset.MetricLengths, subset.UsCustomaryLengths));
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertTurnInstruction</c> (delegates to the verbal turn former).</summary>
    private string FormVerbalAlertTurnInstruction(Maneuver maneuver)
        => FormVerbalTurnInstruction(maneuver, OdinUtil.VerbalAlertElementMaxCount);

    /// <summary>Faithful port of <c>FormVerbalTurnInstruction</c>.</summary>
    private string FormVerbalTurnInstruction(Maneuver maneuver, uint elementMaxCount = 2 /* kVerbalPreElementMaxCount */)
    {
        NarrativeSubset subset = maneuver.Type() switch
        {
            DirectionsLegManeuverType.SlightRight or DirectionsLegManeuverType.SlightLeft => Dictionary.BearVerbalSubset,
            DirectionsLegManeuverType.Right or DirectionsLegManeuverType.Left => Dictionary.TurnVerbalSubset,
            DirectionsLegManeuverType.SharpRight or DirectionsLegManeuverType.SharpLeft => Dictionary.SharpVerbalSubset,
            _ => throw new ValhallaException(230),
        };

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, elementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string beginStreetNames = FormStreetNames(maneuver, maneuver.BeginStreetNames(), subset.EmptyStreetNameLabels,
            false, elementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        UpdateObviousManeuverStreetNames(maneuver, ref beginStreetNames, ref streetNames);

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 5;
            guideSign = maneuver.GetSigns().GetGuideString(elementMaxCount, OdinUtil.LimitByConsecutiveCount,
                OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 4;
            junctionName = maneuver.GetSigns().GetJunctionNameString(elementMaxCount, OdinUtil.LimitByConsecutiveCount,
                OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else
        {
            if (streetNames.Length != 0)
            {
                phraseId = 1;
            }

            if (beginStreetNames.Length != 0)
            {
                phraseId = 2;
            }

            if (maneuver.ToStayOn())
            {
                phraseId = 3;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(BeginStreetNamesTag, beginStreetNames);
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertUturnInstruction</c>.</summary>
    private string FormVerbalAlertUturnInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.UturnVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalAlertElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string crossStreetNames = FormStreetNames(maneuver, maneuver.CrossStreetNames(), subset.EmptyStreetNameLabels,
            false, OdinUtil.VerbalAlertElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 7;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 6;
            junctionName = maneuver.GetSigns().GetJunctionNameString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else
        {
            if (streetNames.Length != 0)
            {
                phraseId = 1;
                if (maneuver.ToStayOn())
                {
                    phraseId = 2;
                }
            }

            if (crossStreetNames.Length != 0)
            {
                phraseId = 3;
            }
        }

        return FormVerbalUturnInstruction(phraseId,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections),
            streetNames, crossStreetNames, junctionName, guideSign);
    }

    /// <summary>Faithful port of <c>FormVerbalUturnInstruction</c> (maneuver overload).</summary>
    private string FormVerbalUturnInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.UturnVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string crossStreetNames = FormStreetNames(maneuver, maneuver.CrossStreetNames(), subset.EmptyStreetNameLabels,
            false, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 7;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 6;
            junctionName = maneuver.GetSigns().GetJunctionNameString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else
        {
            if (streetNames.Length != 0)
            {
                phraseId += 1;
                if (maneuver.ToStayOn())
                {
                    phraseId += 1;
                }
            }

            if (crossStreetNames.Length != 0)
            {
                phraseId += 3;
            }
        }

        return FormVerbalUturnInstruction(phraseId,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections),
            streetNames, crossStreetNames, junctionName, guideSign);
    }

    /// <summary>Faithful port of <c>FormVerbalUturnInstruction</c> (phrase-id overload).</summary>
    private string FormVerbalUturnInstruction(int phraseId, string relativeDir, string streetNames,
        string crossStreetNames, string junctionName, string guideSign)
    {
        string instruction = Dictionary.UturnVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDir);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(CrossStreetNamesTag, crossStreetNames);
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertRampStraightInstruction</c>.</summary>
    private string FormVerbalAlertRampStraightInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if (maneuver.HasExitBranchSign())
        {
            phraseId = 1;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitTowardSign())
        {
            phraseId = 2;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitNameSign())
        {
            phraseId = 4;
            exitNameSign = maneuver.GetSigns().GetExitNameString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        return FormVerbalRampStraightInstruction(phraseId, exitBranchSign, exitTowardSign, exitNameSign);
    }

    /// <summary>Faithful port of <c>FormVerbalRampStraightInstruction</c> (maneuver overload).</summary>
    private string FormVerbalRampStraightInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if (maneuver.HasExitBranchSign())
        {
            phraseId += 1;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (maneuver.HasExitTowardSign())
        {
            phraseId += 2;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (maneuver.HasExitNameSign() && !maneuver.HasExitBranchSign() && !maneuver.HasExitTowardSign())
        {
            phraseId += 4;
            exitNameSign = maneuver.GetSigns().GetExitNameString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        return FormVerbalRampStraightInstruction(phraseId, exitBranchSign, exitTowardSign, exitNameSign);
    }

    /// <summary>Faithful port of <c>FormVerbalRampStraightInstruction</c> (phrase-id overload).</summary>
    private string FormVerbalRampStraightInstruction(int phraseId, string exitBranchSign, string exitTowardSign, string exitNameSign)
    {
        string instruction = Dictionary.RampStraightVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(BranchSignTag, exitBranchSign);
        instruction = instruction.Replace(TowardSignTag, exitTowardSign);
        instruction = instruction.Replace(NameSignTag, exitNameSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertRampInstruction</c>.</summary>
    private string FormVerbalAlertRampInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Right ||
            maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Left)
        {
            phraseId = 5;
        }
        else if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight && maneuver.DriveOnRight()) ||
                 (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft && !maneuver.DriveOnRight()))
        {
            phraseId = 10;
        }

        if (maneuver.HasExitBranchSign())
        {
            phraseId += 1;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitTowardSign())
        {
            phraseId += 2;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitNameSign())
        {
            phraseId += 4;
            exitNameSign = maneuver.GetSigns().GetExitNameString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        return FormVerbalRampInstruction(phraseId,
            FormRelativeTwoDirection(maneuver.Type(), Dictionary.RampVerbalSubset.RelativeDirections),
            exitBranchSign, exitTowardSign, exitNameSign);
    }

    /// <summary>Faithful port of <c>FormVerbalRampInstruction</c> (maneuver overload).</summary>
    private string FormVerbalRampInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Right ||
            maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.Left)
        {
            phraseId = 5;
        }
        else if ((maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepRight && maneuver.DriveOnRight()) ||
                 (maneuver.BeginRelativeDirection() == Maneuver.RelativeDirection.KeepLeft && !maneuver.DriveOnRight()))
        {
            phraseId = 10;
        }

        if (maneuver.HasExitBranchSign())
        {
            phraseId += 1;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (maneuver.HasExitTowardSign())
        {
            phraseId += 2;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (maneuver.HasExitNameSign() && !maneuver.HasExitBranchSign() && !maneuver.HasExitTowardSign())
        {
            phraseId += 4;
            exitNameSign = maneuver.GetSigns().GetExitNameString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        return FormVerbalRampInstruction(phraseId,
            FormRelativeTwoDirection(maneuver.Type(), Dictionary.RampVerbalSubset.RelativeDirections),
            exitBranchSign, exitTowardSign, exitNameSign);
    }

    /// <summary>Faithful port of <c>FormVerbalRampInstruction</c> (phrase-id overload).</summary>
    private string FormVerbalRampInstruction(int phraseId, string relativeDir, string exitBranchSign,
        string exitTowardSign, string exitNameSign)
    {
        string instruction = Dictionary.RampVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDir);
        instruction = instruction.Replace(BranchSignTag, exitBranchSign);
        instruction = instruction.Replace(TowardSignTag, exitTowardSign);
        instruction = instruction.Replace(NameSignTag, exitNameSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertExitInstruction</c>.</summary>
    private string FormVerbalAlertExitInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string exitNumberSign = string.Empty;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if ((maneuver.Type() == DirectionsLegManeuverType.ExitRight && maneuver.DriveOnRight()) ||
            (maneuver.Type() == DirectionsLegManeuverType.ExitLeft && !maneuver.DriveOnRight()))
        {
            phraseId = 15;
        }

        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString(0, false, OdinUtil.VerbalDelim,
                maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitBranchSign())
        {
            phraseId += 2;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitTowardSign())
        {
            phraseId += 4;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitNameSign())
        {
            phraseId += 8;
            exitNameSign = maneuver.GetSigns().GetExitNameString(OdinUtil.VerbalAlertElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        return FormVerbalExitInstruction(phraseId,
            FormRelativeTwoDirection(maneuver.Type(), Dictionary.ExitVerbalSubset.RelativeDirections),
            exitNumberSign, exitBranchSign, exitTowardSign, exitNameSign);
    }

    /// <summary>Faithful port of <c>FormVerbalExitInstruction</c> (maneuver overload).</summary>
    private string FormVerbalExitInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string exitNumberSign = string.Empty;
        string exitBranchSign = string.Empty;
        string exitTowardSign = string.Empty;
        string exitNameSign = string.Empty;

        if ((maneuver.Type() == DirectionsLegManeuverType.ExitRight && maneuver.DriveOnRight()) ||
            (maneuver.Type() == DirectionsLegManeuverType.ExitLeft && !maneuver.DriveOnRight()))
        {
            phraseId = 15;
        }

        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString(0, false, OdinUtil.VerbalDelim,
                maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (maneuver.HasExitBranchSign())
        {
            phraseId += 2;
            exitBranchSign = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (maneuver.HasExitTowardSign())
        {
            phraseId += 4;
            exitTowardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (maneuver.HasExitNameSign() && !maneuver.HasExitNumberSign())
        {
            phraseId += 8;
            exitNameSign = maneuver.GetSigns().GetExitNameString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        return FormVerbalExitInstruction(phraseId,
            FormRelativeTwoDirection(maneuver.Type(), Dictionary.ExitVerbalSubset.RelativeDirections),
            exitNumberSign, exitBranchSign, exitTowardSign, exitNameSign);
    }

    /// <summary>Faithful port of <c>FormVerbalExitInstruction</c> (phrase-id overload).</summary>
    private string FormVerbalExitInstruction(int phraseId, string relativeDir, string exitNumberSign,
        string exitBranchSign, string exitTowardSign, string exitNameSign)
    {
        string instruction = Dictionary.ExitVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDir);
        instruction = instruction.Replace(NumberSignTag, exitNumberSign);
        instruction = instruction.Replace(BranchSignTag, exitBranchSign);
        instruction = instruction.Replace(TowardSignTag, exitTowardSign);
        instruction = instruction.Replace(NameSignTag, exitNameSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertKeepInstruction</c>.</summary>
    private string FormVerbalAlertKeepInstruction(Maneuver maneuver)
    {
        string streetNames = string.Empty;
        string exitNumberSign = string.Empty;
        string towardSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            if (maneuver.HasGuideBranchSign())
            {
                streetNames = maneuver.GetSigns().GetGuideBranchString(OdinUtil.VerbalAlertElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }

            if (maneuver.HasGuideTowardSign())
            {
                towardSign = maneuver.GetSigns().GetGuideTowardString(OdinUtil.VerbalAlertElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }
        }
        else
        {
            if (maneuver.Ramp() && maneuver.HasExitBranchSign())
            {
                streetNames = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalAlertElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }
            else
            {
                streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), Dictionary.KeepVerbalSubset.EmptyStreetNameLabels,
                    true, OdinUtil.VerbalAlertElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

                if (streetNames.Length == 0 && maneuver.HasExitBranchSign())
                {
                    streetNames = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalAlertElementMaxCount,
                        OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
                }
            }

            if (maneuver.HasExitTowardSign())
            {
                towardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalAlertElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }
        }

        int phraseId = 0;
        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString(0, false, OdinUtil.VerbalDelim,
                maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (streetNames.Length != 0)
        {
            phraseId += 2;
        }
        else if (towardSign.Length != 0)
        {
            phraseId += 4;
        }

        return FormVerbalKeepInstruction(phraseId,
            FormRelativeThreeDirection(maneuver.Type(), Dictionary.KeepVerbalSubset.RelativeDirections),
            streetNames, exitNumberSign, towardSign);
    }

    /// <summary>Faithful port of <c>FormVerbalKeepInstruction</c> (maneuver overload).</summary>
    private string FormVerbalKeepInstruction(Maneuver maneuver)
    {
        string exitNumberSign = string.Empty;
        string towardSign = string.Empty;
        string streetNames = string.Empty;

        if (maneuver.HasGuideSign())
        {
            if (maneuver.HasGuideBranchSign())
            {
                streetNames = maneuver.GetSigns().GetGuideBranchString(OdinUtil.VerbalPreElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }

            if (maneuver.HasGuideTowardSign())
            {
                towardSign = maneuver.GetSigns().GetGuideTowardString(OdinUtil.VerbalPreElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }
        }
        else
        {
            if (maneuver.Ramp() && maneuver.HasExitBranchSign())
            {
                streetNames = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalPreElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }
            else
            {
                streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), Dictionary.KeepVerbalSubset.EmptyStreetNameLabels,
                    true, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

                if (streetNames.Length == 0 && maneuver.HasExitBranchSign())
                {
                    streetNames = maneuver.GetSigns().GetExitBranchString(OdinUtil.VerbalPreElementMaxCount,
                        OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
                }
            }

            if (maneuver.HasExitTowardSign())
            {
                towardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalPreElementMaxCount,
                    OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
            }
        }

        int phraseId = 0;
        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString(0, false, OdinUtil.VerbalDelim,
                maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (streetNames.Length != 0)
        {
            phraseId += 2;
        }

        if (towardSign.Length != 0)
        {
            phraseId += 4;
        }

        return FormVerbalKeepInstruction(phraseId,
            FormRelativeThreeDirection(maneuver.Type(), Dictionary.KeepVerbalSubset.RelativeDirections),
            streetNames, exitNumberSign, towardSign);
    }

    /// <summary>Faithful port of <c>FormVerbalKeepInstruction</c> (phrase-id overload).</summary>
    private string FormVerbalKeepInstruction(int phraseId, string relativeDir, string streetNames,
        string exitNumberSign, string towardSign)
    {
        string instruction = Dictionary.KeepVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDir);
        instruction = instruction.Replace(NumberSignTag, exitNumberSign);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(TowardSignTag, towardSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertKeepToStayOnInstruction</c>.</summary>
    private string FormVerbalAlertKeepToStayOnInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.KeepToStayOnVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalAlertElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        return FormVerbalKeepToStayOnInstruction(0,
            FormRelativeThreeDirection(maneuver.Type(), subset.RelativeDirections),
            streetNames, string.Empty, string.Empty);
    }

    /// <summary>Faithful port of <c>FormVerbalKeepToStayOnInstruction</c> (maneuver overload).</summary>
    private string FormVerbalKeepToStayOnInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.KeepToStayOnVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string towardSign = string.Empty;
        if (maneuver.HasGuideTowardSign())
        {
            towardSign = maneuver.GetSigns().GetGuideTowardString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasExitTowardSign())
        {
            towardSign = maneuver.GetSigns().GetExitTowardString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        string exitNumberSign = string.Empty;
        int phraseId = 0;
        if (maneuver.HasExitNumberSign())
        {
            phraseId += 1;
            exitNumberSign = maneuver.GetSigns().GetExitNumberString(0, false, OdinUtil.VerbalDelim,
                maneuver.VerbalFormatter(), MarkupFormatter);
        }

        if (towardSign.Length != 0)
        {
            phraseId += 2;
        }

        return FormVerbalKeepToStayOnInstruction(phraseId,
            FormRelativeThreeDirection(maneuver.Type(), subset.RelativeDirections),
            streetNames, exitNumberSign, towardSign);
    }

    /// <summary>Faithful port of <c>FormVerbalKeepToStayOnInstruction</c> (phrase-id overload).</summary>
    private string FormVerbalKeepToStayOnInstruction(int phraseId, string relativeDir, string streetNames,
        string exitNumberSign, string towardSign)
    {
        string instruction = Dictionary.KeepToStayOnVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDir);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(NumberSignTag, exitNumberSign);
        instruction = instruction.Replace(TowardSignTag, towardSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertMergeInstruction</c> (delegates to the verbal merge former).</summary>
    private string FormVerbalAlertMergeInstruction(Maneuver maneuver)
        => FormVerbalMergeInstruction(maneuver, OdinUtil.VerbalAlertElementMaxCount);

    /// <summary>Faithful port of <c>FormVerbalMergeInstruction</c>.</summary>
    private string FormVerbalMergeInstruction(Maneuver maneuver, uint elementMaxCount = 2 /* kVerbalPreElementMaxCount */)
    {
        NarrativeSubset subset = Dictionary.MergeVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, elementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        int phraseId = 0;
        string guideSign = string.Empty;

        if (streetNames.Length != 0)
        {
            phraseId = 2;
        }
        else if (maneuver.HasGuideSign())
        {
            phraseId = 4;
            guideSign = maneuver.GetSigns().GetGuideString(elementMaxCount, OdinUtil.LimitByConsecutiveCount,
                OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        string relativeDirection = string.Empty;
        if (maneuver.Type() == DirectionsLegManeuverType.MergeLeft ||
            maneuver.Type() == DirectionsLegManeuverType.MergeRight)
        {
            phraseId += 1;
            relativeDirection = FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections);
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDirection);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertEnterRoundaboutInstruction</c> (delegates).</summary>
    private string FormVerbalAlertEnterRoundaboutInstruction(Maneuver maneuver)
        => FormVerbalEnterRoundaboutInstruction(maneuver, OdinUtil.VerbalAlertElementMaxCount);

    /// <summary>Faithful port of <c>FormVerbalEnterRoundaboutInstruction</c>.</summary>
    private string FormVerbalEnterRoundaboutInstruction(Maneuver maneuver, uint elementMaxCount = 2 /* kVerbalPreElementMaxCount */)
    {
        NarrativeSubset subset = Dictionary.EnterRoundaboutVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            false, elementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        // PORT-NOTE: upstream hard-codes option_roundabout_exits = true (see A1 header); the else
        // branch (enhance_empty_street_names) is unreachable and its labels are not applied.
        string roundaboutExitStreetNames = FormStreetNames(maneuver, maneuver.RoundaboutExitStreetNames(),
            subset.EmptyStreetNameLabels, false, elementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string roundaboutExitBeginStreetNames = FormStreetNames(maneuver, maneuver.RoundaboutExitBeginStreetNames(),
            subset.EmptyStreetNameLabels, false, elementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        int phraseId = 0;
        string guideSign = string.Empty;

        if (streetNames.Length != 0)
        {
            phraseId = 8;
        }

        string ordinalValue = string.Empty;
        if (maneuver.RoundaboutExitCount() >= RoundaboutExitCountLowerBound &&
            maneuver.RoundaboutExitCount() <= RoundaboutExitCountUpperBound)
        {
            phraseId += 1;
            ordinalValue = subset.OrdinalValues[(int)maneuver.RoundaboutExitCount() - 1];
        }
        else if (roundaboutExitStreetNames.Length != 0 || roundaboutExitBeginStreetNames.Length != 0 ||
                 maneuver.RoundaboutExitSigns().HasGuide())
        {
            phraseId += 4;
        }

        if (maneuver.RoundaboutExitSigns().HasGuide())
        {
            phraseId += 3;
            guideSign = maneuver.RoundaboutExitSigns().GetGuideString(elementMaxCount, OdinUtil.LimitByConsecutiveCount,
                OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else
        {
            if (roundaboutExitStreetNames.Length != 0)
            {
                phraseId += 1;
            }

            if (roundaboutExitBeginStreetNames.Length != 0)
            {
                phraseId += 1;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(OrdinalValueTag, ordinalValue);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        instruction = instruction.Replace(RoundaboutExitStreetNamesTag, roundaboutExitStreetNames);
        instruction = instruction.Replace(RoundaboutExitBeginStreetNamesTag, roundaboutExitBeginStreetNames);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalExitRoundaboutInstruction</c>.</summary>
    private string FormVerbalExitRoundaboutInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.ExitRoundaboutVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string beginStreetNames = FormStreetNames(maneuver, maneuver.BeginStreetNames(), subset.EmptyStreetNameLabels,
            false, OdinUtil.VerbalPreElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        UpdateObviousManeuverStreetNames(maneuver, ref beginStreetNames, ref streetNames);

        int phraseId = 0;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 3;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else
        {
            if (streetNames.Length != 0)
            {
                phraseId += 1;
            }

            if (beginStreetNames.Length != 0)
            {
                phraseId += 1;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(BeginStreetNamesTag, beginStreetNames);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalAlertEnterFerryInstruction</c> (delegates).</summary>
    private string FormVerbalAlertEnterFerryInstruction(Maneuver maneuver)
        => FormVerbalEnterFerryInstruction(maneuver, OdinUtil.VerbalAlertElementMaxCount);

    /// <summary>Faithful port of <c>FormVerbalEnterFerryInstruction</c>.</summary>
    private string FormVerbalEnterFerryInstruction(Maneuver maneuver, uint elementMaxCount = 2 /* kVerbalPreElementMaxCount */)
    {
        NarrativeSubset subset = Dictionary.EnterFerryVerbalSubset;

        string streetNames = FormStreetNames(maneuver, maneuver.StreetNames(), subset.EmptyStreetNameLabels,
            true, elementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());

        string ferryLabel = subset.FerryLabel;

        int phraseId = 0;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 3;
            guideSign = maneuver.GetSigns().GetGuideString(elementMaxCount, OdinUtil.LimitByConsecutiveCount,
                OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (streetNames.Length != 0)
        {
            phraseId = 1;
            if (!HasLabel(streetNames, ferryLabel))
            {
                phraseId = 2;
            }
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        instruction = instruction.Replace(FerryLabelTag, ferryLabel);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalPostTransitionInstruction</c>.</summary>
    private string FormVerbalPostTransitionInstruction(Maneuver maneuver, bool includeStreetNames = false)
    {
        NarrativeSubset subset = Dictionary.PostTransitionVerbalSubset;

        string streetNames = string.Empty;
        if (!maneuver.ContainsObviousManeuver() && !maneuver.HasLongStreetName())
        {
            StreetNames streetNameList = maneuver.HasCombinedEnterExitRoundabout()
                ? maneuver.RoundaboutExitStreetNames()
                : maneuver.StreetNames();
            streetNames = FormStreetNames(maneuver, streetNameList, subset.EmptyStreetNameLabels,
                true, OdinUtil.VerbalPostElementMaxCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter());
        }

        int phraseId = 0;
        if (includeStreetNames && streetNames.Length != 0)
        {
            phraseId = 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(LengthTag, FormLength(maneuver, subset.MetricLengths, subset.UsCustomaryLengths));
        instruction = instruction.Replace(StreetNamesTag, streetNames);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalSuccinctStartTransitionInstruction</c>.</summary>
    private string FormVerbalSuccinctStartTransitionInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.StartVerbalSubset;

        string cardinalDirection = subset.CardinalDirections[(int)maneuver.BeginCardinalDirection()];

        int phraseId = 0;
        if (maneuver.GetTravelMode() == TravelMode.Drive)
        {
            phraseId += 5;
        }
        else if (maneuver.GetTravelMode() == TravelMode.Pedestrian)
        {
            phraseId += 10;
        }
        else if (maneuver.GetTravelMode() == TravelMode.Bicycle)
        {
            phraseId += 15;
        }

        if (maneuver.IncludeVerbalPreTransitionLength())
        {
            phraseId += 1;
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(CardinalDirectionTag, cardinalDirection);
        instruction = instruction.Replace(LengthTag, FormLength(maneuver, subset.MetricLengths, subset.UsCustomaryLengths));
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalSuccinctTurnTransitionInstruction</c>.</summary>
    private string FormVerbalSuccinctTurnTransitionInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = maneuver.Type() switch
        {
            DirectionsLegManeuverType.SlightRight or DirectionsLegManeuverType.SlightLeft => Dictionary.BearVerbalSubset,
            DirectionsLegManeuverType.Right or DirectionsLegManeuverType.Left => Dictionary.TurnVerbalSubset,
            DirectionsLegManeuverType.SharpRight or DirectionsLegManeuverType.SharpLeft => Dictionary.SharpVerbalSubset,
            _ => throw new ValhallaException(230),
        };

        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 5;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 4;
            junctionName = maneuver.GetSigns().GetJunctionNameString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeTwoDirection(maneuver.Type(), subset.RelativeDirections));
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalSuccinctUturnTransitionInstruction</c>.</summary>
    private string FormVerbalSuccinctUturnTransitionInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string junctionName = string.Empty;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 7;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }
        else if (maneuver.HasJunctionNameSign())
        {
            phraseId = 6;
            junctionName = maneuver.GetSigns().GetJunctionNameString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        string instruction = Dictionary.UturnVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag,
            FormRelativeTwoDirection(maneuver.Type(), Dictionary.UturnVerbalSubset.RelativeDirections));
        instruction = instruction.Replace(JunctionNameTag, junctionName);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalSuccinctMergeTransitionInstruction</c>.</summary>
    private string FormVerbalSuccinctMergeTransitionInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 4;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        string relativeDirection = string.Empty;
        if (maneuver.Type() == DirectionsLegManeuverType.MergeLeft ||
            maneuver.Type() == DirectionsLegManeuverType.MergeRight)
        {
            phraseId += 1;
            relativeDirection = FormRelativeTwoDirection(maneuver.Type(), Dictionary.MergeVerbalSubset.RelativeDirections);
        }

        string instruction = Dictionary.MergeVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(RelativeDirectionTag, relativeDirection);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalSuccinctEnterRoundaboutTransitionInstruction</c>.</summary>
    private string FormVerbalSuccinctEnterRoundaboutTransitionInstruction(Maneuver maneuver)
    {
        NarrativeSubset subset = Dictionary.EnterRoundaboutVerbalSubset;

        int phraseId = 0;
        string guideSign = string.Empty;
        string ordinalValue = string.Empty;

        if (maneuver.RoundaboutExitCount() >= RoundaboutExitCountLowerBound &&
            maneuver.RoundaboutExitCount() <= RoundaboutExitCountUpperBound)
        {
            phraseId += 1;
            ordinalValue = subset.OrdinalValues[(int)maneuver.RoundaboutExitCount() - 1];
        }

        if (maneuver.RoundaboutExitSigns().HasGuide())
        {
            phraseId += 3;
            guideSign = maneuver.RoundaboutExitSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        string instruction = subset.GetPhrase(phraseId);
        instruction = instruction.Replace(OrdinalValueTag, ordinalValue);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>FormVerbalSuccinctExitRoundaboutTransitionInstruction</c>.</summary>
    private string FormVerbalSuccinctExitRoundaboutTransitionInstruction(Maneuver maneuver)
    {
        int phraseId = 0;
        string guideSign = string.Empty;

        if (maneuver.HasGuideSign())
        {
            phraseId = 3;
            guideSign = maneuver.GetSigns().GetGuideString(OdinUtil.VerbalPreElementMaxCount,
                OdinUtil.LimitByConsecutiveCount, OdinUtil.VerbalDelim, maneuver.VerbalFormatter(), MarkupFormatter);
        }

        string instruction = Dictionary.ExitRoundaboutVerbalSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(TowardSignTag, guideSign);
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    // -------------------------------------------------------------------------------------------
    // Length + plural + verbal multi-cue
    // -------------------------------------------------------------------------------------------

    /// <summary>Faithful port of <c>GetPluralCategory</c> (base implementation: "one" or "other").</summary>
    public virtual string GetPluralCategory(int count)
        => count == 1 ? PluralCategoryOneKey : PluralCategoryOtherKey;

    /// <summary>Faithful port of <c>FormLength(Maneuver&amp;, metric, us_customary)</c>.</summary>
    public string FormLength(Maneuver maneuver, IReadOnlyList<string> metricLengths, IReadOnlyList<string> usCustomaryLengths)
    {
        if (Options.Units == OptionsUnits.Miles)
        {
            return FormUsCustomaryLength(
                maneuver.HasCombinedEnterExitRoundabout() ? maneuver.RoundaboutExitLength(true) : maneuver.Length(true),
                usCustomaryLengths);
        }

        return FormMetricLength(
            maneuver.HasCombinedEnterExitRoundabout() ? maneuver.RoundaboutExitLength() : maneuver.Length(),
            metricLengths);
    }

    /// <summary>Faithful port of <c>FormLength(float, metric, us_customary)</c>.</summary>
    public string FormLength(float distance, IReadOnlyList<string> metricLengths, IReadOnlyList<string> usCustomaryLengths)
        => Options.Units == OptionsUnits.Miles
            ? FormUsCustomaryLength(distance, usCustomaryLengths)
            : FormMetricLength(distance, metricLengths);

    /// <summary>
    /// Faithful port of <c>FormMetricLength</c>: rounds a kilometer value to a localized distance
    /// string ("1 kilometer", "&lt;KILOMETERS&gt; kilometers", "&lt;METERS&gt; meters" in 10 m steps,
    /// or "less than 10 meters"). Number formatting uses the invariant culture (the en-US locale for
    /// these &lt; 1000 values inserts no grouping); the precision mirrors the upstream fixed/precision
    /// rules.
    /// </summary>
    public string FormMetricLength(float kilometers, IReadOnlyList<string> metricLengths)
    {
        var lengthString = new StringBuilder();
        string distanceStr = string.Empty;

        float meters = RoundAwayFromZero(kilometers * SharpNinja.Valhalla.Midgard.Constants.MetersPerKm);
        float rounded = 0.0f;

        // For distances that will round to 1 km or greater.
        if (meters > 949)
        {
            if (kilometers > 3)
            {
                // Round to integer for distances greater than 3 km.
                rounded = RoundAwayFromZero(kilometers);
            }
            else
            {
                // Round to whole or half km for 1 km to 3 km distances.
                rounded = RoundAwayFromZero(kilometers * 2.0f) / 2.0f;
            }

            if (rounded == 1.0f)
            {
                lengthString.Append(metricLengths[OneKilometerIndex]);
            }
            else
            {
                lengthString.Append(metricLengths[KilometersIndex]);

                // 1 digit of precision for a fractional value, 0 for a whole number.
                int precision = rounded != (int)rounded ? 1 : 0;
                distanceStr = rounded.ToString("F" + precision, CultureInfo.InvariantCulture);
            }
        }
        else
        {
            if (meters > 94)
            {
                // "<METERS> meters" (100-900 meters)
                lengthString.Append(metricLengths[MetersIndex]);
                distanceStr = (RoundAwayFromZero(meters / 100.0f) * 100.0f).ToString("0", CultureInfo.InvariantCulture);
            }
            else if (meters > 9)
            {
                // "<METERS> meters" (10-90 meters)
                lengthString.Append(metricLengths[MetersIndex]);
                distanceStr = (RoundAwayFromZero(meters / 10.0f) * 10.0f).ToString("0", CultureInfo.InvariantCulture);
            }
            else
            {
                // "less than 10 meters"
                lengthString.Append(metricLengths[SmallMetersIndex]);
            }
        }

        string result = lengthString.ToString();
        result = result.Replace(KilometersTag, distanceStr);
        result = result.Replace(MetersTag, distanceStr);
        return result;
    }

    /// <summary>
    /// Faithful port of <c>FormUsCustomaryLength</c>: rounds a mile value to a localized distance
    /// string ("1 mile", "a half mile", "a quarter mile", "&lt;MILES&gt; miles", "&lt;FEET&gt; feet",
    /// or "less than 10 feet"). Only a rounded value of 1.5 is printed with one decimal, matching the
    /// upstream <c>setprecision(rounded == 1.5f)</c>.
    /// </summary>
    public string FormUsCustomaryLength(float miles, IReadOnlyList<string> usCustomaryLengths)
    {
        var lengthString = new StringBuilder();
        string distanceStr = string.Empty;

        float feet = RoundAwayFromZero(miles * SharpNinja.Valhalla.Midgard.Constants.FeetPerMile);
        float rounded = 0.0f;

        if (feet > 1000)
        {
            if (miles > 2)
            {
                rounded = RoundAwayFromZero(miles);
            }
            else if (miles >= 0.625f)
            {
                rounded = RoundAwayFromZero(miles * 2.0f) / 2.0f;
            }
            else
            {
                rounded = RoundAwayFromZero(miles * 4.0f) / 4.0f;
            }

            if (rounded == 0.25f)
            {
                lengthString.Append(usCustomaryLengths[QuarterMileIndex]);
            }
            else if (rounded == 0.5f)
            {
                lengthString.Append(usCustomaryLengths[HalfMileIndex]);
            }
            else if (rounded == 1.0f)
            {
                lengthString.Append(usCustomaryLengths[OneMileIndex]);
            }
            else
            {
                lengthString.Append(usCustomaryLengths[MilesIndex]);
                int precision = rounded == 1.5f ? 1 : 0;
                distanceStr = rounded.ToString("F" + precision, CultureInfo.InvariantCulture);
            }
        }
        else
        {
            if (feet > 94)
            {
                // "<FEET> feet" (100-1000)
                lengthString.Append(usCustomaryLengths[FeetIndex]);
                distanceStr = (RoundAwayFromZero(feet / 100.0f) * 100.0f).ToString("0", CultureInfo.InvariantCulture);
            }
            else if (feet > 9)
            {
                // "<FEET> feet" (10-90)
                lengthString.Append(usCustomaryLengths[FeetIndex]);
                distanceStr = (RoundAwayFromZero(feet / 10.0f) * 10.0f).ToString("0", CultureInfo.InvariantCulture);
            }
            else
            {
                // "less than 10 feet"
                lengthString.Append(usCustomaryLengths[SmallFeetIndex]);
            }
        }

        string result = lengthString.ToString();
        result = result.Replace(MilesTag, distanceStr);
        result = result.Replace(TenthsOfMilesTag, distanceStr);
        result = result.Replace(FeetTag, distanceStr);
        return result;
    }

    // Faithful port of std::round (rounds halves away from zero, unlike C#'s default banker's rounding).
    private static float RoundAwayFromZero(float value) => MathF.Round(value, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Processes the maneuver list and creates verbal multi-cue instructions for quick maneuvers.
    /// Faithful port of the list-processing <c>FormVerbalMultiCue(std::list&lt;Maneuver&gt;&amp;)</c>.
    /// </summary>
    public void FormVerbalMultiCue(LinkedList<Maneuver> maneuvers)
    {
        Maneuver? prevManeuver = null;
        foreach (Maneuver maneuver in maneuvers)
        {
            if (maneuver.GetPedestrianType() == PedestrianType.Blind)
            {
                continue;
            }

            if (prevManeuver != null && IsVerbalMultiCuePossible(prevManeuver, maneuver))
            {
                switch (maneuver.Type())
                {
                    case DirectionsLegManeuverType.SlightRight:
                    case DirectionsLegManeuverType.Right:
                    case DirectionsLegManeuverType.SharpRight:
                    case DirectionsLegManeuverType.UturnRight:
                    case DirectionsLegManeuverType.RampRight:
                    case DirectionsLegManeuverType.ExitRight:
                    case DirectionsLegManeuverType.StayRight:
                        if (prevManeuver.HasRightTraversableOutboundIntersectingEdge())
                        {
                            prevManeuver.SetDistantVerbalMultiCue(true);
                        }
                        else
                        {
                            prevManeuver.SetImminentVerbalMultiCue(true);
                        }

                        break;

                    case DirectionsLegManeuverType.SlightLeft:
                    case DirectionsLegManeuverType.Left:
                    case DirectionsLegManeuverType.SharpLeft:
                    case DirectionsLegManeuverType.UturnLeft:
                    case DirectionsLegManeuverType.RampLeft:
                    case DirectionsLegManeuverType.ExitLeft:
                    case DirectionsLegManeuverType.StayLeft:
                        if (prevManeuver.HasLeftTraversableOutboundIntersectingEdge())
                        {
                            prevManeuver.SetDistantVerbalMultiCue(true);
                        }
                        else
                        {
                            prevManeuver.SetImminentVerbalMultiCue(true);
                        }

                        break;

                    case DirectionsLegManeuverType.Destination:
                    case DirectionsLegManeuverType.DestinationLeft:
                    case DirectionsLegManeuverType.DestinationRight:
                        if (prevManeuver.HasLeftTraversableOutboundIntersectingEdge() ||
                            prevManeuver.HasRightTraversableOutboundIntersectingEdge())
                        {
                            prevManeuver.SetDistantVerbalMultiCue(true);
                        }
                        else
                        {
                            prevManeuver.SetImminentVerbalMultiCue(true);
                        }

                        break;

                    default:
                        prevManeuver.SetImminentVerbalMultiCue(true);
                        break;
                }

                if (prevManeuver.HasVerbalSuccinctTransitionInstruction())
                {
                    prevManeuver.SetVerbalSuccinctTransitionInstruction(FormVerbalMultiCue(prevManeuver, maneuver, true));
                }

                prevManeuver.SetVerbalPreTransitionInstruction(FormVerbalMultiCue(prevManeuver, maneuver));
            }

            prevManeuver = maneuver;
        }
    }

    /// <summary>Faithful port of <c>FormVerbalMultiCue(Maneuver&amp;, Maneuver&amp;, bool)</c>.</summary>
    private string FormVerbalMultiCue(Maneuver maneuver, Maneuver nextManeuver, bool processSuccinct = false)
    {
        string currentVerbalCue = processSuccinct && maneuver.HasVerbalSuccinctTransitionInstruction()
            ? maneuver.VerbalSuccinctTransitionInstruction()
            : maneuver.VerbalPreTransitionInstruction();

        string nextVerbalCue = nextManeuver.HasVerbalTransitionAlertInstruction()
            ? nextManeuver.VerbalTransitionAlertInstruction()
            : nextManeuver.VerbalPreTransitionInstruction();

        return FormVerbalMultiCue(maneuver, currentVerbalCue, nextVerbalCue);
    }

    /// <summary>Faithful port of <c>FormVerbalMultiCue(Maneuver&amp;, first_cue, second_cue)</c>.</summary>
    private string FormVerbalMultiCue(Maneuver maneuver, string firstVerbalCue, string secondVerbalCue)
    {
        int phraseId = 0;
        if (maneuver.DistantVerbalMultiCue())
        {
            phraseId = 1;
        }

        string instruction = Dictionary.VerbalMultiCueSubset.GetPhrase(phraseId);
        instruction = instruction.Replace(CurrentVerbalCueTag, firstVerbalCue);
        instruction = instruction.Replace(NextVerbalCueTag, secondVerbalCue);
        instruction = instruction.Replace(LengthTag, FormLength(maneuver,
            Dictionary.PostTransitionVerbalSubset.MetricLengths, Dictionary.PostTransitionVerbalSubset.UsCustomaryLengths));
        if (_articulatedPrepositionEnabled)
        {
            FormArticulatedPrepositions(ref instruction);
        }

        return instruction;
    }

    /// <summary>Faithful port of <c>IsVerbalMultiCuePossible</c>.</summary>
    private bool IsVerbalMultiCuePossible(Maneuver maneuver, Maneuver nextManeuver)
        => maneuver.HasVerbalPreTransitionInstruction()
           && (nextManeuver.HasVerbalTransitionAlertInstruction() || nextManeuver.HasVerbalPreTransitionInstruction())
           && IsWithinVerbalMultiCueBounds(maneuver)
           && !nextManeuver.IsMergeType()
           && (!maneuver.Roundabout() || maneuver.HasCombinedEnterExitRoundabout())
           && !(maneuver.Type() == DirectionsLegManeuverType.RoundaboutExit && nextManeuver.Roundabout())
           && !maneuver.IsTransit()
           && !nextManeuver.IsTransit()
           && !maneuver.TransitConnection()
           && !nextManeuver.TransitConnection();

    /// <summary>Faithful port of <c>IsWithinVerbalMultiCueBounds</c>.</summary>
    private static bool IsWithinVerbalMultiCueBounds(Maneuver maneuver)
        => maneuver.IsStartType()
            ? maneuver.BasicTime() < VerbalMultiCueTimeStartManeuverThreshold
            : maneuver.BasicTime() < VerbalMultiCueTimeThreshold;

    // -------------------------------------------------------------------------------------------
    // Shared helpers
    // -------------------------------------------------------------------------------------------

    /// <summary>Faithful port of <c>FormRelativeTwoDirection</c>.</summary>
    private static string FormRelativeTwoDirection(DirectionsLegManeuverType type, IReadOnlyList<string> relativeDirections)
    {
        switch (type)
        {
            case DirectionsLegManeuverType.Left:
            case DirectionsLegManeuverType.SharpLeft:
            case DirectionsLegManeuverType.SlightLeft:
            case DirectionsLegManeuverType.UturnLeft:
            case DirectionsLegManeuverType.RampLeft:
            case DirectionsLegManeuverType.ExitLeft:
            case DirectionsLegManeuverType.MergeLeft:
            case DirectionsLegManeuverType.DestinationLeft:
                return relativeDirections[0]; // "left"
            case DirectionsLegManeuverType.Right:
            case DirectionsLegManeuverType.SharpRight:
            case DirectionsLegManeuverType.SlightRight:
            case DirectionsLegManeuverType.UturnRight:
            case DirectionsLegManeuverType.RampRight:
            case DirectionsLegManeuverType.ExitRight:
            case DirectionsLegManeuverType.MergeRight:
            case DirectionsLegManeuverType.DestinationRight:
                return relativeDirections[1]; // "right"
            default:
                throw new ValhallaException(231);
        }
    }

    /// <summary>Faithful port of <c>FormRelativeThreeDirection</c>.</summary>
    private static string FormRelativeThreeDirection(DirectionsLegManeuverType type, IReadOnlyList<string> relativeDirections)
    {
        return type switch
        {
            DirectionsLegManeuverType.StayLeft => relativeDirections[0],     // "left"
            DirectionsLegManeuverType.StayStraight => relativeDirections[1], // "straight"
            DirectionsLegManeuverType.StayRight => relativeDirections[2],    // "right"
            _ => throw new ValhallaException(232),
        };
    }

    /// <summary>
    /// Faithful port of the maneuver-aware <c>FormStreetNames</c> overload (written path: max_count 0,
    /// delim "/", no verbal formatter). Enhances empty street names to the empty-street-name labels
    /// for unnamed pedestrian / bicycle / blind footways when requested.
    /// </summary>
    private string FormStreetNames(
        Maneuver maneuver,
        StreetNames streetNames,
        IReadOnlyList<string>? emptyStreetNameLabels = null,
        bool enhanceEmptyStreetNames = false,
        uint maxCount = 0,
        string delim = WrittenDelim,
        VerbalTextFormatter? verbalFormatter = null)
    {
        string streetNamesString = string.Empty;

        if (streetNames.Count != 0)
        {
            streetNamesString = FormStreetNames(streetNames, maxCount, delim, verbalFormatter);
        }

        if (enhanceEmptyStreetNames && streetNamesString.Length == 0 && emptyStreetNameLabels != null)
        {
            if (maneuver.GetPedestrianType() == PedestrianType.Blind)
            {
                if (maneuver.IsSteps())
                {
                    streetNamesString = emptyStreetNameLabels[StepsIndex];
                }
                else if (maneuver.IsBridge())
                {
                    streetNamesString = emptyStreetNameLabels[BridgeIndex];
                }
                else if (maneuver.IsTunnel())
                {
                    streetNamesString = emptyStreetNameLabels[TunnelIndex];
                }
            }
            else if (maneuver.GetTravelMode() == TravelMode.Pedestrian && maneuver.UnnamedWalkway())
            {
                int dictionaryIndex = maneuver.PedestrianCrossing() ? PedestrianCrossingIndex : WalkwayIndex;
                streetNamesString = emptyStreetNameLabels[dictionaryIndex];
            }
            else if (maneuver.GetTravelMode() == TravelMode.Bicycle && maneuver.UnnamedCycleway())
            {
                streetNamesString = emptyStreetNameLabels[CyclewayIndex];
            }
            else if (maneuver.GetTravelMode() == TravelMode.Bicycle && maneuver.UnnamedMountainBikeTrail())
            {
                streetNamesString = emptyStreetNameLabels[MountainBikeTrailIndex];
            }
        }

        return streetNamesString;
    }

    /// <summary>
    /// Faithful port of the list-joining <c>FormStreetNames</c> overload (written path: raw
    /// street-name value, no verbal formatter).
    /// </summary>
    private string FormStreetNames(
        StreetNames streetNames,
        uint maxCount = 0,
        string delim = WrittenDelim,
        VerbalTextFormatter? verbalFormatter = null)
    {
        var sb = new StringBuilder();
        uint count = 0;

        foreach (StreetName streetName in streetNames)
        {
            if (maxCount > 0 && count == maxCount)
            {
                break;
            }

            if (sb.Length != 0)
            {
                sb.Append(delim);
            }

            sb.Append(verbalFormatter != null ? verbalFormatter.Format(streetName, MarkupFormatter) : streetName.Value);
            ++count;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Combines a simple preposition and a definite article for certain languages. Faithful port of the
    /// base <c>FormArticulatedPrepositions</c> - a no-op unless a per-locale subclass overrides it (see
    /// <see cref="_articulatedPrepositionEnabled"/>). Every built instruction is passed through this
    /// hook at the end of its former when the flag is enabled.
    /// </summary>
    protected virtual void FormArticulatedPrepositions(ref string instruction)
    {
    }

    /// <summary>Faithful port of <c>UpdateObviousManeuverStreetNames</c>.</summary>
    private static void UpdateObviousManeuverStreetNames(Maneuver maneuver, ref string beginStreetNames, ref string streetNames)
    {
        if (maneuver.ContainsObviousManeuver() && beginStreetNames.Length != 0)
        {
            streetNames = beginStreetNames;
            beginStreetNames = string.Empty;
        }
    }

    /// <summary>
    /// Faithful port of <c>HasLabel</c> (boost iends_with -> case-insensitive suffix match).
    /// </summary>
    private static bool HasLabel(string name, string label)
        => name.EndsWith(label, StringComparison.OrdinalIgnoreCase);

    /// <summary>Faithful port of <c>FormBssManeuverType</c>.</summary>
    private static string FormBssManeuverType(DirectionsLegManeuverBssManeuverType type)
        => type switch
        {
            DirectionsLegManeuverBssManeuverType.RentBikeAtBikeShare => "Then rent a bike at BSS. ",
            DirectionsLegManeuverBssManeuverType.ReturnBikeAtBikeShare => "Then return the bike to BSS. ",
            _ => string.Empty,
        };
}
