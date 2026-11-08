using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;

namespace LectureExtraction.Tests;

/// <summary>
/// Guards the Phase 9 JSON migration: legacy flat config keys are lifted into the new nested
/// sections (<c>Generation</c>, <c>ModelSelection</c>, <c>Paths</c>, …) and must still bind onto
/// the config class afterwards.
///
/// <para>This is the highest-risk code in the configuration layer: <see cref="ConfigLoader{T}"/>
/// runs the migrator against the user's live <c>*.json</c> on load and <b>rewrites the file in
/// place</b>. Microsoft.Extensions.Configuration silently ignores keys it cannot match, so a
/// migrator that moves a value into a section the class does not expose loses that value with no
/// error at all - the user simply finds their setting reverted to a C# default.</para>
/// </summary>
public class ConfigMigratorTests {
    /// <summary>
    /// Mirrors what <see cref="ConfigLoader{T}"/> does after migrating: bind the migrated JSON
    /// onto a fresh instance through <see cref="ConfigLayerBinder"/>, which replaces the lists the
    /// JSON sets instead of appending to their defaults.
    /// </summary>
    private static T MigrateAndBind<T>(string legacyJson) where T : class, new() {
        var root = JObject.Parse(legacyJson);
        ConfigMigrator.Migrate(root, typeof(T));

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(root.ToString()));
        var configuration = new ConfigurationBuilder().AddJsonStream(stream).Build();

