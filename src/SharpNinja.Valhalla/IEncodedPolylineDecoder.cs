namespace SharpNinja.Valhalla;

public interface IEncodedPolylineDecoder
{
	IReadOnlyList<GeoCoordinate> Decode(string? encodedPolyline);
}
