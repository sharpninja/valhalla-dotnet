using System.Collections.Generic;
using System.Linq;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>Parses Valhalla 3.8 language and pronunciation tags retained by the OSM transform.</summary>
internal static class OSMWayLinguisticTagParser
{
    private readonly record struct NameTag(OSMLinguisticType Type, string Key);

    private static readonly NameTag[] NameTags =
    {
        new(OSMLinguisticType.DestinationStreetTo, "destination:street:to"),
        new(OSMLinguisticType.DestinationRefTo, "destination:ref:to"),
        new(OSMLinguisticType.DestinationBackward, "destination:backward"),
        new(OSMLinguisticType.DestinationForward, "destination:forward"),
        new(OSMLinguisticType.OfficialNameRight, "official_name:right"),
        new(OSMLinguisticType.OfficialNameLeft, "official_name:left"),
        new(OSMLinguisticType.DestinationStreet, "destination:street"),
        new(OSMLinguisticType.AltNameRight, "alt_name:right"),
        new(OSMLinguisticType.AltNameLeft, "alt_name:left"),
        new(OSMLinguisticType.TunnelNameRight, "tunnel:name:right"),
        new(OSMLinguisticType.TunnelNameLeft, "tunnel:name:left"),
        new(OSMLinguisticType.NameBackward, "name:backward"),
        new(OSMLinguisticType.NameForward, "name:forward"),
        new(OSMLinguisticType.DestinationRef, "destination:ref"),
        new(OSMLinguisticType.JunctionName, "junction:name"),
        new(OSMLinguisticType.JunctionRef, "junction:ref"),
        new(OSMLinguisticType.NameRight, "name:right"),
        new(OSMLinguisticType.NameLeft, "name:left"),
        new(OSMLinguisticType.IntRefRight, "int_ref:right"),
        new(OSMLinguisticType.IntRefLeft, "int_ref:left"),
        new(OSMLinguisticType.RefRight, "ref:right"),
        new(OSMLinguisticType.RefLeft, "ref:left"),
        new(OSMLinguisticType.OfficialName, "official_name"),
        new(OSMLinguisticType.TunnelName, "tunnel:name"),
        new(OSMLinguisticType.Destination, "destination"),
        new(OSMLinguisticType.AltName, "alt_name"),
        new(OSMLinguisticType.IntRef, "int_ref"),
        new(OSMLinguisticType.Name, "name"),
        new(OSMLinguisticType.Ref, "ref"),
    };

    public static void Apply(
        OSMWay way,
        IReadOnlyDictionary<string, string> tags,
        UniqueNames names)
    {
        ArgumentNullException.ThrowIfNull(way);
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentNullException.ThrowIfNull(names);

        foreach (NameTag nameTag in NameTags)
        {
            if (tags.TryGetValue(nameTag.Key, out string? value) && !string.IsNullOrEmpty(value))
            {
                SetNameIndex(way, nameTag.Type, names.Index(value));
            }
        }

        var languagesByType = new Dictionary<OSMLinguisticType, List<Language>>();
        foreach (KeyValuePair<string, string> tag in tags.OrderBy(value => value.Key, StringComparer.Ordinal))
        {
            if (string.IsNullOrEmpty(tag.Value) ||
                !TryMatchNameTag(tag.Key, out NameTag nameTag, out string suffix))
            {
                continue;
            }

            if (TryParsePronunciationSuffix(suffix, out Language pronunciationLanguage, out PronunciationAlphabet alphabet))
            {
                way.AddPronunciation(nameTag.Type, pronunciationLanguage, alphabet, tag.Value);
                continue;
            }

            if (!TryParseLanguageSuffix(suffix, out Language language))
            {
                continue;
            }

            way.AddLinguisticName(nameTag.Type, language, tag.Value);
            if (!languagesByType.TryGetValue(nameTag.Type, out List<Language>? languages))
            {
                languages = new List<Language>();
                languagesByType[nameTag.Type] = languages;
            }

            foreach (string _ in tag.Value.Split(';'))
            {
                languages.Add(language);
            }
        }

        foreach (KeyValuePair<OSMLinguisticType, List<Language>> entry in languagesByType)
        {
            string languageList = string.Join(
                ';',
                entry.Value.Select(GraphConstants.ToStringValue));
            SetLanguageIndex(way, entry.Key, names.Index(languageList));
        }
    }

