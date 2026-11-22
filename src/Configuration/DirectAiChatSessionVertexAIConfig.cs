namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Localized generation parameters for the Vertex AI Enterprise session.
/// Ensures Vertex workloads can be tuned independently of AI Studio workloads.
/// </summary>
public class DirectAiChatSessionVertexAIConfig {
    public float Temperature { get; set; } = 0.35f;
    public float TopP { get; set; } = 0.9f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 300000;
    public int? ThinkingBudget { get; set; } = 4096;
    public string? ThinkingLevel { get; set; } = "MEDIUM";

    public bool UseContextCaching { get; set; } = false;
    public int ContextCachingMinutes { get; set; } = 15;
    public int ContextCachingIncrementMinutes { get; set; } = 30;
    public bool UseGoogleSearch { get; set; } = false;
}