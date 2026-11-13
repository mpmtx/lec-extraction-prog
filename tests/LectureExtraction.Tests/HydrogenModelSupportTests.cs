using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Google.GenAI.Types;
using LectureExtraction.Chat;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Verifies Gemini Hydrogen (<c>gemini-4-hydrogen</c>) model capabilities and safe older-model
/// fallbacks for <c>ThinkingLevel</c> (XHIGH/higher -> HIGH) and <c>MaxOutputTokens</c>
/// (&gt; 65,536 -> 65,536).
/// </summary>
public class HydrogenModelSupportTests {
    [Theory]
    [InlineData("gemini-4-hydrogen", true)]
    [InlineData("gemini-4-hydrogen-preview", true)]
    [InlineData("models/gemini-4-hydrogen", true)]
    public void SupportsThinking_RecognizesGeminiHydrogen(string modelName, bool expected) {
        Assert.Equal(expected, ModelCapabilities.SupportsThinking(modelName));
    }

    [Theory]
    [InlineData("gemini-4-hydrogen", true)]
    [InlineData("gemini-4-hydrogen-preview", true)]
    [InlineData("models/gemini-4-hydrogen", true)]
    [InlineData("gemini-4-carbon", true)]
    [InlineData("gemini-3.8-flash", false)]
    [InlineData("gemini-3.7-flash", false)]
    [InlineData("gemini-3-flash-preview", false)]
    [InlineData("gemini-2.5-pro", false)]
    [InlineData("gemini-2.5-flash", false)]
    [InlineData("gemma-3-27b", false)]
    [InlineData("", false)]
    public void ExtendedCapabilities_MatchHydrogenAndGemini4ModelsOnly(string modelName, bool expected) {
        Assert.Equal(expected, ModelCapabilities.SupportsXHighThinking(modelName));
        Assert.Equal(expected, ModelCapabilities.SupportsExtendedOutputTokens(modelName));
    }

    [Theory]
    [InlineData("gemini-4-hydrogen", 100000, 100000)]
    [InlineData("gemini-4-hydrogen", 131072, 131072)]
    [InlineData("gemini-4-hydrogen", 65536, 65536)]
    [InlineData("gemini-4-hydrogen", 65535, 65535)]
    [InlineData("gemini-3.8-flash", 100000, 65536)]
    [InlineData("gemini-3.8-flash", 65537, 65536)]
    [InlineData("gemini-3.8-flash", 65536, 65536)]
    [InlineData("gemini-3.8-flash", 65535, 65535)]
    [InlineData("gemini-3.8-flash", 8192, 8192)]
    [InlineData("gemini-3.7-flash", 200000, 65536)]
    [InlineData("gemini-2.5-flash", 100000, 65536)]
    public void ResolveMaxOutputTokens_KeepsConfiguredValueForHydrogenAndCapsOlderModelsAt65536(
        string modelName, int configuredTokens, int expectedTokens) {
        Assert.Equal(expectedTokens, ModelCapabilities.ResolveMaxOutputTokens(modelName, configuredTokens));
    }

