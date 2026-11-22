namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Parameters governing Gemini text and token generation (temperature, topP, topK, max tokens, thinking).
/// [Human] KI-Generierungsparameter für Gemini (Temperatur, TopP, Token-Limits, Thinking).
/// </summary>
public class GenerationParameters {
    public float Temperature { get; set; } = 0.35f;
    public float TopP { get; set; } = 0.9f;
    public int TopK { get; set; } = 10;
    public int MaxOutputTokens { get; set; } = 300000;
    public int? ThinkingBudget { get; set; } = 4096;
    public string? ThinkingLevel { get; set; } = "HIGH";
}
