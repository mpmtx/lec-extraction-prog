using System;
using System.Text.Json.Serialization;
using LectureExtraction.Chat;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] DTO for Vertex AI specific configurations.
/// Composes shared building blocks (VertexEndpoint, WorkspacePaths, ContextSources, ModelSelection).
/// </summary>
public class DirectAiChatSessionVertexConfig : IChatConfig {
    public VertexEndpoint Endpoint { get; set; } = new();
    public WorkspacePaths Paths { get; set; } = new() {
        UploadFolder = AppConfig.UploadFolder,
        LogFolder = AppConfig.LogFolder
    };
    public ContextSources Sources { get; set; } = new() {
        HistoryPreloadPaths = AppConfig.HistoryPreloadPaths,
        SystemInstructionPaths = string.IsNullOrEmpty(AppConfig.SystemInstructionPath) ? [] : [AppConfig.SystemInstructionPath]
    };
    public ModelSelection ModelSelection { get; set; } = new();

    public string[] IncludePaths { get; set; } = [];
    public bool UseGoogleSearch { get; set; } = false;

    /// <summary>Prints full exception objects instead of just their message. Matches the flag of the same name on the extraction configs.</summary>
    public bool VerboseConsoleOutput { get; set; } = false;

    public DirectAiChatSessionVertexAIConfig AI { get; set; } = new();

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string ProjectId { get => Endpoint.ProjectId; set => Endpoint.ProjectId = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string Location { get => Endpoint.Location; set => Endpoint.Location = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string GcsBucketName { get => Endpoint.GcsBucketName; set => Endpoint.GcsBucketName = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string UploadFolder { get => Paths.UploadFolder; set => Paths.UploadFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string LogFolder { get => Paths.LogFolder; set => Paths.LogFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] HistoryPreloadPaths { get => Sources.HistoryPreloadPaths; set => Sources.HistoryPreloadPaths = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string SystemInstructionPath { get => Sources.SystemInstructionPaths.Length > 0 ? Sources.SystemInstructionPaths[0] : ""; set => Sources.SystemInstructionPaths = string.IsNullOrEmpty(value) ? [] : [value]; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] Model { get => ModelSelection.Available; set => ModelSelection.Available = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int CurrentModelIndex { get => ModelSelection.CurrentIndex; set => ModelSelection.CurrentIndex = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string CurrentModel { get => ModelSelection.Current; set => ModelSelection.Current = value; }

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float Temperature { get => AI.Temperature; set => AI.Temperature = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float TopP { get => AI.TopP; set => AI.TopP = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int TopK { get => AI.TopK; set => AI.TopK = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int MaxOutputTokens { get => AI.MaxOutputTokens; set => AI.MaxOutputTokens = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int? ThinkingBudget { get => AI.ThinkingBudget; set => AI.ThinkingBudget = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string? ThinkingLevel { get => AI.ThinkingLevel; set => AI.ThinkingLevel = value; }

    public void Save() => ConfigLoader<DirectAiChatSessionVertexConfig>.Save(this);
}