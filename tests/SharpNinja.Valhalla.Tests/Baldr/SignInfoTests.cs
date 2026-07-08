// Coverage for the baldr SignInfo transfer class. No standalone gtest exists for
// baldr::SignInfo (test/signinfo.cc exercises the excluded mjolnir GraphBuilder path);
// these tests verify the ported constructor/accessor and sort-by-type semantics.

using SharpNinja.Valhalla.Baldr;

namespace SharpNinja.Valhalla.Tests.Baldr;

public class SignInfoTests
{
    [Fact]
    public void ConstructorStoresAllFields()
    {
        var info = new SignInfo(
            Sign.Type.ExitToward,
            rn: true,
            tagged: false,
            hasLinguistic: true,
            linguisticStartIndex: 3u,
            linguisticCount: 2u,
            text: "Harrisburg");

        Assert.Equal(Sign.Type.ExitToward, info.Type);
        Assert.True(info.IsRouteNum);
        Assert.False(info.IsTagged);
        Assert.True(info.HasLinguistic);
        Assert.Equal(3u, info.LinguisticStartIndex);
        Assert.Equal(2u, info.LinguisticCount);
        Assert.Equal("Harrisburg", info.Text);
    }

    [Fact]
    public void SortsByType()
    {
        var number = new SignInfo(Sign.Type.ExitNumber, false, false, false, 0u, 0u, "5");
        var branch = new SignInfo(Sign.Type.ExitBranch, true, false, false, 0u, 0u, "I 81");

        // ExitNumber (0) < ExitBranch (1)
        Assert.True(number < branch);
        Assert.True(branch > number);
    }
}
