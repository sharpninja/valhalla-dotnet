// Faithful C# port of Valhalla's gtest suite test/signs.cc.
// Each [Fact] mirrors a TEST(Signs, ...) case with the same inputs and expected values.
// EXPECT_EQ -> Assert.Equal; ASSERT_THAT(actual, expected) (a vector equality) -> Assert.Equal.

using System.Collections.Generic;

using SharpNinja.Valhalla.Odin;

namespace SharpNinja.Valhalla.Tests.Odin;

public class SignsTests
{
    private static void PopulateSigns(IEnumerable<(string Text, bool IsRouteNumber, uint ConsecutiveCount)> signItems, List<OdinSign> signList)
    {
        foreach ((string text, bool isRouteNumber, uint consecutiveCount) in signItems)
        {
            var sign = new OdinSign(text, isRouteNumber);
            sign.SetConsecutiveCount(consecutiveCount);
            signList.Add(sign);
        }
    }

    private static Signs GetExitNumberSigns(IEnumerable<(string, bool, uint)> signItems)
    {
        var signs = new Signs();
        PopulateSigns(signItems, signs.MutableExitNumberList());
        return signs;
    }

    private static Signs GetExitBranchSigns(IEnumerable<(string, bool, uint)> signItems)
    {
        var signs = new Signs();
        PopulateSigns(signItems, signs.MutableExitBranchList());
        return signs;
    }

    private static Signs GetExitTowardSigns(IEnumerable<(string, bool, uint)> signItems)
    {
        var signs = new Signs();
        PopulateSigns(signItems, signs.MutableExitTowardList());
        return signs;
    }

    private static Signs GetExitNameSigns(IEnumerable<(string, bool, uint)> signItems)
    {
        var signs = new Signs();
        PopulateSigns(signItems, signs.MutableExitNameList());
        return signs;
    }

    private static Signs GetGuideBranchSigns(IEnumerable<(string, bool, uint)> signItems)
    {
        var signs = new Signs();
        PopulateSigns(signItems, signs.MutableGuideBranchList());
        return signs;
    }

    private static Signs GetGuideTowardSigns(IEnumerable<(string, bool, uint)> signItems)
    {
        var signs = new Signs();
        PopulateSigns(signItems, signs.MutableGuideTowardList());
        return signs;
    }

    private static Signs GetGuideSigns(
        IEnumerable<(string, bool, uint)> branchSignItems,
        IEnumerable<(string, bool, uint)> towardSignItems)
    {
        var signs = new Signs();
        PopulateSigns(branchSignItems, signs.MutableGuideBranchList());
        PopulateSigns(towardSignItems, signs.MutableGuideTowardList());
        return signs;
    }

    private static Signs GetJunctionNameSigns(IEnumerable<(string, bool, uint)> signItems)
    {
        var signs = new Signs();
        PopulateSigns(signItems, signs.MutableJunctionNameList());
        return signs;
    }

    private static void TryGetGuideSigns(Signs signs, uint maxCount, bool limitByConsecutiveCount, IEnumerable<(string, bool, uint)> signItems)
    {
        var expectedSigns = new List<OdinSign>();
        PopulateSigns(signItems, expectedSigns);
        Assert.Equal(expectedSigns, signs.GetGuideSigns(maxCount, limitByConsecutiveCount));
    }

    private static void TryTrimSigns(List<OdinSign> signs, IEnumerable<(string, bool, uint)> signItems)
    {
        var expectedSigns = new List<OdinSign>();
        PopulateSigns(signItems, expectedSigns);
        Assert.Equal(expectedSigns, signs);
    }

    [Fact]
    public void TestGetExitTowardString_PA283_onto_PA743()
    {
        Signs signs = GetExitTowardSigns(new (string, bool, uint)[] { ("Elizabethtown", false, 1), ("Hershey", false, 0) });

        Assert.Equal("Elizabethtown/Hershey", signs.GetExitTowardString(4, false));
        Assert.Equal("Elizabethtown/Hershey", signs.GetExitTowardString(2, false));
        Assert.Equal("Elizabethtown", signs.GetExitTowardString(1, false));

        Assert.Equal("Elizabethtown", signs.GetExitTowardString(2, true));
        Assert.Equal("Elizabethtown", signs.GetExitTowardString(1, true));
    }

