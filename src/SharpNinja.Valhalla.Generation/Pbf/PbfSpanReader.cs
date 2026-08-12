using System.Text;

namespace SharpNinja.Valhalla.Generation.Pbf;

internal enum PbfWireType : byte
{
    Varint = 0,
    Fixed64 = 1,
    LengthDelimited = 2,
    StartGroup = 3,
    EndGroup = 4,
    Fixed32 = 5,
}

internal ref struct PbfSpanReader
{
    private readonly ReadOnlySpan<byte> source;
    private int offset;

    public PbfSpanReader(ReadOnlySpan<byte> source)
    {
        this.source = source;
        offset = 0;
    }

    public bool End => offset >= source.Length;

    public int Offset => offset;

    public (int Field, PbfWireType WireType) ReadTag()
    {
        var tag = ReadVarint();
        var field = checked((int)(tag >> 3));
        var wireType = (PbfWireType)(tag & 0x07);
        if (field <= 0 || wireType is > PbfWireType.Fixed32)
        {
            throw Malformed("invalid field tag");
        }

        return (field, wireType);
    }

    public ulong ReadVarint()
    {
        ulong result = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            EnsureAvailable(1);
            var current = source[offset++];
            if (shift == 63 && current > 1)
            {
                throw Malformed("varint exceeds 64 bits");
            }

            result |= (ulong)(current & 0x7f) << shift;
            if ((current & 0x80) == 0)
            {
                return result;
            }
        }

        throw Malformed("unterminated varint");
    }

    public long ReadSignedVarint() => unchecked((long)ReadVarint());

    public long ReadSInt64()
    {
        var value = ReadVarint();
        return unchecked((long)((value >> 1) ^ (ulong)-(long)(value & 1)));
    }

    public ReadOnlySpan<byte> ReadLengthDelimited()
    {
        var range = ReadLengthDelimitedRange();
        return source.Slice(range.Offset, range.Length);
    }

    public (int Offset, int Length) ReadLengthDelimitedRange()
    {
        var length64 = ReadVarint();
        if (length64 > int.MaxValue)
        {
            throw Malformed("length-delimited value is too large");
        }

        var length = (int)length64;
        EnsureAvailable(length);
        var result = (Offset: offset, Length: length);
        offset += length;
        return result;
    }

    public string ReadString() => Encoding.UTF8.GetString(ReadLengthDelimited());

    public void SkipField(PbfWireType wireType)
    {
        switch (wireType)
        {
            case PbfWireType.Varint:
                _ = ReadVarint();
                break;
            case PbfWireType.Fixed64:
                Skip(8);
                break;
            case PbfWireType.LengthDelimited:
                _ = ReadLengthDelimited();
                break;
            case PbfWireType.Fixed32:
                Skip(4);
                break;
            case PbfWireType.StartGroup:
            case PbfWireType.EndGroup:
            default:
                throw Malformed("protobuf groups are not valid in OSM PBF input");
        }
    }

    private void Skip(int count)
    {
        EnsureAvailable(count);
        offset += count;
    }

    private void EnsureAvailable(int count)
    {
        if (count < 0 || count > source.Length - offset)
        {
            throw Malformed("truncated protocol buffer");
        }
    }

    private static StreamingOsmPbfException Malformed(string detail) =>
        new(
            StreamingOsmPbfFailureCode.MalformedProtocolBuffer,
            $"PBF protocol error: {detail}.");
}
