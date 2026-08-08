using System.Globalization;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;

namespace SharpNinja.Valhalla.Generation.Transit;

internal static class GtfsModelParser
{
    private const int ScheduleWindowDays = 60;

    public static ParsedGtfsFeed Parse(GtfsFeedData data, DateOnly buildDate)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (string.IsNullOrWhiteSpace(data.Prefix))
        {
            throw Invalid("GTFS feed prefix is empty");
        }

        GtfsTable agencyTable = data.Required("agency.txt");
        agencyTable.RequireColumns("agency_id", "agency_name", "agency_url", "agency_timezone");
        if (agencyTable.Rows.Count == 0)
        {
            throw Invalid("agency.txt contains no agencies");
        }

        var agencies = agencyTable.Rows
            .Select(row => new GtfsAgency(
                agencyTable.Required(row, "agency_id"),
                agencyTable.Required(row, "agency_name"),
                agencyTable.Required(row, "agency_url"),
                agencyTable.Required(row, "agency_timezone")))
            .ToDictionary(agency => agency.Id, StringComparer.Ordinal);

        GtfsTable stopsTable = data.Required("stops.txt");
        stopsTable.RequireColumns("stop_id", "stop_name", "stop_lat", "stop_lon");
        var stops = new Dictionary<string, GtfsStop>(StringComparer.Ordinal);
        foreach (string[] row in stopsTable.Rows)
        {
            string id = stopsTable.Required(row, "stop_id");
            double latitude = ParseDouble(stopsTable.Required(row, "stop_lat"), "stops.txt.stop_lat");
            double longitude = ParseDouble(stopsTable.Required(row, "stop_lon"), "stops.txt.stop_lon");
            if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            {
                throw Invalid($"Stop {id} has coordinates outside WGS84 bounds");
            }

            int locationType = ParseOptionalInt(stopsTable.Optional(row, "location_type"), 0, "stops.txt.location_type");
            var stop = new GtfsStop(
                id,
                stopsTable.Required(row, "stop_name"),
                new PointLL(longitude, latitude),
                locationType,
                stopsTable.Optional(row, "parent_station"),
                ParseOptionalInt(stopsTable.Optional(row, "wheelchair_boarding"), 0, "stops.txt.wheelchair_boarding"),
                stopsTable.Optional(row, "platform_code"));
            if (!stops.TryAdd(id, stop))
            {
                throw Invalid($"Duplicate stop_id {id}");
            }
        }

        foreach (GtfsStop stop in stops.Values)
        {
            if (stop.LocationType != 0 || string.IsNullOrEmpty(stop.ParentStation))
            {
                continue;
            }

            if (!stops.TryGetValue(stop.ParentStation, out GtfsStop? parent))
            {
                throw Referential($"Stop {stop.Id} references missing parent station {stop.ParentStation}");
            }

            if (parent.LocationType != 1)
            {
                throw Referential($"Stop {stop.Id} parent {stop.ParentStation} is not a station");
            }
        }

        GtfsTable routesTable = data.Required("routes.txt");
        routesTable.RequireColumns("route_id", "route_type");
        var routes = new Dictionary<string, GtfsRoute>(StringComparer.Ordinal);
        foreach (string[] row in routesTable.Rows)
        {
            string id = routesTable.Required(row, "route_id");
            string agencyId = routesTable.Optional(row, "agency_id");
            if (string.IsNullOrEmpty(agencyId) && agencies.Count == 1)
            {
                agencyId = agencies.Keys.Single();
            }

            if (!agencies.ContainsKey(agencyId))
            {
                throw Referential($"Route {id} references missing agency {agencyId}");
            }

            int routeType = ParseInt(routesTable.Required(row, "route_type"), "routes.txt.route_type");
            if (routeType is < 0 or > 7)
            {
                throw new TransitTileBuildException(
                    TransitTileBuildFailureCode.UnsupportedFeed,
                    $"Route {id} uses unsupported GTFS route_type {routeType}");
            }

            var route = new GtfsRoute(
                id,
                agencyId,
                routesTable.Optional(row, "route_short_name"),
                routesTable.Optional(row, "route_long_name"),
                routesTable.Optional(row, "route_desc"),
                (TransitType)routeType,
                ParseColor(routesTable.Optional(row, "route_color")),
                ParseColor(routesTable.Optional(row, "route_text_color")));
            if (!routes.TryAdd(id, route))
            {
                throw Invalid($"Duplicate route_id {id}");
            }
        }