        var config = new T();
        ConfigLayerBinder.Bind(configuration, config);
        return config;
    }

    [Fact]
    public void LegacyModelArrayAndIndex_SurviveMigrationAndBinding() {
        // The shipped AiStudioAutoExtractionConfig.json carries 5 models where the C# default has
        // 3, and a persisted CurrentModelIndex. Both must survive - losing them silently reverts
        // the user's model list and their saved selection.
        string legacy = """
            {
              "Model": [
                "gemini-3.6-flash",
                "gemini-3.5-flash",
                "gemini-3-flash-preview",
                "gemini-3.5-flash-lite",
                "gemini-2.5-flash"
              ],
              "CurrentModelIndex": 3
            }
            """;

        var config = MigrateAndBind<AiStudioAutoExtractionConfig>(legacy);

        Assert.Equal(5, config.Model.Length);
        Assert.Contains("gemini-2.5-flash", config.Model);
        Assert.Equal(3, config.CurrentModelIndex);
        Assert.Equal("gemini-3.5-flash-lite", config.CurrentModel);
    }

    [Fact]
    public void LegacyGenerationParameters_SurviveMigrationAndBinding() {
        string legacy = """
            {
              "Temperature": 0.11,
              "TopP": 0.22,
              "TopK": 33,
              "MaxOutputTokens": 4444,
              "ThinkingBudget": 5555,
              "ThinkingLevel": "LOW"
            }
            """;

        var config = MigrateAndBind<AiStudioAutoExtractionConfig>(legacy);

        Assert.Equal(0.11f, config.Temperature, 3);
        Assert.Equal(0.22f, config.TopP, 3);
        Assert.Equal(33, config.TopK);
        Assert.Equal(4444, config.MaxOutputTokens);
        Assert.Equal(5555, config.ThinkingBudget);
        Assert.Equal("LOW", config.ThinkingLevel);
    }

    /// <summary>
    /// The paths migrate as before; prompt lists in an old config are ignored, because Stage 1 takes its prompt
    /// files from Stage1PromptFiles. Neither the flat keys nor a Sources section can set or empty them.
    /// </summary>
    [Fact]
    public void LegacyPaths_SurviveMigrationAndBinding_PromptListsStayCompiledIn() {
        string legacy = """
            {
              "SourceFolder": "D:\\videos\\src",
              "TargetFolder": "D:\\videos\\out",
              "LogFolder": "D:\\logs",
              "SystemInstructionPaths": ["C:\\prompts\\a.md", "C:\\prompts\\b.md"],
              "HistoryPreloadPaths": ["C:\\prompts\\history"]
            }
            """;
        string withSources = """
            {
              "Sources": {
                "SystemInstructionPaths": ["C:\\prompts\\a.md"],
                "HistoryPreloadPaths": []
              }
            }
            """;

        var config = MigrateAndBind<AiStudioAutoExtractionConfig>(legacy);
        var vertex = MigrateAndBind<VertexAutoExtractionConfig>(withSources);

        Assert.Equal(@"D:\videos\src", config.SourceFolder);
        Assert.Equal(@"D:\videos\out", config.TargetFolder);
        Assert.Equal(@"D:\logs", config.LogFolder);
        Assert.Equal(Stage1PromptFiles.SystemInstructionPaths, config.SystemInstructionPaths);
        Assert.Equal(Stage1PromptFiles.HistoryPaths, config.HistoryPreloadPaths);
        Assert.Equal(Stage1PromptFiles.SystemInstructionPaths, vertex.SystemInstructionPaths);
        Assert.Equal(Stage1PromptFiles.HistoryPaths, vertex.HistoryPreloadPaths);
    }

    [Fact]
    public void LegacyApiKeyProfile_SurvivesMigrationAndBinding() {
        string legacy = """
            {
              "ActiveApiProfile": 2,
              "AiStudioApiKeyEnvNames": ["KEY_A", "KEY_B", "KEY_C"]
            }
            """;

        var config = MigrateAndBind<AiStudioAutoExtractionConfig>(legacy);

        Assert.Equal(2, config.ActiveApiProfile);
        Assert.Equal(3, config.AiStudioApiKeyEnvNames.Length);
        Assert.Equal("KEY_C", config.AiStudioApiKeyEnvNames[2]);
    }

    [Fact]
    public void VertexContextCachingAndEndpoint_SurviveMigrationAndBinding() {
        string legacy = """
            {
              "ProjectId": "my-project",
              "Location": "europe-west4",
              "GcsBucketName": "my-bucket",
              "UseContextCaching": true,
              "ContextCachingMinutes": 42,
              "ContextCachingIncrementMinutes": 43,
              "ContextCachingMinimumRemainingMinutes": 44
            }
            """;

        var config = MigrateAndBind<VertexAutoExtractionConfig>(legacy);

        Assert.Equal("my-project", config.ProjectId);
        Assert.Equal("europe-west4", config.Location);
        Assert.Equal("my-bucket", config.GcsBucketName);
        Assert.True(config.UseContextCaching);
        Assert.Equal(42, config.ContextCachingMinutes);
        Assert.Equal(43, config.ContextCachingIncrementMinutes);
        Assert.Equal(44, config.ContextCachingMinimumRemainingMinutes);
    }

    [Fact]
    public void Migration_IsIdempotent() {
        string legacy = """
            {
              "Temperature": 0.11,
              "Model": ["a-model", "b-model"],
              "CurrentModelIndex": 1,
              "SourceFolder": "D:\\src"
            }
            """;

        var root = JObject.Parse(legacy);
        Assert.True(ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig)));
        string afterFirst = root.ToString();

        // A second pass over already-migrated JSON must report "nothing to do" and change nothing,
        // because ConfigLoader runs the migrator on every single Load().
        Assert.False(ConfigMigrator.Migrate(root, typeof(AiStudioAutoExtractionConfig)));
        Assert.Equal(afterFirst, root.ToString());
    }

    [Fact]
    public void MigrationDoesNotLeakAcrossConfigTypes() {
        // LatexRefinementSessionConfig and FfmpegSessionConfig keep flat SourceFolder/TargetFolder.
        // If the migrator ignored its configType argument it would move them into a "Paths" object
        // the class does not expose, silently blanking both folders.
        string legacy = """
            {
              "SourceFolder": "D:\\refine\\in",
              "TargetFolder": "D:\\refine\\out"
            }
            """;

        var root = JObject.Parse(legacy);
        ConfigMigrator.Migrate(root, typeof(LatexRefinementSessionConfig));

        Assert.Null(root["Paths"]);
        Assert.Equal(@"D:\refine\in", root["SourceFolder"]?.ToString());
    }

    [Fact]
    public void LatexRefinementBackendParameters_MigratesFlatKeys_AndDeduplicatesAvailableModels() {
        string jsonWithLegacyAndDuplicates = """
            {
              "Step1MergeAndTimestamp": {
                "AiStudio": {
                  "Temperature": 0.4,
                  "Model": [
                    "gemini-3.8-flash",
                    "gemini-3.7-flash"
                  ],
                  "Generation": {
                    "Temperature": 0.4
                  },
                  "ModelSelection": {
                    "Available": [
                      "gemini-3.8-flash",
                      "gemini-3.7-flash",
                      "gemini-3.8-flash",
                      "gemini-3.7-flash"
                    ],
                    "CurrentIndex": 0
                  }
                }
              }
            }
            """;

        var root = JObject.Parse(jsonWithLegacyAndDuplicates);
        bool migrated = ConfigMigrator.Migrate(root, typeof(LatexRefinementSessionConfig));
        Assert.True(migrated);

        var aiStudio = (JObject)root["Step1MergeAndTimestamp"]!["AiStudio"]!;
        Assert.Null(aiStudio["Model"]);
        Assert.Null(aiStudio["Temperature"]);

        var available = (JArray)aiStudio["ModelSelection"]!["Available"]!;
        Assert.Equal(2, available.Count);
        Assert.Equal("gemini-3.8-flash", available[0].ToString());
        Assert.Equal("gemini-3.7-flash", available[1].ToString());

        var config = MigrateAndBind<LatexRefinementSessionConfig>(jsonWithLegacyAndDuplicates);
        Assert.Equal(2, config.Step1MergeAndTimestamp.AiStudio.ModelSelection.Available.Length);
        Assert.Equal("gemini-3.8-flash", config.Step1MergeAndTimestamp.AiStudio.ModelSelection.Available[0]);
        Assert.Equal("gemini-3.7-flash", config.Step1MergeAndTimestamp.AiStudio.ModelSelection.Available[1]);
    }

    [Fact]
    public void ModelSelection_Available_DeduplicatesPreservingOrder() {
        var selection = new ModelSelection {
            Available = ["gemini-3.8-flash", "gemini-3.7-flash", "gemini-3.8-flash", "gemini-3.6-flash", "gemini-3.7-flash"]
        };

        Assert.Equal(3, selection.Available.Length);
        Assert.Equal("gemini-3.8-flash", selection.Available[0]);
        Assert.Equal("gemini-3.7-flash", selection.Available[1]);
        Assert.Equal("gemini-3.6-flash", selection.Available[2]);
    }
}
