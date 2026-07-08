// Faithful C# port of Valhalla mjolnir edgeinfobuilder.h + src/mjolnir/edgeinfobuilder.cc @ 3.7.0.
// Sources:
//   F:/github/valhalla/valhalla/mjolnir/edgeinfobuilder.h
//   F:/github/valhalla/src/mjolnir/edgeinfobuilder.cc
//
// WRITE side of baldr::EdgeInfo. Produces the EXACT byte layout the ported Baldr EdgeInfo reader
// parses (see EdgeInfo.cs):
//   [EdgeInfoInner (12 bytes, three LE 32-bit words)]
//   [name_count * NameInfo (4 bytes each)]
//   [encoded_shape bytes]
//   [optional extended_wayid2_ (1 byte) if extended_wayid_size_ > 0]
//   [optional extended_wayid3_ (1 byte) if extended_wayid_size_ > 1]
//   [encoded elevation bytes]
//   [padding to a 4-byte boundary]
//
// EdgeInfoInner bit map (matches EdgeInfo.cs):
//   Word 0: wayid_:32
//   Word 1: mean_elevation_:12 | bike_network_:4 | speed_limit_:8 | extended_wayid0_:8
//   Word 2: name_count_:4 | encoded_shape_size_:16 | extended_wayid1_:8 | extended_wayid_size_:2
//           | has_elevation_:1 | spare0_:1

using System;
using System.Collections.Generic;
using System.IO;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>
/// Builds the variable-size edge information record (way id, names, encoded shape, optional
/// elevation) that is common to both directions of an edge. Faithful port of the C++
/// <c>class EdgeInfoBuilder</c>. Writes a byte image identical to what
/// <see cref="EdgeInfo"/> reads back.
/// </summary>
public sealed class EdgeInfoBuilder
{
    /// <summary>Maximum names per edge. Mirrors C++ <c>kMaxNamesPerEdge</c>.</summary>
    public const int MaxNamesPerEdge = EdgeInfo.MaxNamesPerEdge;

    /// <summary>Maximum encoded shape size in bytes. Mirrors C++ <c>kMaxEncodedShapeSize</c>.</summary>
    public const int MaxEncodedShapeSize = EdgeInfo.MaxEncodedShapeSize;

    /// <summary>Size in bytes of the fixed <c>EdgeInfoInner</c> header.</summary>
    public const int EdgeInfoInnerSize = EdgeInfo.EdgeInfoInnerSize;

    // ---- The decoded EdgeInfoInner fields (packed at serialization time). ----
    private uint _wayid;            // low 32 bits of OSM way id
    private uint _meanElevation;    // 12 bits
    private uint _bikeNetwork;      // 4 bits
    private uint _speedLimit;       // 8 bits
    private uint _extendedWayid0;   // 8 bits
    private uint _extendedWayid1;   // 8 bits
    private uint _extendedWayidSize; // 2 bits
    private bool _hasElevation;

    // The optional last two bytes of a 64-bit way id.
    private byte _extendedWayid2;
    private byte _extendedWayid3;

    // List of name info (offsets, etc.).
    private readonly List<NameInfo> _nameInfoList = new();

    // Encoded lat/lng shape of the edge.
    private string _encodedShape = string.Empty;

    // Encoded elevation.
    private sbyte[] _encodedElevation = Array.Empty<sbyte>();

    /// <summary>Sets the OSM way id. Faithful port of <c>set_wayid</c>.</summary>
    public void SetWayId(ulong wayid)
    {
        // Mask off the various parts of the id into their respective spots.
        _wayid = (uint)(wayid & 0xFFFFFFFF);
        _extendedWayid0 = (uint)((wayid >> 32) & 0xFF);
        _extendedWayid1 = (uint)((wayid >> 40) & 0xFF);
        _extendedWayid2 = (byte)((wayid >> 48) & 0xFF);
        _extendedWayid3 = (byte)((wayid >> 56) & 0xFF);
        _extendedWayidSize = _extendedWayid3 > 0 ? 2u : (_extendedWayid2 > 0 ? 1u : 0u);
    }