        Dictionary<string, GtfsService> services = ParseServices(data, buildDate);

        GtfsTable tripsTable = data.Required("trips.txt");
        tripsTable.RequireColumns("route_id", "service_id", "trip_id");
        var trips = new Dictionary<string, GtfsTrip>(StringComparer.Ordinal);
        foreach (string[] row in tripsTable.Rows)
        {
            string id = tripsTable.Required(row, "trip_id");
            string routeId = tripsTable.Required(row, "route_id");
            string serviceId = tripsTable.Required(row, "service_id");
            if (!routes.ContainsKey(routeId))
            {
                throw Referential($"Trip {id} references missing route {routeId}");
            }

            if (!services.ContainsKey(serviceId))
            {
                throw Referential($"Trip {id} references missing service {serviceId}");
            }

            var trip = new GtfsTrip(
                id,
                routeId,
                serviceId,
                tripsTable.Optional(row, "trip_headsign"),
                ParseOptionalInt(tripsTable.Optional(row, "direction_id"), 0, "trips.txt.direction_id"),
                tripsTable.Optional(row, "block_id"),
                tripsTable.Optional(row, "shape_id"),
                ParseOptionalInt(tripsTable.Optional(row, "wheelchair_accessible"), 0, "trips.txt.wheelchair_accessible") == 1,
                ParseOptionalInt(tripsTable.Optional(row, "bikes_allowed"), 0, "trips.txt.bikes_allowed") == 1);
            if (!trips.TryAdd(id, trip))
            {
                throw Invalid($"Duplicate trip_id {id}");
            }
        }

        GtfsTable stopTimesTable = data.Required("stop_times.txt");
        stopTimesTable.RequireColumns("trip_id", "arrival_time", "departure_time", "stop_id", "stop_sequence");
        var stopTimes = new Dictionary<string, List<GtfsStopTime>>(StringComparer.Ordinal);
        foreach (string[] row in stopTimesTable.Rows)
        {
            string tripId = stopTimesTable.Required(row, "trip_id");
            string stopId = stopTimesTable.Required(row, "stop_id");
            if (!trips.ContainsKey(tripId))
            {
                throw Referential($"stop_times references missing trip {tripId}");
            }

            if (!stops.TryGetValue(stopId, out GtfsStop? stop) || stop.LocationType != 0)
            {
                throw Referential($"stop_times references missing or non-platform stop {stopId}");
            }

            var stopTime = new GtfsStopTime(
                stopId,
                ParseTime(stopTimesTable.Required(row, "arrival_time"), "stop_times.txt.arrival_time"),
                ParseTime(stopTimesTable.Required(row, "departure_time"), "stop_times.txt.departure_time"),
                ParseInt(stopTimesTable.Required(row, "stop_sequence"), "stop_times.txt.stop_sequence"),
                ParseOptionalDouble(stopTimesTable.Optional(row, "shape_dist_traveled")));
            stopTimes.GetValueOrDefault(tripId)?.Add(stopTime);
            if (!stopTimes.ContainsKey(tripId))
            {
                stopTimes[tripId] = [stopTime];
            }
        }

        foreach (GtfsTrip trip in trips.Values)
        {
            if (!stopTimes.TryGetValue(trip.Id, out List<GtfsStopTime>? times) || times.Count < 2)
            {
                throw Referential($"Trip {trip.Id} must contain at least two stop times");
            }

            times.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            for (int index = 1; index < times.Count; index++)
            {
                if (times[index - 1].Sequence >= times[index].Sequence ||
                    times[index - 1].DepartureTime > times[index].ArrivalTime)
                {
                    throw Invalid($"Trip {trip.Id} has invalid stop sequence or decreasing time");
                }
            }
        }

