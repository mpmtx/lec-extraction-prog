using System;
using Google.GenAI.Types;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Collects a streamed response's usage metadata and turns it into a line that
/// distinguishes <b>"not reported"</b> from <b>"zero"</b>.
///
/// <para>Review finding F9: the warm-up's token line printed only <c>if (inputTokens > 0)</c>, and
/// usage arrives on the final chunk of a streamed response - if it never arrived, the line was
/// silently skipped and the handshake <i>looked</i> free. Silence there means "not reported", not
/// "not charged", and the two must never render identically again.</para>
///
/// <para>It also surfaces <see cref="GenerateContentResponseUsageMetadata.ThoughtsTokenCount"/>,
/// which nothing in this app read before. That is the direct answer to "output was only 2 tokens":
/// reasoning tokens are billed but are <b>not</b> part of <c>CandidatesTokenCount</c>, so a thinking
/// model's real output cost was invisible on every path, not just the warm-up. TotalTokenCount is
/// reported alongside because it is the server's own arithmetic, and it settles empirically whether
/// the existing "(inkl. Thinking Tokens)" labels elsewhere in this codebase were ever accurate.</para>
/// [Human] Sammelt die Nutzungsdaten eines Streams und macht sichtbar, ob überhaupt welche kamen -
/// inklusive der bisher nirgends ausgewerteten Denk-Tokens.
/// </summary>
public sealed class UsageReport {
    /// <summary>True once any chunk carried usage metadata at all.</summary>
    public bool WasReported { get; private set; }

    public int PromptTokens { get; private set; }
    public int CandidateTokens { get; private set; }

    /// <summary>
    /// [AI Context] IMPORTANT: Google's CachedContentTokenCount, reported as-is, and NOT to be believed -
    /// neither a reported hit nor a reported miss. The implicit cache is best-effort and the figure is
    /// unreliable: the identical prefix can report a large hit in one run and 0 in the next (maintainer's
    /// long-standing experience; 2026-10-10: 263k vs. 0 for the same 265k handshake; 2026-10-16: the same
    /// 310k resume handshake reported 271,834 cached one night and 0 the next morning, and both were
    /// accepted under a 250k/min limit). Display it, never base logic, rate-limit planning, a "fresh
    /// tokens" verdict or any conclusion about caching on it. Only Google's accept/refuse of a request is
    /// evidence. See Documentation.md §4.4.
    /// [Human] WICHTIG: Von Google gemeldete gecachte Tokens - weder ein gemeldeter Treffer noch ein
    /// gemeldeter Fehlschlag ist glaubwürdig. Nur zur Anzeige.
    /// </summary>
    public int CachedTokens { get; private set; }
    public int ThoughtTokens { get; private set; }
    public int TotalTokens { get; private set; }

    /// <summary>Why the server ended the response (e.g. STOP, MAX_TOKENS); null if it never said.</summary>
    public string? FinishReason { get; private set; }

    /// <summary>
    /// [AI Context] Keeps the last finish reason a chunk carried. Streams send it on the final chunk
    /// only, so null/empty values from earlier chunks are ignored rather than overwriting it.
    /// [Human] Merkt sich, warum der Server die Antwort beendet hat.
    /// </summary>
    public void RecordFinishReason(string? reason) {
        if (!string.IsNullOrWhiteSpace(reason)) {
            FinishReason = reason;
        }
    }

    /// <summary>
    /// [AI Context] One line that answers "why did this response stop?": output and thinking tokens
    /// against MaxOutputTokens - thinking draws from the same budget, which the plain "Output:" figure
    /// hides - plus the server's finish reason. Printed after every refinement request, so a continue
    /// request can be attributed to the output limit (and to thinking's share of it) at a glance.
    /// [Human] Zeigt, ob eine Antwort am Output-Limit endete und wie viel davon Denk-Tokens waren.
    /// </summary>
    public string DescribeOutputBudget(int maxOutputTokens) {
        int used = CandidateTokens + ThoughtTokens;
        string limit = maxOutputTokens > 0
            ? $" von {maxOutputTokens:N0} MaxOutputTokens ({100.0 * used / maxOutputTokens:F1} %)"
            : "";
        string reason = FinishReason ?? "nicht gemeldet";
        string verdict = string.Equals(FinishReason, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase)
            ? $" - Antwort durch das Output-Limit abgeschnitten (Denk-Tokens davon: {ThoughtTokens:N0})"
            : ResponseParts.IsContinuationFinishReason(FinishReason)
                ? $" - Single-Request-Limit für Long Decode erreicht, setze mit ContinuationToken fort (Denk-Tokens davon: {ThoughtTokens:N0})"
                : "";
        return $"[Output-Budget] {CandidateTokens:N0} Output + {ThoughtTokens:N0} Denk-Tokens = {used:N0}{limit} | FinishReason: {reason}{verdict}";
    }

    /// <summary>
    /// [AI Context] Takes the last non-null metadata seen. Usage is cumulative-to-date per chunk
    /// rather than incremental, so the final chunk's values are the request's totals - accumulating
    /// them would multiply the reported cost.
    /// [Human] Übernimmt die zuletzt gemeldeten Werte; Aufsummieren würde die Kosten vervielfachen.
    /// </summary>
    public void Absorb(GenerateContentResponseUsageMetadata? metadata) {
        if (metadata == null) {
            return;
        }

        WasReported = true;
        if (metadata.PromptTokenCount.HasValue) PromptTokens = metadata.PromptTokenCount.Value;
        if (metadata.CandidatesTokenCount.HasValue) CandidateTokens = metadata.CandidatesTokenCount.Value;
        if (metadata.CachedContentTokenCount.HasValue) CachedTokens = metadata.CachedContentTokenCount.Value;
        if (metadata.ThoughtsTokenCount.HasValue) ThoughtTokens = metadata.ThoughtsTokenCount.Value;
        if (metadata.TotalTokenCount.HasValue) TotalTokens = metadata.TotalTokenCount.Value;
    }

    /// <summary>
    /// [AI Context] Renders <paramref name="caller"/>'s own token line, appending what it could not
    /// know: whether the server reported anything, and the thinking/total figures.
    /// [Human] Ergänzt die Token-Zeile des Aufrufers um Denk-Tokens, Gesamtsumme und den Hinweis,
    /// falls gar nichts gemeldet wurde.
    /// </summary>
    /// <param name="tag">
    /// The line's leading tag. Callers pass their own - including its padding - because the verbose
    /// report aligns "[Request Tokens]" with "[Part Total Tokens]" and "[Session Total Tokens]" in a
    /// column, and a fixed tag here would break that alignment.
    /// </param>
    public string Describe(string caller, string tag = "[Tokens]") {
        if (!WasReported) {
            return $"{tag} Keine Nutzungsdaten vom Server erhalten - die Anfrage war trotzdem kostenpflichtig.";
        }

        string line = $"{tag} {caller}";
        if (ThoughtTokens > 0) {
            line += $" | Denk-Tokens: {ThoughtTokens:N0}";
        }
        if (TotalTokens > 0) {
            line += $" | Gesamt: {TotalTokens:N0}";
        }
        return line;
    }
}
