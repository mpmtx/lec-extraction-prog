using System.Text;
using Google.GenAI.Types;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Splits one streamed chunk into the answer text and the thought-summary text.
///
/// <para>With <c>IncludeThoughts</c> on, Gemini sends its thought summary as text parts marked
/// <c>Thought = true</c>, usually before the answer and sometimes inside the same chunk. The old
/// readers took <c>chunk.Text ?? Parts[0].Text</c>; the SDK does not document whether
/// <c>chunk.Text</c> skips thought parts, and <c>Parts[0]</c> would be the thought itself, so either
/// would put the summary into the .tex output and into the continue history. Every reader goes
/// through this instead. Without thought parts the answer is all text parts in order - what
/// <c>chunk.Text</c> returned before.</para>
/// [Human] Trennt Antwort-Text und Gedanken-Text eines gestreamten Chunks.
/// </summary>
public static class ResponseParts {
    public static (string Answer, string Thought) Split(GenerateContentResponse? chunk) {
        var parts = FirstCandidate(chunk)?.Content?.Parts;
        if (parts == null || parts.Count == 0) {
            return (chunk?.Text ?? "", "");
        }

        var answer = new StringBuilder();
        var thought = new StringBuilder();
        foreach (var part in parts) {
            if (string.IsNullOrEmpty(part?.Text)) {
                continue;
            }
            (part.Thought == true ? thought : answer).Append(part.Text);
        }
        return (answer.ToString(), thought.ToString());
    }

    /// <summary>The chunk's finish reason (STOP, MAX_TOKENS, CONTINUATION...), or null - also for an empty candidate list.</summary>
    public static string? FinishReason(GenerateContentResponse? chunk) =>
        FirstCandidate(chunk)?.FinishReason?.ToString();

    /// <summary>The chunk's opaque Long Decode continuation token, or null when absent/empty.</summary>
    public static byte[]? ContinuationToken(GenerateContentResponse? chunk) =>
        FirstCandidate(chunk)?.ContinuationToken is { Length: > 0 } token ? token : null;

    /// <summary>True when <paramref name="finishReason"/> equals <c>CONTINUATION</c>.</summary>
    public static bool IsContinuationFinishReason(string? finishReason) =>
        string.Equals(finishReason, Google.GenAI.Types.FinishReason.Continuation.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>The chunk's grounding (Google Search sources), or null - also for an empty candidate list.</summary>
    public static GroundingMetadata? Grounding(GenerateContentResponse? chunk) =>
        FirstCandidate(chunk)?.GroundingMetadata;

    /// <summary>
    /// The first candidate, or null. <c>chunk.Candidates?[0]</c> is not the same: it throws on an
    /// empty list, which then aborts the stream as a non-transient error.
    /// </summary>
    public static Candidate? FirstCandidate(GenerateContentResponse? chunk) =>
        chunk?.Candidates is { Count: > 0 } candidates ? candidates[0] : null;
}
