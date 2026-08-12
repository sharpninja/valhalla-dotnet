using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Transit;

public sealed class TransitRecordLayoutTests
{
    [Fact]
    public void Valhalla383TransitRecords_HaveExactPackedLayouts()
    {
        var fixedDeparture = new TransitDeparture(
            lineId: 0x54321,
            tripId: 0x12345678,
            routeIndex: 0x321,
            blockId: 0x45678,
            headsignOffset: 0x654321,
            departureTime: 86_400,
            elapsedTime: 7_200,
            scheduleIndex: 0x234,
            wheelchairAccessible: true,
            bicycleAccessible: false);
        var frequencyDeparture = new TransitDeparture(
            lineId: 3,
            tripId: 4,
            routeIndex: 5,
            blockId: 6,
            headsignOffset: 7,
            departureTime: 8,
            endTime: 9,
            frequency: 10,
            elapsedTime: 11,
            scheduleIndex: 12,
            wheelchairAccessible: false,
            bicycleAccessible: true);
        var stop = new TransitStop(0x123456, 0x654321, generated: true, Traversability.Both);
        var route = new TransitRoute(
            TransitType.Bus,
            oneStopOffset: 1,
            operatedByOneStopIdOffset: 2,
            operatedByNameOffset: 3,
            operatedByWebsiteOffset: 4,
            routeColor: 0x11223344,
            routeTextColor: 0x55667788,
            shortNameOffset: 5,
            longNameOffset: 6,
            descriptionOffset: 7);
        var schedule = new TransitSchedule(
            days: 0x0123456789ABCDEF,
            daysOfWeek: GraphConstants.AllDaysOfWeek,
            endDay: 42);
        var transfer = new TransitTransfer(8, 9, TransferType.MinTime, 600);

        Assert.Equal(24, Marshal.SizeOf<TransitDeparture>());
        Assert.Equal(8, Marshal.SizeOf<TransitStop>());
        Assert.Equal(40, Marshal.SizeOf<TransitRoute>());
        Assert.Equal(16, Marshal.SizeOf<TransitSchedule>());
        Assert.Equal(12, Marshal.SizeOf<TransitTransfer>());

        byte[] fixedBytes = BytesOf(fixedDeparture);
        Assert.Equal(
            (0x54321ul | (0x321ul << 20) | (0x12345678ul << 32)),
            BinaryPrimitives.ReadUInt64LittleEndian(fixedBytes));
        Assert.Equal(
            (0x45678ul | (0x234ul << 20) | (0x654321ul << 32) | (1ul << 58)),
            BinaryPrimitives.ReadUInt64LittleEndian(fixedBytes.AsSpan(8)));
        Assert.Equal(
            (86_400ul | (7_200ul << 17)),
            BinaryPrimitives.ReadUInt64LittleEndian(fixedBytes.AsSpan(16)));

        byte[] frequencyBytes = BytesOf(frequencyDeparture);
        Assert.Equal(
            (8ul | (9ul << 17) | (10ul << 34) | (11ul << 47)),
            BinaryPrimitives.ReadUInt64LittleEndian(frequencyBytes.AsSpan(16)));
        Assert.Equal(TransitDeparture.FrequencySchedule, frequencyDeparture.Type);

        Assert.Equal(
            (0x123456ul | (0x654321ul << 24) | (1ul << 48) | (3ul << 49)),
            BinaryPrimitives.ReadUInt64LittleEndian(BytesOf(stop)));
        byte[] routeBytes = BytesOf(route);
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32LittleEndian(routeBytes));
        Assert.Equal(0x55667788u, BinaryPrimitives.ReadUInt32LittleEndian(routeBytes.AsSpan(4)));
        Assert.Equal(
            ((ulong)TransitType.Bus | (1ul << 8)),
            BinaryPrimitives.ReadUInt64LittleEndian(routeBytes.AsSpan(8)));
        Assert.Equal(
            (2ul | (3ul << 24)),
            BinaryPrimitives.ReadUInt64LittleEndian(routeBytes.AsSpan(16)));
        Assert.Equal(
            (4ul | (5ul << 24)),
            BinaryPrimitives.ReadUInt64LittleEndian(routeBytes.AsSpan(24)));
        Assert.Equal(
            (6ul | (7ul << 24)),
            BinaryPrimitives.ReadUInt64LittleEndian(routeBytes.AsSpan(32)));

