// Faithful C# port of Valhalla baldr NameInfo + linguistic_text_header_t (edgeinfo.h) @ 3.7.0.
// Source: valhalla/baldr/edgeinfo.h
//
// These structs are bit-packed and read directly from the on-disk tile blob, so
// the exact bit layout and struct size are reproduced.
//
// EXACT BIT LAYOUT of NameInfo (LSB first, packed into a uint32, little-endian):
//   bits  0..23 (24 bits) : name_offset_       (offset to start of text string)
//   bits 24..27 ( 4 bits) : additional_fields_ (count of following text fields)
//   bit  28      ( 1 bit) : is_route_num_
//   bit  29      ( 1 bit) : tagged_
//   bits 30..31 ( 2 bits) : spare_
// Total NameInfo size: 4 bytes.
//
// EXACT BIT LAYOUT of linguistic_text_header_t (LSB first, packed into a uint32):
//   bits  0..7   ( 8 bits) : language_
//   bits  8..15  ( 8 bits) : length_            (pronunciation length)
//   bits 16..18  ( 3 bits) : phonetic_alphabet_
//   bits 19..22  ( 4 bits) : name_index_
//   bit  23      ( 1 bit) : spare_
//   bits 24..31  ( 8 bits) : DO_NOT_USE_        (NOT stored on disk)
// In-memory struct size: 4 bytes. ONLY THE FIRST 3 BYTES (kLinguisticHeaderSize)
// are persisted in the tile text list; DO_NOT_USE_ is never written.

using System.Runtime.InteropServices;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Name information. Describes a single name added to the names list within a tile: an offset to
/// the text string plus optional flags. Faithful port of C++ <c>struct NameInfo</c>.
/// </summary>
/// <remarks>
/// Tile-layout fidelity: bit-packed into a single 4-byte little-endian word read directly from the
/// on-disk tile blob. See file header for the bit map.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct NameInfo : IEquatable<NameInfo>
{
    private const uint NameOffsetMask = 0x00FFFFFF; // 24 bits
    private const int AdditionalFieldsShift = 24;
    private const uint AdditionalFieldsMask = 0xF; // 4 bits
    private const int IsRouteNumShift = 28;
    private const int TaggedShift = 29;
    private const int SpareShift = 30;
    private const uint SpareMask = 0x3; // 2 bits

    // The single packed 32-bit word.
    private uint _word;

    /// <summary>Constructs a <see cref="NameInfo"/> from a raw packed 32-bit word (as read from a tile).</summary>
    public NameInfo(uint word)
    {
        _word = word;
    }

    /// <summary>
    /// Constructs a <see cref="NameInfo"/> from its component fields. Field order mirrors the C++
    /// aggregate-initialization order used by tests: {name_offset, additional_fields, is_route_num,
    /// tagged, spare}.
    /// </summary>
    public NameInfo(uint nameOffset, uint additionalFields, bool isRouteNum, bool tagged, uint spare)
    {
        _word = (nameOffset & NameOffsetMask)
                | ((additionalFields & AdditionalFieldsMask) << AdditionalFieldsShift)
                | ((isRouteNum ? 1u : 0u) << IsRouteNumShift)
                | ((tagged ? 1u : 0u) << TaggedShift)
                | ((spare & SpareMask) << SpareShift);
    }

    /// <summary>Raw packed 32-bit word.</summary>
    public readonly uint Word => _word;

    /// <summary>Offset to start of text string (24-bit field).</summary>
    public uint NameOffset
    {
        readonly get => _word & NameOffsetMask;
        set => _word = (_word & ~NameOffsetMask) | (value & NameOffsetMask);
    }

    /// <summary>
    /// Additional text fields following the name (4-bit field). These can be used for additional
    /// information like language, phonetic string, etc.
    /// </summary>
    public readonly uint AdditionalFields => (_word >> AdditionalFieldsShift) & AdditionalFieldsMask;

    /// <summary>Flag used to indicate if this is a route number vs just a name.</summary>
    public readonly bool IsRouteNum => ((_word >> IsRouteNumShift) & 1u) != 0u;

    /// <summary>
    /// Indicates the text string is specially tagged (e.g. uses the first char as the tag type).
    /// Tagged text is not returned by GetNames / GetNamesAndTags until code is ready to use it.
    /// </summary>
    public readonly bool Tagged => ((_word >> TaggedShift) & 1u) != 0u;

    /// <summary>Spare 2-bit field.</summary>
    public readonly uint Spare => (_word >> SpareShift) & SpareMask;

    /// <summary>Operator equality (compares only the name offset, matching C++ <c>operator==</c>).</summary>
    public readonly bool Equals(NameInfo other) => NameOffset == other.NameOffset;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is NameInfo ni && Equals(ni);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => NameOffset.GetHashCode();

    /// <summary>Operator equality (compares name offset). Mirrors C++ <c>operator==</c>.</summary>
    public static bool operator ==(NameInfo a, NameInfo b) => a.NameOffset == b.NameOffset;

    /// <summary>Operator inequality.</summary>
    public static bool operator !=(NameInfo a, NameInfo b) => a.NameOffset != b.NameOffset;

    /// <summary>operator&lt; for sorting (compares name offset). Mirrors C++ <c>operator&lt;</c>.</summary>
    public readonly int CompareTo(NameInfo other) => NameOffset.CompareTo(other.NameOffset);

    /// <summary>Less-than comparison mirroring the C++ <c>operator&lt;</c>.</summary>
    public static bool operator <(NameInfo a, NameInfo b) => a.NameOffset < b.NameOffset;

    /// <summary>Greater-than comparison.</summary>
    public static bool operator >(NameInfo a, NameInfo b) => a.NameOffset > b.NameOffset;
}