    [Fact]
    public void TestGetExitNumberString_I81S_onto_US322W()
    {
        Signs signs = GetExitNumberSigns(new (string, bool, uint)[] { ("67B", false, 1), ("67A", false, 0) });

        Assert.Equal("67B/67A", signs.GetExitNumberString(4, false));
        Assert.Equal("67B/67A", signs.GetExitNumberString(2, false));
        Assert.Equal("67B", signs.GetExitNumberString(1, false));

        Assert.Equal("67B", signs.GetExitNumberString(4, true));
        Assert.Equal("67B", signs.GetExitNumberString(2, true));
        Assert.Equal("67B", signs.GetExitNumberString(1, true));
    }

    [Fact]
    public void TestGetExitBranchString_I81S_onto_US322W()
    {
        Signs signs = GetExitBranchSigns(new (string, bool, uint)[]
        {
            ("US 322 West", true, 2), ("US 22 West", true, 1), ("US 22 East", true, 0),
            ("PA 230 East", true, 0), ("Cameron Street", false, 0),
        });

        Assert.Equal("US 322 West/US 22 West/US 22 East/PA 230 East/Cameron Street", signs.GetExitBranchString(0, false));
        Assert.Equal("US 322 West/US 22 West/US 22 East/PA 230 East/Cameron Street", signs.GetExitBranchString(5, false));
        Assert.Equal("US 322 West/US 22 West/US 22 East/PA 230 East", signs.GetExitBranchString(4, false));
        Assert.Equal("US 322 West/US 22 West", signs.GetExitBranchString(2, false));
        Assert.Equal("US 322 West", signs.GetExitBranchString(1, false));

        Assert.Equal("US 322 West", signs.GetExitBranchString(0, true));
        Assert.Equal("US 322 West", signs.GetExitBranchString(5, true));
        Assert.Equal("US 322 West", signs.GetExitBranchString(4, true));
        Assert.Equal("US 322 West", signs.GetExitBranchString(2, true));
        Assert.Equal("US 322 West", signs.GetExitBranchString(1, true));
    }

    [Fact]
    public void TestGetExitTowardString_I81S_onto_US322W()
    {
        Signs signs = GetExitTowardSigns(new (string, bool, uint)[]
        {
            ("Lewistown", false, 1), ("State College", false, 1), ("Harrisburg", false, 0),
        });

        Assert.Equal("Lewistown/State College/Harrisburg", signs.GetExitTowardString(4, false));
        Assert.Equal("Lewistown/State College", signs.GetExitTowardString(2, false));
        Assert.Equal("Lewistown", signs.GetExitTowardString(1, false));

        Assert.Equal("Lewistown/State College", signs.GetExitTowardString(4, true));
        Assert.Equal("Lewistown/State College", signs.GetExitTowardString(2, true));
        Assert.Equal("Lewistown", signs.GetExitTowardString(1, true));
    }

    [Fact]
    public void TestGetExitNameString()
    {
        Signs signs = GetExitNameSigns(new (string, bool, uint)[] { ("Gettysburg Pike", false, 1), ("Harrisburg Pike", false, 0) });

        Assert.Equal("Gettysburg Pike/Harrisburg Pike", signs.GetExitNameString(4, false));
        Assert.Equal("Gettysburg Pike/Harrisburg Pike", signs.GetExitNameString(2, false));
        Assert.Equal("Gettysburg Pike", signs.GetExitNameString(1, false));

        Assert.Equal("Gettysburg Pike", signs.GetExitNameString(4, true));
        Assert.Equal("Gettysburg Pike", signs.GetExitNameString(2, true));
        Assert.Equal("Gettysburg Pike", signs.GetExitNameString(1, true));
    }

    [Fact]
    public void TestGetGuideBranchString_LinglestownRoad_onto_US322W()
    {
        Signs signs = GetGuideBranchSigns(new (string, bool, uint)[] { ("US 322 West", true, 1), ("US 22 West", true, 0) });

        Assert.Equal("US 322 West/US 22 West", signs.GetGuideBranchString(0, false));
        Assert.Equal("US 322 West/US 22 West", signs.GetGuideBranchString(4, false));
        Assert.Equal("US 322 West/US 22 West", signs.GetGuideBranchString(2, false));
        Assert.Equal("US 322 West", signs.GetGuideBranchString(1, false));

        Assert.Equal("US 322 West", signs.GetGuideBranchString(0, true));
        Assert.Equal("US 322 West", signs.GetGuideBranchString(4, true));
        Assert.Equal("US 322 West", signs.GetGuideBranchString(2, true));
        Assert.Equal("US 322 West", signs.GetGuideBranchString(1, true));
    }

