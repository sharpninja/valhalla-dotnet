// Faithful C# port of the odin locale-loading path
// (valhalla/odin/util.{h,cc} get_locales / load_narrative_locals + narrative_dictionary.cc Load) @ 3.7.0,
// reduced to what this port needs: map a BCP-47 language tag to an embedded locale JSON, parse it into a
// NarrativeDictionary, cache it, and fall back to en-US when the tag is unknown.
//
// PORT-NOTE: upstream parses the localized files with a boost property_tree; here System.Text.Json reads
// the same verbatim JSON. Unknown members (example_phrases inside every subset, top-level aliases) are
// ignored. Subset keys absent from a given locale map to an empty subset rather than throwing (upstream
// get_child would throw); this keeps every shipped locale loadable and matches the plan's "loader
// tolerates keys it does not map".

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace SharpNinja.Valhalla.Odin;

/// <summary>
/// Loads and caches <see cref="NarrativeDictionary"/> instances by BCP-47 language tag from the
/// embedded locale resources (SharpNinja.Valhalla.Odin.Locales.&lt;tag&gt;.json).
/// </summary>
public static class NarrativeDictionaryLoader
{
    /// <summary>The default language tag used when a requested tag is unknown or empty.</summary>
    public const string DefaultLanguageTag = "en-US";

    private const string ResourcePrefix = "SharpNinja.Valhalla.Odin.Locales.";
    private const string ResourceSuffix = ".json";
    private const string DefaultPosixLocale = "en_US.UTF-8";

    private static readonly Assembly ResourceAssembly = typeof(NarrativeDictionaryLoader).Assembly;

    // requested-normalized tag -> resolved NarrativeDictionary (the resolved dictionary carries the
    // resolved tag, so an unknown request that falls back to en-US returns a dictionary tagged en-US).
    private static readonly ConcurrentDictionary<string, NarrativeDictionary> Cache = new();

    // Canonical tag (as it appears in the resource name) keyed by lower-cased tag, built once from the
    // embedded resource names.
    private static readonly Lazy<IReadOnlyDictionary<string, string>> AvailableTags =
        new(BuildAvailableTags);

    /// <summary>
    /// Returns the cached <see cref="NarrativeDictionary"/> for the specified BCP-47 language tag,
    /// falling back to <see cref="DefaultLanguageTag"/> when the tag is null, empty, or has no embedded
    /// resource.
    /// </summary>
    public static NarrativeDictionary Get(string? language)
    {
        string resolved = Resolve(language);
        return Cache.GetOrAdd(resolved, static tag => Build(tag));
    }

    /// <summary>Returns the canonical tags of every embedded locale (as they appear in file names).</summary>
    public static IReadOnlyCollection<string> AvailableLanguageTags => (IReadOnlyCollection<string>)AvailableTags.Value.Values;

    private static string Resolve(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return DefaultLanguageTag;
        }

