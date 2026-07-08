// Minimal protobuf wire-format reader used by OsmPbfReader.
// Implements just the protobuf wire types needed to decode the OSM PBF fileformat/osmformat
// messages by hand (no protobuf library): varint, 64-bit, length-delimited, 32-bit; plus
// helpers for zig-zag sint64, packed varint arrays, and field skipping.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SharpNinja.Valhalla.Mjolnir;

/// <summary>Protobuf wire types (the low 3 bits of a field tag).</summary>
internal enum WireType : byte
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5,
}

/// <summary>
/// Hand-rolled protobuf reader over a byte span. Faithful to the protobuf wire format used by
/// the OSM PBF schema. Not a general-purpose protobuf implementation; only the constructs the
/// PBF messages use are supported.
/// </summary>
internal sealed class ProtoReader
{
    private readonly byte[] _data;
    private int _pos;
    private readonly int _end;

    public ProtoReader(byte[] data)
    {
        _data = data;
        _pos = 0;
        _end = data.Length;
    }

    /// <summary>True when the reader has consumed all bytes.</summary>
    public bool Eof => _pos >= _end;

    /// <summary>Reads a field tag, returning the field number and its wire type.</summary>
    public (int field, WireType wireType) ReadTag()
    {
        ulong key = ReadVarint();
        int field = (int)(key >> 3);
        var wt = (WireType)(byte)(key & 0x7);
        return (field, wt);
    }

    /// <summary>Reads a base-128 varint (up to 10 bytes).</summary>
    public ulong ReadVarint()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_pos >= _end)
            {
                throw new InvalidDataException("PBF error: varint ran off the end of the buffer");
            }

            byte b = _data[_pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }

            shift += 7;
            if (shift >= 64)
            {
                throw new InvalidDataException("PBF error: varint too long");
            }
        }

        return result;
    }

    /// <summary>Reads a varint that encodes a (possibly negative) two's-complement int64.</summary>
    public long ReadVarintSignedAsInt64() => unchecked((long)ReadVarint());

    /// <summary>Reads a zig-zag encoded sint64 (the encoding OSM PBF uses for ids and lat/lon).</summary>
    public long ReadSInt64()
    {
        ulong n = ReadVarint();
        return (long)(n >> 1) ^ -(long)(n & 1);
    }

    /// <summary>Reads a length-delimited byte array.</summary>
    public byte[] ReadBytes()
    {
        int len = (int)ReadVarint();
        if (len < 0 || _pos + len > _end)
        {
            throw new InvalidDataException("PBF error: length-delimited field exceeds buffer");
        }

        byte[] result = new byte[len];
        Array.Copy(_data, _pos, result, 0, len);
        _pos += len;
        return result;
    }

    /// <summary>Reads a length-delimited UTF-8 string.</summary>
    public string ReadString()
    {
        int len = (int)ReadVarint();
        if (len < 0 || _pos + len > _end)
        {
            throw new InvalidDataException("PBF error: string field exceeds buffer");
        }

        string s = Encoding.UTF8.GetString(_data, _pos, len);
        _pos += len;
        return s;
    }

    /// <summary>Reads a packed (length-delimited) array of unsigned varints into the destination as uint.</summary>
    public void ReadPackedVarintsUInt32(List<uint> dest)
    {
        int len = (int)ReadVarint();
        int target = _pos + len;
        CheckBounds(target);
        while (_pos < target)
        {
            dest.Add((uint)ReadVarint());
        }
    }

    /// <summary>Reads a packed array of (two's-complement) int32 varints.</summary>
    public void ReadPackedVarintsInt32(List<int> dest)
    {
        int len = (int)ReadVarint();
        int target = _pos + len;
        CheckBounds(target);
        while (_pos < target)
        {
            dest.Add((int)(long)ReadVarint());
        }
    }

    /// <summary>Reads a packed array of zig-zag sint64 varints (used for delta-coded ids/lat/lon).</summary>
    public void ReadPackedSInt64(List<long> dest)
    {
        int len = (int)ReadVarint();
        int target = _pos + len;
        CheckBounds(target);
        while (_pos < target)
        {
            dest.Add(ReadSInt64());
        }
    }

    /// <summary>Skips a field of the given wire type.</summary>
    public void SkipField(WireType wt)
    {
        switch (wt)
        {
            case WireType.Varint:
                ReadVarint();
                break;
            case WireType.Fixed64:
                Advance(8);
                break;
            case WireType.LengthDelimited:
                int len = (int)ReadVarint();
                Advance(len);
                break;
            case WireType.Fixed32:
                Advance(4);
                break;
            case WireType.StartGroup:
                // Skip until the matching EndGroup tag.
                while (!Eof)
                {
                    (int _, WireType inner) = ReadTag();
                    if (inner == WireType.EndGroup)
                    {
                        break;
                    }

                    SkipField(inner);
                }

                break;
            case WireType.EndGroup:
                break;
            default:
                throw new InvalidDataException($"PBF error: unsupported wire type {wt}");
        }
    }

    private void Advance(int count)
    {
        if (count < 0 || _pos + count > _end)
        {
            throw new InvalidDataException("PBF error: skip ran off the end of the buffer");
        }

        _pos += count;
    }

    private void CheckBounds(int target)
    {
        if (target < 0 || target > _end)
        {
            throw new InvalidDataException("PBF error: packed field exceeds buffer");
        }
    }
}
