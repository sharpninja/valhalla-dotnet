using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace SharpNinja.Valhalla.Traffic.Providers;

internal static class TrafficNormalizationJson
{
	public static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
				{
					value = property.Value;
					return true;
				}
			}
		}

		value = default;
		return false;
	}

	public static JsonElement? Property(JsonElement element, params string[] names)
	{
		foreach (string name in names)
		{
			if (TryGetProperty(element, name, out JsonElement value))
			{
				return value;
			}
		}

		return null;
	}

	public static string? String(JsonElement element, params string[] names)
	{
		JsonElement? value = Property(element, names);
		if (value is null)
		{
			return null;
		}

		return value.Value.ValueKind switch
		{
			JsonValueKind.String => value.Value.GetString(),
			JsonValueKind.Number => value.Value.GetRawText(),
			JsonValueKind.True => bool.TrueString,
			JsonValueKind.False => bool.FalseString,
			_ => null,
		};
	}

	public static double? Double(JsonElement element, params string[] names)
	{
		JsonElement? value = Property(element, names);
		if (value is null)
		{
			return null;
		}

		if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetDouble(out double number))
		{
			return number;
		}

		return value.Value.ValueKind == JsonValueKind.String
			&& double.TryParse(value.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
				? number
				: null;
	}

	public static int? Int(JsonElement element, params string[] names)
	{
		JsonElement? value = Property(element, names);
		if (value is null)
		{
			return null;
		}

		if (value.Value.ValueKind == JsonValueKind.Number && value.Value.TryGetInt32(out int number))
		{
			return number;
		}

		return value.Value.ValueKind == JsonValueKind.String
			&& int.TryParse(value.Value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
				? number
				: null;
	}

	public static bool? Bool(JsonElement element, params string[] names)
	{
		JsonElement? value = Property(element, names);
		if (value is null)
		{
			return null;
		}

		return value.Value.ValueKind switch
		{
			JsonValueKind.True => true,
			JsonValueKind.False => false,
			JsonValueKind.String when bool.TryParse(value.Value.GetString(), out bool parsed) => parsed,
			_ => null,
		};
	}

	public static DateTimeOffset? DateTimeOffset(JsonElement element, params string[] names)
	{
		string? value = String(element, names);
		return System.DateTimeOffset.TryParse(
			value,
			CultureInfo.InvariantCulture,
			DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
			out System.DateTimeOffset parsed)
				? parsed
				: null;
	}

	public static DateTimeOffset? MetadataDate(
		IReadOnlyDictionary<string, string> metadata,
		params string[] names)
	{
		foreach (string name in names)
		{
			if (metadata.TryGetValue(name, out string? value)
				&& System.DateTimeOffset.TryParse(
					value,
					CultureInfo.InvariantCulture,
					DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
					out System.DateTimeOffset parsed))
			{
				return parsed;
			}
		}

		return null;
	}

	public static IReadOnlyList<GeoCoordinate> Coordinates(JsonElement geometry)
	{
		var points = new List<GeoCoordinate>();
		AddCoordinates(geometry, points);
		return points;
	}

	public static TrafficGeometry Geometry(
		IReadOnlyList<GeoCoordinate> points,
		TrafficGeometryDirection direction = TrafficGeometryDirection.Unknown)
		=> new(
			points.Count == 1 ? TrafficGeometryKind.Point : TrafficGeometryKind.LineString,
			points,
			direction);

	/// <summary>
	/// Reads only the explicit <c>sharpNinjaTraffic.geometryDirection</c> normalized-proxy
	/// extension. Native provider direction codes such as TMC positive/negative are intentionally
	/// ignored because they do not prove GeoJSON coordinate order without a location-reference decoder.
	/// </summary>
	public static TrafficGeometryDirection ExplicitGeometryDirection(
		params JsonElement[] elements)
	{
		foreach (JsonElement element in elements)
		{
			JsonElement? extension = Property(element, "sharpNinjaTraffic");
			if (extension is not { ValueKind: JsonValueKind.Object })
			{
				continue;
			}

			string? value = String(
				extension.Value,
				"geometryDirection",
				"trafficGeometryDirection");
			if (string.IsNullOrWhiteSpace(value))
			{
				continue;
			}

			if (value.Equals("alongCoordinates", StringComparison.OrdinalIgnoreCase)
				|| value.Equals("forward", StringComparison.OrdinalIgnoreCase)
				|| value.Equals("oneDirection", StringComparison.OrdinalIgnoreCase))
			{
				return TrafficGeometryDirection.AlongCoordinates;
			}

			if (value.Equals("bothDirections", StringComparison.OrdinalIgnoreCase)
				|| value.Equals("both", StringComparison.OrdinalIgnoreCase)
				|| value.Equals("bidirectional", StringComparison.OrdinalIgnoreCase))
			{
				return TrafficGeometryDirection.BothDirections;
			}
		}

		return TrafficGeometryDirection.Unknown;
	}

	/// <summary>
	/// Reads only the explicit <c>sharpNinjaTraffic.restrictionApplicability</c>
	/// normalized-proxy extension. Provider-native conditions remain advisory until a
	/// provider-specific applicability decoder establishes an unconditional all-vehicle restriction.
	/// </summary>
	public static TrafficRestrictionApplicability RestrictionApplicability(
		params JsonElement[] elements)
	{
		foreach (JsonElement element in elements)
		{
			JsonElement? extension = Property(element, "sharpNinjaTraffic");
			if (extension is not { ValueKind: JsonValueKind.Object })
			{
				continue;
			}

			string? value = String(
				extension.Value,
				"restrictionApplicability",
				"applicability");
			if (string.IsNullOrWhiteSpace(value))
			{
				continue;
			}

			if (value.Equals("allVehicles", StringComparison.OrdinalIgnoreCase)
				|| value.Equals("unconditionalAllVehicles", StringComparison.OrdinalIgnoreCase))
			{
				return TrafficRestrictionApplicability.UnconditionalAllVehicles;
			}

			if (value.Equals("conditional", StringComparison.OrdinalIgnoreCase))
			{
				return TrafficRestrictionApplicability.Conditional;
			}

			if (value.Equals("truck", StringComparison.OrdinalIgnoreCase)
				|| value.Equals("vehicleSpecific", StringComparison.OrdinalIgnoreCase))
			{
				return TrafficRestrictionApplicability.VehicleSpecific;
			}
		}

		return TrafficRestrictionApplicability.Unknown;
	}

	public static int? Delay(int? explicitDelay, int? currentTravelTime, int? freeFlowTravelTime)
	{
		if (explicitDelay is not null)
		{
			return Math.Max(0, explicitDelay.Value);
		}

		return currentTravelTime is not null && freeFlowTravelTime is not null
			? Math.Max(0, currentTravelTime.Value - freeFlowTravelTime.Value)
			: null;
	}

	public static double Confidence(double? confidence)
		=> confidence is not null && double.IsFinite(confidence.Value)
			? Math.Clamp(confidence.Value, 0d, 1d)
			: 0d;

	public static string FallbackId(RawTrafficFeedPayload payload, int ordinal)
	{
		byte[] digest = SHA256.HashData(payload.Content.Span);
		return string.Create(
			CultureInfo.InvariantCulture,
			$"{payload.ProviderId}-{payload.FeedKind.ToString().ToLowerInvariant()}-{Convert.ToHexString(digest.AsSpan(0, 6)).ToLowerInvariant()}-{ordinal}");
	}

	public static TrafficProviderDiagnostic Diagnostic(
		RawTrafficFeedPayload payload,
		string code,
		string message)
		=> new(
			code,
			payload.ProviderId,
			payload.FeedKind,
			message,
			RedactedSourceUrl(payload.SourceUri));

	public static Uri? RedactedSourceUri(Uri? sourceUri)
	{
		if (sourceUri is null)
		{
			return null;
		}

		if (!sourceUri.IsAbsoluteUri)
		{
			return null;
		}

		var builder = new UriBuilder(sourceUri)
		{
			Port = sourceUri.IsDefaultPort ? -1 : sourceUri.Port,
			UserName = string.Empty,
			Password = string.Empty,
			Query = string.Empty,
			Fragment = string.Empty,
		};
		return builder.Uri;
	}

	public static string RedactedSourceUrl(Uri? sourceUri)
		=> RedactedSourceUri(sourceUri)?.ToString() ?? string.Empty;

	private static void AddCoordinates(JsonElement element, List<GeoCoordinate> points)
	{
		if (element.ValueKind == JsonValueKind.Object)
		{
			double? latitude = Double(element, "latitude", "lat");
			double? longitude = Double(element, "longitude", "lng", "lon");
			if (latitude is not null
				&& longitude is not null
				&& latitude is >= -90d and <= 90d
				&& longitude is >= -180d and <= 180d)
			{
				points.Add(new GeoCoordinate(latitude.Value, longitude.Value));
				return;
			}

			foreach (JsonProperty property in element.EnumerateObject())
			{
				AddCoordinates(property.Value, points);
			}

			return;
		}

		if (element.ValueKind != JsonValueKind.Array)
		{
			return;
		}

		if (element.GetArrayLength() >= 2
			&& element[0].ValueKind == JsonValueKind.Number
			&& element[1].ValueKind == JsonValueKind.Number
			&& element[0].TryGetDouble(out double geoJsonLongitude)
			&& element[1].TryGetDouble(out double geoJsonLatitude)
			&& geoJsonLatitude is >= -90d and <= 90d
			&& geoJsonLongitude is >= -180d and <= 180d)
		{
			points.Add(new GeoCoordinate(geoJsonLatitude, geoJsonLongitude));
			return;
		}

		foreach (JsonElement child in element.EnumerateArray())
		{
			AddCoordinates(child, points);
		}
	}
}
