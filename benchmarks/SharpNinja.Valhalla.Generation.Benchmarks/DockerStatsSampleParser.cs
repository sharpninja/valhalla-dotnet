using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SharpNinja.Valhalla.Generation.Benchmarks;

public readonly record struct DockerStatsSample(
    long MemoryBytes,
    double CpuPercentage);

public static partial class DockerStatsSampleParser
{
    [GeneratedRegex(
        @"^(?<value>[0-9]+(?:[.][0-9]+)?)(?<unit>B|KiB|MiB|GiB|TiB)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex MemoryValueRegex();

    public static bool TryParse(
        string json,
        out DockerStatsSample sample)
    {
        sample = default;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            int objectStart = json.IndexOf('{');
            int objectEnd = json.LastIndexOf('}');
            if (objectStart < 0 || objectEnd < objectStart)
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(
                json[objectStart..(objectEnd + 1)]);
            if (!document.RootElement.TryGetProperty("MemUsage", out JsonElement memoryElement) ||
                !document.RootElement.TryGetProperty("CPUPerc", out JsonElement cpuElement))
            {
                return false;
            }

            string? memoryText = memoryElement.GetString();
            string? cpuText = cpuElement.GetString();
            if (!TryParseMemory(memoryText, out long memoryBytes) ||
                !TryParseCpu(cpuText, out double cpuPercentage))
            {
                return false;
            }

            sample = new DockerStatsSample(memoryBytes, cpuPercentage);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryParseMemory(
        string? value,
        out long bytes)
    {
        bytes = 0;
        string current = value?.Split('/', 2, StringSplitOptions.TrimEntries)[0] ?? string.Empty;
        Match match = MemoryValueRegex().Match(current);
        if (!match.Success ||
            !double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out double amount))
        {
            return false;
        }

        double multiplier = match.Groups["unit"].Value switch
        {
            "B" => 1,
            "KiB" => 1_024,
            "MiB" => 1_048_576,
            "GiB" => 1_073_741_824,
            "TiB" => 1_099_511_627_776,
            _ => 0,
        };
        double result = amount * multiplier;
        if (result < 0 || result > long.MaxValue)
        {
            return false;
        }

        bytes = checked((long)Math.Round(result, MidpointRounding.AwayFromZero));
        return true;
    }

    private static bool TryParseCpu(
        string? value,
        out double percentage)
    {
        percentage = 0;
        if (string.IsNullOrWhiteSpace(value) ||
            !value.EndsWith('%'))
        {
            return false;
        }

        return double.TryParse(
            value.AsSpan(0, value.Length - 1),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out percentage) &&
            percentage >= 0;
    }
}
