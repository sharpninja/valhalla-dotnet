// Faithful C# port of Valhalla baldr EdgeInfo (edgeinfo.h + src/baldr/edgeinfo.cc) @ 3.7.0.
// Source: valhalla/baldr/edgeinfo.h, src/baldr/edgeinfo.cc
//
// EdgeInfo holds the edge information not required by the shortest-path algorithm
// and that is common to both directions of an edge: the OSM way id, names, the
// encoded shape, optional elevation, and tagged values. It is parsed directly
// from a region of the on-disk tile blob.
//
// The fixed header (EdgeInfoInner) is bit-packed across three 32-bit little-endian
// words (12 bytes total):
//   Word 0:
//     bits  0..31 (32 bits) : wayid_              (low 32 bits of OSM way id)
//   Word 1:
//     bits  0..11 (12 bits) : mean_elevation_     (2 m precision)
//     bits 12..15 ( 4 bits) : bike_network_       (mask of bicycle network types)
//     bits 16..23 ( 8 bits) : speed_limit_        (kph)
//     bits 24..31 ( 8 bits) : extended_wayid0_    (next byte of the way id)
//   Word 2:
//     bits  0..3  ( 4 bits) : name_count_
//     bits  4..19 (16 bits) : encoded_shape_size_ (bytes)
//     bits 20..27 ( 8 bits) : extended_wayid1_    (next-next byte of the way id)
//     bits 28..29 ( 2 bits) : extended_wayid_size_
//     bit  30      ( 1 bit) : has_elevation_
//     bit  31      ( 1 bit) : spare0_
// Total EdgeInfoInner size: 12 bytes.
//
// After the header the record layout is:
//   [EdgeInfoInner (12 bytes)]
//   [name_count * NameInfo (4 bytes each)]
//   [encoded_shape (encoded_shape_size bytes)]
//   [optional extended_wayid2_ (1 byte) if extended_wayid_size_ > 0]
//   [optional extended_wayid3_ (1 byte) if extended_wayid_size_ > 1]
//   [encoded elevation bytes (consumed lazily by encoded_elevation())]
//
// PORT-NOTE: EdgeInfo::json() (rapidjson serialization) is intentionally NOT
// ported; JSON/rapidjson serialization is excluded from this slice. All non-JSON
// public accessors are ported faithfully.

using System.Collections.Generic;
using System.Text;

using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Edge information not required in the shortest path algorithm and common to both directions of an
/// edge. Faithful port of C++ <c>class EdgeInfo</c> from <c>valhalla/baldr/edgeinfo.h</c>.
/// </summary>
/// <remarks>
/// The fixed header (<see cref="EdgeInfoInner"/>) is bit-packed into 12 bytes read directly from a
/// tile blob; see the file header for the exact bit map. The remaining fields (names, shape,
/// extended way id, elevation) follow immediately after the header in the tile.
/// </remarks>
public sealed class EdgeInfo : IEquatable<EdgeInfo>
{
    /// <summary>Maximum number of names per edge. Mirrors C++ <c>kMaxNamesPerEdge</c>.</summary>
    public const int MaxNamesPerEdge = 15;

    /// <summary>Maximum encoded shape size in bytes. Mirrors C++ <c>kMaxEncodedShapeSize</c>.</summary>
    public const int MaxEncodedShapeSize = 65535;

    /// <summary>Maximum stored elevation (12 bits). Mirrors C++ <c>kMaxStoredElevation</c>.</summary>
    public const uint MaxStoredElevation = 4095;

    /// <summary>Elevation bin size in meters. Mirrors C++ <c>kElevationBinSize</c>.</summary>
    public const float ElevationBinSize = 2.0f;

    /// <summary>Minimum elevation in meters. Mirrors C++ <c>kMinElevation</c>.</summary>
    public const float MinElevation = -500.0f;

    /// <summary>Maximum elevation in meters. Mirrors C++ <c>kMaxElevation</c>.</summary>
    public const float MaxElevation = MinElevation + (ElevationBinSize * MaxStoredElevation);

    /// <summary>Number of bytes in the fixed <see cref="EdgeInfoInner"/> header.</summary>
    public const int EdgeInfoInnerSize = 12;