    [Fact]
    public void TestGetGuideTowardString_roundabout_toward_A1()
    {
        Signs signs = GetGuideTowardSigns(new (string, bool, uint)[] { ("A 1", true, 1), ("Remscheid", false, 1), ("Wermelskirchen", false, 0) });

        Assert.Equal("A 1/Remscheid/Wermelskirchen", signs.GetGuideTowardString(4, false));
        Assert.Equal("A 1/Remscheid", signs.GetGuideTowardString(2, false));
        Assert.Equal("A 1", signs.GetGuideTowardString(1, false));

        Assert.Equal("A 1/Remscheid", signs.GetGuideTowardString(4, true));
        Assert.Equal("A 1/Remscheid", signs.GetGuideTowardString(2, true));
        Assert.Equal("A 1", signs.GetGuideTowardString(1, true));
    }

    [Fact]
    public void TestGetGuideString_BranchOnly()
    {
        Signs signs = GetGuideSigns(
            new (string, bool, uint)[] { ("US 322 West", true, 1), ("US 22 West", true, 0), ("Freedom Highway", false, 0), ("Valhalla Highway", false, 0) },
            System.Array.Empty<(string, bool, uint)>());

        Assert.Equal("US 322 West/US 22 West/Freedom Highway/Valhalla Highway", signs.GetGuideString(0, false));
        Assert.Equal("US 322 West/US 22 West/Freedom Highway/Valhalla Highway", signs.GetGuideString(4, false));
        Assert.Equal("US 322 West/US 22 West/Freedom Highway", signs.GetGuideString(3, false));
        Assert.Equal("US 322 West/US 22 West", signs.GetGuideString(2, false));
        Assert.Equal("US 322 West", signs.GetGuideString(1, false));

        Assert.Equal("US 322 West", signs.GetGuideString(0, true));
        Assert.Equal("US 322 West", signs.GetGuideString(4, true));
        Assert.Equal("US 322 West", signs.GetGuideString(2, true));
        Assert.Equal("US 322 West", signs.GetGuideString(1, true));
    }

    [Fact]
    public void TestGetGuideString_TowardOnly()
    {
        Signs signs = GetGuideSigns(
            System.Array.Empty<(string, bool, uint)>(),
            new (string, bool, uint)[] { ("A 1", true, 1), ("Remscheid", false, 1), ("Wermelskirchen", false, 0), ("Hückeswagen", false, 0) });

        Assert.Equal("A 1/Remscheid/Wermelskirchen/Hückeswagen", signs.GetGuideString(0, false));
        Assert.Equal("A 1/Remscheid/Wermelskirchen/Hückeswagen", signs.GetGuideString(4, false));
        Assert.Equal("A 1/Remscheid/Wermelskirchen", signs.GetGuideString(3, false));
        Assert.Equal("A 1/Remscheid", signs.GetGuideString(2, false));
        Assert.Equal("A 1", signs.GetGuideString(1, false));

        Assert.Equal("A 1/Remscheid", signs.GetGuideString(0, true));
        Assert.Equal("A 1/Remscheid", signs.GetGuideString(4, true));
        Assert.Equal("A 1/Remscheid", signs.GetGuideString(2, true));
        Assert.Equal("A 1", signs.GetGuideString(1, true));
    }

    [Fact]
    public void TestGetGuideString_NoConsecutiveCount()
    {
        Signs signs = GetGuideSigns(
            new (string, bool, uint)[] { ("US 322 West", true, 0), ("US 22 West", true, 0), ("Freedom Highway", false, 0), ("Valhalla Highway", false, 0) },
            new (string, bool, uint)[] { ("A 1", true, 0), ("Remscheid", false, 0), ("Wermelskirchen", false, 0), ("Hückeswagen", false, 0) });

        Assert.Equal(
            "US 322 West/US 22 West/Freedom Highway/Valhalla Highway/A 1/Remscheid/Wermelskirchen/Hückeswagen",
            signs.GetGuideString(0, false));
        Assert.Equal("US 322 West/US 22 West/A 1/Remscheid", signs.GetGuideString(4, false));
        Assert.Equal("US 322 West/US 22 West/A 1", signs.GetGuideString(3, false));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(2, false));
        Assert.Equal("US 322 West", signs.GetGuideString(1, false));

