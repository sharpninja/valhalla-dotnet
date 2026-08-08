using System.Buffers;
using System.Globalization;
using System.Text;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Generation.Pbf;

namespace SharpNinja.Valhalla.Generation.BikeShare;

internal sealed record BikeShareStationSource(
    OsmEntityOrdinal Ordinal,
    ulong OsmId,
    double Latitude,
    double Longitude,
    string Name,
    string Reference,
    uint Capacity,
    string Network,
    string OperatorName,
    string EncodedTaggedValue);

internal sealed class BikeShareStationReader
{
    public async ValueTask<(IReadOnlyList<BikeShareStationSource> Stations, long BytesRead)> ReadAsync(
        IReadOnlyList<string> pbfPaths,
        long memoryBudgetBytes,
        CancellationToken cancellationToken)
    {
        var sink = new BikeShareStationSink(memoryBudgetBytes);
        StreamingOsmPbfReadResult result;
        try
        {
            result = await new StreamingOsmPbfReader().ReadAsync(
                pbfPaths,
                sink,
                cancellationToken).ConfigureAwait(false);
        }
        catch (StreamingOsmPbfException exception)
        {
            throw new BikeShareTileBuildException(
                BikeShareTileBuildFailureCode.MalformedFeed,
                "The bike-share OSM PBF input is malformed.",
                exception);
        }

        IReadOnlyList<BikeShareStationSource> stations = sink.Stations
            .OrderBy(station => station.Ordinal.FileOrdinal)
            .ThenBy(station => station.Ordinal.BlockOrdinal)
            .ThenBy(station => station.Ordinal.EntityOrdinal)
            .ThenBy(station => station.OsmId)
            .ToArray();
        return (stations, result.Metrics.BytesRead);
    }

    private sealed class BikeShareStationSink : IStreamingOsmEntitySink
    {
        private const int EstimatedStationBytes = 512;
        private readonly long memoryBudgetBytes;
        private readonly List<BikeShareStationSource> stations = [];

        public BikeShareStationSink(long memoryBudgetBytes)
        {
            this.memoryBudgetBytes = memoryBudgetBytes;
        }

        public IReadOnlyList<BikeShareStationSource> Stations => stations;

        public bool ShouldRetain(OsmEntityKind kind) => kind == OsmEntityKind.Node;

        public void AddNode(scoped in OsmNodeView node)
        {
            string amenity = string.Empty;
            string bicycleRental = string.Empty;
            string name = string.Empty;
            string reference = string.Empty;
            string network = string.Empty;
            string operatorName = string.Empty;
            uint capacity = 0;

            for (int index = 0; index < node.Tags.Count; index++)
            {
                OsmTag tag = node.Tags[index];
                switch (tag.Key)
                {
                    case "amenity":
                        amenity = tag.Value;
                        break;
                    case "bicycle_rental":
                        bicycleRental = tag.Value;
                        break;
                    case "name":
                        name = tag.Value;
                        break;
                    case "ref":
                        reference = tag.Value;
                        break;
                    case "network":
                        network = tag.Value;
                        break;
                    case "operator":
                        operatorName = tag.Value;
                        break;
                    case "capacity":
                        _ = uint.TryParse(
                            tag.Value,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out capacity);
                        break;
                }
            }

            if (!string.Equals(amenity, "bicycle_rental", StringComparison.Ordinal)
                && !string.Equals(bicycleRental, "true", StringComparison.Ordinal))
            {
                return;
            }

            long nextEstimate = checked((long)(stations.Count + 1) * EstimatedStationBytes);
            if (nextEstimate > memoryBudgetBytes)
            {
                throw new BikeShareTileBuildException(
                    BikeShareTileBuildFailureCode.ResourceExhausted,
                    "The bike-share station index would exceed the configured memory budget.");
            }

            byte[] encoded = BikeShareStationInfoEncoder.Encode(
                name,
                reference,
                capacity,
                network,
                operatorName);
            var tagged = new char[encoded.Length + 1];
            tagged[0] = (char)(byte)TaggedValue.BssInfo;
            for (int index = 0; index < encoded.Length; index++)
            {
                tagged[index + 1] = (char)encoded[index];
            }

            stations.Add(new BikeShareStationSource(
                node.Ordinal,
                node.Id,
                node.Latitude,
                node.Longitude,
                name,
                reference,
                capacity,
                network,
                operatorName,
                new string(tagged)));
        }

        public void AddWay(scoped in OsmWayView way)
        {
        }

        public void AddRelation(scoped in OsmRelationView relation)
        {
        }
    }
}

internal static class BikeShareStationInfoEncoder
{
    public static byte[] Encode(
        string name,
        string reference,
        uint capacity,
        string network,
        string operatorName)
    {
        var writer = new ArrayBufferWriter<byte>(128);
        WriteString(writer, fieldNumber: 1, name);
        WriteString(writer, fieldNumber: 2, reference);
        if (capacity > 0)
        {
            WriteVarint(writer, (3u << 3) | 0u);
            WriteVarint(writer, capacity);
        }

        WriteString(writer, fieldNumber: 4, network);
        WriteString(writer, fieldNumber: 5, operatorName);
        return writer.WrittenSpan.ToArray();
    }

    private static void WriteString(
        ArrayBufferWriter<byte> writer,
        uint fieldNumber,
        string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        int byteCount = Encoding.UTF8.GetByteCount(value);
        WriteVarint(writer, (fieldNumber << 3) | 2u);
        WriteVarint(writer, checked((uint)byteCount));
        Span<byte> target = writer.GetSpan(byteCount);
        int written = Encoding.UTF8.GetBytes(value, target);
        writer.Advance(written);
    }

    private static void WriteVarint(ArrayBufferWriter<byte> writer, uint value)
    {
        Span<byte> target = writer.GetSpan(5);
        int written = 0;
        while (value >= 0x80)
        {
            target[written++] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        target[written++] = (byte)value;
        writer.Advance(written);
    }
}