    /// <summary>Number of bytes in a <see cref="NameInfo"/> entry.</summary>
    public const int NameInfoSize = 4;

    // -- Fixed header (the C++ EdgeInfoInner) bit fields, decoded into the three words. --

    // Word 0
    private readonly uint _wayid;

    // Word 1
    private readonly uint _meanElevation;   // 12 bits
    private readonly uint _bikeNetwork;     // 4 bits
    private readonly uint _speedLimit;      // 8 bits
    private readonly uint _extendedWayid0;  // 8 bits

    // Word 2
    private readonly uint _nameCount;        // 4 bits
    private readonly uint _encodedShapeSize; // 16 bits
    private readonly uint _extendedWayid1;   // 8 bits
    private readonly uint _extendedWayidSize; // 2 bits
    private readonly bool _hasElevation;     // 1 bit

    // The optional last 2 bytes of a 64-bit way id.
    private readonly byte _extendedWayid2;
    private readonly byte _extendedWayid3;

    // Backing buffer plus offsets into it.
    private readonly byte[] _buffer;
    private readonly int _nameInfoOffset;
    private readonly int _encodedShapeOffset;
    private readonly int _encodedElevationOffset;

    // The names list (tile text list) plus its length.
    private readonly byte[] _namesList;
    private readonly int _namesListLength;

    // Lazily-decoded shape.
    private List<PointLL>? _shape;

    // Lazily-built tag cache.
    private List<KeyValuePair<TaggedValue, byte[]>>? _tagCache;
    private bool _tagCacheReady;

    /// <summary>
    /// Constructs an <see cref="EdgeInfo"/> from a tile region. Faithful port of the C++
    /// <c>EdgeInfo(char* ptr, const char* names_list, size_t names_list_length)</c>.
    /// </summary>
    /// <param name="buffer">Buffer containing the edge-info record.</param>
    /// <param name="offset">Offset to the start of the edge-info record within <paramref name="buffer"/>.</param>
    /// <param name="namesList">Buffer containing the start of the text/names list (may be null/empty).</param>
    /// <param name="namesListLength">Length (bytes) of the text/names list.</param>
    public EdgeInfo(byte[] buffer, int offset, byte[]? namesList, int namesListLength)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _namesList = namesList ?? Array.Empty<byte>();
        _namesListLength = namesListLength;

        int ptr = offset;

        // Read EdgeInfoInner (12 bytes, three little-endian 32-bit words).
        uint word0 = ReadUInt32(buffer, ptr);
        uint word1 = ReadUInt32(buffer, ptr + 4);
        uint word2 = ReadUInt32(buffer, ptr + 8);

        _wayid = word0;

        _meanElevation = word1 & 0xFFFu;          // 12 bits
        _bikeNetwork = (word1 >> 12) & 0xFu;       // 4 bits
        _speedLimit = (word1 >> 16) & 0xFFu;       // 8 bits
        _extendedWayid0 = (word1 >> 24) & 0xFFu;   // 8 bits

        _nameCount = word2 & 0xFu;                 // 4 bits
        _encodedShapeSize = (word2 >> 4) & 0xFFFFu; // 16 bits
        _extendedWayid1 = (word2 >> 20) & 0xFFu;   // 8 bits
        _extendedWayidSize = (word2 >> 28) & 0x3u; // 2 bits
        _hasElevation = ((word2 >> 30) & 0x1u) != 0u; // 1 bit
        // bit 31 = spare0_

        ptr += EdgeInfoInnerSize;

        // Name info list pointer.
        _nameInfoOffset = ptr;
        ptr += (int)_nameCount * NameInfoSize;

        // Encoded shape pointer.
        _encodedShapeOffset = ptr;
        ptr += (int)_encodedShapeSize;

        // Optional second half of the 64-bit way id.
        _extendedWayid2 = 0;
        _extendedWayid3 = 0;
        if (_extendedWayidSize > 0)
        {
            _extendedWayid2 = buffer[ptr];
            ptr += 1;
        }

        if (_extendedWayidSize > 1)
        {
            _extendedWayid3 = buffer[ptr];
            ptr += 1;
        }

