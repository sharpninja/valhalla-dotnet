using System.Collections.Generic;
using System.Linq;
using System.Text;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

internal sealed record OSMWayNameData(
    IReadOnlyList<string> Names,
    IReadOnlyList<string> Linguistics,
    ushort Types,
    bool DiffNames);

/// <summary>
/// Builds the ordered EdgeInfo name list and Valhalla 3.8 linguistic records for one directed way.
/// </summary>
internal static class OSMWayLinguisticBuilder
{
    private sealed record NameSlot(byte Index, Language Language);

    public static OSMWayNameData Build(
        OSMWay way,
        string relationRef,
        UniqueNames names,
        bool forward)
    {
        ArgumentNullException.ThrowIfNull(way);
        ArgumentNullException.ThrowIfNull(names);

        var orderedNames = new List<string>(EdgeInfo.MaxNamesPerEdge);
        var linguisticRecords = new List<string>();
        ushort types = 0;
        bool diffNames = false;
        int nameLimit = way.LinguisticNames.Count != 0 || way.Pronunciations.Count != 0
            ? EdgeInfo.MaxNamesPerEdge - 1
            : EdgeInfo.MaxNamesPerEdge;

        (uint refIndex, OSMLinguisticType refType, bool refDiff) =
            SelectDirectionalIndex(
                way.RefIndex,
                way.RefLeftIndex,
                way.RefRightIndex,
                0,
                0,
                forward,
                OSMLinguisticType.Ref,
                OSMLinguisticType.RefLeft,
                OSMLinguisticType.RefRight,
                OSMLinguisticType.Ref,
                OSMLinguisticType.Ref);
        diffNames |= refDiff;

        string refValue = string.IsNullOrEmpty(relationRef)
            ? names.Name(refIndex)
            : relationRef;
        AppendGroup(
            way,
            refType,
            refValue,
            isRouteNumber: true,
            nameLimit,
            orderedNames,
            linguisticRecords,
            ref types);

        (uint nameIndex, OSMLinguisticType nameType, bool nameDiff) =
            SelectDirectionalIndex(
                way.NameIndex,
                way.NameLeftIndex,
                way.NameRightIndex,
                way.NameForwardIndex,
                way.NameBackwardIndex,
                forward,
                OSMLinguisticType.Name,
                OSMLinguisticType.NameLeft,
                OSMLinguisticType.NameRight,
                OSMLinguisticType.NameForward,
                OSMLinguisticType.NameBackward);
        diffNames |= nameDiff;
        AppendGroup(
            way,
            nameType,
            names.Name(nameIndex),
            isRouteNumber: false,
            nameLimit,
            orderedNames,
            linguisticRecords,
            ref types);

        (uint altIndex, OSMLinguisticType altType, bool altDiff) =
            SelectDirectionalIndex(
                way.AltNameIndex,
                way.AltNameLeftIndex,
                way.AltNameRightIndex,
                0,
                0,
                forward,
                OSMLinguisticType.AltName,
                OSMLinguisticType.AltNameLeft,
                OSMLinguisticType.AltNameRight,
                OSMLinguisticType.AltName,
                OSMLinguisticType.AltName);
        diffNames |= altDiff;
        AppendGroup(
            way,
            altType,
            names.Name(altIndex),
            isRouteNumber: false,
            nameLimit,
            orderedNames,
            linguisticRecords,
            ref types);

        (uint officialIndex, OSMLinguisticType officialType, bool officialDiff) =
            SelectDirectionalIndex(
                way.OfficialNameIndex,
                way.OfficialNameLeftIndex,
                way.OfficialNameRightIndex,
                0,
                0,
                forward,
                OSMLinguisticType.OfficialName,
                OSMLinguisticType.OfficialNameLeft,
                OSMLinguisticType.OfficialNameRight,
                OSMLinguisticType.OfficialName,
                OSMLinguisticType.OfficialName);
        diffNames |= officialDiff;
        AppendGroup(
            way,
            officialType,
            names.Name(officialIndex),
            isRouteNumber: false,
            nameLimit,
            orderedNames,
            linguisticRecords,
            ref types);

        return new OSMWayNameData(orderedNames, linguisticRecords, types, diffNames);
    }

