namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] The common configuration properties needed by <see cref="DirectAiChatSession"/> across both AI Studio and Vertex backends.
/// [Human] Gemeinsame Konfigurationseinstellungen für die Chat-Sitzungen in AI Studio und Vertex.
/// </summary>
public interface IChatConfig {
    string UploadFolder { get; }
    string LogFolder { get; }
    string[] HistoryPreloadPaths { get; set; }
    string SystemInstructionPath { get; set; }
    string[] Model { get; }
    int CurrentModelIndex { get; set; }
    string CurrentModel { get; set; }
    string GcsBucketName { get; }
    string[] IncludePaths { get; }
    bool UseGoogleSearch { get; }
    bool VerboseConsoleOutput { get; }

    // Generation parameters
    float Temperature { get; }
    float TopP { get; }
    int TopK { get; }
    int MaxOutputTokens { get; }
    int? ThinkingBudget { get; }
    string? ThinkingLevel { get; }

    void Save();
}
