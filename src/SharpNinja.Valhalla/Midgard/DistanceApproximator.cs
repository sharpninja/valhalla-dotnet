// Faithful C# port of Valhalla midgard DistanceApproximator.
// Source: valhalla/midgard/distanceapproximator.h
// Self-contained engine port: does NOT reuse other TruckMate types.
// Generic over the lat/lng point type via the IGeoPoint interface so it works with
// PointLL (GeoPoint<double>) just like the C++ template DistanceApproximator<PointT>.

using System.Numerics;

namespace SharpNinja.Valhalla.Midgard;

/// <summary>
/// Minimal interface describing the lat/lng accessors the
/// <see cref="DistanceApproximator{TPoint,TPrecision}"/> needs. Mirrors the implicit
/// requirements of the C++ template parameter <c>PointT</c> (it must expose <c>lat()</c>
/// and <c>lng()</c>).
/// </summary>
/// <typeparam name="TPrecision">Numeric precision type (float or double).</typeparam>
public interface IGeoPoint<TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    /// <summary>Gets the longitude (degrees).</summary>
    TPrecision Lng { get; }

    /// <summary>Gets the latitude (degrees).</summary>
    TPrecision Lat { get; }
}

/// <summary>
/// Provides distance approximation in latitude, longitude space. Approximates
/// distance in meters between two points. This method is more efficient
/// than using spherical distance calculations. It computes an approximate
/// distance using the Pythagorean theorem with the meters of latitude change
/// (exact) and the meters of longitude change at the "test point". Longitude
/// is inexact since meters per degree of longitude changes with latitude.
/// This approximation has very little error (less than 1%) if the positions
/// are close to one another (within several hundred meters). Error
/// increases at high (near polar) latitudes. This method will not work if the
/// points cross 180 degrees longitude.
/// </summary>
/// <typeparam name="TPoint">Lat/lng point type implementing <see cref="IGeoPoint{TPrecision}"/>.</typeparam>
/// <typeparam name="TPrecision">Numeric precision type (float or double).</typeparam>
public sealed class DistanceApproximator<TPoint, TPrecision>
    where TPoint : IGeoPoint<TPrecision>
    where TPrecision : IFloatingPointIeee754<TPrecision>, IMinMaxValue<TPrecision>
{
    private TPrecision _centerLat;
    private TPrecision _centerLng;
    private TPrecision _mLngScale;
    private TPrecision _mPerLngDegree;

    /// <summary>
    /// Constructor. Sets the test point. This method is used when a distance is to be
    /// checked for a series of positions relative to a single point. This precalculates
    /// the meters per degree of longitude.
    /// </summary>
    /// <param name="ll">Latitude, longitude of the test point (degrees).</param>
    public DistanceApproximator(TPoint ll)
    {
        _centerLat = ll.Lat;
        _centerLng = ll.Lng;
        _mLngScale = LngScalePerLat(_centerLat);
        _mPerLngDegree = _mLngScale * MetersPerDegreeLat;
    }

    private static TPrecision MetersPerDegreeLat => TPrecision.CreateChecked(Constants.MetersPerDegreeLat);

    /// <summary>
    /// Sets the test point. This method is used when a distance is to be checked for a
    /// series of positions relative to a single point. This precalculates the meters per
    /// degree of longitude.
    /// </summary>
    /// <param name="ll">Latitude, longitude of the test point (degrees).</param>
    public void SetTestPoint(TPoint ll)
    {
        _centerLat = ll.Lat;
        _centerLng = ll.Lng;
        _mLngScale = LngScalePerLat(_centerLat);
        _mPerLngDegree = _mLngScale * MetersPerDegreeLat;
    }

    /// <summary>Getter for lng scale. Returns the distance scale for lng at this point's latitude.</summary>
    public TPrecision GetLngScale() => _mLngScale;

    /// <summary>
    /// Approximates the arc distance between the supplied position and the current test point.
    /// It uses the pythagorean theorem with meters per latitude and longitude degree. Assumes
    /// the number of meters per degree of longitude at the test point latitude.
    /// </summary>
    /// <param name="ll">Latitude, longitude of the point (degrees).</param>
    /// <returns>
    /// Returns the squared distance in meters by using pythagorean theorem. Squared distance is
    /// returned for more efficient searching (avoids sqrt).
    /// </returns>
    public TPrecision DistanceSquared(TPoint ll)
        => Sqr((ll.Lat - _centerLat) * MetersPerDegreeLat)
           + Sqr((ll.Lng - _centerLng) * _mPerLngDegree);

    /// <summary>
    /// Approximates arc distance between 2 lat,lng positions using meters per latitude and
    /// longitude degree. Uses the mid latitude of the 2 positions to estimate the number of
    /// meters per degree of longitude.
    /// </summary>
    /// <param name="ll1">First point (lat,lng).</param>
    /// <param name="ll2">Second point (lat,lng).</param>
    /// <returns>Returns the approximate distance squared (in meters).</returns>
    public static TPrecision DistanceSquared(TPoint ll1, TPoint ll2)
    {
        TPrecision latm = (ll1.Lat - ll2.Lat) * MetersPerDegreeLat;
        TPrecision lngm = (ll1.Lng - ll2.Lng)
                          * MetersPerLngDegree((ll1.Lat + ll2.Lat) * TPrecision.CreateChecked(0.5));
        return (latm * latm) + (lngm * lngm);
    }

    /// <summary>
    /// Gets the number of meters per degree of longitude for a specified latitude. While the
    /// number of meters per degree of latitude is constant, the number of meters per degree of
    /// longitude varies: it has a maximum at the equator and lessens as latitude approaches the
    /// poles.
    /// </summary>
    /// <param name="lat">Latitude in degrees.</param>
    /// <returns>Returns the number of meters per degree of longitude.</returns>
    public static TPrecision MetersPerLngDegree(TPrecision lat)
        => LngScalePerLat(lat) * MetersPerDegreeLat;

    /// <summary>
    /// Gets the distance scale needed when computing units of longitude at a certain latitude.
    /// </summary>
    /// <param name="lat">Latitude in degrees.</param>
    /// <returns>Returns the scale to use for longitude at this degree of latitude.</returns>
    public static TPrecision LngScalePerLat(TPrecision lat)
    {
        // C++ uses cosf (float-precision cosine) regardless of PrecisionT and kRadPerDeg (float).
        float radPerDeg = Constants.RadPerDeg;
        float latF = float.CreateTruncating(lat);
        float scale = MathF.Cos(latF * radPerDeg);
        return TPrecision.CreateTruncating(scale);
    }

    private static TPrecision Sqr(TPrecision v) => v * v;
}