    [Theory]
    [InlineData("gemini-4-hydrogen", "XHIGH", "XHIGH")]
    [InlineData("gemini-4-hydrogen", "xhigh", "XHIGH")]
    [InlineData("gemini-4-hydrogen", "HIGH", "HIGH")]
    [InlineData("gemini-4-hydrogen", "MEDIUM", "MEDIUM")]
    [InlineData("gemini-4-hydrogen", "LOW", "LOW")]
    [InlineData("gemini-4-hydrogen", "XXHIGH", "XXHIGH")]
    [InlineData("gemini-3.8-flash", "XHIGH", "HIGH")]
    [InlineData("gemini-3.8-flash", "xhigh", "HIGH")]
    [InlineData("gemini-3.8-flash", "ULTRA", "HIGH")]
    [InlineData("gemini-3.8-flash", "HIGH", "HIGH")]
    [InlineData("gemini-3.8-flash", "MEDIUM", "MEDIUM")]
    [InlineData("gemini-3.8-flash", "low", "LOW")]
    [InlineData("gemini-3.8-flash", "MINIMAL", "MINIMAL")]
    [InlineData("gemini-3.7-flash", "XHIGH", "HIGH")]
    [InlineData("gemini-3-flash-preview", "XHIGH", "HIGH")]
    public void ResolveThinkingLevel_PreservesConfiguredForHydrogenAndFallsBackAboveHighToHighForOlderModels(
        string modelName, string configuredLevel, string expectedLevel) {
        Assert.Equal(expectedLevel, ModelCapabilities.ResolveThinkingLevel(modelName, configuredLevel));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ResolveThinkingLevel_PreservesNullAndEmpty(string? configuredLevel) {
        Assert.Equal(configuredLevel, ModelCapabilities.ResolveThinkingLevel("gemini-4-hydrogen", configuredLevel));
        Assert.Equal(configuredLevel, ModelCapabilities.ResolveThinkingLevel("gemini-3.8-flash", configuredLevel));
    }

    [Fact]
    public void ThinkingConfigFactory_SendsXHighAndOmitsThinkingBudgetForHydrogen() {
        ThinkingConfig? config = ThinkingConfigFactory.Build("gemini-4-hydrogen", 32765, "XHIGH", includeThoughts: true);

        Assert.NotNull(config);
        Assert.Equal("XHIGH", config!.ThinkingLevel.ToString());
        Assert.Null(config.ThinkingBudget);
        Assert.True(config.IncludeThoughts);

        string json = JsonSerializer.Serialize(config);
        Assert.Contains("\"XHIGH\"", json);
    }

    [Fact]
    public void ThinkingConfigFactory_DropsUnsupportedThinkingBudgetForHydrogenWhenLevelIsOmitted() {
        Assert.Null(ThinkingConfigFactory.Build("gemini-4-hydrogen", 4096, null));

        ThinkingConfig? thoughtsOnly = ThinkingConfigFactory.Build("gemini-4-hydrogen", 4096, null, includeThoughts: true);
        Assert.NotNull(thoughtsOnly);
        Assert.Null(thoughtsOnly!.ThinkingBudget);
        Assert.Null(thoughtsOnly.ThinkingLevel);
        Assert.True(thoughtsOnly.IncludeThoughts);
    }

    [Theory]
    [InlineData("XHIGH")]
    [InlineData("xhigh")]
    [InlineData("ULTRA")]
    public void ThinkingConfigFactory_FallsBackHigherThanHighToHighOnGemini38(string configuredLevel) {
        ThinkingConfig? config = ThinkingConfigFactory.Build("gemini-3.8-flash", 32765, configuredLevel);

        Assert.NotNull(config);
        Assert.Equal(ThinkingLevel.High, config!.ThinkingLevel);
        Assert.Null(config.ThinkingBudget);
    }

    [Fact]
    public void ThinkingConfigFactory_BuildForWarmupDisabled_AvoidsThinkingBudgetOnHydrogen() {
        ThinkingConfig hydrogenWarmup = ThinkingConfigFactory.BuildForWarmupDisabled("gemini-4-hydrogen");
        Assert.Equal(ThinkingLevel.Low, hydrogenWarmup.ThinkingLevel);
        Assert.Null(hydrogenWarmup.ThinkingBudget);

        ThinkingConfig flashWarmup = ThinkingConfigFactory.BuildForWarmupDisabled("gemini-3.8-flash");
        Assert.Equal(0, flashWarmup.ThinkingBudget);
        Assert.Null(flashWarmup.ThinkingLevel);
    }

    [Fact]
    public void ChatCommandParser_AcceptsXHighThinkingLevelAndListsItOnInvalidValue() {
        ChatCommand valid = ChatCommandParser.Parse("set thinking-level xhigh");
        Assert.Equal(ChatCommandKind.SetThinkingLevel, valid.Kind);
        Assert.Equal("XHIGH", valid.Text);
        Assert.Null(valid.Error);

        ChatCommand invalid = ChatCommandParser.Parse("set thinking-level EXTREME");
        Assert.Contains("MINIMAL, LOW, MEDIUM, HIGH", invalid.Error!);
        Assert.Contains("XHIGH", invalid.Error!);
    }

    [Fact]
    public void HydrogenConfiguredJsonSwitchedTo38Flash_ResolvesToHighAnd65536AndRestoresForHydrogen() {
        const string json = """
        {
          "ModelSelection": {
            "Available": [ "gemini-3.8-flash", "gemini-4-hydrogen" ],
            "CurrentIndex": 0
          },
          "Generation": {
            "MaxOutputTokens": 100000,
            "ThinkingLevel": "XHIGH"
          }
        }
        """;

        var config = new AiStudioAutoExtractionConfig();
        IConfigurationRoot root = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
        ConfigLayerBinder.Bind(root, config);

        // Switching to gemini-3.8-flash keeps JSON values intact in config, while effective API values fall back.
        Assert.Equal("gemini-3.8-flash", config.CurrentModel);
        Assert.Equal(100000, config.MaxOutputTokens);
        Assert.Equal("XHIGH", config.ThinkingLevel);

        Assert.Equal(65536, ModelCapabilities.ResolveMaxOutputTokens(config.CurrentModel, config.MaxOutputTokens));
        Assert.Equal("HIGH", ModelCapabilities.ResolveThinkingLevel(config.CurrentModel, config.ThinkingLevel));

        string olderHeader = TexDocumentWriter.BuildPartHeader(
            1, 2, "part1.mp4", 0, new TokenUsage(100, 50, 50, 10, 0), config.CurrentModel, config.Generation);
        Assert.Contains("MaxOutputTokens: 65536", olderHeader);
        Assert.Contains("ThinkingLevel: HIGH", olderHeader);

        // Selecting gemini-4-hydrogen uses the configured XHIGH / 100000 values verbatim.
        config.CurrentModel = "gemini-4-hydrogen";
        Assert.Equal(100000, ModelCapabilities.ResolveMaxOutputTokens(config.CurrentModel, config.MaxOutputTokens));
        Assert.Equal("XHIGH", ModelCapabilities.ResolveThinkingLevel(config.CurrentModel, config.ThinkingLevel));

        string hydrogenHeader = TexDocumentWriter.BuildPartHeader(
            1, 2, "part1.mp4", 0, new TokenUsage(100, 50, 50, 10, 0), config.CurrentModel, config.Generation);
        Assert.Contains("MaxOutputTokens: 100000", hydrogenHeader);
        Assert.Contains("ThinkingLevel: XHIGH", hydrogenHeader);
    }

    [Fact]
    public void ModelCatalog_IncludesGemini4HydrogenWhileKeepingFlashDefaultFirst() {
        List<string> models = [.. ModelCatalog.Default];
        Assert.Equal("gemini-3.8-flash", models[0]);
        Assert.Contains("gemini-4-hydrogen", models);
    }

    [Theory]
    [InlineData("gemini-4-hydrogen", true)]
    [InlineData("models/gemini-4-hydrogen", true)]
    [InlineData("gemini-4-carbon", true)]
    [InlineData("gemini-3.8-flash", false)]
    [InlineData("gemini-3.7-flash", false)]
    [InlineData("gemini-2.5-pro", false)]
    [InlineData("gemma-3-27b", false)]
    [InlineData("", false)]
    public void SupportsLongDecode_AndSdkAutoContinuation_OnlyEnableForHydrogen(string modelName, bool expected) {
        Assert.Equal(expected, ModelCapabilities.SupportsLongDecode(modelName));
        Assert.Equal(expected, ModelCapabilities.ResolveSdkAutomaticContinuation(modelName, enableForLongDecode: true));
        Assert.False(ModelCapabilities.ResolveSdkAutomaticContinuation(modelName, enableForLongDecode: false));
    }

    [Fact]
    public void PrepareHopConfig_KeepsLegacyPathForOlderModelsAndPassesContinuationTokenForHydrogen() {
        byte[] token = [1, 2, 3, 4];
        var baseConfig = new GenerateContentConfig {
            MaxOutputTokens = 100000,
            Temperature = 0.35f,
            AutomaticContinuation = null
        };

        GenerateContentConfig legacyHop = GenerationLoop.PrepareHopConfig(baseConfig, "gemini-3.8-flash", token);
        Assert.False(legacyHop.AutomaticContinuation);
        Assert.Null(legacyHop.ContinuationToken);

        GenerateContentConfig hydrogenHop = GenerationLoop.PrepareHopConfig(baseConfig, "gemini-4-hydrogen", token);
        Assert.False(hydrogenHop.AutomaticContinuation);
        Assert.Equal(token, hydrogenHop.ContinuationToken);
        Assert.Equal(100000, hydrogenHop.MaxOutputTokens);
        Assert.Null(baseConfig.ContinuationToken);
    }
}
