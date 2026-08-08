using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using K4os.Compression.LZ4;
using SharpNinja.Valhalla.Generation.Pbf;

namespace SharpNinja.Valhalla.Generation.Tests.Pbf;

internal static class TestOsmPbfFixtureBuilder
{
    private static readonly string[] Strings =
    [
        string.Empty,
        "highway",
        "residential",
        "type",
        "route",
        "from",
    ];

    public static byte[] Create(OsmPbfCompressionKind compression, int dataBlockCount = 1)
    {
        var result = new MemoryStream();
        var primitiveBlock = CreatePrimitiveBlock();
        for (var block = 0; block < dataBlockCount; block++)
        {
            WriteFileBlock(result, "OSMData", CreateBlob(primitiveBlock, compression));
        }

        return result.ToArray();
    }

    public static byte[] CreateUnsupportedCompression()
    {
        var primitiveBlock = CreatePrimitiveBlock();
        using var blob = new MemoryStream();
        WriteInt32(blob, 2, primitiveBlock.Length);
        WriteBytes(blob, 7, primitiveBlock);

        using var result = new MemoryStream();
        WriteFileBlock(result, "OSMData", blob.ToArray());
        return result.ToArray();
    }

    public static byte[] CreateOversizedHeaderPrefix(int headerLength)
    {
        var result = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(result, headerLength);
        return result;
    }

    private static byte[] CreatePrimitiveBlock()
    {
        using var block = new MemoryStream();
        WriteBytes(block, 1, CreateStringTable());
        WriteBytes(block, 2, CreatePrimitiveGroup());
        WriteInt32(block, 17, 100);
        return block.ToArray();
    }

    private static byte[] CreateStringTable()
    {
        using var table = new MemoryStream();
        foreach (var value in Strings)
        {
            WriteBytes(table, 1, Encoding.UTF8.GetBytes(value));
        }

        return table.ToArray();
    }

    private static byte[] CreatePrimitiveGroup()
    {
        using var group = new MemoryStream();
        WriteBytes(group, 1, CreateNode());
        WriteBytes(group, 3, CreateWay());
        WriteBytes(group, 4, CreateRelation());
        return group.ToArray();
    }

    private static byte[] CreateNode()
    {
        using var node = new MemoryStream();
        WriteSInt64(node, 1, 1);
        WritePackedUInt32(node, 2, [1]);
        WritePackedUInt32(node, 3, [2]);
        WriteSInt64(node, 8, 360_000_000);
        WriteSInt64(node, 9, -860_000_000);
        return node.ToArray();
    }

    private static byte[] CreateWay()
    {
        using var way = new MemoryStream();
        WriteInt64(way, 1, 10);
        WritePackedUInt32(way, 2, [1]);
        WritePackedUInt32(way, 3, [2]);
        WritePackedSInt64(way, 8, [1, 1]);
        return way.ToArray();
    }

    private static byte[] CreateRelation()
    {
        using var relation = new MemoryStream();
        WriteInt64(relation, 1, 20);
        WritePackedUInt32(relation, 2, [3]);
        WritePackedUInt32(relation, 3, [4]);
        WritePackedInt32(relation, 8, [5]);
        WritePackedSInt64(relation, 9, [10]);
        WritePackedInt32(relation, 10, [1]);
        return relation.ToArray();
    }

    private static byte[] CreateBlob(byte[] data, OsmPbfCompressionKind compression)
    {
        using var blob = new MemoryStream();
        switch (compression)
        {
            case OsmPbfCompressionKind.Raw:
                WriteBytes(blob, 1, data);
                break;
            case OsmPbfCompressionKind.Zlib:
                WriteInt32(blob, 2, data.Length);
                WriteBytes(blob, 3, CompressZlib(data));
                break;
            case OsmPbfCompressionKind.Lz4:
                WriteInt32(blob, 2, data.Length);
                WriteBytes(blob, 6, CompressLz4(data));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(compression), compression, null);
        }

        return blob.ToArray();
    }

    private static byte[] CompressZlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static byte[] CompressLz4(byte[] data)
    {
        var target = new byte[LZ4Codec.MaximumOutputSize(data.Length)];
        var written = LZ4Codec.Encode(data, target);
        return target.AsSpan(0, written).ToArray();
    }

    private static void WriteFileBlock(Stream destination, string type, byte[] blob)
    {
        using var header = new MemoryStream();
        WriteBytes(header, 1, Encoding.UTF8.GetBytes(type));
        WriteInt32(header, 3, blob.Length);
        var headerBytes = header.ToArray();

        Span<byte> headerLength = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(headerLength, headerBytes.Length);
        destination.Write(headerLength);
        destination.Write(headerBytes);
        destination.Write(blob);
    }

    private static void WriteBytes(Stream destination, int field, ReadOnlySpan<byte> value)
    {
        WriteTag(destination, field, 2);
        WriteVarint(destination, checked((ulong)value.Length));
        destination.Write(value);
    }

    private static void WriteInt32(Stream destination, int field, int value)
    {
        WriteTag(destination, field, 0);
        WriteVarint(destination, checked((ulong)value));
    }

    private static void WriteInt64(Stream destination, int field, long value)
    {
        WriteTag(destination, field, 0);
        WriteVarint(destination, unchecked((ulong)value));
    }

    private static void WriteSInt64(Stream destination, int field, long value)
    {
        WriteTag(destination, field, 0);
        WriteVarint(destination, ZigZag(value));
    }

    private static void WritePackedUInt32(Stream destination, int field, IReadOnlyList<uint> values)
    {
        using var packed = new MemoryStream();
        foreach (var value in values)
        {
            WriteVarint(packed, value);
        }

        WriteBytes(destination, field, packed.ToArray());
    }

    private static void WritePackedInt32(Stream destination, int field, IReadOnlyList<int> values)
    {
        using var packed = new MemoryStream();
        foreach (var value in values)
        {
            WriteVarint(packed, checked((ulong)value));
        }

        WriteBytes(destination, field, packed.ToArray());
    }

    private static void WritePackedSInt64(Stream destination, int field, IReadOnlyList<long> values)
    {
        using var packed = new MemoryStream();
        foreach (var value in values)
        {
            WriteVarint(packed, ZigZag(value));
        }

        WriteBytes(destination, field, packed.ToArray());
    }

    private static void WriteTag(Stream destination, int field, int wireType) =>
        WriteVarint(destination, checked((ulong)((field << 3) | wireType)));

    private static ulong ZigZag(long value) =>
        unchecked((ulong)((value << 1) ^ (value >> 63)));

    private static void WriteVarint(Stream destination, ulong value)
    {
        Span<byte> encoded = stackalloc byte[10];
        var count = 0;
        do
        {
            var current = (byte)(value & 0x7f);
            value >>= 7;
            if (value != 0)
            {
                current |= 0x80;
            }

            encoded[count++] = current;
        }
        while (value != 0);

        destination.Write(encoded[..count]);
    }
}
