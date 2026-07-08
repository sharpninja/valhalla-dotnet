// Faithful C# port of Valhalla's gtest suite test/predictedspeeds.cc to xUnit.
// Each [Fact] mirrors a TEST(PredictedSpeeds, ...) / TEST_F(EncoderDecoderTest, ...) case
// with the same inputs and expected values.
//   EXPECT_EQ        -> Assert.Equal
//   EXPECT_LE        -> Assert.True(x <= bound)
//   ASSERT_GE        -> Assert.True(x >= bound)
//   ASSERT_PRED2     -> Assert.True(within_threshold(a, b))
//   ASSERT_TRUE(equal) -> Assert.Equal(sequence, sequence)
//
// The decoding tests use the same "+1 byte offset" fixture path as the C++ test (see the
// HACK note in the C++ source): the fixtures are kDecodedSpeedSize + 1 bytes and the
// coefficients are read starting at byte offset 1.

using System;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class PredictedSpeedsTests
{
    // Check if the speed is within threshold for the test. C++ within_threshold.
    private const uint SpeedErrorThreshold = 2;

    private static bool WithinThreshold(uint v1, uint v2)
        => (v2 > v1) ? (v2 - v1) < SpeedErrorThreshold : (v1 - v2) < SpeedErrorThreshold;

    // Normalized l1 norm of the vector. C++ normalized_l1_norm.
    private static float NormalizedL1Norm(ReadOnlySpan<float> vec)
    {
        float sum = 0f;
        for (int i = 0; i < vec.Length; ++i)
        {
            sum += MathF.Abs(vec[i]);
        }

        return sum / vec.Length;
    }

    // Read the coefficients from a "+1 byte offset" fixture, mirroring the C++ test loop:
    //   int16_t coefficients[200]; idx=1; coefficients[i] = to_little_endian(*(int16_t*)&raw[idx]);
    private static short[] ReadCoefficientsOffsetOne(byte[] raw)
    {
        int count = (int)PredictedSpeedConstants.CoefficientCount;
        var coefficients = new short[count];
        int idx = 1;
        for (int i = 0; i < count; ++i, idx += 2)
        {
            // raw bytes are stored low-byte/high-byte (host memory of a big-endian int16);
            // to_little_endian byte-swaps to recover the host value.
            ushort be = (ushort)(raw[idx] | (raw[idx + 1] << 8));
            coefficients[i] = Util.ToLittleEndian(be);
        }

        return coefficients;
    }

    // Latin1 string -> raw bytes, matching how midgard Decode64 yields a latin1 std::string.
    private static byte[] DecodedBytes(string encoded)
    {
        string decoded = Util.Decode64(encoded);
        var raw = new byte[decoded.Length];
        for (int i = 0; i < decoded.Length; ++i)
        {
            raw[i] = (byte)decoded[i];
        }

        return raw;
    }

    private static void TryFreeFlowSpeed(string encodedStr, uint expFreeFlow, uint expConstrainedFlow)
    {
        byte[] raw = DecodedBytes(encodedStr);
        int index = 0;
        uint t = (uint)(raw[index++] & 0x1f);
        Assert.Equal(0u, t);

        uint freeFlowSpeed = (uint)(raw[index++] & 0xff);
        Assert.Equal(expFreeFlow, freeFlowSpeed);

        uint constrainedFlowSpeed = (uint)(raw[index++] & 0xff);
        Assert.Equal(expConstrainedFlow, constrainedFlowSpeed);
    }

    // TEST(PredictedSpeeds, test_free_flow_speed)
    [Fact]
    public void TestFreeFlowSpeed()
    {
        TryFreeFlowSpeed("AAie", 8, 158);

        // Add additional cases below
        TryFreeFlowSpeed("AACe", 0, 158);
    }

    // TEST(PredictedSpeeds, test_decoding)
    [Fact]
    public void TestDecoding()
    {
        const string encodedSpeedString =
            "AQXFAAkABAAhAAz/+//bABn/3wAMABsAEQAF//gAAAAdABQAEv/wABf//gAsAAkAKgAAACj/+gBDAAQAbAALAQQAKv63AAD/mwAM/87/7P/TAAX/2P/1//P//f/sAAn/z//xAA7//P/y//z/8v/x////+wAMABX/+f/6AA4AGQAEABX/9//vAAf/8gAfAAb/9AAFABH//P/0ABQABv/2////4//7//0AE//+//n/5AATAAcAAQAL/+v//P/3ABMAAAAU//L/+v/8AAAAEP/3AAsABQAE/9f/7AABAAwAAQAGABP//QAJ/+4AB//gABUAAf/+AAv/6P/oABP//gAAABX/5f/5AAT//v/5AAgABv/3AB7/6gAdAAL/+P/r//sACwADAAT/9wAE//MACAAK//cACv/4//sABAAA//j//P/7//H/9v/y//wACwAHAAYABv/4AAL/+QAKAB7//wAHABX/8wAQ/+wAFAAL/+7//AAIAAgADf/9AAz/4gAQ//X/9//+//j/9wAEAAz//wADAAc=";

        byte[] raw = DecodedBytes(encodedSpeedString);

        // HACK(mookerji): kDecodedSpeedSize+1 is the expected size (see C++ note).
        Assert.Equal((int)PredictedSpeedConstants.DecodedSpeedSize + 1, raw.Length);

        // Check that the first value pair == 1
        Assert.Equal((sbyte)1, unchecked((sbyte)raw[0]));

        short[] coefficients = ReadCoefficientsOffsetOne(raw);

        // Bucketized speeds - decoded speeds are compared against this set.
        ushort[] speeds = ExpectedDecodingSpeeds;

        var predSpeeds = new PredictedSpeeds();
        predSpeeds.SetOffset(new uint[] { 0 });
        predSpeeds.SetProfiles(coefficients);

        // Test against 5-minute bucket values.
        for (int i = 0; i < 2016; ++i)
        {
            uint secs = (uint)(i * 5 * 60);
            uint s = (uint)(predSpeeds.Speed(0, secs) + 0.5f);
            Assert.True(WithinThreshold(s, speeds[i]), "Speed outside of range");
        }
    }

    // TEST(PredictedSpeeds, test_negative_speeds)
    [Fact]
    public void TestNegativeSpeeds()
    {
        const string encodedSpeedString =
            "AQRu//UAEAAC/+4AA//6//gAAwAFAA//9wAHAAH/4AAd/+wACwAH//0AGQAYAA7//wANAAL/9//mAAUACgATAAb/8v/2//8AC//1ABMAAAAGABX/9//0//0AAAAQAAIAAv/6////9gAJAAcACf/zAAQAAwAC//oACf/2//sADQAVABD/+QADAAcACf/2//gABwAHAAAABv/9AAf/+QAM//kAEAAE//r//wAMAAD/9AAN//D/7QAK//EAE//7AAkAAQAF//f/+AAB//z/6f/y//MAAP/6ABL//AATABX//wAFAAMAGv/2AAf//wAI//sACv/5AAb/8gAOAAYADv/5AAMACP////T/7gAH//P/+f/9//n/9f/0//0AAwAP//3/8gAA//8ACv////gAAgAHAAP//QALAAcAFAAA//8ABP/vAAIAEAAM/+3/9QAC//j//v/tABj/+wAA//sAC//6//0ABwAAAAoABgAMAAb/+P/3AAX/9//7//0ADP/sAAwAB//v/+3//wAMABAACgAF//o=";

        byte[] raw = DecodedBytes(encodedSpeedString);

        // HACK(mookerji): See note above.
        Assert.Equal((int)PredictedSpeedConstants.DecodedSpeedSize + 1, raw.Length);

        Assert.Equal((sbyte)1, unchecked((sbyte)raw[0]));

        short[] coefficients = ReadCoefficientsOffsetOne(raw);

        var predSpeeds = new PredictedSpeeds();
        predSpeeds.SetOffset(new uint[] { 0 });
        predSpeeds.SetProfiles(coefficients);

        for (int i = 0; i < 2016; ++i)
        {
            uint secs = (uint)(i * 5 * 60);
            float s = predSpeeds.Speed(0, secs);
            Assert.True(s >= 0.0f, "Negative speed");
        }
    }

    // TEST(PredictedSpeeds, test_compress_decompress_accuracy)
    [Fact]
    public void TestCompressDecompressAccuracy()
    {
        int buckets = (int)PredictedSpeedConstants.BucketsPerWeek;

        // generate speed values for buckets
        var speeds = new float[buckets];
        for (int i = 0; i < buckets; ++i)
        {
            speeds[i] = MathF.Round(30f + (15f * MathF.Sin(i / 20f)), MidpointRounding.AwayFromZero);
        }

        // compress speed buckets
        short[] compressedSpeeds = PredictedSpeedCompression.CompressSpeedBuckets(speeds);

        // decompress speed buckets
        var decompressedSpeeds = new float[buckets];
        for (uint i = 0; i < buckets; ++i)
        {
            decompressedSpeeds[i] = PredictedSpeedCompression.DecompressSpeedBucket(compressedSpeeds, i);
        }

        var diffSpeeds = new float[buckets];
        for (int i = 0; i < buckets; ++i)
        {
            diffSpeeds[i] = MathF.Abs(speeds[i] - decompressedSpeeds[i]);
        }

        // check that average error of decompressing is no more than threshold
        float l1Err = NormalizedL1Norm(diffSpeeds);
        Assert.True(l1Err <= 1f, "Low decompression accuracy"); // <= 1 KPH

        // check that all decompressed speeds differ from original by no more than threshold
        float maxDiff = 0f;
        for (int i = 0; i < buckets; ++i)
        {
            if (diffSpeeds[i] > maxDiff)
            {
                maxDiff = diffSpeeds[i];
            }
        }

        Assert.True(maxDiff <= 2f, "Low decompression accuracy"); // <= 2 KPH
    }

    // Shared fixture for the encoder/decoder tests (C++ EncoderDecoderTest).
    private static short[] BuildEncoderDecoderCoefficients()
    {
        int count = (int)PredictedSpeedConstants.CoefficientCount;
        var coefficients = new short[count];
        for (int i = 0; i < count; ++i)
        {
            coefficients[i] = unchecked((short)((i % 2 == 0) ? (10 * i) : (-10 * i)));
        }

        return coefficients;
    }

    private const string EncoderDecoderEncoded =
        "AAD/9gAU/+IAKP/OADz/ugBQ/6YAZP+SAHj/fgCM/2oAoP9WALT/QgDI/y4A3P8aAPD/BgEE/vIBGP7eASz+ygFA/rYBVP6iAWj+jgF8/noBkP5mAaT+UgG4/j4BzP4qAeD+FgH0/gICCP3uAhz92gIw/cYCRP2yAlj9ngJs/YoCgP12ApT9YgKo/U4CvP06AtD9JgLk/RIC+Pz+Awz86gMg/NYDNPzCA0j8rgNc/JoDcPyGA4T8cgOY/F4DrPxKA8D8NgPU/CID6PwOA/z7+gQQ++YEJPvSBDj7vgRM+6oEYPuWBHT7ggSI+24EnPtaBLD7RgTE+zIE2PseBOz7CgUA+vYFFPriBSj6zgU8+roFUPqmBWT6kgV4+n4FjPpqBaD6VgW0+kIFyPouBdz6GgXw+gYGBPnyBhj53gYs+coGQPm2BlT5ogZo+Y4GfPl6BpD5Zgak+VIGuPk+Bsz5Kgbg+RYG9PkCBwj47gcc+NoHMPjGB0T4sgdY+J4HbPiKB4D4dgeU+GIHqPhOB7z4Og==";

    // TEST_F(EncoderDecoderTest, test_speeds_encoder)
    [Fact]
    public void TestSpeedsEncoder()
    {
        short[] coefficients = BuildEncoderDecoderCoefficients();
        string myEncoded = PredictedSpeedCompression.EncodeCompressedSpeeds(coefficients);
        Assert.Equal(EncoderDecoderEncoded, myEncoded);
    }

    // TEST_F(EncoderDecoderTest, test_speeds_decoder)
    [Fact]
    public void TestSpeedsDecoder()
    {
        short[] coefficients = BuildEncoderDecoderCoefficients();
        short[] myCoefficients = PredictedSpeedCompression.DecodeCompressedSpeeds(EncoderDecoderEncoded);
        Assert.Equal(coefficients, myCoefficients);
    }

    // Expected per-bucket speeds for TestDecoding (verbatim from test/predictedspeeds.cc).
    private static readonly ushort[] ExpectedDecodingSpeeds =
    {
        36, 36, 36, 36, 36, 36, 36, 36, 36, 37, 37, 37, 38, 38, 39, 40, 40, 41, 41, 42, 42, 42, 42,
        42, 42, 42, 42, 41, 41, 41, 41, 41, 41, 41, 41, 42, 42, 43, 43, 44, 44, 45, 45, 45, 46, 46,
        45, 45, 45, 44, 43, 43, 42, 41, 40, 40, 39, 39, 38, 38, 37, 37, 37, 36, 36, 35, 34, 34, 33,
        32, 30, 29, 27, 26, 24, 23, 21, 20, 19, 18, 17, 17, 16, 16, 16, 16, 16, 16, 17, 17, 16, 16,
        16, 15, 15, 14, 13, 12, 12, 11, 11, 10, 10, 11, 12, 13, 14, 16, 17, 19, 21, 24, 25, 27, 29,
        30, 31, 32, 33, 33, 33, 33, 33, 32, 32, 32, 33, 33, 34, 35, 36, 38, 39, 41, 42, 44, 45, 46,
        47, 47, 48, 48, 47, 47, 46, 45, 45, 44, 43, 43, 43, 43, 43, 44, 44, 45, 46, 46, 47, 48, 48,
        49, 49, 48, 48, 48, 47, 46, 46, 45, 44, 44, 43, 43, 43, 43, 43, 43, 43, 42, 42, 42, 41, 41,
        40, 39, 38, 37, 35, 34, 33, 31, 30, 29, 28, 27, 26, 26, 25, 25, 25, 25, 25, 25, 25, 25, 25,
        25, 25, 25, 25, 25, 24, 24, 24, 23, 23, 22, 22, 21, 20, 19, 18, 18, 17, 17, 16, 16, 16, 16,
        17, 17, 18, 19, 20, 22, 23, 24, 25, 27, 27, 28, 29, 29, 29, 29, 29, 29, 29, 30, 30, 30, 31,
        32, 33, 34, 36, 37, 39, 41, 42, 43, 45, 45, 46, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47, 47,
        48, 49, 49, 50, 51, 52, 52, 53, 53, 53, 53, 52, 52, 51, 50, 49, 48, 46, 45, 44, 44, 43, 42,
        41, 41, 40, 40, 39, 38, 38, 37, 36, 35, 34, 34, 33, 33, 32, 32, 32, 32, 33, 33, 34, 34, 35,
        35, 35, 35, 35, 34, 33, 32, 31, 29, 27, 26, 24, 23, 21, 20, 19, 19, 18, 18, 18, 19, 19, 20,
        20, 20, 21, 21, 21, 21, 21, 21, 21, 20, 20, 21, 21, 21, 22, 22, 23, 24, 25, 26, 27, 28, 29,
        29, 29, 30, 30, 29, 29, 29, 29, 29, 30, 30, 31, 32, 33, 35, 37, 39, 41, 43, 44, 46, 47, 48,
        49, 50, 50, 50, 49, 49, 48, 48, 47, 46, 46, 46, 46, 46, 46, 46, 47, 48, 48, 49, 49, 49, 49,
        49, 49, 49, 48, 47, 46, 45, 44, 43, 42, 42, 41, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40, 40,
        39, 39, 38, 37, 36, 35, 34, 32, 31, 29, 27, 26, 24, 23, 21, 20, 19, 19, 18, 18, 18, 18, 18,
        19, 19, 20, 20, 21, 22, 22, 23, 23, 24, 24, 24, 23, 23, 23, 22, 22, 21, 20, 20, 19, 19, 18,
        18, 18, 18, 18, 18, 18, 18, 18, 18, 19, 19, 20, 21, 22, 23, 24, 25, 27, 28, 30, 32, 33, 35,
        37, 39, 40, 41, 42, 43, 44, 44, 44, 44, 43, 42, 42, 41, 40, 39, 39, 38, 38, 38, 38, 39, 40,
        41, 42, 43, 44, 45, 47, 48, 49, 50, 50, 51, 51, 51, 51, 50, 50, 49, 48, 47, 46, 45, 44, 44,
        43, 42, 41, 41, 40, 40, 39, 38, 38, 37, 36, 35, 34, 33, 32, 31, 30, 29, 28, 27, 27, 26, 26,
        26, 26, 26, 27, 27, 27, 28, 28, 28, 28, 28, 28, 27, 26, 24, 22, 20, 18, 16, 14, 12, 10, 8,
        7, 6, 5, 5, 5, 5, 6, 7, 8, 9, 10, 12, 13, 13, 14, 15, 15, 15, 15, 15, 15, 15, 16,
        16, 17, 18, 19, 21, 23, 25, 27, 29, 32, 34, 36, 38, 40, 41, 42, 43, 44, 44, 45, 45, 44, 44,
        44, 43, 43, 43, 42, 42, 41, 41, 41, 40, 40, 39, 38, 38, 37, 37, 36, 36, 36, 36, 36, 37, 38,
        38, 39, 41, 42, 43, 44, 44, 45, 45, 45, 44, 44, 42, 41, 39, 38, 36, 34, 32, 31, 29, 28, 28,
        27, 27, 27, 27, 28, 28, 29, 29, 29, 30, 29, 29, 29, 28, 27, 26, 25, 23, 22, 21, 20, 20, 19,
        19, 18, 18, 19, 19, 19, 20, 20, 20, 21, 21, 21, 22, 22, 22, 22, 22, 22, 23, 23, 23, 24, 24,
        24, 25, 25, 25, 25, 25, 25, 25, 25, 25, 25, 24, 24, 24, 25, 25, 26, 27, 28, 29, 30, 32, 33,
        35, 36, 38, 39, 41, 42, 43, 44, 45, 46, 46, 47, 48, 49, 49, 50, 51, 52, 53, 53, 54, 54, 54,
        54, 54, 54, 53, 52, 50, 49, 47, 46, 44, 43, 42, 40, 40, 39, 39, 39, 39, 40, 40, 41, 42, 43,
        43, 44, 44, 44, 44, 44, 43, 42, 41, 40, 39, 38, 36, 35, 34, 33, 32, 32, 31, 30, 29, 29, 28,
        28, 27, 26, 25, 25, 24, 23, 22, 21, 21, 20, 19, 19, 19, 18, 18, 18, 18, 18, 17, 17, 17, 16,
        16, 15, 15, 14, 14, 14, 13, 13, 13, 13, 14, 14, 15, 16, 17, 18, 19, 21, 22, 23, 24, 26, 27,
        27, 28, 29, 30, 30, 31, 32, 32, 33, 34, 35, 35, 36, 38, 39, 40, 41, 42, 43, 44, 45, 45, 46,
        46, 46, 46, 46, 46, 46, 45, 45, 44, 44, 43, 43, 43, 43, 43, 43, 43, 43, 44, 44, 44, 45, 45,
        45, 46, 46, 46, 45, 45, 45, 44, 44, 43, 42, 41, 41, 40, 39, 37, 36, 35, 34, 32, 31, 30, 28,
        27, 25, 24, 23, 21, 20, 19, 19, 18, 18, 18, 18, 18, 19, 19, 20, 21, 21, 22, 22, 23, 23, 23,
        22, 22, 21, 20, 19, 18, 18, 17, 16, 16, 16, 16, 17, 17, 18, 19, 20, 22, 23, 24, 25, 26, 26,
        27, 27, 27, 27, 27, 27, 27, 27, 27, 28, 28, 29, 30, 31, 33, 34, 36, 37, 39, 40, 41, 41, 42,
        42, 42, 42, 41, 40, 40, 39, 38, 38, 37, 37, 37, 38, 38, 39, 40, 41, 42, 43, 44, 45, 46, 47,
        48, 48, 49, 49, 49, 49, 49, 50, 50, 50, 50, 50, 50, 50, 50, 50, 50, 49, 49, 48, 47, 46, 45,
        44, 42, 41, 40, 38, 37, 36, 35, 34, 33, 33, 33, 32, 32, 32, 32, 32, 31, 31, 30, 30, 29, 28,
        27, 26, 25, 23, 22, 21, 20, 18, 18, 17, 16, 16, 16, 16, 17, 17, 18, 19, 19, 20, 21, 22, 23,
        24, 24, 25, 25, 25, 26, 26, 26, 26, 26, 27, 27, 27, 28, 28, 29, 30, 30, 31, 32, 32, 33, 33,
        34, 34, 34, 34, 34, 34, 34, 34, 33, 34, 34, 34, 35, 36, 37, 38, 39, 41, 42, 43, 45, 46, 47,
        48, 49, 50, 50, 51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 51, 52, 52, 51, 51, 51, 50, 50, 49,
        47, 46, 45, 43, 42, 40, 39, 38, 37, 36, 36, 36, 36, 36, 36, 36, 37, 37, 37, 37, 37, 36, 35,
        34, 33, 32, 31, 29, 28, 27, 27, 26, 26, 26, 26, 26, 27, 27, 27, 28, 28, 28, 27, 27, 26, 25,
        24, 23, 22, 22, 21, 20, 20, 20, 21, 21, 22, 23, 24, 25, 26, 26, 27, 28, 28, 28, 28, 28, 28,
        27, 27, 26, 26, 26, 26, 26, 27, 27, 28, 29, 30, 30, 31, 32, 33, 34, 35, 36, 37, 37, 38, 39,
        40, 41, 42, 43, 44, 45, 46, 46, 47, 47, 47, 46, 46, 45, 44, 43, 42, 40, 39, 39, 38, 38, 38,
        38, 39, 41, 42, 44, 45, 47, 49, 50, 51, 52, 52, 52, 52, 51, 50, 48, 46, 45, 43, 42, 40, 39,
        38, 38, 37, 37, 37, 37, 38, 38, 38, 38, 38, 38, 37, 37, 36, 36, 35, 34, 34, 33, 32, 32, 31,
        31, 30, 30, 29, 28, 27, 26, 24, 22, 20, 18, 16, 14, 12, 10, 9, 8, 7, 7, 7, 7, 8, 9,
        10, 12, 13, 15, 16, 17, 18, 19, 20, 20, 21, 21, 21, 21, 21, 21, 21, 22, 23, 24, 25, 26, 27,
        29, 30, 32, 33, 35, 36, 38, 39, 40, 42, 43, 44, 45, 46, 46, 47, 48, 49, 49, 49, 49, 49, 49,
        48, 48, 47, 45, 44, 42, 41, 40, 38, 37, 36, 36, 36, 36, 36, 37, 39, 40, 42, 43, 45, 47, 48,
        49, 50, 50, 50, 50, 49, 47, 46, 43, 41, 39, 36, 34, 32, 29, 28, 26, 25, 24, 24, 23, 23, 24,
        24, 25, 25, 26, 26, 26, 26, 26, 26, 25, 24, 23, 22, 21, 19, 18, 17, 16, 15, 14, 13, 13, 13,
        13, 14, 14, 15, 16, 17, 17, 18, 19, 19, 20, 20, 20, 20, 20, 20, 20, 20, 20, 21, 21, 21, 22,
        23, 24, 25, 26, 27, 28, 29, 30, 31, 31, 32, 32, 32, 33, 33, 33, 33, 33, 33, 34, 34, 35, 36,
        38, 39, 40, 42, 43, 45, 46, 47, 48, 48, 48, 48, 48, 47, 47, 46, 45, 44, 43, 42, 42, 41, 41,
        41, 41, 42, 42, 42, 43, 43, 44, 44, 44, 43, 43, 43, 42, 41, 40, 40, 39, 38, 38, 38, 38, 38,
        38, 38, 39, 39, 39, 39, 39, 39, 38, 37, 35, 33, 31, 29, 27, 25, 23, 21, 19, 18, 18, 17, 17,
        18, 19, 20, 21, 22, 23, 23, 24, 24, 24, 23, 22, 21, 19, 17, 16, 14, 13, 12, 11, 11, 11, 11,
        12, 14, 15, 17, 19, 21, 23, 24, 25, 26, 27, 28, 28, 28, 28, 28, 28, 29, 29, 30, 31, 33, 35,
        36, 38, 40, 42, 43, 44, 45, 46, 46, 45, 45, 44, 42, 41, 40, 39, 39, 38, 39, 39, 40, 42, 44,
        46, 48, 50, 52, 53, 55, 56, 56, 56, 56, 55, 54, 52, 51, 49, 47, 46, 45, 44, 44, 44, 44, 44,
        45, 45, 46, 47, 47, 47, 47, 47, 46, 46, 45, 43, 42, 41, 40, 39, 37, 37, 36, 35, 34, 34, 33,
        33, 32, 31, 30, 29, 27, 26, 25, 23, 22, 20, 19, 19, 18, 18, 18, 18, 19, 19, 20, 21, 22, 22,
        23, 23, 23, 22, 21, 21, 20, 19, 18, 17, 16, 16, 16, 17, 18, 19, 21, 23, 26, 28, 30, 33, 35,
        37, 38, 39, 40, 40, 40, 40, 39, 39, 38, 37, 37, 36, 36, 36, 36, 37, 37, 38, 39, 40, 41, 42,
        43, 44, 44, 45, 45, 45, 45, 45, 45, 44, 44, 44, 43, 43, 43, 42, 42, 42, 42, 43, 43, 43, 44,
        44, 45, 46, 47, 48, 49, 50, 51, 52, 53, 53, 53, 53, 53, 52, 51, 50, 48, 46, 43, 40, 37, 34,
        32, 29, 26, 24, 22, 20, 19, 19, 19, 19, 19, 20, 21, 22, 24, 25, 26, 27, 28, 29, 29, 29, 29,
        28, 28, 27, 26, 25, 24, 22, 21, 20, 18, 17, 16, 14, 13, 12, 11, 10, 10, 9, 9, 9, 10, 10,
        11, 13, 14, 16, 17, 19, 21, 23, 24, 26, 27, 28, 29, 29, 30, 30, 30, 30, 31, 31, 31, 32, 32,
        33, 34, 35, 36, 37, 39, 40, 41, 41, 42, 42, 43, 43, 43, 43, 42, 42, 42, 42, 42, 42, 43, 43,
        44, 45, 46, 47, 47, 48, 49, 49, 49, 49, 49, 49, 48, 47, 47, 46, 45, 44, 44, 44, 43, 43, 43,
        43, 43, 43, 43, 43, 43, 42, 41, 40, 39, 37, 36, 34, 33, 31, 30, 29, 28, 27, 27, 27, 27, 27,
        27, 27, 27, 26, 26, 25, 24, 23, 21, 20, 18, 17, 15, 14, 14, 13, 13, 14, 15, 16, 18, 20, 21,
        23, 25, 27, 28, 29, 30, 30, 29, 29, 28, 27, 25, 24, 23, 22, 22, 21, 21, 22, 23, 24, 25, 26,
        27, 29, 30, 31, 31, 32, 32, 32, 32, 31, 31, 30, 30, 30, 29,
    };
}