        Dictionary<string, GtfsFrequency> frequencies = ParseFrequencies(data, trips);
        Dictionary<string, IReadOnlyList<PointLL>> shapes = ParseShapes(data);
        foreach (GtfsTrip trip in trips.Values)
        {
            if (!string.IsNullOrEmpty(trip.ShapeId) && !shapes.ContainsKey(trip.ShapeId))
            {
                throw Referential($"Trip {trip.Id} references missing shape {trip.ShapeId}");
            }
        }

        return new ParsedGtfsFeed(
            data.Prefix,
            agencies,
            stops,
            routes,
            trips,
            stopTimes.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<GtfsStopTime>)pair.Value),
            services,
            frequencies,
            shapes);
    }

    private static Dictionary<string, GtfsService> ParseServices(
        GtfsFeedData data,
        DateOnly buildDate)
    {
        var definitions = new Dictionary<string, ServiceDefinition>(StringComparer.Ordinal);
        GtfsTable? calendar = data.Optional("calendar.txt");
        if (calendar is not null)
        {
            calendar.RequireColumns(
                "service_id",
                "monday",
                "tuesday",
                "wednesday",
                "thursday",
                "friday",
                "saturday",
                "sunday",
                "start_date",
                "end_date");
            foreach (string[] row in calendar.Rows)
            {
                string id = calendar.Required(row, "service_id");
                uint daysOfWeek =
                    (ParseFlag(calendar.Required(row, "sunday"), "calendar.txt.sunday") ? 1u : 0u) |
                    (ParseFlag(calendar.Required(row, "monday"), "calendar.txt.monday") ? 2u : 0u) |
                    (ParseFlag(calendar.Required(row, "tuesday"), "calendar.txt.tuesday") ? 4u : 0u) |
                    (ParseFlag(calendar.Required(row, "wednesday"), "calendar.txt.wednesday") ? 8u : 0u) |
                    (ParseFlag(calendar.Required(row, "thursday"), "calendar.txt.thursday") ? 16u : 0u) |
                    (ParseFlag(calendar.Required(row, "friday"), "calendar.txt.friday") ? 32u : 0u) |
                    (ParseFlag(calendar.Required(row, "saturday"), "calendar.txt.saturday") ? 64u : 0u);
                var definition = new ServiceDefinition(
                    id,
                    daysOfWeek,
                    ParseDate(calendar.Required(row, "start_date"), "calendar.txt.start_date"),
                    ParseDate(calendar.Required(row, "end_date"), "calendar.txt.end_date"));
                if (definition.EndDate < definition.StartDate || !definitions.TryAdd(id, definition))
                {
                    throw Invalid($"Service {id} has invalid or duplicate calendar definition");
                }
            }
        }

        var exceptions = new Dictionary<(string ServiceId, DateOnly Date), int>();
        GtfsTable? calendarDates = data.Optional("calendar_dates.txt");
        if (calendarDates is not null)
        {
            calendarDates.RequireColumns("service_id", "date", "exception_type");
            foreach (string[] row in calendarDates.Rows)
            {
                string id = calendarDates.Required(row, "service_id");
                DateOnly date = ParseDate(calendarDates.Required(row, "date"), "calendar_dates.txt.date");
                int type = ParseInt(calendarDates.Required(row, "exception_type"), "calendar_dates.txt.exception_type");
                if (type is < 1 or > 2 || !exceptions.TryAdd((id, date), type))
                {
                    throw Invalid($"Service {id} has an invalid or duplicate calendar exception");
                }

                if (!definitions.ContainsKey(id))
                {
                    definitions[id] = new ServiceDefinition(id, 0, date, date);
                }
            }
        }

        if (definitions.Count == 0)
        {
            throw new TransitTileBuildException(
                TransitTileBuildFailureCode.MissingRequiredFile,
                "GTFS feed requires calendar.txt or calendar_dates.txt");
        }

        var result = new Dictionary<string, GtfsService>(StringComparer.Ordinal);
        foreach (ServiceDefinition definition in definitions.Values)
        {
            ulong days = 0;
            for (int day = 0; day < ScheduleWindowDays; day++)
            {
                DateOnly date = buildDate.AddDays(day);
                bool active = date >= definition.StartDate &&
                              date <= definition.EndDate &&
                              (definition.DaysOfWeek & DayBit(date.DayOfWeek)) != 0;
                if (exceptions.TryGetValue((definition.Id, date), out int exceptionType))
                {
                    active = exceptionType == 1;
                }

                if (active)
                {
                    days |= 1ul << day;
                }
            }

            int endDay = Math.Clamp(definition.EndDate.DayNumber - buildDate.DayNumber, 0, ScheduleWindowDays - 1);
            result.Add(
                definition.Id,
                new GtfsService(definition.Id, days, definition.DaysOfWeek, (uint)endDay));
        }

        return result;
    }

    private static Dictionary<string, GtfsFrequency> ParseFrequencies(
        GtfsFeedData data,
        IReadOnlyDictionary<string, GtfsTrip> trips)
    {
        var result = new Dictionary<string, GtfsFrequency>(StringComparer.Ordinal);
        GtfsTable? table = data.Optional("frequencies.txt");
        if (table is null)
        {
            return result;
        }

        table.RequireColumns("trip_id", "start_time", "end_time", "headway_secs");
        foreach (string[] row in table.Rows)
        {
            string tripId = table.Required(row, "trip_id");
            if (!trips.ContainsKey(tripId))
            {
                throw Referential($"frequencies references missing trip {tripId}");
            }

            var frequency = new GtfsFrequency(
                ParseTime(table.Required(row, "start_time"), "frequencies.txt.start_time"),
                ParseTime(table.Required(row, "end_time"), "frequencies.txt.end_time"),
                ParseInt(table.Required(row, "headway_secs"), "frequencies.txt.headway_secs"));
            if (frequency.EndTime <= frequency.StartTime ||
                frequency.HeadwaySeconds <= 0 ||
                !result.TryAdd(tripId, frequency))
            {
                throw Invalid($"Trip {tripId} has invalid or duplicate frequency data");
            }
        }

        return result;
    }

    private static Dictionary<string, IReadOnlyList<PointLL>> ParseShapes(GtfsFeedData data)
    {
        var result = new Dictionary<string, IReadOnlyList<PointLL>>(StringComparer.Ordinal);
        GtfsTable? table = data.Optional("shapes.txt");
        if (table is null)
        {
            return result;
        }

        table.RequireColumns("shape_id", "shape_pt_lat", "shape_pt_lon", "shape_pt_sequence");
        var groups = new Dictionary<string, List<(int Sequence, PointLL Point)>>(StringComparer.Ordinal);
        foreach (string[] row in table.Rows)
        {
            string id = table.Required(row, "shape_id");
            double latitude = ParseDouble(table.Required(row, "shape_pt_lat"), "shapes.txt.shape_pt_lat");
            double longitude = ParseDouble(table.Required(row, "shape_pt_lon"), "shapes.txt.shape_pt_lon");
            int sequence = ParseInt(table.Required(row, "shape_pt_sequence"), "shapes.txt.shape_pt_sequence");
            if (latitude is < -90 or > 90 || longitude is < -180 or > 180)
            {
                throw Invalid($"Shape {id} has coordinates outside WGS84 bounds");
            }

            if (!groups.TryGetValue(id, out List<(int Sequence, PointLL Point)>? points))
            {
                points = [];
                groups.Add(id, points);
            }

            points.Add((sequence, new PointLL(longitude, latitude)));
        }

        foreach ((string id, List<(int Sequence, PointLL Point)> points) in groups)
        {
            points.Sort((left, right) => left.Sequence.CompareTo(right.Sequence));
            if (points.Count < 2 ||
                points.Zip(points.Skip(1), (left, right) => left.Sequence >= right.Sequence).Any(invalid => invalid))
            {
                throw Invalid($"Shape {id} has invalid sequence data");
            }

            result.Add(id, points.Select(item => item.Point).ToArray());
        }

        return result;
    }

    private static uint DayBit(DayOfWeek day)
        => 1u << (int)day;

    private static int ParseTime(string value, string field)
    {
        string[] parts = value.Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int hours) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int minutes) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) ||
            hours is < 0 or > 36 ||
            minutes is < 0 or > 59 ||
            seconds is < 0 or > 59)
        {
            throw Invalid($"{field} has invalid GTFS time {value}");
        }

        return checked((hours * 3600) + (minutes * 60) + seconds);
    }

    private static DateOnly ParseDate(string value, string field)
    {
        if (!DateOnly.TryParseExact(
                value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateOnly date))
        {
            throw Invalid($"{field} has invalid GTFS date {value}");
        }

        return date;
    }

    private static bool ParseFlag(string value, string field)
    {
        int parsed = ParseInt(value, field);
        if (parsed is < 0 or > 1)
        {
            throw Invalid($"{field} must be zero or one");
        }

        return parsed == 1;
    }

    private static int ParseInt(string value, string field)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            throw Invalid($"{field} has invalid integer {value}");
        }

        return parsed;
    }

    private static int ParseOptionalInt(string value, int defaultValue, string field)
        => string.IsNullOrWhiteSpace(value) ? defaultValue : ParseInt(value, field);

    private static double ParseDouble(string value, string field)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ||
            !double.IsFinite(parsed))
        {
            throw Invalid($"{field} has invalid number {value}");
        }

        return parsed;
    }

    private static double? ParseOptionalDouble(string value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : ParseDouble(value, "GTFS distance");

    private static uint ParseColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (value.Length != 6 ||
            !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color))
        {
            throw Invalid($"Invalid GTFS color {value}");
        }

        return color;
    }

    private static TransitTileBuildException Invalid(string message)
        => new(TransitTileBuildFailureCode.InvalidValue, message);

    private static TransitTileBuildException Referential(string message)
        => new(TransitTileBuildFailureCode.ReferentialIntegrity, message);

    private sealed record ServiceDefinition(
        string Id,
        uint DaysOfWeek,
        DateOnly StartDate,
        DateOnly EndDate);
}