    private static void AppendGroup(
        OSMWay way,
        OSMLinguisticType type,
        string baseValue,
        bool isRouteNumber,
        int nameLimit,
        List<string> names,
        List<string> linguistics,
        ref ushort types)
    {
        var slots = new List<NameSlot>();
        ushort updatedTypes = types;

        foreach (string token in SplitTokens(baseValue))
        {
            AddName(token, Language.None);
        }

        foreach (OSMLinguisticName linguisticName in way.LinguisticNames.Where(value => value.Type == type))
        {
            foreach (string token in SplitTokens(linguisticName.Text))
            {
                AddName(token, linguisticName.Language);
            }
        }

        if (slots.Count == 0)
        {
            return;
        }

        var slotsWithPronunciation = new HashSet<byte>();
        foreach (OSMPronunciation pronunciation in OrderPronunciations(
                     way.Pronunciations.Where(value => value.Type == type)))
        {
            List<string> pronunciationTokens = SplitTokens(pronunciation.Text);
            if (pronunciationTokens.Count == 0)
            {
                continue;
            }

            List<NameSlot> candidates = pronunciation.Language == Language.None
                ? slots
                : slots.Where(slot => slot.Language == pronunciation.Language).ToList();

            if (candidates.Count == 0)
            {
                candidates = slots.Where(slot => slot.Language == Language.None).ToList();
            }

            int count = Math.Min(candidates.Count, pronunciationTokens.Count);
            for (int index = 0; index < count; index++)
            {
                NameSlot slot = candidates[index];
                Language language = pronunciation.Language == Language.None
                    ? slot.Language
                    : pronunciation.Language;
                linguistics.Add(CreateRecord(
                    slot.Index,
                    language,
                    pronunciation.Alphabet,
                    pronunciationTokens[index]));
                slotsWithPronunciation.Add(slot.Index);
            }
        }

        foreach (NameSlot slot in slots)
        {
            if (slot.Language != Language.None && !slotsWithPronunciation.Contains(slot.Index))
            {
                linguistics.Add(CreateRecord(
                    slot.Index,
                    slot.Language,
                    PronunciationAlphabet.None,
                    string.Empty));
            }
        }

        types = updatedTypes;

        void AddName(string value, Language language)
        {
            if (string.IsNullOrEmpty(value) || names.Count >= nameLimit)
            {
                return;
            }

            byte index = checked((byte)names.Count);
            names.Add(value);
            slots.Add(new NameSlot(index, language));
            if (isRouteNumber)
            {
                updatedTypes |= checked((ushort)(1u << index));
            }
        }
    }

    private static IEnumerable<OSMPronunciation> OrderPronunciations(
        IEnumerable<OSMPronunciation> values) =>
        values.OrderBy(value => AlphabetOrder(value.Alphabet))
            .ThenBy(value => (byte)value.Language)
            .ThenBy(value => value.Text, StringComparer.Ordinal);

    private static int AlphabetOrder(PronunciationAlphabet alphabet) => alphabet switch
    {
        PronunciationAlphabet.Ipa => 0,
        PronunciationAlphabet.NtSampa => 1,
        PronunciationAlphabet.Katakana => 2,
        PronunciationAlphabet.Jeita => 3,
        _ => 4,
    };

    private static string CreateRecord(
        byte nameIndex,
        Language language,
        PronunciationAlphabet alphabet,
        string pronunciation)
    {
        byte[] pronunciationBytes = Encoding.UTF8.GetBytes(pronunciation);
        if (pronunciationBytes.Length > byte.MaxValue)
        {
            throw new InvalidOperationException("A Valhalla linguistic record cannot exceed 255 UTF-8 bytes.");
        }

        var header = new LinguisticTextHeader
        {
            Language = (byte)language,
            Length = checked((byte)pronunciationBytes.Length),
            PhoneticAlphabet = (byte)alphabet,
            NameIndex = nameIndex,
        };

        var result = new StringBuilder(LinguisticConstants.HeaderSize + pronunciationBytes.Length);
        foreach (byte value in header.ToStoredBytes())
        {
            result.Append((char)value);
        }

        foreach (byte value in pronunciationBytes)
        {
            result.Append((char)value);
        }

        return result.ToString();
    }

    private static List<string> SplitTokens(string value)
    {
        var result = new List<string>();
        if (!string.IsNullOrEmpty(value))
        {
            result.AddRange(value.Split(';'));
        }

        return result;
    }

    private static (uint Index, OSMLinguisticType Type, bool DiffNames) SelectDirectionalIndex(
        uint baseIndex,
        uint leftIndex,
        uint rightIndex,
        uint forwardIndex,
        uint backwardIndex,
        bool forward,
        OSMLinguisticType baseType,
        OSMLinguisticType leftType,
        OSMLinguisticType rightType,
        OSMLinguisticType forwardType,
        OSMLinguisticType backwardType)
    {
        if (rightIndex != 0 && forward)
        {
            return (rightIndex, rightType, true);
        }

        if (leftIndex != 0 && !forward)
        {
            return (leftIndex, leftType, true);
        }

        if (forwardIndex != 0 && forward)
        {
            return (forwardIndex, forwardType, true);
        }

        if (backwardIndex != 0 && !forward)
        {
            return (backwardIndex, backwardType, true);
        }

        return (baseIndex, baseType, false);
    }
}
