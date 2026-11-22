using System;
using System.Text.Json;
using LectureExtraction.Configuration;
using Newtonsoft.Json;

namespace LectureExtraction.Tests;

public class NumberOfPartsTests {
    [Fact]
    public void Auto_HasCorrectProperties() {
        var auto = NumberOfParts.Auto;

        Assert.True(auto.IsAuto);
        Assert.Null(auto.FixedParts);
        Assert.Equal("auto", auto.ToString());
    }

    [Fact]
    public void Fixed_HasCorrectProperties() {
        var fixedParts = new NumberOfParts(2);

        Assert.False(fixedParts.IsAuto);
        Assert.Equal(2, fixedParts.FixedParts);
        Assert.Equal("2", fixedParts.ToString());
    }

    [Theory]
    [InlineData("auto", true, 0)]
    [InlineData("AUTO", true, 0)]
    [InlineData("Auto", true, 0)]
    [InlineData("  auto  ", true, 0)]
    [InlineData("1", false, 1)]
    [InlineData("2", false, 2)]
    [InlineData("10", false, 10)]
    public void TryParse_ParsesValidInputs(string input, bool expectedAuto, int expectedFixed) {
        Assert.True(NumberOfParts.TryParse(input, out var parts));
        Assert.Equal(expectedAuto, parts.IsAuto);
        if (!expectedAuto) {
            Assert.Equal(expectedFixed, parts.FixedParts);
        }
    }

