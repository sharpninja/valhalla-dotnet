using System.Buffers;

namespace SharpNinja.Valhalla.Midgard;

internal static class MinimumBoundingCircle
{
    internal static (PointLL Center, double RadiusMeters)? Compute(
        IReadOnlyList<PointLL> points,
        double distanceThresholdMeters)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
        {
            return null;
        }

        double minLongitude = points[0].Lng;
        double maxLongitude = points[0].Lng;
        double minLatitude = points[0].Lat;
        double maxLatitude = points[0].Lat;
        for (int index = 1; index < points.Count; index++)
        {
            PointLL point = points[index];
            minLongitude = Math.Min(minLongitude, point.Lng);
            maxLongitude = Math.Max(maxLongitude, point.Lng);
            minLatitude = Math.Min(minLatitude, point.Lat);
            maxLatitude = Math.Max(maxLatitude, point.Lat);
        }

        if (DistanceMeters(
                minLongitude,
                minLatitude,
                maxLongitude,
                maxLatitude) > distanceThresholdMeters)
        {
            return null;
        }

        var projection = new AzimuthalEquidistantProjection(
            (minLongitude + maxLongitude) * 0.5,
            (minLatitude + maxLatitude) * 0.5);
        if (points.Count <= 128)
        {
            Span<PlanePoint> projected = stackalloc PlanePoint[points.Count];
            return ProjectAndCompute(points, projection, projected);
        }

