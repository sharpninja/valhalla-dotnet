namespace SharpNinja.Valhalla.Tests;

public sealed class ValhallaPolylineDecoderTests
{
    [Fact]
    public void Decode_KnownPrecision6_RoundTripsWithin1e6()
    {
        var sut = new ValhallaPolylineDecoder();

        var points = sut.Decode("aizlhA`slgdFa{geCbzwl@ckwzCt`{nI");

        Assert.Equal(3, points.Count);
        Assert.Equal(38.500001, points[0].Latitude, 6);
        Assert.Equal(-120.200001, points[0].Longitude, 6);
        Assert.Equal(40.700002, points[1].Latitude, 6);
        Assert.Equal(-120.950003, points[1].Longitude, 6);
        Assert.Equal(43.252004, points[2].Latitude, 6);
        Assert.Equal(-126.453006, points[2].Longitude, 6);
    }

    [Fact]
    public void Decode_NullOrEmpty_ReturnsEmpty()
    {
        var sut = new ValhallaPolylineDecoder();

        Assert.Empty(sut.Decode(null));
        Assert.Empty(sut.Decode(string.Empty));
    }

    [Fact]
    public void Decode_Truncated_ThrowsFormatException()
    {
        var sut = new ValhallaPolylineDecoder();

        Assert.Throws<FormatException>(() => sut.Decode("_"));
    }
}