        byte[] scheduleBytes = BytesOf(schedule);
        Assert.Equal(0x0123456789ABCDEFul, BinaryPrimitives.ReadUInt64LittleEndian(scheduleBytes));
        Assert.Equal(
            ((ulong)GraphConstants.AllDaysOfWeek | (42ul << 7)),
            BinaryPrimitives.ReadUInt64LittleEndian(scheduleBytes.AsSpan(8)));

        byte[] transferBytes = BytesOf(transfer);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32LittleEndian(transferBytes));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(transferBytes.AsSpan(4)));
        Assert.Equal(
            ((uint)TransferType.MinTime | (600u << 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(transferBytes.AsSpan(8)));
    }

    [Fact]
    public void GraphTileBuilder_RoundTripsAllTransitSections()
    {
        var builder = new GraphTileBuilder(new GraphId(3016, 0, 0));
        uint headsign = builder.AddName("Downtown");
        uint oneStop = builder.AddName("s-example");
        uint stopName = builder.AddName("Example Station");
        uint operatorId = builder.AddName("o-example");
        uint operatorName = builder.AddName("Example Transit");
        uint operatorWebsite = builder.AddName("https://example.invalid");
        uint shortName = builder.AddName("10");
        uint longName = builder.AddName("Airport Express");
        uint description = builder.AddName("Fixture route");
        builder.Departures.Add(new TransitDeparture(
            1, 2, 0, 4, headsign, 6, 7, 0, wheelchairAccessible: true, bicycleAccessible: true));
        builder.TransitStops.Add(new TransitStop(oneStop, stopName, generated: false, Traversability.Both));
        builder.TransitRoutes.Add(new TransitRoute(
            TransitType.Bus,
            oneStop,
            operatorId,
            operatorName,
            operatorWebsite,
            15,
            16,
            shortName,
            longName,
            description));
        builder.TransitSchedules.Add(new TransitSchedule(20, 21, 22));
        builder.TransitTransfers.Add(new TransitTransfer(23, 24, TransferType.MinTime, 25));

        GraphTile tile = GraphTile.Create(builder.Header().Graphid(), builder.StoreTileData());

        Assert.Equal(1u, tile.Header().Departurecount());
        Assert.Equal(1u, tile.Header().Stopcount());
        Assert.Equal(1u, tile.Header().Routecount());
        Assert.Equal(1u, tile.Header().Schedulecount());
        Assert.Equal(1u, tile.Header().Transfercount());
        Assert.Equal(2u, tile.TransitDeparture(0).TripId);
        Assert.Equal(stopName, tile.TransitStop(0).NameOffset);
        Assert.Equal(TransitType.Bus, tile.TransitRoute(0).RouteType);
        Assert.Equal(21u, tile.TransitSchedule(0).DaysOfWeek);
        Assert.Equal(24u, tile.TransitTransfer(0).ToStopId);

        var copy = new GraphTileBuilder(tile);
        GraphTile roundTripped = GraphTile.Create(copy.Header().Graphid(), copy.StoreTileData());
        Assert.Equal(BytesOf(tile.TransitDeparture(0)), BytesOf(roundTripped.TransitDeparture(0)));
        Assert.Equal(BytesOf(tile.TransitRoute(0)), BytesOf(roundTripped.TransitRoute(0)));
        Assert.Equal(BytesOf(tile.TransitTransfer(0)), BytesOf(roundTripped.TransitTransfer(0)));
    }

    private static byte[] BytesOf<T>(T value)
        where T : struct
    {
        byte[] bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }
}
