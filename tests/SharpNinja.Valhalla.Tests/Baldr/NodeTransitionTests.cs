// C# tests for the ported Valhalla baldr NodeTransition (nodetransition.h).
// nodetransition.h has no dedicated gtest in test/; these tests guard the on-disk bit-packing
// fidelity (8-byte size, endnode_:46 / up_:1 / spare_:17) and the constructor/accessor semantics.

using System.Runtime.InteropServices;

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class NodeTransitionTests
{
    // The single packed uint64 word makes the struct exactly 8 bytes.
    private const int NodeTransitionExpectedSize = 8;

    [Fact]
    public void Sizeof()
    {
        Assert.Equal(NodeTransitionExpectedSize, Marshal.SizeOf<NodeTransition>());
    }

    [Fact]
    public void DefaultHasInvalidEndNodeAndIsNotUp()
    {
        NodeTransition t = NodeTransition.Default;
        // kInvalidGraphId fits exactly in the 46-bit endnode_ field.
        Assert.Equal(GraphId.InvalidGraphId, t.EndNode().Value);
        Assert.False(t.EndNode().IsValid());
        Assert.False(t.Up());
    }

    [Fact]
    public void ConstructorStoresEndNodeAndUpFlag()
    {
        var node = new GraphId(tileid: 1234, level: 2, id: 56789);

        var up = new NodeTransition(node, true);
        Assert.Equal(node.Value, up.EndNode().Value);
        Assert.Equal(node.Level(), up.EndNode().Level());
        Assert.Equal(node.Tileid(), up.EndNode().Tileid());
        Assert.Equal(node.Id(), up.EndNode().Id());
        Assert.True(up.Up());

        var down = new NodeTransition(node, false);
        Assert.Equal(node.Value, down.EndNode().Value);
        Assert.False(down.Up());
    }

    [Fact]
    public void UpBitDoesNotCorruptEndNode()
    {
        // A GraphId whose 46-bit value has its top representable bits set, to verify the up bit
        // (bit 46) is cleanly separated from the endnode_ field.
        var node = new GraphId(GraphConstants.MaxGraphTileId, GraphId.MaxGraphHierarchy, GraphConstants.MaxGraphId);

        var t = new NodeTransition(node, true);
        Assert.Equal(node.Value, t.EndNode().Value);
        Assert.True(t.Up());
    }
}
