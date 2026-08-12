using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Baldr;

/// <summary>
/// Four-byte Valhalla 3.8.3 discretized edge bounding circle.
/// </summary>
public readonly record struct DiscretizedBoundingCircle
{
    public const int SizeOf = sizeof(uint);
    public const uint CoordinateBits = 13;
    public const uint RadiusBits = 32 - (CoordinateBits * 2);
    public const uint RadiusCount = 1u << (int)RadiusBits;
    public const uint MaxOffsetValue = (1u << (int)CoordinateBits) - 1;
    public const double MaxCircleRadiusMeters = 2500;
    public const double MaxCircleBoundingBoxMeters =
        (2 * 1.41421356 * MaxCircleRadiusMeters) + 1;

    private const uint CoordinateMask = MaxOffsetValue;
    private const double MaxOffsetMeters =
        (0.05 * Constants.MetersPerDegreeLat / 2) + MaxCircleRadiusMeters;
    private const double OffsetIncrement =
        MaxOffsetMeters / (1 << ((int)CoordinateBits - 1));

    private static readonly double[] BoundingCircleRadii =
    [
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 13, 15, 17,
        18, 20, 23, 25, 27, 30, 35, 40, 43, 45, 50, 55, 60,
        65, 70, 75, 80, 85, 90, 95, 100, 110, 120, 130, 140, 150,
        160, 170, 180, 190, 200, 210, 220, 230, 240, 250, 275, 300, 325,
        350, 375, 400, 500, 550, 600, 700, 800, 1000, 1500, 2000, 2500,
    ];

    private readonly uint _rawValue;

    /// <summary>Creates the official impossible-circle sentinel.</summary>
    public DiscretizedBoundingCircle()
        : this(MaxOffsetValue | (MaxOffsetValue << (int)CoordinateBits))
    {
    }

    /// <summary>Creates a discretized circle relative to a bin center.</summary>
    public DiscretizedBoundingCircle(
        PointLL binCenter,
        PointLL circleCenter,
        double radiusMeters)
    {
        var approximation = new DistanceApproximator<PointLL, double>(binCenter);
        double xMeters =
            (circleCenter.Lng - binCenter.Lng) *
            approximation.GetLngScale() *
            Constants.MetersPerDegreeLat;
        double yMeters =
            (circleCenter.Lat - binCenter.Lat) *
            Constants.MetersPerDegreeLat;

        if (Math.Abs(xMeters) >= MaxOffsetMeters - 0.5 ||
            Math.Abs(yMeters) >= MaxOffsetMeters - 0.5)
        {
            _rawValue = Invalid.RawValue;
            return;
        }

        uint xOffset = unchecked(
            (uint)(((xMeters + MaxOffsetMeters) / OffsetIncrement) + 0.5));
        uint yOffset = unchecked(
            (uint)(((yMeters + MaxOffsetMeters) / OffsetIncrement) + 0.5));

        double discretizedY =
            (((double)yOffset / MaxOffsetValue) * MaxOffsetMeters * 2) -
            MaxOffsetMeters;
        double discretizedX =
            (((double)xOffset / MaxOffsetValue) * MaxOffsetMeters * 2) -
            MaxOffsetMeters;
        var discretizedCenter = new PointLL(
            (discretizedX /
             (approximation.GetLngScale() * Constants.MetersPerDegreeLat)) +
            binCenter.Lng,
            (discretizedY / Constants.MetersPerDegreeLat) + binCenter.Lat);

        double conservativeRadius =
            radiusMeters + circleCenter.Distance(discretizedCenter);
        int radiusIndex = Array.FindIndex(
            BoundingCircleRadii,
            radius => conservativeRadius <= radius);
        if (radiusIndex < 0)
        {
            _rawValue = Invalid.RawValue;
            return;
        }

        _rawValue =
            (yOffset & CoordinateMask) |
            ((xOffset & CoordinateMask) << (int)CoordinateBits) |
            ((uint)radiusIndex << (int)(CoordinateBits * 2));
    }

    private DiscretizedBoundingCircle(uint rawValue)
    {
        _rawValue = rawValue;
    }

    public static DiscretizedBoundingCircle Invalid => new();

    public uint RawValue => _rawValue;

    public uint YOffset => _rawValue & CoordinateMask;

    public uint XOffset =>
        (_rawValue >> (int)CoordinateBits) & CoordinateMask;

    public uint RadiusIndex =>
        _rawValue >> (int)(CoordinateBits * 2);

    public bool IsValid =>
        !(XOffset == MaxOffsetValue &&
          YOffset == MaxOffsetValue &&
          RadiusIndex == 0);

    public static DiscretizedBoundingCircle FromRaw(uint rawValue)
        => new(rawValue);

    /// <summary>Expands the stored center and radius relative to the supplied bin center.</summary>
    public (PointLL Center, double RadiusMeters) Get(PointLL binCenter)
    {
        double yOffsetMeters =
            (((double)YOffset / MaxOffsetValue) * MaxOffsetMeters * 2) -
            MaxOffsetMeters;
        double xOffsetMeters =
            (((double)XOffset / MaxOffsetValue) * MaxOffsetMeters * 2) -
            MaxOffsetMeters;
        double metersPerLongitudeDegree =
            DistanceApproximator<PointLL, double>.MetersPerLngDegree(binCenter.Lat);
        var center = new PointLL(
            (xOffsetMeters / metersPerLongitudeDegree) + binCenter.Lng,
            (yOffsetMeters / Constants.MetersPerDegreeLat) + binCenter.Lat);

        return (center, BoundingCircleRadii[RadiusIndex]);
    }
}
