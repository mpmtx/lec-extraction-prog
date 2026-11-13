using System;
using Google.GenAI.Types;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] The one place that turns a stage's thinking settings into a <see cref="ThinkingConfig"/>.
/// The same if/else was copied into every generating call site (both extraction sessions, their
/// warm-up and history requests, the refinement steps and the PDF repair) before it moved here.
///
/// <para>The rule, unchanged: a model without thinking gets no config at all. Gemini 3.x gets only the
/// <c>ThinkingLevel</c> when one is set - its <c>ThinkingBudget</c> is then <b>not sent</b>, so a
/// configured budget does not cap a Gemini 3 request. Gemini 2.5 (and a 3.x config without a level)
/// gets the budget, clamped to Vertex's maximum of 32768.</para>
///
/// <para><paramref name="includeThoughts"/> adds <c>IncludeThoughts = true</c>, creating an otherwise
/// empty config when no level or budget is set. It is ignored for models without thinking.</para>
/// [Human] Baut die Thinking-Konfiguration einer Anfrage; vorher an jeder Aufrufstelle kopiert.
/// </summary>
public static class ThinkingConfigFactory {
    public const int MaxThinkingBudget = 32768;

    public static ThinkingConfig? Build(string model, int? thinkingBudget, string? thinkingLevel, bool includeThoughts = false) {
        if (!ModelCapabilities.SupportsThinking(model)) {
            return null;
        }

        ThinkingConfig? config = null;
        bool isGemini25 = model.Contains("2.5", StringComparison.OrdinalIgnoreCase);
        string? effectiveLevel = ModelCapabilities.ResolveThinkingLevel(model, thinkingLevel);
        if (!isGemini25 && !string.IsNullOrWhiteSpace(effectiveLevel)) {
            config = new() { ThinkingLevel = effectiveLevel };
        }
        else if (thinkingBudget.HasValue && ModelCapabilities.SupportsThinkingBudget(model)) {
            config = new() { ThinkingBudget = Math.Min(thinkingBudget.Value, MaxThinkingBudget) };
        }

        if (includeThoughts) {
            config ??= new();
            config.IncludeThoughts = true;
        }

        return config;
    }

    /// <summary>
    /// [AI Context] Builds the minimal-thinking config used when <c>DisableThinkingDuringWarmUp</c> is enabled.
    /// Gemini 2.5/3.x use <c>ThinkingBudget = 0</c>, whereas Gemini 4 / Hydrogen rejects <c>thinking_budget</c> and
    /// <c>MINIMAL</c>, so it falls back to its minimum supported level (<c>LOW</c>).
    /// [Human] Erstellt die Warmup-Konfiguration mit minimalem Thinking (Budget 0 bzw. LOW bei Hydrogen).
    /// </summary>
    public static ThinkingConfig BuildForWarmupDisabled(string model) =>
        ModelCapabilities.SupportsXHighThinking(model)
            ? new() { ThinkingLevel = "LOW" }
            : new() { ThinkingBudget = 0 };
}
