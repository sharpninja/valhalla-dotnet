// Faithful C# port of Valhalla's gtest suite test/compression.cc.
// Each [Fact] mirrors a TEST(Compression, ...) case. The original src/dst callbacks
// manipulated zlib's z_stream (next_in/avail_in/total_out/next_out/avail_out); here
// they manipulate the managed <see cref="ZStream"/> shim with identical semantics.
//
// The original C++ stored bytes in std::string; here we use a growable byte buffer.
// The dst callbacks grow the output buffer in 16-byte chunks exactly as the C++ did
// (deflated.resize(size + 16)), exercising the dst_func "need more space" path.

using System.Text;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class CompressionTests
{
    // A growable byte buffer playing the role of the std::string output buffers in
    // the C++ test. Length tracks std::string::size(); the backing array is grown
    // to hold writes by the dst callbacks.
    private sealed class GrowBuffer
    {
        private byte[] data_ = Array.Empty<byte>();
        private int size_;

        public int Size => size_;

        public byte[] Backing => data_;

        // Mirrors std::string::resize: grows/shrinks the logical size, allocating as needed.
        public void Resize(int newSize)
        {
            if (newSize > data_.Length)
            {
                var grown = new byte[newSize];
                Array.Copy(data_, grown, size_);
                data_ = grown;
            }

            size_ = newSize;
        }

        public byte[] ToArray()
        {
            var result = new byte[size_];
            Array.Copy(data_, result, size_);
            return result;
        }

        public string ToAsciiString() => Encoding.ASCII.GetString(data_, 0, size_);

        public static GrowBuffer FromString(string s)
        {
            var buf = new GrowBuffer();
            byte[] bytes = Encoding.ASCII.GetBytes(s);
            buf.Resize(bytes.Length);
            Array.Copy(bytes, buf.data_, bytes.Length);
            return buf;
        }
    }

    // int deflate_src(z_stream& s, std::string& data)
    private static int DeflateSrc(ZStream s, GrowBuffer data)
    {
        s.NextIn = data.Backing;
        s.NextInOffset = 0;
        s.AvailIn = (uint)data.Size;
        return CompressionUtils.ZFinish;
    }

    // void deflate_dst(z_stream& s, std::string& deflated)
    private static void DeflateDst(ZStream s, GrowBuffer deflated)
    {
        int size = deflated.Size;
        if (s.TotalOut < (ulong)size)
        {
            // if the whole buffer wasn't used we are done
            deflated.Resize((int)s.TotalOut);
        }
        else
        {
            // we need more space: set the pointer to the next spot
            deflated.Resize(size + 16);
            s.NextOut = deflated.Backing;
            s.NextOutOffset = size;
            s.AvailOut = 16;
        }
    }

    // void inflate_src(z_stream& s, std::string& data)
    private static void InflateSrc(ZStream s, GrowBuffer data)
    {
        s.NextIn = data.Backing;
        s.NextInOffset = 0;
        s.AvailIn = (uint)data.Size;
    }

    // int inflate_dst(z_stream& s, std::string& inflated)
    private static int InflateDst(ZStream s, GrowBuffer inflated)
    {
        int size = inflated.Size;
        if (s.TotalOut < (ulong)size)
        {
            inflated.Resize((int)s.TotalOut);
        }
        else
        {
            inflated.Resize(size + 16);
            s.NextOut = inflated.Backing;
            s.NextOutOffset = size;
            s.AvailOut = 16;
        }

        return CompressionUtils.ZNoFlush;
    }

    [Fact]
    public void Roundtrip()
    {
        // deflate
        var message = GrowBuffer.FromString("message in a gzipped bottle");
        var deflated = new GrowBuffer();
        Assert.True(
            CompressionUtils.Deflate(s => DeflateSrc(s, message), s => DeflateDst(s, deflated)),
            "Can't write gzipped string");

        // inflate
        var inflated = new GrowBuffer();
        Assert.True(
            CompressionUtils.Inflate(s => InflateSrc(s, deflated), s => InflateDst(s, inflated)),
            "failed to inflate string");

        // check the data
        Assert.Equal("message in a gzipped bottle", inflated.ToAsciiString());
    }

    [Fact]
    public void FailDeflate()
    {
        int DeflateSrcFail(ZStream s) => throw new InvalidOperationException("you cant catch me");
        void DeflateDstFail(ZStream s) => throw new InvalidOperationException("im the gingerbread man");

        // deflate it
        var src = GrowBuffer.FromString("who cares");
        var dst = new GrowBuffer();

        Assert.False(
            CompressionUtils.Deflate(DeflateSrcFail, s => DeflateDst(s, dst)),
            "src should fail");

        Assert.False(
            CompressionUtils.Deflate(s => DeflateSrc(s, src), DeflateDstFail),
            "dst should fail");
    }

    [Fact]
    public void FailInflate()
    {
        void InflateSrcFail(ZStream s) => throw new InvalidOperationException("you cant catch me");

        var bad = GrowBuffer.FromString("this isn't gzipped");
        void InflateSrcFail2(ZStream s)
        {
            s.NextIn = bad.Backing;
            s.NextInOffset = 0;
            s.AvailIn = (uint)bad.Size;
        }

        // Nothing to do, simulates 'cannot inflate' - reproducible if no disk space.
        void InflateSrcFail3(ZStream s)
        {
        }

        int InflateDstFail(ZStream s) => throw new InvalidOperationException("im the gingerbread man");

        // we do need some deflated stuff
        var message = GrowBuffer.FromString("message in a gzipped bottle");
        var deflated = new GrowBuffer();

        Assert.True(
            CompressionUtils.Deflate(s => DeflateSrc(s, message), s => DeflateDst(s, deflated)),
            "Can't write gzipped string");

        var inflated = new GrowBuffer();
        Assert.False(
            CompressionUtils.Inflate(InflateSrcFail, s => InflateDst(s, inflated)),
            "src should fail");

        Assert.False(
            CompressionUtils.Inflate(InflateSrcFail2, s => InflateDst(s, inflated)),
            "src should fail (bad data)");

        Assert.False(
            CompressionUtils.Inflate(s => InflateSrc(s, deflated), InflateDstFail),
            "dst should fail");

        bool inflateResult = false;
        var ex = Record.Exception(
            () => inflateResult = CompressionUtils.Inflate(InflateSrcFail3, s => InflateDst(s, inflated)));
        Assert.Null(ex);
        Assert.False(inflateResult);
    }
}