internal sealed record ParsedGtfsFeed(
    string Prefix,
    IReadOnlyDictionary<string, GtfsAgency> Agencies,
    IReadOnlyDictionary<string, GtfsStop> Stops,
    IReadOnlyDictionary<string, GtfsRoute> Routes,
    IReadOnlyDictionary<string, GtfsTrip> Trips,
    IReadOnlyDictionary<string, IReadOnlyList<GtfsStopTime>> StopTimes,
    IReadOnlyDictionary<string, GtfsService> Services,
    IReadOnlyDictionary<string, GtfsFrequency> Frequencies,
    IReadOnlyDictionary<string, IReadOnlyList<PointLL>> Shapes);

internal sealed record GtfsAgency(
    string Id,
    string Name,
    string Website,
    string TimeZone);

internal sealed record GtfsStop(
    string Id,
    string Name,
    PointLL Coordinate,
    int LocationType,
    string ParentStation,
    int WheelchairBoarding,
    string PlatformCode);

internal sealed record GtfsRoute(
    string Id,
    string AgencyId,
    string ShortName,
    string LongName,
    string Description,
    TransitType Type,
    uint Color,
    uint TextColor);

internal sealed record GtfsTrip(
    string Id,
    string RouteId,
    string ServiceId,
    string Headsign,
    int DirectionId,
    string BlockId,
    string ShapeId,
    bool WheelchairAccessible,
    bool BicycleAccessible);

internal sealed record GtfsStopTime(
    string StopId,
    int ArrivalTime,
    int DepartureTime,
    int Sequence,
    double? ShapeDistance);

internal sealed record GtfsService(
    string Id,
    ulong Days,
    uint DaysOfWeek,
    uint EndDay);

internal sealed record GtfsFrequency(
    int StartTime,
    int EndTime,
    int HeadwaySeconds);
