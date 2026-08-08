using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Routing-name category carried by an OSM linguistic record. Numeric values match
/// Valhalla 3.8.3 <c>OSMLinguistic::Type</c>.
/// </summary>
public enum OSMLinguisticType : byte
{
    Name = 0,
    NameLeft = 1,
    NameRight = 2,
    NameForward = 3,
    NameBackward = 4,
    NodeName = 5,
    AltName = 6,
    AltNameLeft = 7,
    AltNameRight = 8,
    OfficialName = 9,
    OfficialNameLeft = 10,
    OfficialNameRight = 11,
    TunnelName = 12,
    TunnelNameLeft = 13,
    TunnelNameRight = 14,
    Ref = 15,
    RefLeft = 16,
    RefRight = 17,
    NodeRef = 18,
    IntRef = 19,
    IntRefLeft = 20,
    IntRefRight = 21,
    Destination = 22,
    DestinationForward = 23,
    DestinationBackward = 24,
    DestinationRef = 25,
    DestinationRefTo = 26,
    DestinationStreet = 27,
    DestinationStreetTo = 28,
    JunctionRef = 29,
    JunctionName = 30,
}

/// <summary>A language-qualified OSM name that participates in EdgeInfo name ordering.</summary>
public readonly record struct OSMLinguisticName(
    OSMLinguisticType Type,
    Language Language,
    string Text);

/// <summary>A language-qualified OSM pronunciation associated with an EdgeInfo name.</summary>
public readonly record struct OSMPronunciation(
    OSMLinguisticType Type,
    Language Language,
    PronunciationAlphabet Alphabet,
    string Text);