    [Theory]
    [InlineData("seven")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_RejectsInvalidInputs(string? input) {
        Assert.False(NumberOfParts.TryParse(input, out _));
    }

    [Fact]
    public void Parse_ThrowsOnInvalidInput() {
        Assert.Throws<FormatException>(() => NumberOfParts.Parse("seven"));
        Assert.Throws<FormatException>(() => NumberOfParts.Parse("-5"));
    }

    [Theory]
    [InlineData(0, 3)] // fallback when duration is zero/unknown
    [InlineData(-10, 3)] // fallback when duration is negative
    // Older models / unspecified model: one part per 22.5 min, rounded, at least 1
    [InlineData(10 * 60, 1)] // 10 min
    [InlineData(30 * 60, 1)] // 30 min
    [InlineData(33 * 60, 1)] // 33 min, just under the 33.75 min boundary
    [InlineData(35 * 60, 2)] // 35 min
    // The real lecture lengths on older models: a single lecture -> 2 parts, a double lecture -> 4
    [InlineData(2568, 2)] // 42m 48s, a Wednesday algebra lecture
    [InlineData(45 * 60, 2)] // 45 min
    [InlineData(2642, 2)] // 44m 02s
    [InlineData(5298, 4)] // 88m 18s, Week 7 Tuesday double lecture
    [InlineData(5400, 4)] // 90 min
    [InlineData(5451, 4)] // 90m 51s, a Tuesday double lecture
    // In between and beyond
    [InlineData(60 * 60, 3)] // 60 min
    [InlineData(75 * 60, 3)] // 75 min
    [InlineData(100 * 60, 4)] // 100 min
    [InlineData(135 * 60, 6)] // 135 min, three lecture units
    public void Auto_ResolvesLegacyParts_WhenModelIsOlderOrOmitted(double durationSeconds, int expectedParts) {
        var auto = NumberOfParts.Auto;
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds));
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds, null));
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds, "gemini-3.8-flash"));
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds, "gemini-3.7-flash"));
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds, "gemini-2.5-flash"));
    }

    [Theory]
    [InlineData(0, 2)] // fallback when duration is zero/unknown on Hydrogen
    [InlineData(-10, 2)] // fallback when duration is negative on Hydrogen
    // Hydrogen: one part per 45 min, rounded, at least 1 (boundaries at 67.5, 112.5, 157.5 min)
    [InlineData(10 * 60, 1)] // 10 min
    [InlineData(30 * 60, 1)] // 30 min
    [InlineData(2568, 1)] // 42m 48s, a Wednesday algebra lecture -> 1 pass
    [InlineData(2634, 1)] // 43m 54s, Week 7 Wednesday -> 1 pass
    [InlineData(2642, 1)] // 44m 02s, Week 1 Wednesday -> 1 pass
    [InlineData(45 * 60, 1)] // 45 min
    [InlineData(60 * 60, 1)] // 60 min
    [InlineData(67 * 60, 1)] // 67 min, just under the 67.5 min boundary
    // 90 min +/- 10 min (and anything in 67.5..112.5 min) -> 2 parts, no 3-part band between 45 and 90 min
    [InlineData(68 * 60, 2)] // 68 min, just above the 67.5 min boundary
    [InlineData(75 * 60, 2)] // 75 min
    [InlineData(80 * 60, 2)] // 80 min (90 - 10)
    [InlineData(5298, 2)] // 88m 18s, Week 7 Tuesday double lecture
    [InlineData(5400, 2)] // 90 min
    [InlineData(5451, 2)] // 90m 51s, a Tuesday double lecture
    [InlineData(100 * 60, 2)] // 100 min (90 + 10)
    [InlineData(110 * 60, 2)] // 110 min, just under the 112.5 min boundary
    // Triple and quadruple lecture units
    [InlineData(113 * 60, 3)] // 113 min, just above the 112.5 min boundary
    [InlineData(135 * 60, 3)] // 135 min, three lecture units
    [InlineData(180 * 60, 4)] // 180 min, four lecture units
    public void Auto_ResolvesHydrogenParts_WhenModelIsHydrogen(double durationSeconds, int expectedParts) {
        var auto = NumberOfParts.Auto;
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds, "gemini-4-hydrogen"));
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds, "models/gemini-4-hydrogen"));
        Assert.Equal(expectedParts, auto.Resolve(durationSeconds, "gemini-4-hydrogen-preview"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30 * 60)]
    [InlineData(45 * 60)]
    [InlineData(90 * 60)]
    [InlineData(180 * 60)]
    public void Fixed_AlwaysResolvesToConfiguredValue_RegardlessOfDurationOrModel(double durationSeconds) {
        var fixedTwo = new NumberOfParts(2);
        Assert.Equal(2, fixedTwo.Resolve(durationSeconds));
        Assert.Equal(2, fixedTwo.Resolve(durationSeconds, "gemini-3.8-flash"));
        Assert.Equal(2, fixedTwo.Resolve(durationSeconds, "gemini-4-hydrogen"));

        var fixedFive = new NumberOfParts(5);
        Assert.Equal(5, fixedFive.Resolve(durationSeconds));
        Assert.Equal(5, fixedFive.Resolve(durationSeconds, "gemini-4-hydrogen"));
    }

    [Fact]
    public void NewtonsoftJson_SerializesAndDeserializes_Correctly() {
        var configWithNumber = new DummyConfig { Parts = 2 };
        string jsonNumber = JsonConvert.SerializeObject(configWithNumber);
        Assert.Contains("\"Parts\":2", jsonNumber);

        var restoredFromNumber = JsonConvert.DeserializeObject<DummyConfig>(jsonNumber);
        Assert.NotNull(restoredFromNumber);
        Assert.False(restoredFromNumber.Parts.IsAuto);
        Assert.Equal(2, restoredFromNumber.Parts.FixedParts);

        var configWithAuto = new DummyConfig { Parts = NumberOfParts.Auto };
        string jsonAuto = JsonConvert.SerializeObject(configWithAuto);
        Assert.Contains("\"Parts\":\"auto\"", jsonAuto);

        var restoredFromAuto = JsonConvert.DeserializeObject<DummyConfig>(jsonAuto);
        Assert.NotNull(restoredFromAuto);
        Assert.True(restoredFromAuto.Parts.IsAuto);

        // Deserializing from JSON string "2"
        string jsonStringNumber = "{\"Parts\": \"2\"}";
        var restoredFromStringNumber = JsonConvert.DeserializeObject<DummyConfig>(jsonStringNumber);
        Assert.NotNull(restoredFromStringNumber);
        Assert.False(restoredFromStringNumber.Parts.IsAuto);
        Assert.Equal(2, restoredFromStringNumber.Parts.FixedParts);
    }

    [Fact]
    public void SystemTextJson_SerializesAndDeserializes_Correctly() {
        var configWithNumber = new DummyConfig { Parts = 2 };
        string jsonNumber = System.Text.Json.JsonSerializer.Serialize(configWithNumber);
        Assert.Contains("\"Parts\":2", jsonNumber);

        var restoredFromNumber = System.Text.Json.JsonSerializer.Deserialize<DummyConfig>(jsonNumber);
        Assert.NotNull(restoredFromNumber);
        Assert.False(restoredFromNumber.Parts.IsAuto);
        Assert.Equal(2, restoredFromNumber.Parts.FixedParts);

        var configWithAuto = new DummyConfig { Parts = NumberOfParts.Auto };
        string jsonAuto = System.Text.Json.JsonSerializer.Serialize(configWithAuto);
        Assert.Contains("\"Parts\":\"auto\"", jsonAuto);

        var restoredFromAuto = System.Text.Json.JsonSerializer.Deserialize<DummyConfig>(jsonAuto);
        Assert.NotNull(restoredFromAuto);
        Assert.True(restoredFromAuto.Parts.IsAuto);
    }

    [Fact]
    public void TypeConverter_ConvertsStrings() {
        var converter = System.ComponentModel.TypeDescriptor.GetConverter(typeof(NumberOfParts));
        Assert.True(converter.CanConvertFrom(typeof(string)));

        var auto = (NumberOfParts)converter.ConvertFromString(null, System.Globalization.CultureInfo.InvariantCulture, "auto")!;
        Assert.True(auto.IsAuto);

        var fixedParts = (NumberOfParts)converter.ConvertFromString(null, System.Globalization.CultureInfo.InvariantCulture, "4")!;
        Assert.False(fixedParts.IsAuto);
        Assert.Equal(4, fixedParts.FixedParts);
    }

    [Fact]
    public void ExtractionConfigs_Deserialize_SendDummyFileDuringTranscription() {
        string json = """
        {
            "NumberOfParts": "auto",
            "SendDummyFileDuringTranscription": false
        }
        """;

        var aiConfig = JsonConvert.DeserializeObject<AiStudioAutoExtractionConfig>(json);
        Assert.NotNull(aiConfig);
        Assert.True(aiConfig.NumberOfParts.IsAuto);
        Assert.False(aiConfig.SendDummyFileDuringTranscription);

        var vertexConfig = JsonConvert.DeserializeObject<VertexAutoExtractionConfig>(json);
        Assert.NotNull(vertexConfig);
        Assert.True(vertexConfig.NumberOfParts.IsAuto);
        Assert.False(vertexConfig.SendDummyFileDuringTranscription);
    }

    private sealed class DummyConfig {
        public NumberOfParts Parts { get; set; } = 3;
    }
}