        // Encoded elevation pointer.
        _encodedElevationOffset = ptr;
    }

    /// <summary>Gets the OSM way Id. Faithful port of C++ <c>wayid()</c>.</summary>
    public ulong WayId =>
        ((ulong)_extendedWayid3 << 56)
        | ((ulong)_extendedWayid2 << 48)
        | ((ulong)_extendedWayid1 << 40)
        | ((ulong)_extendedWayid0 << 32)
        | _wayid;

    /// <summary>Gets the mean elevation along the edge in meters relative to sea level.</summary>
    public float MeanElevation => MinElevation + (_meanElevation * ElevationBinSize);

    /// <summary>Gets the bike network mask for this directed edge.</summary>
    public uint BikeNetwork => _bikeNetwork;

    /// <summary>Gets the speed limit in KPH.</summary>
    public uint SpeedLimit => _speedLimit;

    /// <summary>Does this EdgeInfo have elevation data along the edge.</summary>
    public bool HasElevation => _hasElevation;

    /// <summary>Gets the number of names.</summary>
    public uint NameCount => _nameCount;

    /// <summary>Gets the size (number of bytes) of the encoded shape.</summary>
    public uint EncodedShapeSize => _encodedShapeSize;

    /// <summary>How many extra bytes the way id is stored in (0, 1 or 2).</summary>
    public uint ExtendedWayIdSize => _extendedWayidSize;

    /// <summary>
    /// Calculate the size of a tagged value in bytes (including the tag byte and null terminator).
    /// Faithful port of C++ <c>EdgeInfo::TaggedValueSize</c>.
    /// </summary>
    /// <param name="buffer">Buffer containing the tagged value.</param>
    /// <param name="ptr">Offset to the start of the tagged value (including the tag byte).</param>
    /// <returns>The size of the tagged value in bytes.</returns>
    public static int TaggedValueSize(byte[] buffer, int ptr)
    {
        var tv = (TaggedValue)buffer[ptr];
        switch (tv)
        {
            case TaggedValue.Layer:
            case TaggedValue.BssInfo:
            case TaggedValue.Level:
            case TaggedValue.LevelRef:
            case TaggedValue.Tunnel:
            case TaggedValue.Bridge:
                // Null-terminated strings after the tag byte: strlen(ptr) + 1.
                return StrLen(buffer, ptr) + 1;

            case TaggedValue.Landmark:
                {
                    // Fixed 9-byte header + null-terminated name + null terminator.
                    // C++: std::string landmark_name = ptr + 10; -> name starts at ptr + 10.
                    int nameLen = StrLen(buffer, ptr + 10);
                    return nameLen + 10 + 1;
                }

            case TaggedValue.Levels:
            case TaggedValue.OSMNodeIds:
                {
                    // Tag byte + varint size + data + null terminator.
                    int start = ptr + 1;
                    int size = ParseVarint(buffer, ref start);
                    return (start + size) - ptr + 1;
                }

            case TaggedValue.ConditionalSpeedLimits:
                // Tag byte + fixed-size struct + null terminator.
                return 1 + ConditionalSpeedLimitSize + 1;

            case TaggedValue.Linguistic:
                {
                    int current = ptr + 1; // Skip the tag byte.
                    while (buffer[current] != 0)
                    {
                        var header = ReadLinguisticHeader(buffer, current);
                        current += header.Length + LinguisticConstants.HeaderSize;
                    }

                    return (current - ptr) + 1;
                }

            default:
                throw new InvalidOperationException(
                    "Unknown tag type: " + ((int)(sbyte)buffer[ptr]));
        }
    }

    /// <summary>sizeof(ConditionalSpeedLimit) in C++ (8 bytes).</summary>
    private const int ConditionalSpeedLimitSize = 8;

    /// <summary>
    /// Get the name info for the specified name index. Faithful port of C++ <c>GetNameInfo</c>.
    /// </summary>
    public NameInfo GetNameInfo(byte index)
    {
        if (index < _nameCount)
        {
            return ReadNameInfo(index);
        }

        throw new InvalidOperationException("StreetNameOffset index was out of bounds");
    }

    /// <summary>Convenience method to get the (untagged) names for an edge. Faithful port of C++ <c>GetNames()</c>.</summary>
    public List<string> GetNames()
    {
        var names = new List<string>((int)_nameCount);
        for (uint i = 0; i < _nameCount; i++)
        {
            NameInfo ni = ReadNameInfo((int)i);
            if (ni.Tagged)
            {
                continue;
            }

            if (ni.NameOffset < _namesListLength)
            {
                names.Add(ReadCString(_namesList, (int)ni.NameOffset));
            }
            else
            {
                throw new InvalidOperationException("GetNames: offset exceeds size of text list");
            }
        }

        return names;
    }

    /// <summary>
    /// Convenience method to get the names and route-number flags for an edge, optionally including
    /// tagged (non-linguistic) values. Faithful port of C++ <c>GetNames(bool include_tagged_values)</c>.
    /// </summary>
    public List<(string Name, bool IsRouteNum)> GetNames(bool includeTaggedValues)
    {
        var pairs = new List<(string, bool)>((int)_nameCount);
        for (uint i = 0; i < _nameCount; i++)
        {
            NameInfo ni = ReadNameInfo((int)i);

            // Skip any tagged names unless requested.
            if (ni.Tagged && !includeTaggedValues)
            {
                continue;
            }

            if (ni.Tagged)
            {
                if (ni.NameOffset < _namesListLength)
                {
                    int nameStart = (int)ni.NameOffset;
                    if (IsNonLinguisticTagValue(_namesList[nameStart]))
                    {
                        pairs.Add((ReadCString(_namesList, nameStart + 1), false));
                    }
                }
                else
                {
                    throw new InvalidOperationException("GetNames: offset exceeds size of text list");
                }
            }
            else if (ni.NameOffset < _namesListLength)
            {
                pairs.Add((ReadCString(_namesList, (int)ni.NameOffset), ni.IsRouteNum));
            }
            else
            {
                throw new InvalidOperationException("GetNames: offset exceeds size of text list");
            }
        }

        return pairs;
    }

    /// <summary>
    /// Convenience method to get the non-linguistic, tagged values for an edge. Faithful port of
    /// C++ <c>GetTaggedValues()</c>. Each returned string excludes the leading tag byte's null but
    /// includes the tag byte itself (matching <c>get_tagged_value</c> which returns the whole
    /// tagged slice minus the null terminator).
    /// </summary>
    public List<string> GetTaggedValues()
    {
        var taggedValues = new List<string>((int)_nameCount);
        for (uint i = 0; i < _nameCount; i++)
        {
            NameInfo ni = ReadNameInfo((int)i);
            if (!ni.Tagged)
            {
                continue;
            }

            if (ni.NameOffset < _namesListLength)
            {
                int value = (int)ni.NameOffset;
                byte[]? contents = GetTaggedValueSlice(_namesList, value);
                if (contents != null)
                {
                    taggedValues.Add(RawToString(contents));
                }
            }
            else
            {
                throw new InvalidOperationException("GetTaggedNames: offset exceeds size of text list");
            }
        }

        return taggedValues;
    }

    /// <summary>
    /// Convenience method to get the linguistic, tagged names for an edge. Faithful port of C++
    /// <c>GetLinguisticTaggedValues()</c>. Each returned string is the 3 stored header bytes
    /// followed by the pronunciation text.
    /// </summary>
    public List<string> GetLinguisticTaggedValues()
    {
        var names = new List<string>((int)_nameCount);
        for (uint i = 0; i < _nameCount; i++)
        {
            NameInfo ni = ReadNameInfo((int)i);
            if (!ni.Tagged)
            {
                continue;
            }

            if (ni.NameOffset < _namesListLength)
            {
                int name = (int)ni.NameOffset;
                var tv = (TaggedValue)_namesList[name];
                if (tv == TaggedValue.Linguistic)
                {
                    name += 1;
                    while (_namesList[name] != 0)
                    {
                        var header = ReadLinguisticHeader(_namesList, name);
                        var sb = new StringBuilder();
                        foreach (byte b in header.ToStoredBytes())
                        {
                            sb.Append((char)b);
                        }

                        for (int k = 0; k < header.Length; k++)
                        {
                            sb.Append((char)_namesList[name + LinguisticConstants.HeaderSize + k]);
                        }

                        names.Add(sb.ToString());
                        name += header.Length + LinguisticConstants.HeaderSize;
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("GetTaggedNames: offset exceeds size of text list");
            }
        }

        return names;
    }

    /// <summary>
    /// Convenience method to get the names, route-number flags and tag value type for an edge.
    /// Faithful port of C++ <c>GetNamesAndTypes(bool include_tagged_names)</c>.
    /// </summary>
    public List<(string Name, bool IsRouteNum, byte Type)> GetNamesAndTypes(bool includeTaggedValues = false)
    {
        var pairs = new List<(string, bool, byte)>((int)_nameCount);
        for (uint i = 0; i < _nameCount; i++)
        {
            NameInfo ni = ReadNameInfo((int)i);

            if (ni.Tagged && !includeTaggedValues)
            {
                continue;
            }

            if (ni.Tagged)
            {
                if (ni.NameOffset < _namesListLength)
                {
                    int name = (int)ni.NameOffset;
                    byte tag = _namesList[name];
                    if (IsNonLinguisticTagValue(tag))
                    {
                        pairs.Add((ReadCString(_namesList, name + 1), false, tag));
                    }
                }
                else
                {
                    throw new InvalidOperationException("GetNamesAndTypes: offset exceeds size of text list");
                }
            }
            else if (ni.NameOffset < _namesListLength)
            {
                pairs.Add((ReadCString(_namesList, (int)ni.NameOffset), ni.IsRouteNum, 0));
            }
            else
            {
                throw new InvalidOperationException("GetNamesAndTypes: offset exceeds size of text list");
            }
        }

        return pairs;
    }

    /// <summary>
    /// Convenience method to get the tags of the edge as a multimap (tag -> raw value, with the
    /// leading tag byte removed). Linguistic tagged values are NOT included. Faithful port of C++
    /// <c>GetTags()</c>.
    /// </summary>
    public IReadOnlyList<KeyValuePair<TaggedValue, byte[]>> GetTags()
    {
        if (!_tagCacheReady)
        {
            _tagCache ??= new List<KeyValuePair<TaggedValue, byte[]>>();
            for (uint i = 0; i < _nameCount; i++)
            {
                NameInfo ni = ReadNameInfo((int)i);
                if (!ni.Tagged)
                {
                    continue;
                }

                if (ni.NameOffset < _namesListLength)
                {
                    int value = (int)ni.NameOffset;
                    byte[]? contents = GetTaggedValueSlice(_namesList, value);
                    if (contents != null)
                    {
                        var tv = (TaggedValue)_namesList[value];
                        // remove the leading TaggedValue byte from the content
                        var stripped = new byte[contents.Length - 1];
                        Array.Copy(contents, 1, stripped, 0, stripped.Length);
                        _tagCache.Add(new KeyValuePair<TaggedValue, byte[]>(tv, stripped));
                    }
                }
                else
                {
                    throw new InvalidOperationException("GetTags: offset exceeds size of text list");
                }
            }

            if (_tagCache.Count > 0)
            {
                _tagCacheReady = true;
            }
        }

        return _tagCache ?? (IReadOnlyList<KeyValuePair<TaggedValue, byte[]>>)Array.Empty<KeyValuePair<TaggedValue, byte[]>>();
    }

    /// <summary>
    /// Convenience method to get a linguistic map for an edge: name index -> (phonetic alphabet,
    /// language, pronunciation). Faithful port of C++ <c>GetLinguisticMap()</c>.
    /// </summary>
    public Dictionary<byte, (byte Language, byte PhoneticAlphabet, string Pronunciation)> GetLinguisticMap()
    {
        var map = new Dictionary<byte, (byte Language, byte PhoneticAlphabet, string Pronunciation)>((int)_nameCount);
        for (uint i = 0; i < _nameCount; i++)
        {
            NameInfo ni = ReadNameInfo((int)i);
            if (!ni.Tagged)
            {
                continue;
            }

            if (ni.NameOffset < _namesListLength)
            {
                int name = (int)ni.NameOffset;
                var tv = (TaggedValue)_namesList[name];
                if (tv == TaggedValue.Linguistic)
                {
                    name += 1;
                    while (_namesList[name] != 0)
                    {
                        var header = ReadLinguisticHeader(_namesList, name);

                        byte phoneticAlphabet = header.PhoneticAlphabet;
                        byte language = header.Language;

                        string pronunciation = Encoding.UTF8.GetString(
                            _namesList,
                            name + LinguisticConstants.HeaderSize,
                            header.Length);

                        name += header.Length + LinguisticConstants.HeaderSize;
                        byte nameIndex = header.NameIndex;

                        (byte Language, byte PhoneticAlphabet, string Pronunciation) attributes =
                            (language, phoneticAlphabet, pronunciation);

                        if (!map.TryGetValue(nameIndex, out var existing))
                        {
                            map[nameIndex] = attributes;
                        }
                        else if (attributes.PhoneticAlphabet > existing.PhoneticAlphabet
                                 && attributes.PhoneticAlphabet != (byte)PronunciationAlphabet.None
                                 && attributes.Language == existing.Language)
                        {
                            // Keep the highest ranking phoneme type over the language.
                            map[nameIndex] = attributes;
                        }
                    }
                }
            }
            else
            {
                throw new InvalidOperationException("GetLinguisticMap: offset exceeds size of text list");
            }
        }

        return map;
    }

    /// <summary>
    /// Convenience method to get the types for the names: if a bit is set, that name is a route
    /// number. Faithful port of C++ <c>GetTypes()</c>.
    /// </summary>
    public ushort GetTypes()
    {
        ushort types = 0;
        for (uint i = 0; i < _nameCount; i++)
        {
            NameInfo info = GetNameInfo((byte)i);
            types |= (ushort)((info.IsRouteNum ? 1 : 0) << (int)i);
        }

        return types;
    }

    /// <summary>
    /// Get the shape of the edge as a list of lat,lng points. Decoded lazily and cached. Faithful
    /// port of C++ <c>shape()</c>.
    /// </summary>
    public IReadOnlyList<PointLL> Shape()
    {
        if (_encodedShapeSize > 0 && (_shape == null || _shape.Count == 0))
        {
            _shape = Encoded.Decode7(EncodedShape());
        }

        return _shape ??= new List<PointLL>();
    }

    /// <summary>Returns the encoded shape string. Faithful port of C++ <c>encoded_shape()</c>.</summary>
    public string EncodedShape()
    {
        var sb = new StringBuilder((int)_encodedShapeSize);
        for (int k = 0; k < _encodedShapeSize; k++)
        {
            sb.Append((char)_buffer[_encodedShapeOffset + k]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns the encoded elevation along the edge as a list of signed bytes, and the sampling
    /// interval via <paramref name="interval"/>. Faithful port of C++ <c>encoded_elevation()</c>.
    /// </summary>
    public List<sbyte> EncodedElevation(uint length, out double interval)
    {
        if (!HasElevation)
        {
            // If no elevation then the edge length is shorter than the sampling interval; set the
            // interval to the edge length. The elevation at the nodes will be used in this case.
            interval = length;
            return new List<sbyte>();
        }

        interval = ElevationEncoding.SamplingInterval(length);

        uint n = ElevationEncoding.EncodedElevationCount(length);
        var result = new List<sbyte>((int)n);
        for (int k = 0; k < n; k++)
        {
            result.Add((sbyte)_buffer[_encodedElevationOffset + k]);
        }

        return result;
    }

    /// <summary>Returns the list of conditional speed limits for the edge. Faithful port of C++ <c>conditional_speed_limits()</c>.</summary>
    public List<ConditionalSpeedLimit> ConditionalSpeedLimits()
    {
        var limits = new List<ConditionalSpeedLimit>();
        foreach (KeyValuePair<TaggedValue, byte[]> tag in GetTags())
        {
            if (tag.Key != TaggedValue.ConditionalSpeedLimits)
            {
                continue;
            }

            limits.Add(new ConditionalSpeedLimit(ReadUInt64(tag.Value, 0)));
        }

        return limits;
    }

    /// <summary>Get the layer index (Z-level) of the edge. Faithful port of C++ <c>layer()</c>.</summary>
    public sbyte Layer()
    {
        foreach (KeyValuePair<TaggedValue, byte[]> tag in GetTags())
        {
            if (tag.Key != TaggedValue.Layer)
            {
                continue;
            }

            if (tag.Value.Length != 1)
            {
                throw new InvalidOperationException("layer must contain 1-byte value");
            }

            return (sbyte)tag.Value[0];
        }

        return 0;
    }

    /// <summary>
    /// Get levels of the edge as contiguous (inclusive) ranges plus the max precision. Faithful
    /// port of C++ <c>levels()</c>.
    /// </summary>
    public (List<(float First, float Second)> Ranges, uint Precision) Levels()
    {
        foreach (KeyValuePair<TaggedValue, byte[]> tag in GetTags())
        {
            if (tag.Key != TaggedValue.Levels)
            {
                continue;
            }

            try
            {
                return DecodeLevels(tag.Value);
            }
            catch
            {
                throw new InvalidOperationException("failed to decode levels");
            }
        }

        return (new List<(float, float)>(), 0u);
    }

    /// <summary>Checks whether the edge connects the passed level. Faithful port of C++ <c>includes_level()</c>.</summary>
    public bool IncludesLevel(float lvl)
    {
        foreach (KeyValuePair<TaggedValue, byte[]> tag in GetTags())
        {
            if (tag.Key != TaggedValue.Levels)
            {
                continue;
            }

            List<(float First, float Second)> decoded = DecodeLevels(tag.Value).Ranges;

            // std::lower_bound on the .second field, then check the matched range contains lvl.
            int lo = 0;
            int hi = decoded.Count;
            while (lo < hi)
            {
                int mid = lo + ((hi - lo) >> 1);
                if (decoded[mid].Second < lvl)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            return lo < decoded.Count && decoded[lo].First <= lvl && lvl <= decoded[lo].Second;
        }

        return false;
    }

    /// <summary>Get the level:ref values of the edge. Faithful port of C++ <c>level_ref()</c>.</summary>
    public List<string> LevelRef()
    {
        var values = new List<string>();
        foreach (KeyValuePair<TaggedValue, byte[]> tag in GetTags())
        {
            if (tag.Key == TaggedValue.LevelRef)
            {
                values.Add(RawToString(tag.Value));
            }
        }

        return values;
    }

    /// <summary>Get the OSM node ids along this edge, if any. Faithful port of C++ <c>osm_node_ids()</c>.</summary>
    public List<ulong> OsmNodeIds()
    {
        foreach (KeyValuePair<TaggedValue, byte[]> tag in GetTags())
        {
            if (tag.Key != TaggedValue.OSMNodeIds)
            {
                continue;
            }

            try
            {
                byte[] data = tag.Value;
                int p = 0;
                // skip the size prefix
                ParseVarint(data, ref p);
                int length = data.Length - p;
                var sub = new byte[length];
                Array.Copy(data, p, sub, 0, length);
                return Encoded.Decode7Int<ulong>(RawToString(sub));
            }
            catch
            {
                throw new InvalidOperationException("failed to decode osm node ids");
            }
        }

        return new List<ulong>();
    }

    /// <summary>Operator EqualTo based on the way id (matching the routing-relevant identity of an edge info).</summary>
    public bool Equals(EdgeInfo? other) => other != null && WayId == other.WayId
        && _encodedShapeSize == other._encodedShapeSize && _nameCount == other._nameCount;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as EdgeInfo);

    /// <inheritdoc/>
    public override int GetHashCode() => WayId.GetHashCode();

    // ------------------------------------------------------------------
    // decode_levels (free function in edgeinfo.cc)
    // ------------------------------------------------------------------

    /// <summary>
    /// Decode the level information encoded as variable-length, variable-precision numbers.
    /// Faithful port of the free function C++ <c>decode_levels</c>.
    /// </summary>
    /// <param name="encoded">The encoded varint byte array.</param>
    /// <returns>A tuple of the decoded level ranges and the precision used.</returns>
    public static (List<(float First, float Second)> Ranges, uint Precision) DecodeLevels(byte[] encoded)
    {
        uint precision = 0;
        var decoded = new List<(float, float)>(4);

        int ptr = 0;

        // first varint is the size
        int size = ParseVarint(encoded, ref ptr);
        // we keep track of where the string ends
        int end = ptr + size;
        // second varint is the precision
        // C++: if (int32_t prec_power = parse_varint(ptr) > 0) precision = pow(10, prec_power);
        // NOTE: due to C++ operator precedence, prec_power is assigned the BOOLEAN (parse_varint > 0),
        // so precision = pow(10, 1) = 10 whenever the precision varint is positive, else stays 0.
        int precPower = ParseVarint(encoded, ref ptr) > 0 ? 1 : 0;
        if (precPower > 0)
        {
            precision = (uint)System.Math.Pow(10, precPower);
        }

        bool prev = false;

        while (ptr != end)
        {
            int val = ParseVarint(encoded, ref ptr);
            if (val == (int)GraphConstants.LevelRangeSeparator)
            {
                prev = false;
                continue;
            }

            float f = precision == 0 ? val : (float)val / precision;
            if (!prev)
            {
                decoded.Add((f, f));
                prev = true;
            }
            else
            {
                (float first, float _) = decoded[decoded.Count - 1];
                decoded[decoded.Count - 1] = (first, f);
            }
        }

        return (decoded, precision);
    }

    // ------------------------------------------------------------------
    // Private helpers
    // ------------------------------------------------------------------

    private NameInfo ReadNameInfo(int index)
        => new NameInfo(ReadUInt32(_buffer, _nameInfoOffset + (index * NameInfoSize)));

    // should return true if not equal to TaggedValue::kLinguistic
    private static bool IsNonLinguisticTagValue(byte ch) => (TaggedValue)ch != TaggedValue.Linguistic;

    // per-tag slice. The returned bytes include the leading TaggedValue byte. Returns null for
    // linguistic tags (handled separately) and for zero-size results. Faithful port of the
    // anonymous-namespace get_tagged_value().
    private static byte[]? GetTaggedValueSlice(byte[] buffer, int ptr)
    {
        var tv = (TaggedValue)buffer[ptr];
        if (tv == TaggedValue.Linguistic)
        {
            return null;
        }

        int size = TaggedValueSize(buffer, ptr);
        if (size > 0)
        {
            // -1 to exclude the null terminator
            var slice = new byte[size - 1];
            Array.Copy(buffer, ptr, slice, 0, size - 1);
            return slice;
        }

        return null;
    }

    // Parse a 7-bit encoded zig-zag varint, advancing the offset. Faithful port of the
    // anonymous-namespace parse_varint() in edgeinfo.cc.
    private static int ParseVarint(byte[] encoded, ref int pos)
    {
        int b = 0;
        int shift = 0;
        int result = 0;

        while ((b & 0x80) != 0 || shift == 0)
        {
            // C++: byte = int32_t(*encoded); where *encoded is a (signed) char.
            b = (sbyte)encoded[pos];
            result |= (b & 0x7f) << shift;
            shift += 7;
            ++pos;
        }

        return ((result & 1) != 0 ? ~result : result) >> 1;
    }

    private static LinguisticTextHeader ReadLinguisticHeader(byte[] buffer, int offset)
        => new LinguisticTextHeader(ReadUInt32(buffer, offset));

    // strlen over a byte buffer (counts bytes up to but excluding the null terminator).
    private static int StrLen(byte[] buffer, int offset)
    {
        int i = offset;
        while (buffer[i] != 0)
        {
            i++;
        }

        return i - offset;
    }

    // Read a null-terminated C string as a .NET string (raw byte -> char, matching the byte
    // fidelity used everywhere else in this port).
    private static string ReadCString(byte[] buffer, int offset)
    {
        // Valhalla stores names as raw UTF-8 bytes in the text list (std::string). Decode as UTF-8,
        // not byte-per-char, or accented names (e.g. Monaco French streets) become mojibake.
        int len = StrLen(buffer, offset);
        return System.Text.Encoding.UTF8.GetString(buffer, offset, len);
    }

    // Convert a raw byte slice to a string (byte -> char), preserving exact byte values.
    private static string RawToString(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (byte b in bytes)
        {
            sb.Append((char)b);
        }

        return sb.ToString();
    }

    private static uint ReadUInt32(byte[] buffer, int offset)
        => (uint)(buffer[offset]
                  | (buffer[offset + 1] << 8)
                  | (buffer[offset + 2] << 16)
                  | (buffer[offset + 3] << 24));

    private static ulong ReadUInt64(byte[] buffer, int offset)
        => ReadUInt32(buffer, offset) | ((ulong)ReadUInt32(buffer, offset + 4) << 32);
}
