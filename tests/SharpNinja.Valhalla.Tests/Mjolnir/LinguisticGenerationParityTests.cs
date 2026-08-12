using System.Collections.Generic;

using SharpNinja.Valhalla.Baldr;
using SharpNinja.Valhalla.Midgard;
using SharpNinja.Valhalla.Mjolnir;

namespace SharpNinja.Valhalla.Tests.Mjolnir;

public sealed class LinguisticGenerationParityTests
{
    [Fact]
    public void ManagedNamesAndPronunciations_MatchOfficialFixture()
    {
        var names = new UniqueNames();
        var way = new OSMWay(383)
        {
            RefIndex = names.Index("US 41"),
            NameIndex = names.Index("Murfreesboro Road"),
        };

        way.AddLinguisticName(
            OSMLinguisticType.Name,
            Language.Es,
            "Camino Murfreesboro");
        way.AddPronunciation(
            OSMLinguisticType.Ref,
            Language.En,
            PronunciationAlphabet.NtSampa,
            "you ess forty one");
        way.AddPronunciation(
            OSMLinguisticType.Name,
            Language.En,
            PronunciationAlphabet.Ipa,
            "mur frees burrow");

        OSMWayNameData nameData = OSMWayLinguisticBuilder.Build(
            way,
            string.Empty,
            names,
            forward: true);

        Assert.Equal(
            new[] { "US 41", "Murfreesboro Road", "Camino Murfreesboro" },
            nameData.Names);
        Assert.Equal(0b0000_0000_0000_0001, nameData.Types);
        Assert.Equal(3, nameData.Linguistics.Count);

        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));
        builder.DirectedEdges.Add(new DirectedEdge());

        uint offset = builder.AddEdgeInfo(
            0,
            new GraphId(0, 2, 0),
            new GraphId(0, 2, 1),
            way.WayId(),
            GraphConstants.NoElevationData,
            0,
            55,
            new List<PointLL> { new(0, 0), new(1, 1) },
            nameData.Names,
            System.Array.Empty<string>(),
            nameData.Linguistics,
            nameData.Types,
            out bool added);

        Assert.True(added);
        DirectedEdge edge = builder.DirectedEdges[0];
        edge.SetEdgeInfoOffset(offset);
        builder.DirectedEdges[0] = edge;

        GraphTile tile = GraphTile.Create(new GraphId(0, 2, 0), builder.StoreTileData());
        EdgeInfo edgeInfo = tile.EdgeInfo(tile.DirectedEdge(0));

        Assert.Equal(nameData.Names, edgeInfo.GetNames());
        Assert.Equal(nameData.Types, edgeInfo.GetTypes());

        Dictionary<byte, (byte Language, byte PhoneticAlphabet, string Pronunciation)> linguistics =
            edgeInfo.GetLinguisticMap();

        Assert.Equal(3, linguistics.Count);
        Assert.Equal(
            ((byte)Language.En, (byte)PronunciationAlphabet.NtSampa, "you ess forty one"),
            linguistics[0]);
        Assert.Equal(
            ((byte)Language.En, (byte)PronunciationAlphabet.Ipa, "mur frees burrow"),
            linguistics[1]);
        Assert.Equal(
            ((byte)Language.Es, (byte)PronunciationAlphabet.None, string.Empty),
            linguistics[2]);

        List<string> rawRecords = edgeInfo.GetLinguisticTaggedValues();
        Assert.Equal(3, rawRecords.Count);

        LinguisticTextHeader refHeader = ReadHeader(rawRecords[0]);
        Assert.Equal((byte)Language.En, refHeader.Language);
        Assert.Equal((byte)PronunciationAlphabet.NtSampa, refHeader.PhoneticAlphabet);
        Assert.Equal((byte)0, refHeader.NameIndex);
        Assert.Equal((byte)"you ess forty one".Length, refHeader.Length);

        LinguisticTextHeader nameHeader = ReadHeader(rawRecords[1]);
        Assert.Equal((byte)Language.En, nameHeader.Language);
        Assert.Equal((byte)PronunciationAlphabet.Ipa, nameHeader.PhoneticAlphabet);
        Assert.Equal((byte)1, nameHeader.NameIndex);

        LinguisticTextHeader spanishHeader = ReadHeader(rawRecords[2]);
        Assert.Equal((byte)Language.Es, spanishHeader.Language);
        Assert.Equal((byte)PronunciationAlphabet.None, spanishHeader.PhoneticAlphabet);
        Assert.Equal((byte)2, spanishHeader.NameIndex);
        Assert.Equal((byte)0, spanishHeader.Length);
    }

    [Fact]
    public void UnicodeNamesAndPronunciations_RoundTripAsUtf8()
    {
        var names = new UniqueNames();
        var way = new OSMWay(384)
        {
            NameIndex = names.Index("Rue de l'École"),
        };

        way.AddLinguisticName(OSMLinguisticType.Name, Language.Ja, "学校通り");
        way.AddPronunciation(
            OSMLinguisticType.Name,
            Language.Fr,
            PronunciationAlphabet.Ipa,
            "e.kɔl");

        OSMWayNameData nameData = OSMWayLinguisticBuilder.Build(
            way,
            string.Empty,
            names,
            forward: true);

        var builder = new GraphTileBuilder(new GraphId(0, 2, 0));
        builder.DirectedEdges.Add(new DirectedEdge());
        uint offset = builder.AddEdgeInfo(
            0,
            new GraphId(0, 2, 0),
            new GraphId(0, 2, 1),
            way.WayId(),
            GraphConstants.NoElevationData,
            0,
            30,
            new List<PointLL> { new(0, 0), new(1, 1) },
            nameData.Names,
            System.Array.Empty<string>(),
            nameData.Linguistics,
            nameData.Types,
            out _);

        DirectedEdge edge = builder.DirectedEdges[0];
        edge.SetEdgeInfoOffset(offset);
        builder.DirectedEdges[0] = edge;

        GraphTile tile = GraphTile.Create(new GraphId(0, 2, 0), builder.StoreTileData());
        EdgeInfo edgeInfo = tile.EdgeInfo(tile.DirectedEdge(0));

        Assert.Equal(new[] { "Rue de l'École", "学校通り" }, edgeInfo.GetNames());
        Assert.Equal(
            ((byte)Language.Fr, (byte)PronunciationAlphabet.Ipa, "e.kɔl"),
            edgeInfo.GetLinguisticMap()[0]);
        Assert.Equal(
            ((byte)Language.Ja, (byte)PronunciationAlphabet.None, string.Empty),
            edgeInfo.GetLinguisticMap()[1]);
    }

    private static LinguisticTextHeader ReadHeader(string value)
    {
        uint word = (byte)value[0]
            | ((uint)(byte)value[1] << 8)
            | ((uint)(byte)value[2] << 16);
        return new LinguisticTextHeader(word);
    }
}
