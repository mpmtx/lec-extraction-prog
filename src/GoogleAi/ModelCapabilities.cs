using System;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Was copy-pasted byte-identically 5x (both extraction sessions, both chat
/// sessions, LatexRefinementSession) before being consolidated here.
/// [Human] Prueft, ob ein Gemini-Modell die "Thinking"-Parameter unterstuetzt. War vorher 5x dupliziert.
/// </summary>
public static class ModelCapabilities {
    /// <summary>Maximum synchronous output token ceiling supported by pre-Hydrogen models (Gemini 2.5 / 3.x).</summary>
    public const int OlderModelMaxOutputTokens = 65536;

    /// <summary>Highest thinking level supported by older thinking models (Gemini 3.x).</summary>
    public const string OlderModelMaxThinkingLevel = "HIGH";

    private static readonly HashSet<string> OlderSupportedThinkingLevels = new(StringComparer.OrdinalIgnoreCase) {
        "MINIMAL", "LOW", "MEDIUM", "HIGH"
    };

    public static bool SupportsThinking(string modelName) {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        return modelName.StartsWith("gemini-2.5", StringComparison.OrdinalIgnoreCase) ||
               modelName.StartsWith("gemini-3", StringComparison.OrdinalIgnoreCase) ||
               IsGemini4OrHydrogen(modelName) ||
               modelName.Contains("thinking", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [AI Context] Gemini Hydrogen / Gemini 4 models accept <c>XHIGH</c> thinking levels in addition to
    /// <c>LOW</c>/<c>MEDIUM</c>/<c>HIGH</c>. Older 3.x models only support up to <c>HIGH</c>.
    /// [Human] Prüft, ob das Modell erweiterte Thinking-Level wie XHIGH unterstützt (Gemini Hydrogen / 4).
    /// </summary>
    public static bool SupportsXHighThinking(string modelName) => IsGemini4OrHydrogen(modelName);

    /// <summary>
    /// [AI Context] Gemini Hydrogen / Gemini 4 models support output budgets above 65,536 tokens, whereas older
    /// models (2.5 / 3.x) cap at 65,536 tokens.
    /// [Human] Prüft, ob das Modell mehr als 65'536 Output-Tokens unterstützt (Gemini Hydrogen / 4).
    /// </summary>
    public static bool SupportsExtendedOutputTokens(string modelName) => IsGemini4OrHydrogen(modelName);

    /// <summary>
    /// [AI Context] Gemini Hydrogen / Gemini 4 models support Long Decode (<c>CONTINUATION</c> finish reason
    /// and <c>ContinuationToken</c>) across per-request 65,536-token hops. Older models stay strictly on
    /// the legacy C# single-request / text continue-prompt path with SDK automatic continuation off.
    /// [Human] Prüft, ob das Modell Long Decode (ContinuationToken) unterstützt; ältere Modelle nutzen den Legacy-Pfad.
    /// </summary>
    public static bool SupportsLongDecode(string modelName) => IsGemini4OrHydrogen(modelName);

    /// <summary>
    /// [AI Context] <c>Google.GenAI</c> 2.1+ turns SDK-level <c>AutomaticContinuation</c> on by default
    /// when left <see langword="null"/>. Forces <see langword="false"/> for older models so they keep legacy
    /// behavior; callers that only want SDK automatic continuation on Hydrogen pass <paramref name="enableForLongDecode"/> = true.
    /// [Human] Deaktiviert die SDK-Auto-Continuation für ältere Modelle explizit (Legacy-C#-Pfad).
    /// </summary>
    public static bool ResolveSdkAutomaticContinuation(string modelName, bool enableForLongDecode = false) =>
        enableForLongDecode && SupportsLongDecode(modelName);

    /// <summary>
    /// [AI Context] Only Gemini 2.5 (and legacy Gemini 3 fallback when ThinkingLevel is omitted) accept
    /// <c>ThinkingBudget</c>; Gemini 4 / Hydrogen rejects <c>thinking_budget</c> with HTTP 400.
    /// [Human] Prüft, ob das Modell einen numerischen ThinkingBudget-Wert unterstützt.
    /// </summary>
    public static bool SupportsThinkingBudget(string modelName) =>
        !string.IsNullOrWhiteSpace(modelName) && SupportsThinking(modelName) && !IsGemini4OrHydrogen(modelName);

    /// <summary>
    /// [AI Context] Caps <paramref name="configuredMaxOutputTokens"/> to <see cref="OlderModelMaxOutputTokens"/> (65,536)
    /// when an older model is selected so a Hydrogen-sized budget in config/JSON does not fail on 3.x/2.5 models.
    /// Hydrogen keeps the configured value unchanged.
    /// [Human] Begrenzt MaxOutputTokens für ältere Modelle auf 65'536 und übernimmt bei Gemini Hydrogen den konfigurierten Wert.
    /// </summary>
    public static int ResolveMaxOutputTokens(string modelName, int configuredMaxOutputTokens) =>
        configuredMaxOutputTokens > OlderModelMaxOutputTokens && !SupportsExtendedOutputTokens(modelName)
            ? OlderModelMaxOutputTokens
            : configuredMaxOutputTokens;

    /// <summary>
    /// [AI Context] Resolves the effective thinking level for <paramref name="modelName"/>. Gemini Hydrogen keeps the
    /// configured level (normalized to uppercase); older models keep <c>MINIMAL</c>/<c>LOW</c>/<c>MEDIUM</c>/<c>HIGH</c>
    /// and fall back any higher level (such as <c>XHIGH</c>) to <see cref="OlderModelMaxThinkingLevel"/> (<c>HIGH</c>).
    /// [Human] Löst das effektive ThinkingLevel für das gewählte Modell auf (XHIGH/höher -> HIGH bei älteren Modellen, konfigurierter Wert bei Hydrogen).
    /// </summary>
    public static string? ResolveThinkingLevel(string modelName, string? configuredThinkingLevel) {
        if (string.IsNullOrWhiteSpace(configuredThinkingLevel)) {
            return configuredThinkingLevel;
        }

        string normalized = configuredThinkingLevel.Trim().ToUpperInvariant();
        if (SupportsXHighThinking(modelName)) {
            return normalized;
        }

        return OlderSupportedThinkingLevels.Contains(normalized)
            ? normalized
            : OlderModelMaxThinkingLevel;
    }

    private static bool IsGemini4OrHydrogen(string modelName) {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        return modelName.StartsWith("gemini-4", StringComparison.OrdinalIgnoreCase) ||
               modelName.Contains("gemini-4-", StringComparison.OrdinalIgnoreCase) ||
               modelName.Contains("hydrogen", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [AI Context] True when the model rejects the dedicated <c>system</c> role and the system
    /// instruction must instead be prepended into the first user turn. Gemma before v4 is the only
    /// such family here.
    ///
    /// <para>This capability is unusually easy to lose: "gemma" appears in no <c>AvailableModels</c>
    /// array, so it is reachable only by typing the model name as freetext, which means no menu
    /// exercises it and no smoke test would notice it disappearing. It was duplicated verbatim in
    /// both chat sessions, expressed inline as
    /// <c>StartsWith("gemma") &amp;&amp; !Contains("gemma-4")</c> in the middle of a method that also
    /// reads session state - so it could not be tested without constructing a chat session. Pulled
    /// out here because it is what it always was: a pure function of the model name.</para>
    /// [Human] Gemma vor v4 kennt keine "system"-Rolle - die System-Instruction muss dann in die
    /// erste Nutzer-Nachricht wandern. Nur über Freitext-Modellnamen erreichbar, daher leicht zu
    /// verlieren und deshalb hier separat und getestet.
    /// </summary>
    public static bool RequiresSystemInstructionInFirstUserTurn(string modelName) {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        return modelName.StartsWith("gemma", StringComparison.OrdinalIgnoreCase)
            && !modelName.Contains("gemma-4", StringComparison.OrdinalIgnoreCase);
    }
}