        Assert.Equal(
            "US 322 West/US 22 West/Freedom Highway/Valhalla Highway/A 1/Remscheid/Wermelskirchen/Hückeswagen",
            signs.GetGuideString(0, true));
        Assert.Equal("US 322 West/US 22 West/A 1/Remscheid", signs.GetGuideString(4, true));
        Assert.Equal("US 322 West/US 22 West/A 1", signs.GetGuideString(3, true));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(2, true));
        Assert.Equal("US 322 West", signs.GetGuideString(1, true));
    }

    [Fact]
    public void TestGetGuideString_SingleConsecutiveCount()
    {
        Signs signs = GetGuideSigns(
            new (string, bool, uint)[] { ("US 322 West", true, 1), ("US 22 West", true, 0), ("Freedom Highway", false, 0), ("Valhalla Highway", false, 0) },
            new (string, bool, uint)[] { ("A 1", true, 1), ("Remscheid", false, 0), ("Wermelskirchen", false, 0), ("Hückeswagen", false, 0) });

        Assert.Equal(
            "US 322 West/US 22 West/Freedom Highway/Valhalla Highway/A 1/Remscheid/Wermelskirchen/Hückeswagen",
            signs.GetGuideString(0, false));
        Assert.Equal("US 322 West/US 22 West/A 1/Remscheid", signs.GetGuideString(4, false));
        Assert.Equal("US 322 West/US 22 West/A 1", signs.GetGuideString(3, false));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(2, false));
        Assert.Equal("US 322 West", signs.GetGuideString(1, false));

        Assert.Equal("US 322 West/A 1", signs.GetGuideString(0, true));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(4, true));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(3, true));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(2, true));
        Assert.Equal("US 322 West", signs.GetGuideString(1, true));
    }

    [Fact]
    public void TestGetGuideString_MultipleConsecutiveCount()
    {
        Signs signs = GetGuideSigns(
            new (string, bool, uint)[] { ("US 322 West", true, 2), ("US 22 West", true, 2), ("Freedom Highway", false, 1), ("Valhalla Highway", false, 0) },
            new (string, bool, uint)[] { ("A 1", true, 2), ("Remscheid", false, 2), ("Wermelskirchen", false, 1), ("Hückeswagen", false, 0) });

        Assert.Equal(
            "US 322 West/US 22 West/Freedom Highway/Valhalla Highway/A 1/Remscheid/Wermelskirchen/Hückeswagen",
            signs.GetGuideString(0, false));
        Assert.Equal("US 322 West/US 22 West/A 1/Remscheid", signs.GetGuideString(4, false));
        Assert.Equal("US 322 West/US 22 West/A 1", signs.GetGuideString(3, false));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(2, false));
        Assert.Equal("US 322 West", signs.GetGuideString(1, false));

        Assert.Equal("US 322 West/US 22 West/A 1/Remscheid", signs.GetGuideString(0, true));
        Assert.Equal("US 322 West/US 22 West/A 1/Remscheid", signs.GetGuideString(4, true));
        Assert.Equal("US 322 West/US 22 West/A 1", signs.GetGuideString(3, true));
        Assert.Equal("US 322 West/A 1", signs.GetGuideString(2, true));
        Assert.Equal("US 322 West", signs.GetGuideString(1, true));
    }

    [Fact]
    public void TestGetJunctionNameString()
    {
        Signs signs = GetJunctionNameSigns(new (string, bool, uint)[] { ("万年橋東", false, 1), ("Mannenbashi East", false, 0) });

        Assert.Equal("万年橋東/Mannenbashi East", signs.GetJunctionNameString(4, false));
        Assert.Equal("万年橋東/Mannenbashi East", signs.GetJunctionNameString(2, false));
        Assert.Equal("万年橋東", signs.GetJunctionNameString(1, false));

        Assert.Equal("万年橋東", signs.GetJunctionNameString(4, true));
        Assert.Equal("万年橋東", signs.GetJunctionNameString(2, true));
        Assert.Equal("万年橋東", signs.GetJunctionNameString(1, true));
    }

    [Fact]
    public void TestGetGuideSigns_BranchOnly()
    {
        var us322West = ("US 322 West", true, 1u);
        var us22West = ("US 22 West", true, 0u);
        var freedomHighway = ("Freedom Highway", false, 0u);
        var valhallaHighway = ("Valhalla Highway", false, 0u);

        Signs signs = GetGuideSigns(
            new[] { us322West, us22West, freedomHighway, valhallaHighway },
            System.Array.Empty<(string, bool, uint)>());

        TryGetGuideSigns(signs, 0, false, new[] { us322West, us22West, freedomHighway, valhallaHighway });
        TryGetGuideSigns(signs, 10, false, new[] { us322West, us22West, freedomHighway, valhallaHighway });
        TryGetGuideSigns(signs, 4, false, new[] { us322West, us22West, freedomHighway, valhallaHighway });
        TryGetGuideSigns(signs, 3, false, new[] { us322West, us22West, freedomHighway });
        TryGetGuideSigns(signs, 2, false, new[] { us322West, us22West });
        TryGetGuideSigns(signs, 1, false, new[] { us322West });

        TryGetGuideSigns(signs, 0, true, new[] { us322West });
        TryGetGuideSigns(signs, 4, true, new[] { us322West });
        TryGetGuideSigns(signs, 3, true, new[] { us322West });
        TryGetGuideSigns(signs, 2, true, new[] { us322West });
        TryGetGuideSigns(signs, 1, true, new[] { us322West });
    }

    [Fact]
    public void TestGetGuideSigns_TowardOnly()
    {
        var a1 = ("A 1", true, 1u);
        var remscheid = ("Remscheid", false, 1u);
        var wermelskirchen = ("Wermelskirchen", false, 0u);
        var huckeswagen = ("Hückeswagen", false, 0u);

        Signs signs = GetGuideSigns(
            System.Array.Empty<(string, bool, uint)>(),
            new[] { a1, remscheid, wermelskirchen, huckeswagen });

        TryGetGuideSigns(signs, 0, false, new[] { a1, remscheid, wermelskirchen, huckeswagen });
        TryGetGuideSigns(signs, 10, false, new[] { a1, remscheid, wermelskirchen, huckeswagen });
        TryGetGuideSigns(signs, 4, false, new[] { a1, remscheid, wermelskirchen, huckeswagen });
        TryGetGuideSigns(signs, 3, false, new[] { a1, remscheid, wermelskirchen });
        TryGetGuideSigns(signs, 2, false, new[] { a1, remscheid });
        TryGetGuideSigns(signs, 1, false, new[] { a1 });

        TryGetGuideSigns(signs, 0, true, new[] { a1, remscheid });
        TryGetGuideSigns(signs, 4, true, new[] { a1, remscheid });
        TryGetGuideSigns(signs, 3, true, new[] { a1, remscheid });
        TryGetGuideSigns(signs, 2, true, new[] { a1, remscheid });
        TryGetGuideSigns(signs, 1, true, new[] { a1 });
    }

    [Fact]
    public void TestGetGuideSigns_NoConsecutiveCount()
    {
        var us322West = ("US 322 West", true, 0u);
        var us22West = ("US 22 West", true, 0u);
        var freedomHighway = ("Freedom Highway", false, 0u);
        var valhallaHighway = ("Valhalla Highway", false, 0u);
        var a1 = ("A 1", true, 0u);
        var remscheid = ("Remscheid", false, 0u);
        var wermelskirchen = ("Wermelskirchen", false, 0u);
        var huckeswagen = ("Hückeswagen", false, 0u);

        Signs signs = GetGuideSigns(
            new[] { us322West, us22West, freedomHighway, valhallaHighway },
            new[] { a1, remscheid, wermelskirchen, huckeswagen });

        TryGetGuideSigns(signs, 0, false, new[] { us322West, us22West, freedomHighway, valhallaHighway, a1, remscheid, wermelskirchen, huckeswagen });
        TryGetGuideSigns(signs, 4, false, new[] { us322West, us22West, a1, remscheid });
        TryGetGuideSigns(signs, 3, false, new[] { us322West, us22West, a1 });
        TryGetGuideSigns(signs, 2, false, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 1, false, new[] { us322West });

        TryGetGuideSigns(signs, 0, true, new[] { us322West, us22West, freedomHighway, valhallaHighway, a1, remscheid, wermelskirchen, huckeswagen });
        TryGetGuideSigns(signs, 4, true, new[] { us322West, us22West, a1, remscheid });
        TryGetGuideSigns(signs, 3, true, new[] { us322West, us22West, a1 });
        TryGetGuideSigns(signs, 2, true, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 1, true, new[] { us322West });
    }

    [Fact]
    public void TestGetGuideSigns_SingleConsecutiveCount()
    {
        var us322West = ("US 322 West", true, 1u);
        var us22West = ("US 22 West", true, 0u);
        var freedomHighway = ("Freedom Highway", false, 0u);
        var valhallaHighway = ("Valhalla Highway", false, 0u);
        var a1 = ("A 1", true, 1u);
        var remscheid = ("Remscheid", false, 0u);
        var wermelskirchen = ("Wermelskirchen", false, 0u);
        var huckeswagen = ("Hückeswagen", false, 0u);

        Signs signs = GetGuideSigns(
            new[] { us322West, us22West, freedomHighway, valhallaHighway },
            new[] { a1, remscheid, wermelskirchen, huckeswagen });

        TryGetGuideSigns(signs, 0, false, new[] { us322West, us22West, freedomHighway, valhallaHighway, a1, remscheid, wermelskirchen, huckeswagen });
        TryGetGuideSigns(signs, 4, false, new[] { us322West, us22West, a1, remscheid });
        TryGetGuideSigns(signs, 3, false, new[] { us322West, us22West, a1 });
        TryGetGuideSigns(signs, 2, false, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 1, false, new[] { us322West });

        TryGetGuideSigns(signs, 0, true, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 4, true, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 3, true, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 2, true, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 1, true, new[] { us322West });
    }

    [Fact]
    public void TestGetGuideSigns_MultipleConsecutiveCount()
    {
        var us322West = ("US 322 West", true, 2u);
        var us22West = ("US 22 West", true, 2u);
        var freedomHighway = ("Freedom Highway", false, 1u);
        var valhallaHighway = ("Valhalla Highway", false, 0u);
        var a1 = ("A 1", true, 2u);
        var remscheid = ("Remscheid", false, 2u);
        var wermelskirchen = ("Wermelskirchen", false, 1u);
        var huckeswagen = ("Hückeswagen", false, 0u);

        Signs signs = GetGuideSigns(
            new[] { us322West, us22West, freedomHighway, valhallaHighway },
            new[] { a1, remscheid, wermelskirchen, huckeswagen });

        TryGetGuideSigns(signs, 0, false, new[] { us322West, us22West, freedomHighway, valhallaHighway, a1, remscheid, wermelskirchen, huckeswagen });
        TryGetGuideSigns(signs, 4, false, new[] { us322West, us22West, a1, remscheid });
        TryGetGuideSigns(signs, 3, false, new[] { us322West, us22West, a1 });
        TryGetGuideSigns(signs, 2, false, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 1, false, new[] { us322West });

        TryGetGuideSigns(signs, 0, true, new[] { us322West, us22West, a1, remscheid });
        TryGetGuideSigns(signs, 4, true, new[] { us322West, us22West, a1, remscheid });
        TryGetGuideSigns(signs, 3, true, new[] { us322West, us22West, a1 });
        TryGetGuideSigns(signs, 2, true, new[] { us322West, a1 });
        TryGetGuideSigns(signs, 1, true, new[] { us322West });
    }

    [Fact]
    public void TestTrimSigns()
    {
        var us322West = ("US 322 West", true, 2u);
        var us22West = ("US 22 West", true, 2u);
        var freedomHighway = ("Freedom Highway", false, 1u);
        var valhallaHighway = ("Valhalla Highway", false, 0u);

        var signs = new List<OdinSign>();
        PopulateSigns(new[] { us322West, us22West, freedomHighway, valhallaHighway }, signs);

        TryTrimSigns(Signs.TrimSigns(signs), new[] { us322West, us22West, freedomHighway, valhallaHighway });
        TryTrimSigns(Signs.TrimSigns(signs, 10, false), new[] { us322West, us22West, freedomHighway, valhallaHighway });
        TryTrimSigns(Signs.TrimSigns(signs, 0, false), new[] { us322West, us22West, freedomHighway, valhallaHighway });
        TryTrimSigns(Signs.TrimSigns(signs, 4, false), new[] { us322West, us22West, freedomHighway, valhallaHighway });
        TryTrimSigns(Signs.TrimSigns(signs, 3, false), new[] { us322West, us22West, freedomHighway });
        TryTrimSigns(Signs.TrimSigns(signs, 2, false), new[] { us322West, us22West });
        TryTrimSigns(Signs.TrimSigns(signs, 1, false), new[] { us322West });

        TryTrimSigns(Signs.TrimSigns(signs, 10, true), new[] { us322West, us22West });
        TryTrimSigns(Signs.TrimSigns(signs, 4, true), new[] { us322West, us22West });
        TryTrimSigns(Signs.TrimSigns(signs, 3, true), new[] { us322West, us22West });
        TryTrimSigns(Signs.TrimSigns(signs, 2, true), new[] { us322West, us22West });
        TryTrimSigns(Signs.TrimSigns(signs, 1, false), new[] { us322West });
    }
}