    private static bool TryMatchNameTag(
        string key,
        out NameTag nameTag,
        out string suffix)
    {
        foreach (NameTag candidate in NameTags)
        {
            if (key.Length > candidate.Key.Length &&
                key.StartsWith(candidate.Key, StringComparison.Ordinal) &&
                key[candidate.Key.Length] == ':')
            {
                nameTag = candidate;
                suffix = key[(candidate.Key.Length + 1)..];
                return true;
            }
        }

        nameTag = default;
        suffix = string.Empty;
        return false;
    }

    private static bool TryParseLanguageSuffix(string suffix, out Language language)
    {
        string value = suffix.StartsWith("lang:", StringComparison.Ordinal)
            ? suffix[5..]
            : suffix;
        language = GraphConstants.StringLanguage(value);
        return language != Language.None;
    }

    private static bool TryParsePronunciationSuffix(
        string suffix,
        out Language language,
        out PronunciationAlphabet alphabet)
    {
        string[] tokens = suffix.Split(':', StringSplitOptions.RemoveEmptyEntries);
        int pronunciationIndex = Array.FindIndex(
            tokens,
            token => token.Equals("pronunciation", StringComparison.OrdinalIgnoreCase));
        if (pronunciationIndex < 0)
        {
            language = Language.None;
            alphabet = PronunciationAlphabet.None;
            return false;
        }

        language = Language.None;
        alphabet = PronunciationAlphabet.Ipa;
        for (int index = 0; index < tokens.Length; index++)
        {
            if (index == pronunciationIndex ||
                tokens[index].Equals("lang", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (TryParseAlphabet(tokens[index], out PronunciationAlphabet parsedAlphabet))
            {
                alphabet = parsedAlphabet;
                continue;
            }

            Language parsedLanguage = GraphConstants.StringLanguage(tokens[index]);
            if (parsedLanguage != Language.None)
            {
                language = parsedLanguage;
            }
        }

        return true;
    }

    private static bool TryParseAlphabet(
        string value,
        out PronunciationAlphabet alphabet)
    {
        alphabet = value.ToLowerInvariant() switch
        {
            "ipa" => PronunciationAlphabet.Ipa,
            "nt-sampa" or "ntsampa" => PronunciationAlphabet.NtSampa,
            "katakana" => PronunciationAlphabet.Katakana,
            "jeita" => PronunciationAlphabet.Jeita,
            _ => PronunciationAlphabet.None,
        };
        return alphabet != PronunciationAlphabet.None;
    }

    private static void SetNameIndex(OSMWay way, OSMLinguisticType type, uint index)
    {
        switch (type)
        {
            case OSMLinguisticType.Name: way.NameIndex = index; break;
            case OSMLinguisticType.NameLeft: way.NameLeftIndex = index; break;
            case OSMLinguisticType.NameRight: way.NameRightIndex = index; break;
            case OSMLinguisticType.NameForward: way.NameForwardIndex = index; break;
            case OSMLinguisticType.NameBackward: way.NameBackwardIndex = index; break;
            case OSMLinguisticType.AltName: way.AltNameIndex = index; break;
            case OSMLinguisticType.AltNameLeft: way.AltNameLeftIndex = index; break;
            case OSMLinguisticType.AltNameRight: way.AltNameRightIndex = index; break;
            case OSMLinguisticType.OfficialName: way.OfficialNameIndex = index; break;
            case OSMLinguisticType.OfficialNameLeft: way.OfficialNameLeftIndex = index; break;
            case OSMLinguisticType.OfficialNameRight: way.OfficialNameRightIndex = index; break;
            case OSMLinguisticType.TunnelName: way.TunnelNameIndex = index; break;
            case OSMLinguisticType.TunnelNameLeft: way.TunnelNameLeftIndex = index; break;
            case OSMLinguisticType.TunnelNameRight: way.TunnelNameRightIndex = index; break;
            case OSMLinguisticType.Ref: way.RefIndex = index; break;
            case OSMLinguisticType.RefLeft: way.RefLeftIndex = index; break;
            case OSMLinguisticType.RefRight: way.RefRightIndex = index; break;
            case OSMLinguisticType.IntRef: way.IntRefIndex = index; break;
            case OSMLinguisticType.IntRefLeft: way.IntRefLeftIndex = index; break;
            case OSMLinguisticType.IntRefRight: way.IntRefRightIndex = index; break;
            case OSMLinguisticType.Destination: way.DestinationIndex = index; break;
            case OSMLinguisticType.DestinationForward: way.DestinationForwardIndex = index; break;
            case OSMLinguisticType.DestinationBackward: way.DestinationBackwardIndex = index; break;
            case OSMLinguisticType.DestinationRef: way.DestinationRefIndex = index; break;
            case OSMLinguisticType.DestinationRefTo: way.DestinationRefToIndex = index; break;
            case OSMLinguisticType.DestinationStreet: way.DestinationStreetIndex = index; break;
            case OSMLinguisticType.DestinationStreetTo: way.DestinationStreetToIndex = index; break;
            case OSMLinguisticType.JunctionRef: way.JunctionRefIndex = index; break;
            case OSMLinguisticType.JunctionName: way.JunctionNameIndex = index; break;
        }
    }

    private static void SetLanguageIndex(OSMWay way, OSMLinguisticType type, uint index)
    {
        switch (type)
        {
            case OSMLinguisticType.Name: way.NameLangIndex = index; break;
            case OSMLinguisticType.NameLeft: way.NameLeftLangIndex = index; break;
            case OSMLinguisticType.NameRight: way.NameRightLangIndex = index; break;
            case OSMLinguisticType.NameForward: way.NameForwardLangIndex = index; break;
            case OSMLinguisticType.NameBackward: way.NameBackwardLangIndex = index; break;
            case OSMLinguisticType.AltName: way.AltNameLangIndex = index; break;
            case OSMLinguisticType.AltNameLeft: way.AltNameLeftLangIndex = index; break;
            case OSMLinguisticType.AltNameRight: way.AltNameRightLangIndex = index; break;
            case OSMLinguisticType.OfficialName: way.OfficialNameLangIndex = index; break;
            case OSMLinguisticType.OfficialNameLeft: way.OfficialNameLeftLangIndex = index; break;
            case OSMLinguisticType.OfficialNameRight: way.OfficialNameRightLangIndex = index; break;
            case OSMLinguisticType.TunnelName: way.TunnelNameLangIndex = index; break;
            case OSMLinguisticType.TunnelNameLeft: way.TunnelNameLeftLangIndex = index; break;
            case OSMLinguisticType.TunnelNameRight: way.TunnelNameRightLangIndex = index; break;
            case OSMLinguisticType.Ref: way.RefLangIndex = index; break;
            case OSMLinguisticType.RefLeft: way.RefLeftLangIndex = index; break;
            case OSMLinguisticType.RefRight: way.RefRightLangIndex = index; break;
            case OSMLinguisticType.IntRef: way.IntRefLangIndex = index; break;
            case OSMLinguisticType.IntRefLeft: way.IntRefLeftLangIndex = index; break;
            case OSMLinguisticType.IntRefRight: way.IntRefRightLangIndex = index; break;
            case OSMLinguisticType.Destination: way.DestinationLangIndex = index; break;
            case OSMLinguisticType.DestinationForward: way.DestinationForwardLangIndex = index; break;
            case OSMLinguisticType.DestinationBackward: way.DestinationBackwardLangIndex = index; break;
            case OSMLinguisticType.DestinationRef: way.DestinationRefLangIndex = index; break;
            case OSMLinguisticType.DestinationRefTo: way.DestinationRefToLangIndex = index; break;
            case OSMLinguisticType.DestinationStreet: way.DestinationStreetLangIndex = index; break;
            case OSMLinguisticType.DestinationStreetTo: way.DestinationStreetToLangIndex = index; break;
            case OSMLinguisticType.JunctionRef: way.JunctionRefLangIndex = index; break;
            case OSMLinguisticType.JunctionName: way.JunctionNameLangIndex = index; break;
        }
    }
}
