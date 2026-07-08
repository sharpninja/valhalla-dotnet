namespace SharpNinja.Valhalla;

/// <summary>
/// Decodes Valhalla precision-6 encoded route shapes into decimal-degree coordinates.
/// </summary>
public sealed class ValhallaPolylineDecoder : IEncodedPolylineDecoder
{
    private const double Precision = 1_000_000.0;

    public IReadOnlyList<GeoCoordinate> Decode(string? encodedPolyline)
    {
        if (string.IsNullOrEmpty(encodedPolyline))
        {
            return Array.Empty<GeoCoordinate>();
        }

        var points = new List<GeoCoordinate>();
        var index = 0;
        var latitude = 0;
        var longitude = 0;

        while (index < encodedPolyline.Length)
        {
            latitude += DecodeNextValue(encodedPolyline, ref index);
            longitude += DecodeNextValue(encodedPolyline, ref index);
            points.Add(new GeoCoordinate(latitude / Precision, longitude / Precision));
        }

        return points;
    }

    private static int DecodeNextValue(string value, ref int index)
    {
        var result = 0;
        var shift = 0;

        while (true)
        {
            if (index >= value.Length)
            {
                throw new FormatException("Encoded polyline ended in the middle of a coordinate value.");
            }

            var b = value[index++] - 63;
            result |= (b & 0x1f) << shift;
            shift += 5;

            if (b < 0x20)
            {
                break;
            }
        }

        return (result & 1) == 1 ? ~(result >> 1) : result >> 1;
    }
}