        return AvailableTags.Value.TryGetValue(language.Trim().ToLowerInvariant(), out var canonical)
            ? canonical
            : DefaultLanguageTag;
    }

    private static IReadOnlyDictionary<string, string> BuildAvailableTags()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in ResourceAssembly.GetManifestResourceNames())
        {
            if (name.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                && name.EndsWith(ResourceSuffix, StringComparison.Ordinal))
            {
                var tag = name.Substring(
                    ResourcePrefix.Length,
                    name.Length - ResourcePrefix.Length - ResourceSuffix.Length);
                map[tag.ToLowerInvariant()] = tag;
            }
        }

        return map;
    }

    private static NarrativeDictionary Build(string tag)
    {
        using var stream = ResourceAssembly.GetManifestResourceStream(ResourcePrefix + tag + ResourceSuffix)
            ?? throw new InvalidOperationException($"Embedded narrative locale resource not found for tag '{tag}'.");
        using var doc = JsonDocument.Parse(stream);
        JsonElement root = doc.RootElement;

        string posixLocale = root.TryGetProperty("posix_locale", out var pl) && pl.ValueKind == JsonValueKind.String
            ? pl.GetString() ?? DefaultPosixLocale
            : DefaultPosixLocale;

        JsonElement instructions = root.GetProperty("instructions");

        NarrativeSubset Subset(string key) => ParseSubset(instructions, key);

        return new NarrativeDictionary(tag, posixLocale)
        {
            StartSubset = Subset("start"),
            StartVerbalSubset = Subset("start_verbal"),
            DestinationSubset = Subset("destination"),
            DestinationVerbalAlertSubset = Subset("destination_verbal_alert"),
            DestinationVerbalSubset = Subset("destination_verbal"),
            BecomesSubset = Subset("becomes"),
            BecomesVerbalSubset = Subset("becomes_verbal"),
            ContinueSubset = Subset("continue"),
            ContinueVerbalAlertSubset = Subset("continue_verbal_alert"),
            ContinueVerbalSubset = Subset("continue_verbal"),
            BearSubset = Subset("bear"),
            BearVerbalSubset = Subset("bear_verbal"),
            TurnSubset = Subset("turn"),
            TurnVerbalSubset = Subset("turn_verbal"),
            SharpSubset = Subset("sharp"),
            SharpVerbalSubset = Subset("sharp_verbal"),
            UturnSubset = Subset("uturn"),
            UturnVerbalSubset = Subset("uturn_verbal"),
            RampStraightSubset = Subset("ramp_straight"),
            RampStraightVerbalSubset = Subset("ramp_straight_verbal"),
            RampSubset = Subset("ramp"),
            RampVerbalSubset = Subset("ramp_verbal"),
            ExitSubset = Subset("exit"),
            ExitVerbalSubset = Subset("exit_verbal"),
            ExitVisualSubset = Subset("exit_visual"),
            KeepSubset = Subset("keep"),
            KeepVerbalSubset = Subset("keep_verbal"),
            KeepToStayOnSubset = Subset("keep_to_stay_on"),
            KeepToStayOnVerbalSubset = Subset("keep_to_stay_on_verbal"),
            MergeSubset = Subset("merge"),
            MergeVerbalSubset = Subset("merge_verbal"),
            EnterRoundaboutSubset = Subset("enter_roundabout"),
            EnterRoundaboutVerbalSubset = Subset("enter_roundabout_verbal"),
            ExitRoundaboutSubset = Subset("exit_roundabout"),
            ExitRoundaboutVerbalSubset = Subset("exit_roundabout_verbal"),
            EnterFerrySubset = Subset("enter_ferry"),
            EnterFerryVerbalSubset = Subset("enter_ferry_verbal"),
            TransitConnectionStartSubset = Subset("transit_connection_start"),
            TransitConnectionStartVerbalSubset = Subset("transit_connection_start_verbal"),
            TransitConnectionTransferSubset = Subset("transit_connection_transfer"),
            TransitConnectionTransferVerbalSubset = Subset("transit_connection_transfer_verbal"),
            TransitConnectionDestinationSubset = Subset("transit_connection_destination"),
            TransitConnectionDestinationVerbalSubset = Subset("transit_connection_destination_verbal"),
            DepartSubset = Subset("depart"),
            DepartVerbalSubset = Subset("depart_verbal"),
            ArriveSubset = Subset("arrive"),
            ArriveVerbalSubset = Subset("arrive_verbal"),
            TransitSubset = Subset("transit"),
            TransitVerbalSubset = Subset("transit_verbal"),
            TransitRemainOnSubset = Subset("transit_remain_on"),
            TransitRemainOnVerbalSubset = Subset("transit_remain_on_verbal"),
            TransitTransferSubset = Subset("transit_transfer"),
            TransitTransferVerbalSubset = Subset("transit_transfer_verbal"),
            PostTransitionVerbalSubset = Subset("post_transition_verbal"),
            PostTransitionTransitVerbalSubset = Subset("post_transition_transit_verbal"),
            VerbalMultiCueSubset = Subset("verbal_multi_cue"),
            ApproachVerbalAlertSubset = Subset("approach_verbal_alert"),
            PassSubset = Subset("pass"),
            ElevatorSubset = Subset("elevator"),
            StepsSubset = Subset("steps"),
            EscalatorSubset = Subset("escalator"),
            LevelChangeSubset = Subset("level_change"),
            EnterBuildingSubset = Subset("enter_building"),
            ExitBuildingSubset = Subset("exit_building"),
            ParkVehicleSubset = Subset("park_vehicle"),
        };
    }

    private static NarrativeSubset ParseSubset(JsonElement instructions, string key)
    {
        if (!instructions.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return new NarrativeSubset();
        }

        return new NarrativeSubset
        {
            Phrases = ReadStringMap(node, "phrases"),
            CardinalDirections = ReadStringArray(node, "cardinal_directions"),
            RelativeDirections = ReadStringArray(node, "relative_directions"),
            OrdinalValues = ReadStringArray(node, "ordinal_values"),
            EmptyStreetNameLabels = ReadStringArray(node, "empty_street_name_labels"),
            MetricLengths = ReadStringArray(node, "metric_lengths"),
            UsCustomaryLengths = ReadStringArray(node, "us_customary_lengths"),
            EmptyTransitNameLabels = ReadStringArray(node, "empty_transit_name_labels"),
            ObjectLabels = ReadStringArray(node, "object_labels"),
            FerryLabel = ReadString(node, "ferry_label"),
            StationLabel = ReadString(node, "station_label"),
            TransitStopCountLabels = ReadStringMap(node, "transit_stop_count_labels"),
        };
    }

    private static IReadOnlyDictionary<string, string> ReadStringMap(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string>(0);
        }

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in node.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                map[prop.Name] = prop.Value.GetString() ?? string.Empty;
            }
        }

        return map;
    }

    private static IReadOnlyList<string> ReadStringArray(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var node) || node.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>(node.GetArrayLength());
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString() ?? string.Empty);
            }
        }

        return list;
    }

    private static string ReadString(JsonElement parent, string key)
        => parent.TryGetProperty(key, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? string.Empty
            : string.Empty;
}