        PlanePoint[] rented = ArrayPool<PlanePoint>.Shared.Rent(points.Count);
        try
        {
            return ProjectAndCompute(
                points,
                projection,
                rented.AsSpan(0, points.Count));
        }
        finally
        {
            ArrayPool<PlanePoint>.Shared.Return(rented);
        }
    }

    private static (PointLL Center, double RadiusMeters) ProjectAndCompute(
        IReadOnlyList<PointLL> points,
        AzimuthalEquidistantProjection projection,
        Span<PlanePoint> projected)
    {
        for (int index = 0; index < points.Count; index++)
        {
            projected[index] = projection.Project(points[index]);
        }

        PlaneCircle circle = ComputeProjected(projected);
        return (projection.ProjectInverse(circle.Center), circle.Radius);
    }

    private static PlaneCircle ComputeProjected(ReadOnlySpan<PlanePoint> points)
    {
        PlaneCircle circle = PlaneCircle.Invalid;
        for (int first = 0; first < points.Length; first++)
        {
            PlanePoint firstPoint = points[first];
            if (circle.Contains(firstPoint))
            {
                continue;
            }

            circle = new PlaneCircle(firstPoint, 0);
            for (int second = 0; second < first; second++)
            {
                PlanePoint secondPoint = points[second];
                if (circle.Contains(secondPoint))
                {
                    continue;
                }

                circle = Diameter(firstPoint, secondPoint);
                for (int third = 0; third < second; third++)
                {
                    PlanePoint thirdPoint = points[third];
                    if (circle.Contains(thirdPoint))
                    {
                        continue;
                    }

                    circle = ThroughThree(firstPoint, secondPoint, thirdPoint);
                }
            }
        }

        return circle;
    }

    private static PlaneCircle Diameter(PlanePoint first, PlanePoint second)
    {
        var center = new PlanePoint(
            (first.X + second.X) * 0.5,
            (first.Y + second.Y) * 0.5);
        return new PlaneCircle(center, Math.Sqrt(DistanceSquared(center, first)));
    }

    private static PlaneCircle ThroughThree(
        PlanePoint first,
        PlanePoint second,
        PlanePoint third)
    {
        PlaneCircle best = PlaneCircle.Invalid;
        best = SelectSmallerContainingCircle(
            best,
            Diameter(first, second),
            first,
            second,
            third);
        best = SelectSmallerContainingCircle(
            best,
            Diameter(first, third),
            first,
            second,
            third);
        best = SelectSmallerContainingCircle(
            best,
            Diameter(second, third),
            first,
            second,
            third);

        double determinant =
            (2 * first.X * (second.Y - third.Y)) +
            (2 * second.X * (third.Y - first.Y)) +
            (2 * third.X * (first.Y - second.Y));
        if (Math.Abs(determinant) <= 1e-12)
        {
            return best;
        }

        double firstMagnitude = (first.X * first.X) + (first.Y * first.Y);
        double secondMagnitude = (second.X * second.X) + (second.Y * second.Y);
        double thirdMagnitude = (third.X * third.X) + (third.Y * third.Y);
        var center = new PlanePoint(
            ((firstMagnitude * (second.Y - third.Y)) +
             (secondMagnitude * (third.Y - first.Y)) +
             (thirdMagnitude * (first.Y - second.Y))) /
            determinant,
            ((firstMagnitude * (third.X - second.X)) +
             (secondMagnitude * (first.X - third.X)) +
             (thirdMagnitude * (second.X - first.X))) /
            determinant);
        var circumcircle =
            new PlaneCircle(center, Math.Sqrt(DistanceSquared(center, first)));

        return !best.IsValid || circumcircle.Radius < best.Radius
            ? circumcircle
            : best;
    }

    private static PlaneCircle SelectSmallerContainingCircle(
        PlaneCircle best,
        PlaneCircle candidate,
        PlanePoint first,
        PlanePoint second,
        PlanePoint third) =>
        candidate.Contains(first) &&
        candidate.Contains(second) &&
        candidate.Contains(third) &&
        (!best.IsValid || candidate.Radius < best.Radius)
            ? candidate
            : best;

    private static double DistanceMeters(
        double firstLongitude,
        double firstLatitude,
        double secondLongitude,
        double secondLatitude)
    {
        if (firstLongitude == secondLongitude && firstLatitude == secondLatitude)
        {
            return 0.0;
        }

        double firstLatitudeRadians = firstLatitude * Constants.RadPerDegD;
        double secondLatitudeRadians = secondLatitude * Constants.RadPerDegD;
        double latitudeDelta = secondLatitudeRadians - firstLatitudeRadians;
        double firstLongitudeRadians = firstLongitude * Constants.RadPerDegD;
        double secondLongitudeRadians = secondLongitude * Constants.RadPerDegD;
        double longitudeDelta = secondLongitudeRadians - firstLongitudeRadians;
        double firstCosine = Math.Cos(firstLatitudeRadians);
        double secondCosine = Math.Cos(secondLatitudeRadians);
        double halfLatitudeSine = Math.Sin(latitudeDelta / 2.0);
        double halfLongitudeSine = Math.Sin(longitudeDelta / 2.0);
        double haversine =
            (halfLatitudeSine * halfLatitudeSine) +
            (firstCosine * secondCosine * halfLongitudeSine * halfLongitudeSine);
        haversine = Math.Min(1.0, Math.Max(0.0, haversine));
        double distance = 2.0 * Math.Asin(Math.Sqrt(haversine));
        return Constants.RadEarthMeters * distance;
    }

    private static double DistanceSquared(PlanePoint first, PlanePoint second)
    {
        double x = first.X - second.X;
        double y = first.Y - second.Y;
        return (x * x) + (y * y);
    }

    private readonly record struct PlanePoint(double X, double Y);

    private readonly record struct PlaneCircle(PlanePoint Center, double Radius)
    {
        internal static PlaneCircle Invalid =>
            new(default, double.NegativeInfinity);

        internal bool IsValid => Radius >= 0;

        internal bool Contains(PlanePoint point)
        {
            if (!IsValid)
            {
                return false;
            }

            double tolerance = Math.Max(1e-7, Radius * 1e-12);
            double maximum = Radius + tolerance;
            return DistanceSquared(Center, point) <= maximum * maximum;
        }
    }

    private readonly struct AzimuthalEquidistantProjection
    {
        private readonly double _longitudeRadians;
        private readonly double _latitudeRadians;
        private readonly double _sinLatitude;
        private readonly double _cosLatitude;

        internal AzimuthalEquidistantProjection(
            double longitudeDegrees,
            double latitudeDegrees)
        {
            _longitudeRadians = longitudeDegrees * Constants.RadPerDegD;
            _latitudeRadians = latitudeDegrees * Constants.RadPerDegD;
            _sinLatitude = Math.Sin(_latitudeRadians);
            _cosLatitude = Math.Cos(_latitudeRadians);
        }

        internal PlanePoint Project(PointLL point)
        {
            double longitude = point.Lng * Constants.RadPerDegD;
            double latitude = point.Lat * Constants.RadPerDegD;
            double longitudeDelta = longitude - _longitudeRadians;
            double sinLatitude = Math.Sin(latitude);
            double cosLatitude = Math.Cos(latitude);
            double cosine =
                (_sinLatitude * sinLatitude) +
                (_cosLatitude * cosLatitude * Math.Cos(longitudeDelta));
            double angle = Math.Acos(Math.Clamp(cosine, -1, 1));
            double scale = angle <= 1e-15 ? 1 : angle / Math.Sin(angle);
            double radius = Constants.RadEarthMeters;

            return new PlanePoint(
                radius * scale * cosLatitude * Math.Sin(longitudeDelta),
                radius * scale *
                ((_cosLatitude * sinLatitude) -
                 (_sinLatitude * cosLatitude * Math.Cos(longitudeDelta))));
        }

        internal PointLL ProjectInverse(PlanePoint point)
        {
            double radius = Constants.RadEarthMeters;
            double distance = Math.Sqrt((point.X * point.X) + (point.Y * point.Y));
            if (distance <= 1e-15)
            {
                return new PointLL(
                    _longitudeRadians * Constants.DegPerRadD,
                    _latitudeRadians * Constants.DegPerRadD);
            }

            double angle = distance / radius;
            double sinAngle = Math.Sin(angle);
            double cosAngle = Math.Cos(angle);
            double latitude = Math.Asin(
                (cosAngle * _sinLatitude) +
                ((point.Y * sinAngle * _cosLatitude) / distance));
            double longitude = _longitudeRadians + Math.Atan2(
                point.X * sinAngle,
                (distance * _cosLatitude * cosAngle) -
                (point.Y * _sinLatitude * sinAngle));

            return new PointLL(
                longitude * Constants.DegPerRadD,
                latitude * Constants.DegPerRadD);
        }
    }
}
