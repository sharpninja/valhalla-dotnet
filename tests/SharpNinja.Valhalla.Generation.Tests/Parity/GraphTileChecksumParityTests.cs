using System.Text;
using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Mjolnir;
using Xunit;

namespace SharpNinja.Valhalla.Generation.Tests.Parity;

public sealed class GraphTileChecksumParityTests
{
    [Fact]
    public void ManagedWriter_ComputesOfficialBodyHashAndTilesetBuildId()
    {
        byte[] body = Encoding.ASCII.GetBytes("valhalla-3.8.3-body-checksum");

        ulong tileHash = GraphTileChecksum.ComputeTileHash(body);

        Assert.Equal(0xB2FFD923E91AUL, tileHash);

        byte[] first = new GraphTileBuilder(new GraphId(1, 2, 0)).StoreTileData();
        byte[] second = new GraphTileBuilder(new GraphId(2, 2, 0)).StoreTileData();

        GraphTileHeader firstBeforeStamp = GraphTileHeader.FromBytes(first);
        GraphTileHeader secondBeforeStamp = GraphTileHeader.FromBytes(second);
        Assert.Equal(0x02D47F5BF111UL, firstBeforeStamp.TileChecksum());
        Assert.Equal(0x02D47F5BF111UL, secondBeforeStamp.TileChecksum());
        Assert.Equal(0, firstBeforeStamp.BuildId());
        Assert.Equal(0, secondBeforeStamp.BuildId());

        ushort buildId = GraphTileChecksum.StampTilesetBuildId([first, second]);

        Assert.Equal(0x193D, buildId);
        Assert.Equal(buildId, GraphTileHeader.FromBytes(first).BuildId());
        Assert.Equal(buildId, GraphTileHeader.FromBytes(second).BuildId());
        Assert.Equal(0x02D47F5BF111UL, GraphTileHeader.FromBytes(first).TileChecksum());
        Assert.Equal(0x02D47F5BF111UL, GraphTileHeader.FromBytes(second).TileChecksum());
    }

    [Fact]
    public void ComputeTilesetBuildId_IsOrderIndependent()
    {
        ulong[] forward = [1UL, 2UL, GraphTileHeader.TileHashMask];
        ulong[] reverse = [GraphTileHeader.TileHashMask, 2UL, 1UL];

        ushort expected = GraphTileChecksum.ComputeTilesetBuildId(forward);

        Assert.Equal(0x0003, expected);
        Assert.Equal(expected, GraphTileChecksum.ComputeTilesetBuildId(reverse));
    }

    [Fact]
    public void StampTilesetBuildId_RejectsTruncatedTile()
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => GraphTileChecksum.StampTilesetBuildId([new byte[GraphTileHeader.HeaderSize - 1]]));

        Assert.Contains("272", exception.Message, StringComparison.Ordinal);
    }
}