    /// <summary>Sets the mean elevation in meters. Faithful port of <c>set_mean_elevation</c>.</summary>
    public void SetMeanElevation(float meanElev)
    {
        if (meanElev <= EdgeInfo.MinElevation)
        {
            _meanElevation = 0;
        }
        else
        {
            uint elev = (uint)((meanElev - EdgeInfo.MinElevation) / EdgeInfo.ElevationBinSize);
            _meanElevation = elev > EdgeInfo.MaxStoredElevation ? EdgeInfo.MaxStoredElevation : elev;
        }
    }

    /// <summary>Sets the bike network mask. Faithful port of <c>set_bike_network</c>.</summary>
    public void SetBikeNetwork(uint bikeNetwork) => _bikeNetwork = bikeNetwork;

    /// <summary>Sets the speed limit in KPH. Faithful port of <c>set_speed_limit</c>.</summary>
    public void SetSpeedLimit(uint speedLimit)
    {
        if (speedLimit == GraphConstants.UnlimitedSpeedLimit)
        {
            _speedLimit = GraphConstants.UnlimitedSpeedLimit;
        }
        else if (speedLimit > GraphConstants.MaxAssumedSpeed)
        {
            _speedLimit = GraphConstants.MaxAssumedSpeed;
        }
        else
        {
            _speedLimit = speedLimit;
        }
    }

    /// <summary>Sets the elevation flag. Faithful port of <c>set_has_elevation</c>.</summary>
    public void SetHasElevation(bool elevation) => _hasElevation = elevation;

    /// <summary>Sets the list of name info. Faithful port of <c>set_name_info_list</c>.</summary>
    public void SetNameInfoList(IReadOnlyList<NameInfo> nameInfoList)
    {
        if (nameInfoList.Count > MaxNamesPerEdge)
        {
            // C++ logs and increments a build stat; it does NOT store the over-long list.
            return;
        }

        _nameInfoList.Clear();
        _nameInfoList.AddRange(nameInfoList);
    }

    /// <summary>Adds a name info to the list. Faithful port of <c>AddNameInfo</c>.</summary>
    public void AddNameInfo(NameInfo info)
    {
        if (_nameInfoList.Count == MaxNamesPerEdge)
        {
            // C++ logs and increments a build stat; it does NOT add past the max.
            return;
        }

        _nameInfoList.Add(info);
    }

    /// <summary>
    /// Sets the shape of the edge by encoding the list of lat,lng points. Faithful port of the
    /// templated <c>set_shape</c> (encode7).
    /// </summary>
    public void SetShape(IReadOnlyList<PointLL> shape) => _encodedShape = Encoded.Encode7(shape);

    /// <summary>Sets the encoded shape string directly. Faithful port of <c>set_encoded_shape</c>.</summary>
    public void SetEncodedShape(string encodedShape) => _encodedShape += encodedShape;

    /// <summary>Sets the encoded elevation vector. Faithful port of <c>set_encoded_elevation</c>.</summary>
    public void SetEncodedElevation(IReadOnlyList<sbyte> encodedElevation)
    {
        if (encodedElevation.Count > 0)
        {
            _encodedElevation = new sbyte[encodedElevation.Count];
            for (int i = 0; i < encodedElevation.Count; i++)
            {
                _encodedElevation[i] = encodedElevation[i];
            }
        }
    }

    /// <summary>Bike network mask getter (mirrors C++ <c>bike_network()</c>).</summary>
    public uint BikeNetwork => _bikeNetwork;

    /// <summary>Mean elevation getter (mirrors C++ <c>mean_elevation()</c>).</summary>
    public float MeanElevation => EdgeInfo.MinElevation + (_meanElevation * EdgeInfo.ElevationBinSize);

