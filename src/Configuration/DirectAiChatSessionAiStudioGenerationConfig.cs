namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Localized generation parameters for the Direct AI Chat Session (AI Studio).
/// Dictates the deterministic vs. creative output distribution of the LLM.
/// </summary>
public class DirectAiChatSessionAiStudioGenerationConfig {
    public float Temperature { get; set; } = 0.34f;
    public float TopP { get; set; } = 0.9f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 300000;
    public int? ThinkingBudget { get; set; } = 4096;
    public string? ThinkingLevel { get; set; } = "HIGH";

    public bool UseContextCaching { get; set; } = false;
    public int ContextCachingMinutes { get; set; } = 15;
    public int ContextCachingIncrementMinutes { get; set; } = 30;
    public bool UseGoogleSearch { get; set; } = false;
}
