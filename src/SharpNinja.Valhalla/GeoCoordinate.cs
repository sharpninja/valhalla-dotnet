namespace SharpNinja.Valhalla;

/// <summary>
/// Plain decimal-degree coordinate. Latitude in [-90, 90], longitude in
/// [-180, 180]. Raw decimal degrees only; DM/DMS conversion is out of scope.
/// </summary>
public sealed record GeoCoordinate(double Latitude, double Longitude);
