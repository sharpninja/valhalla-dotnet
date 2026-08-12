using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Elevation;

internal readonly record struct ElevationGrade(
    double Weighted,
    double MaximumUp,
    double MaximumDown,
    double Mean);

internal readonly record struct EdgeElevationComputation(
    float MeanElevation,
    IReadOnlyList<sbyte> EncodedElevation,
    uint ForwardWeightedGrade,
    uint ReverseWeightedGrade,
    float ForwardMaximumUp,
    float ForwardMaximumDown,
    float ReverseMaximumUp,
    float ReverseMaximumDown,
    bool EncodingClamped);

internal static class ValhallaElevationAlgorithms
{
    private const double PostingInterval = 60.0;
    private const double MinimumGradeInterval = 10.0;
    private const double RadPerMeter = 1.0 / 6_378_160.187;

    public static EdgeElevationComputation Compute(
        IElevationSampleSource source,
        IReadOnlyList<PointLL> shape,
        uint length,
        bool interpolateEndpoints)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(shape);
        if (shape.Count < 2)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidGraphTile,
                "An edge shape must contain at least two coordinates");
        }

        List<PointLL> gradePoints = ResampleSphericalPolyline(
            shape,
            PostingInterval);
        gradePoints.Add(shape[^1]);

        double[] gradeHeights;
        if (interpolateEndpoints)
        {
            double first = source.Sample(gradePoints[0]);
            double last = source.Sample(gradePoints[^1]);
            gradeHeights = new double[gradePoints.Count];
            gradeHeights[0] = first;
            float delta = (float)((last - first) / gradeHeights.Length);
            for (int index = 1; index < gradeHeights.Length; index++)
            {
                gradeHeights[index] = gradeHeights[index - 1] + delta;
            }
        }
        else
        {
            gradeHeights = source.SampleAll(gradePoints);
        }

        ElevationGrade forward = WeightedGrade(gradeHeights, PostingInterval);
        ElevationGrade reverse;
        if (length < MinimumGradeInterval)
        {
            forward = new ElevationGrade(0.0, 0.0, 0.0, forward.Mean);
            reverse = forward;
        }
        else
        {
            double[] reverseHeights = (double[])gradeHeights.Clone();
            Array.Reverse(reverseHeights);
            reverse = WeightedGrade(reverseHeights, PostingInterval);
        }

        bool encodingClamped = false;
        IReadOnlyList<sbyte> encoded = interpolateEndpoints
            ? EncodeInterpolatedElevation(
                source,
                shape,
                length,
                ref encodingClamped)
            : EncodeSampledElevation(
                source,
                shape,
                length,
                ref encodingClamped);

        return new EdgeElevationComputation(
            (float)forward.Mean,
            encoded,
            ToWeightedGradeFactor(forward.Weighted),
            ToWeightedGradeFactor(reverse.Weighted),
            (float)forward.MaximumUp,
            (float)forward.MaximumDown,
            (float)reverse.MaximumUp,
            (float)reverse.MaximumDown,
            encodingClamped);
    }

    private static List<sbyte> EncodeSampledElevation(
        IElevationSampleSource source,
        IReadOnlyList<PointLL> shape,
        uint length,
        ref bool encodingClamped)
    {
        uint count = ElevationEncoding.EncodedElevationCount(length) + 2;
        List<PointLL> points = UniformResampleSphericalPolyline(
            shape,
            length,
            count);
        return EncodeElevation(
            source.SampleAll(points),
            ref encodingClamped);
    }

    private static List<sbyte> EncodeInterpolatedElevation(
        IElevationSampleSource source,
        IReadOnlyList<PointLL> shape,
        uint length,
        ref bool encodingClamped)
    {
        double interval = ElevationEncoding.SamplingInterval(length);
        uint count = checked((uint)(length / interval) + 1);
        double[] heights = new double[count];
        double first = source.Sample(shape[0]);
        double last = source.Sample(shape[^1]);
        heights[0] = first;
        float delta = (float)((last - first) / count);
        for (int index = 1; index < heights.Length - 1; index++)
        {
            heights[index] = heights[index - 1] + delta;
        }

        heights[^1] = last;
        return EncodeElevation(heights, ref encodingClamped);
    }

    private static ElevationGrade WeightedGrade(
        IReadOnlyList<double> heights,
        double intervalDistance)
    {
        double totalGrade = 0.0;
        double totalWeight = 0.0;
        double maximumUp = 0.0;
        double maximumDown = 0.0;
        int validCount = 0;
        double totalElevation = 0.0;
        if (heights[0] != HgtElevationSource.NoDataValue)
        {
            totalElevation += heights[0];
            validCount++;
        }

        double scale = 100.0 / intervalDistance;
        for (int index = 1; index < heights.Count; index++)
        {
            double grade =
                heights[index] == HgtElevationSource.NoDataValue ||
                heights[index - 1] == HgtElevationSource.NoDataValue
                    ? 0.0
                    : (heights[index] - heights[index - 1]) * scale;
            if (heights[index] != HgtElevationSource.NoDataValue)
            {
                totalElevation += heights[index];
                validCount++;
            }

            maximumUp = Math.Max(grade, maximumUp);
            maximumDown = Math.Min(grade, maximumDown);
            grade = Math.Clamp(grade, -10.0, 15.0);
            double weight = 1.0 + (grade / (grade > 0.0 ? 7.0 : 17.0));
            totalGrade += grade * weight;
            totalWeight += weight;
        }

        return new ElevationGrade(
            totalGrade / totalWeight,
            maximumUp,
            maximumDown,
            validCount == 0
                ? HgtElevationSource.NoDataValue
                : totalElevation / validCount);
    }

    private static List<sbyte> EncodeElevation(
        IReadOnlyList<double> elevations,
        ref bool error)
    {
        var encoding = new List<sbyte>(Math.Max(0, elevations.Count - 2));
        if (elevations[0] == HgtElevationSource.NoDataValue ||
            elevations[^1] == HgtElevationSource.NoDataValue)
        {
            return encoding;
        }

        int prior = ToFixedPrecision(elevations[0]);
        for (int index = 1; index < elevations.Count - 1; index++)
        {
            if (elevations[index] == HgtElevationSource.NoDataValue)
            {
                encoding.Add(0);
                continue;
            }

            int value = ToFixedPrecision(elevations[index]);
            int delta = value - prior;
            if (delta > sbyte.MaxValue)
            {
                error |= delta > 256;
                delta = sbyte.MaxValue;
                prior += sbyte.MaxValue;
            }
            else if (delta < sbyte.MinValue)
            {
                error |= delta < -256;
                delta = sbyte.MinValue;
                prior += sbyte.MinValue;
            }
            else
            {
                prior = value;
            }

            encoding.Add((sbyte)delta);
        }

        return encoding;
    }

    private static List<PointLL> ResampleSphericalPolyline(
        IReadOnlyList<PointLL> polyline,
        double resolution)
    {
        var resampled = new List<PointLL> { polyline[0] };
        double remaining = resolution * RadPerMeter;
        PointLL last = polyline[0];
        for (int pointIndex = 1; pointIndex < polyline.Count; pointIndex++)
        {
            PointLL point = polyline[pointIndex];
            double distance = AngularDistance(last, point);
            if (double.IsNaN(distance))
            {
                distance = 0.0;
            }

            while (distance > remaining)
            {
                last = Interpolate(last, point, distance, remaining);
                resampled.Add(last);
                distance -= remaining;
                remaining = resolution * RadPerMeter;
            }

            remaining -= distance;
            last = point;
        }

        return resampled;
    }

    private static List<PointLL> UniformResampleSphericalPolyline(
        IReadOnlyList<PointLL> polyline,
        double length,
        uint count)
    {
        if (count == 2)
        {
            return [polyline[0], polyline[^1]];
        }

        double sampleDistance = (length / (count - 1)) * RadPerMeter;
        var resampled = new List<PointLL>(checked((int)count)) { polyline[0] };
        double remaining = sampleDistance;
        PointLL last = polyline[0];
        for (int pointIndex = 1; pointIndex < polyline.Count; pointIndex++)
        {
            PointLL point = polyline[pointIndex];
            double distance = AngularDistance(last, point);
            if (double.IsNaN(distance))
            {
                continue;
            }

            while (remaining < distance)
            {
                last = Interpolate(last, point, distance, remaining);
                resampled.Add(last);
                distance -= remaining;
                remaining = sampleDistance;
            }

            remaining -= distance;
            last = point;
        }

        int requiredCount = checked((int)count);
        if (resampled.Count < requiredCount)
        {
            resampled.Add(polyline[^1]);
        }
        else if (resampled.Count == requiredCount)
        {
            resampled[^1] = polyline[^1];
        }
        else
        {
            resampled.RemoveRange(requiredCount, resampled.Count - requiredCount);
            resampled[^1] = polyline[^1];
        }

        if (resampled.Count != requiredCount)
        {
            throw new ElevationDatasetBuildException(
                ElevationDatasetFailureCode.InvalidGraphTile,
                $"Uniform resampling produced {resampled.Count} points; expected {requiredCount}");
        }

        return resampled;
    }

    private static PointLL Interpolate(
        PointLL first,
        PointLL second,
        double distance,
        double remaining)
    {
        double longitude1 = first.First * -Constants.RadPerDegD;
        double latitude1 = first.Second * Constants.RadPerDegD;
        double longitude2 = second.First * -Constants.RadPerDegD;
        double latitude2 = second.Second * Constants.RadPerDegD;
        double sineDistance = Math.Sin(distance);
        double a = Math.Sin(distance - remaining) / sineDistance;
        double b = Math.Sin(remaining) / sineDistance;
        double aCosineLatitude1 = a * Math.Cos(latitude1);
        double bCosineLatitude2 = b * Math.Cos(latitude2);
        double x =
            (aCosineLatitude1 * Math.Cos(longitude1)) +
            (bCosineLatitude2 * Math.Cos(longitude2));
        double y =
            (aCosineLatitude1 * Math.Sin(longitude1)) +
            (bCosineLatitude2 * Math.Sin(longitude2));
        double z =
            (a * Math.Sin(latitude1)) +
            (b * Math.Sin(latitude2));
        return new PointLL(
            Math.Atan2(y, x) * -Constants.DegPerRadD,
            Math.Atan2(z, Math.Sqrt((x * x) + (y * y))) * Constants.DegPerRadD);
    }

    private static double AngularDistance(PointLL first, PointLL second)
    {
        if (first.Equals(second))
        {
            return 0.0;
        }

        double longitude2 = second.First * -Constants.RadPerDegD;
        double latitude2 = second.Second * Constants.RadPerDegD;
        return Math.Acos(
            (Math.Sin(first.Second * Constants.RadPerDegD) * Math.Sin(latitude2)) +
            (Math.Cos(first.Second * Constants.RadPerDegD) * Math.Cos(latitude2) *
             Math.Cos((first.First * -Constants.RadPerDegD) - longitude2)));
    }

    private static int ToFixedPrecision(double value) =>
        (int)((value * ElevationEncoding.InvElevationPrecision) + 0.5);

    private static uint ToWeightedGradeFactor(double weightedGrade) =>
        (uint)((weightedGrade * 0.6) + 6.5);
}