    /// <summary>Returns true if this edge info has the given name offset. Faithful port of <c>has_name_info</c>.</summary>
    public bool HasNameInfo(uint offset)
    {
        foreach (NameInfo name in _nameInfoList)
        {
            if (name.NameOffset == offset)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Get the size of this edge info (without padding). Faithful port of <c>BaseSizeOf</c>.
    /// </summary>
    public int BaseSizeOf()
    {
        int size = EdgeInfoInnerSize;
        size += _nameInfoList.Count * EdgeInfo.NameInfoSize;
        size += _encodedShape.Length; // 1 byte per char (raw bytes)
        size += (int)_extendedWayidSize;
        size += _encodedElevation.Length;
        return size;
    }

    /// <summary>
    /// Get the size of this edge info including padding to align to a 4-byte boundary. Faithful port
    /// of <c>SizeOf</c>.
    /// </summary>
    public int SizeOf()
    {
        int size = BaseSizeOf();
        int n = size % 4;
        if (n != 0)
        {
            size += 4 - n;
        }

        return size;
    }

    /// <summary>
    /// Serializes the edge info to the stream in the exact on-disk byte layout (matching the C++
    /// <c>operator&lt;&lt;(std::ostream&amp;, const EdgeInfoBuilder&amp;)</c>).
    /// </summary>
    public void Serialize(Stream output)
    {
        ArgumentNullException.ThrowIfNull(output);

        // Pack the name count and encoded shape size, checking against limits.
        uint nameCount = (uint)_nameInfoList.Count;
        if (nameCount > MaxNamesPerEdge)
        {
            nameCount = MaxNamesPerEdge;
        }

        uint encodedShapeSize;
        if (_encodedShape.Length > MaxEncodedShapeSize)
        {
            encodedShapeSize = MaxEncodedShapeSize;
        }
        else
        {
            encodedShapeSize = (uint)_encodedShape.Length;
        }

        bool hasElevation = _encodedElevation.Length > 0;

        // Word 0: wayid_:32
        uint word0 = _wayid;

        // Word 1: mean_elevation_:12 | bike_network_:4 | speed_limit_:8 | extended_wayid0_:8
        uint word1 = (_meanElevation & 0xFFFu)
                     | ((_bikeNetwork & 0xFu) << 12)
                     | ((_speedLimit & 0xFFu) << 16)
                     | ((_extendedWayid0 & 0xFFu) << 24);

        // Word 2: name_count_:4 | encoded_shape_size_:16 | extended_wayid1_:8
        //         | extended_wayid_size_:2 | has_elevation_:1 | spare0_:1
        uint word2 = (nameCount & 0xFu)
                     | ((encodedShapeSize & 0xFFFFu) << 4)
                     | ((_extendedWayid1 & 0xFFu) << 20)
                     | ((_extendedWayidSize & 0x3u) << 28)
                     | ((hasElevation ? 1u : 0u) << 30);

        WriteUInt32(output, word0);
        WriteUInt32(output, word1);
        WriteUInt32(output, word2);

        // name_info_list (4 bytes each).
        for (int i = 0; i < nameCount; i++)
        {
            WriteUInt32(output, _nameInfoList[i].Word);
        }

        // Encoded shape (raw bytes, 1 per char).
        for (int i = 0; i < _encodedShape.Length; i++)
        {
            output.WriteByte((byte)_encodedShape[i]);
        }

        if (_extendedWayidSize > 0)
        {
            output.WriteByte(_extendedWayid2);
        }

        if (_extendedWayidSize > 1)
        {
            output.WriteByte(_extendedWayid3);
        }

        if (_encodedElevation.Length > 0)
        {
            for (int i = 0; i < _encodedElevation.Length; i++)
            {
                output.WriteByte((byte)_encodedElevation[i]);
            }
        }

        // Pad to a 4-byte boundary.
        int padding = BaseSizeOf() % 4;
        padding = padding > 0 ? 4 - padding : 0;
        for (int i = 0; i < padding; i++)
        {
            output.WriteByte(0);
        }
    }

    private static void WriteUInt32(Stream output, uint value)
    {
        output.WriteByte((byte)(value & 0xFF));
        output.WriteByte((byte)((value >> 8) & 0xFF));
        output.WriteByte((byte)((value >> 16) & 0xFF));
        output.WriteByte((byte)((value >> 24) & 0xFF));
    }
}
