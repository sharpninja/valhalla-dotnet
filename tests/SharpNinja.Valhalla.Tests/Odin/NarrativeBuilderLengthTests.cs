// Tests for the ported odin NarrativeBuilder localized distance strings (FormLength /
// FormMetricLength / FormUsCustomaryLength) and the verbal alert approach instruction (en-US).
//
// The rounding rules and boundary values mirror src/odin/narrativebuilder.cc FormMetricLength /
// FormUsCustomaryLength; the metric/us arrays come from the en-US.json post_transition_verbal /
// approach_verbal_alert subsets. Approach boundary values are taken from the upstream
// test/narrativebuilder.cc TestFormVerbalAlertApproachInstruction oracle.

using SharpNinja.Valhalla.Odin;
using SharpNinja.Valhalla.Sif;

namespace SharpNinja.Valhalla.Tests.Odin;

public class NarrativeBuilderLengthTests
{
    private static readonly NarrativeDictionary Dict = NarrativeDictionaryLoader.Get("en-US");

    private static readonly System.Collections.Generic.IReadOnlyList<string> Metric =
        Dict.PostTransitionVerbalSubset.MetricLengths;

    private static readonly System.Collections.Generic.IReadOnlyList<string> UsCustomary =
        Dict.PostTransitionVerbalSubset.UsCustomaryLengths;

    private static NarrativeBuilder Builder(OptionsUnits units = OptionsUnits.Kilometers)
        => NarrativeBuilderFactory.Create(new Options { Units = units }, null, Dict);

    [Theory]
    [InlineData(0.005f, "less than 10 meters")]
    [InlineData(0.05f, "50 meters")]
    [InlineData(0.09f, "90 meters")]
    [InlineData(0.095f, "100 meters")]
    [InlineData(0.125f, "100 meters")]
    [InlineData(0.16f, "200 meters")]
    [InlineData(0.4f, "400 meters")]
    [InlineData(0.8f, "800 meters")]
    [InlineData(1.0f, "1 kilometer")]
    [InlineData(1.5f, "1.5 kilometers")]
    [InlineData(2.0f, "2 kilometers")]
    [InlineData(2.5f, "2.5 kilometers")]
    [InlineData(3.0f, "3 kilometers")]
    [InlineData(4.0f, "4 kilometers")]
    [InlineData(10.4f, "10 kilometers")]
    public void FormMetricLength_Boundaries(float kilometers, string expected)
        => Assert.Equal(expected, Builder().FormMetricLength(kilometers, Metric));

    [Theory]
    [InlineData(0.001f, "less than 10 feet")]
    [InlineData(0.05f, "300 feet")]
    [InlineData(0.1f, "500 feet")]
    [InlineData(0.125f, "700 feet")]
    [InlineData(0.25f, "a quarter mile")]
    [InlineData(0.5f, "a half mile")]
    [InlineData(1.0f, "1 mile")]
    [InlineData(1.5f, "1.5 miles")]
    [InlineData(2.0f, "2 miles")]
    [InlineData(3.0f, "3 miles")]
    [InlineData(10.0f, "10 miles")]
    public void FormUsCustomaryLength_Boundaries(float miles, string expected)
        => Assert.Equal(expected, Builder().FormUsCustomaryLength(miles, UsCustomary));

    [Fact]
    public void FormLength_UsesMetricWhenKilometers()
        => Assert.Equal("400 meters", Builder(OptionsUnits.Kilometers).FormLength(0.4f, Metric, UsCustomary));

    [Fact]
    public void FormLength_UsesUsCustomaryWhenMiles()
        => Assert.Equal("a quarter mile", Builder(OptionsUnits.Miles).FormLength(0.25f, Metric, UsCustomary));

    [Theory]
    [InlineData(0.125f, "In 100 meters, Turn right onto Main Street.")]
    [InlineData(1.0f, "In 1 kilometer, Take exit 1 30.")]
    public void FormVerbalAlertApproachInstruction_Metric(float distance, string expected)
    {
        string cue = distance == 1.0f ? "Take exit 1 30." : "Turn right onto Main Street.";
        Assert.Equal(expected, Builder(OptionsUnits.Kilometers).FormVerbalAlertApproachInstruction(distance, cue));
    }

    [Theory]
    [InlineData(0.125f, "In 700 feet, Turn right onto Main Street")]
    [InlineData(0.25f, "In a quarter mile, Turn right onto Main Street")]
    [InlineData(0.5f, "In a half mile, Turn right onto Main Street")]
    public void FormVerbalAlertApproachInstruction_UsCustomary(float distance, string expected)
        => Assert.Equal(expected,
            Builder(OptionsUnits.Miles).FormVerbalAlertApproachInstruction(distance, "Turn right onto Main Street"));
}
