// Faithful C# port of Valhalla's gtest suite test/edgeinfo.cc.
// Each [Fact] mirrors a TEST(EdgeInfo*/EdgeInfoBuilder, ...) case.
//
// The C++ TestWriteRead case round-trips through mjolnir's EdgeInfoBuilder (which is part of the
// excluded mjolnir module). To keep the test self-contained and to exercise the EXACT on-disk
// byte layout that EdgeInfo parses, this file includes a minimal local serializer
// (EdgeInfoBlobBuilder) that reproduces mjolnir::EdgeInfoBuilder::operator<< byte-for-byte:
//   [EdgeInfoInner (12 bytes)][name infos][encoded shape][extended wayid bytes][elevation][pad to 4].
// This is a test-only helper and is not part of the baldr port surface.
//
// The TaggedValueSize_* cases construct tagged-value byte buffers exactly as the C++ tests do and
// assert EdgeInfo.TaggedValueSize matches.

using System.Collections.Generic;
using System.Text;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class EdgeInfoTests
{
    // ----------------------------------------------------------------------
    // EdgeInfoBuilder.TestWriteRead (round-trip through the on-disk byte layout)
    // ----------------------------------------------------------------------

    [Fact]
    public void EdgeInfoBuilder_TestWriteRead()
    {
        var builder = new EdgeInfoBlobBuilder();
        builder.SetWayId(6472927700900931484UL);

        // Name info list (offset, additional, is_route_num, tagged, spare) — matching the C++
        // aggregate-init {963, 0, 0, 0, 0}.
        var nameInfoList = new List<NameInfo>
        {
            new NameInfo(963, 0, false, false, 0),
            new NameInfo(957, 0, false, false, 0),
            new NameInfo(862, 0, false, false, 0),
        };
        builder.SetNameInfoList(nameInfoList);

        // Shape
        var shape = new List<PointLL>
        {
            new PointLL(-76.3002, 40.0433),
            new PointLL(-76.3036, 40.043),
        };
        builder.SetShape(shape);

        byte[] memblock = builder.ToBytes();
        var ei = new EdgeInfo(memblock, 0, null, 0);

        Assert.Equal(6472927700900931484UL, ei.WayId);

        // Validate the read-in fields against the originals.
        Assert.Equal((uint)nameInfoList.Count, ei.NameCount);
        Assert.Equal(shape.Count, ei.Shape().Count);

        // Check the name indices.
        for (byte i = 0; i < ei.NameCount; ++i)
        {
            Assert.Equal(nameInfoList[i].NameOffset, ei.GetNameInfo(i).NameOffset);
        }

        // Check the shape points.
        for (int i = 0; i < ei.Shape().Count; ++i)
        {
            Assert.True(shape[i].ApproximatelyEqual(ei.Shape()[i]), $"index {i}");
        }
    }

    // ----------------------------------------------------------------------
    // TaggedValueSize_* cases
    // ----------------------------------------------------------------------

    [Fact]
    public void TaggedValueSize_Layer()
    {
        var tagged = new List<byte>
        {
            (byte)TaggedValue.Layer,
            unchecked((byte)(sbyte)-3), // layer value
            0,                          // null terminator
        };

        int size = EdgeInfo.TaggedValueSize(tagged.ToArray(), 0);

        Assert.Equal(3, size);
        Assert.Equal(tagged.Count, size);
    }

    [Fact]
    public void TaggedValueSize_Tunnel()
    {
        const string tunnelName = "Fort McHenry Tunnel";
        byte[] tagged = BuildStringTag(TaggedValue.Tunnel, tunnelName);

        int size = EdgeInfo.TaggedValueSize(tagged, 0);

        int expected = 1 + tunnelName.Length + 1;
        Assert.Equal(expected, size);
        Assert.Equal(tagged.Length, size);
    }

    [Fact]
    public void TaggedValueSize_Bridge()
    {
        const string bridgeName = "Golden Gate Bridge";
        byte[] tagged = BuildStringTag(TaggedValue.Bridge, bridgeName);

        int size = EdgeInfo.TaggedValueSize(tagged, 0);

        int expected = 1 + bridgeName.Length + 1;
        Assert.Equal(expected, size);
        Assert.Equal(tagged.Length, size);
    }

    [Fact]
    public void TaggedValueSize_Level()
    {
        const string levelStr = "2";
        byte[] tagged = BuildStringTag(TaggedValue.Level, levelStr);

        int size = EdgeInfo.TaggedValueSize(tagged, 0);

        int expected = 1 + levelStr.Length + 1;
        Assert.Equal(expected, size);
        Assert.Equal(tagged.Length, size);
    }

    [Fact]
    public void TaggedValueSize_LevelRef()
    {
        const string levelRefStr = "Ground Floor";
        byte[] tagged = BuildStringTag(TaggedValue.LevelRef, levelRefStr);

        int size = EdgeInfo.TaggedValueSize(tagged, 0);

        int expected = 1 + levelRefStr.Length + 1;
        Assert.Equal(expected, size);
        Assert.Equal(tagged.Length, size);
    }

    [Fact]
    public void TaggedValueSize_BssInfo()
    {
        const string bssInfo = "station_123";
        byte[] tagged = BuildStringTag(TaggedValue.BssInfo, bssInfo);

        int size = EdgeInfo.TaggedValueSize(tagged, 0);

        int expected = 1 + bssInfo.Length + 1;
        Assert.Equal(expected, size);
        Assert.Equal(tagged.Length, size);
    }

    [Fact]
    public void TaggedValueSize_OSMNodeIds()
    {
        var nodeIds = new List<ulong> { 987653, 987654, 987655, 987656 };
        string encodedIds = Encoded.Encode7Int(nodeIds);

        var tagged = new List<byte> { (byte)TaggedValue.OSMNodeIds };
        // varint size prefix (encode7int of a single-element int vector)
        tagged.AddRange(StringToBytes(Encoded.Encode7Int(new List<int> { encodedIds.Length })));
        tagged.AddRange(StringToBytes(encodedIds));
        tagged.Add(0); // null terminator

        int size = EdgeInfo.TaggedValueSize(tagged.ToArray(), 0);

        Assert.Equal(tagged.Count, size);
        Assert.True(size > 1);
    }

    [Fact]
    public void TaggedValueSize_Levels()
    {
        // Build the levels data using encode7int with single-element vectors (matches encode_level).
        string levelsData =
            Encoded.Encode7Int(new List<int> { 5 }) +
            Encoded.Encode7Int(new List<int> { 10 }) +
            Encoded.Encode7Int(new List<int> { 100 });

        var tagged = new List<byte> { (byte)TaggedValue.Levels };
        tagged.AddRange(StringToBytes(Encoded.Encode7Int(new List<int> { levelsData.Length })));
        tagged.AddRange(StringToBytes(levelsData));
        tagged.Add(0); // null terminator

        int size = EdgeInfo.TaggedValueSize(tagged.ToArray(), 0);

        Assert.Equal(tagged.Count, size);
        Assert.True(size > 1);
    }

    [Fact]
    public void TaggedValueSize_ConditionalSpeedLimits()
    {
        var tagged = new List<byte> { (byte)TaggedValue.ConditionalSpeedLimits };
        // ConditionalSpeedLimit struct: 8 bytes of zeros for testing.
        tagged.AddRange(new byte[8]);
        tagged.Add(0); // null terminator

        int size = EdgeInfo.TaggedValueSize(tagged.ToArray(), 0);

        int expected = 1 + 8 + 1;
        Assert.Equal(expected, size);
        Assert.Equal(tagged.Count, size);
    }

    [Fact]
    public void TaggedValueSize_Linguistic()
    {
        var tagged = new List<byte> { (byte)TaggedValue.Linguistic };

        // One linguistic entry: language_=1, length_=5, phonetic_alphabet_=1, name_index_=0.
        var header = new LinguisticTextHeader(0)
        {
            Language = 1,
            Length = 5,
            PhoneticAlphabet = 1,
            NameIndex = 0,
        };

        // Write only the first 3 (stored) header bytes, then the pronunciation, then the null.
        tagged.AddRange(header.ToStoredBytes());
        tagged.AddRange(StringToBytes("hello"));
        tagged.Add(0); // null terminator

        int size = EdgeInfo.TaggedValueSize(tagged.ToArray(), 0);

        Assert.Equal(tagged.Count, size);
        Assert.True(size > 1);
    }

    [Fact]
    public void TaggedValueSize_EmptyString()
    {
        var tagged = new List<byte>
        {
            (byte)TaggedValue.Tunnel,
            0, // just tag + null terminator
        };

        int size = EdgeInfo.TaggedValueSize(tagged.ToArray(), 0);

        Assert.Equal(2, size);
        Assert.Equal(tagged.Count, size);
    }

    [Fact]
    public void TaggedValueSize_MultipleLayerValues()
    {
        for (sbyte layer = -5; layer <= 5; ++layer)
        {
            if (layer == 0)
            {
                // Layer 0 is never serialized (it's the OSM default).
                continue;
            }

            var tagged = new List<byte>
            {
                (byte)TaggedValue.Layer,
                unchecked((byte)layer),
                0,
            };

            int size = EdgeInfo.TaggedValueSize(tagged.ToArray(), 0);

            Assert.Equal(3, size);
            Assert.Equal(tagged.Count, size);
        }
    }

    [Fact]
    public void TaggedValueSize_LongStrings()
    {
        string longTunnelName = new string('x', 100);
        byte[] tagged = BuildStringTag(TaggedValue.Tunnel, longTunnelName);

        int size = EdgeInfo.TaggedValueSize(tagged, 0);

        int expected = 1 + longTunnelName.Length + 1;
        Assert.Equal(expected, size);
        Assert.Equal(tagged.Length, size);
    }

    // ----------------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------------

    private static byte[] BuildStringTag(TaggedValue tag, string value)
    {
        var bytes = new List<byte> { (byte)tag };
        bytes.AddRange(StringToBytes(value));
        bytes.Add(0); // null terminator
        return bytes.ToArray();
    }

    private static byte[] StringToBytes(string s)
    {
        // The C++ code treats these strings as raw bytes (char), so map char -> byte directly.
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            bytes[i] = (byte)s[i];
        }

        return bytes;
    }

    /// <summary>
    /// Minimal test-only reproduction of mjolnir::EdgeInfoBuilder::operator&lt;&lt;. Produces the
    /// exact on-disk byte layout that <see cref="EdgeInfo"/> parses.
    /// </summary>
    private sealed class EdgeInfoBlobBuilder
    {
        private uint _wayid;
        private uint _extendedWayid0;
        private uint _extendedWayid1;
        private byte _extendedWayid2;
        private byte _extendedWayid3;
        private uint _extendedWayidSize;
        private List<NameInfo> _nameInfoList = new();
        private string _encodedShape = string.Empty;

        public void SetWayId(ulong wayid)
        {
            _wayid = (uint)(wayid & 0xFFFFFFFF);
            _extendedWayid0 = (uint)((wayid >> 32) & 0xFF);
            _extendedWayid1 = (uint)((wayid >> 40) & 0xFF);
            _extendedWayid2 = (byte)((wayid >> 48) & 0xFF);
            _extendedWayid3 = (byte)((wayid >> 56) & 0xFF);
            _extendedWayidSize = _extendedWayid3 > 0 ? 2u : (_extendedWayid2 > 0 ? 1u : 0u);
        }

        public void SetNameInfoList(List<NameInfo> nameInfoList) => _nameInfoList = nameInfoList;

        public void SetShape(IReadOnlyList<PointLL> shape) => _encodedShape = Encoded.Encode7(shape);

        public byte[] ToBytes()
        {
            uint nameCount = (uint)_nameInfoList.Count;
            uint encodedShapeSize = (uint)_encodedShape.Length;

            // Pack the three EdgeInfoInner words (little-endian).
            uint word0 = _wayid;

            // word1: mean_elevation_:12 | bike_network_:4 | speed_limit_:8 | extended_wayid0_:8
            // (mean elevation / bike / speed default to 0 in this fixture).
            uint word1 = (_extendedWayid0 & 0xFFu) << 24;

            // word2: name_count_:4 | encoded_shape_size_:16 | extended_wayid1_:8
            //        | extended_wayid_size_:2 | has_elevation_:1 | spare0_:1
            uint word2 = (nameCount & 0xFu)
                         | ((encodedShapeSize & 0xFFFFu) << 4)
                         | ((_extendedWayid1 & 0xFFu) << 20)
                         | ((_extendedWayidSize & 0x3u) << 28);
            // has_elevation_ = 0 (no elevation in this fixture).

            var os = new List<byte>();
            WriteUInt32(os, word0);
            WriteUInt32(os, word1);
            WriteUInt32(os, word2);

            foreach (NameInfo ni in _nameInfoList)
            {
                WriteUInt32(os, ni.Word);
            }

            foreach (char c in _encodedShape)
            {
                os.Add((byte)c);
            }

            if (_extendedWayidSize > 0)
            {
                os.Add(_extendedWayid2);
            }

            if (_extendedWayidSize > 1)
            {
                os.Add(_extendedWayid3);
            }

            // BaseSizeOf for padding = header + names + shape + extended-wayid bytes (+ elevation = 0).
            int baseSize = EdgeInfo.EdgeInfoInnerSize
                           + (_nameInfoList.Count * EdgeInfo.NameInfoSize)
                           + _encodedShape.Length
                           + (int)_extendedWayidSize;
            int padding = baseSize % 4;
            padding = padding > 0 ? 4 - padding : 0;
            for (int i = 0; i < padding; i++)
            {
                os.Add(0);
            }

            return os.ToArray();
        }

        private static void WriteUInt32(List<byte> os, uint value)
        {
            os.Add((byte)(value & 0xFF));
            os.Add((byte)((value >> 8) & 0xFF));
            os.Add((byte)((value >> 16) & 0xFF));
            os.Add((byte)((value >> 24) & 0xFF));
        }
    }
}