/// <summary>
/// Header for a single linguistic (pronunciation / language) record. Faithful port of C++
/// <c>struct linguistic_text_header_t</c>.
/// </summary>
/// <remarks>
/// Unfortunately a bug was found where Valhalla returned a blank phoneme (kNone = 0) for a
/// linguistic record that contained a language and no phoneme; this caused header parsing to stop
/// and threw the name index off. This is why <see cref="PronunciationAlphabet.None"/> is now 5.
/// <para>
/// Tile-layout fidelity: in memory this is a 4-byte word, but ONLY the first 3 bytes
/// (<see cref="LinguisticConstants.HeaderSize"/>) are stored on disk; the <c>DO_NOT_USE_</c> byte
/// is never persisted. The reader still consumes a 4-byte word (matching the C++
/// <c>unaligned_read&lt;linguistic_text_header_t&gt;</c>) but advances by only 3 bytes plus the
/// pronunciation length.
/// </para>
/// </remarks>
public struct LinguisticTextHeader
{
    private const int LanguageShift = 0;
    private const int LengthShift = 8;
    private const int PhoneticAlphabetShift = 16;
    private const uint PhoneticAlphabetMask = 0x7; // 3 bits
    private const int NameIndexShift = 19;
    private const uint NameIndexMask = 0xF; // 4 bits
    private const int SpareShift = 23;

    private uint _word;

    /// <summary>Constructs a header from a raw packed 32-bit word (as read from a tile, only low 3 bytes meaningful).</summary>
    public LinguisticTextHeader(uint word)
    {
        _word = word;
    }

    /// <summary>Language (8-bit). Locale is derived later by getting admin info.</summary>
    public byte Language
    {
        readonly get => (byte)((_word >> LanguageShift) & 0xFF);
        set => _word = (_word & ~(0xFFu << LanguageShift)) | (((uint)value & 0xFF) << LanguageShift);
    }

    /// <summary>Pronunciation length in bytes (8-bit).</summary>
    public byte Length
    {
        readonly get => (byte)((_word >> LengthShift) & 0xFF);
        set => _word = (_word & ~(0xFFu << LengthShift)) | (((uint)value & 0xFF) << LengthShift);
    }

    /// <summary>Phonetic alphabet (3-bit).</summary>
    public byte PhoneticAlphabet
    {
        readonly get => (byte)((_word >> PhoneticAlphabetShift) & PhoneticAlphabetMask);
        set => _word = (_word & ~(PhoneticAlphabetMask << PhoneticAlphabetShift))
                       | (((uint)value & PhoneticAlphabetMask) << PhoneticAlphabetShift);
    }

    /// <summary>Which name this pronunciation is for (4-bit).</summary>
    public byte NameIndex
    {
        readonly get => (byte)((_word >> NameIndexShift) & NameIndexMask);
        set => _word = (_word & ~(NameIndexMask << NameIndexShift))
                       | (((uint)value & NameIndexMask) << NameIndexShift);
    }

    /// <summary>Spare 1-bit field.</summary>
    public readonly byte Spare => (byte)((_word >> SpareShift) & 0x1);

    /// <summary>
    /// Serializes the 3 persisted header bytes (language, length, and the packed
    /// phonetic/name_index/spare byte) in the on-disk little-endian order. The DO_NOT_USE_ byte is
    /// intentionally excluded.
    /// </summary>
    public readonly byte[] ToStoredBytes()
    {
        return new[]
        {
            (byte)(_word & 0xFF),
            (byte)((_word >> 8) & 0xFF),
            (byte)((_word >> 16) & 0xFF),
        };
    }
}

/// <summary>
/// Indexes and sizes for the linguistic-map value tuple. Mirrors the C++ constants in edgeinfo.h.
/// </summary>
public static class LinguisticConstants
{
    /// <summary>Index of the language member in the linguistic-map value tuple.</summary>
    public const int LanguageIndex = 0;

    /// <summary>Index of the phonetic-alphabet member in the linguistic-map value tuple.</summary>
    public const int PhoneticAlphabetIndex = 1;

    /// <summary>Index of the pronunciation string in the linguistic-map value tuple.</summary>
    public const int PronunciationIndex = 2;

    /// <summary>
    /// Number of header bytes stored on disk per linguistic record (language + length + packed byte).
    /// Mirrors C++ <c>kLinguisticHeaderSize = 3</c>.
    /// </summary>
    public const int HeaderSize = 3;
}
