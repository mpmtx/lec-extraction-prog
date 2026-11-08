using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Implicit prefix-cache warm-up for Vertex, ported from AiStudioAutoExtractionSession.
/// Member Index:
/// - GetStaticPromptBeginning: Returns the static per-part prompt preamble.
/// - WarmUpSystemInstructionCacheAsync: Sends warm-up handshake using dummy-part0.tex.
/// - TryLoadSystemInstructionWithHistoryAsync: Loads system instruction text & triggers warmup if enabled.
/// [Human] Der von AI Studio portierte Cache-Warmup-Teil der Vertex-Session.
/// </summary>
public partial class VertexAutoExtractionSession {
    /// <summary>
    /// [AI Context] Returns the static, per-partNumber prefix of the user-turn prompt. This text is
    /// deterministic and placed BEFORE the video payload in every request, forming a stable, growing cache
    /// prefix that the warm-up can pre-activate in the same token order. Ported (2026-07-28) from the
    /// content Vertex's own UploadSegmentAndBuildPromptAsync already sent — merging_and_scope and segment_start
    /// are the exact same wording Vertex used before this port, just relocated from after the video to
    /// before it; nothing new was invented. partNumber == 1  → no segment_start parameter (matches the
    /// warm-up dummy turn exactly). partNumber  > 1 → adds the segment_start parameter for mid-lecture
    /// continuity.
    /// [Human] Gibt den immer gleichen statischen Anfang des Prompts zurück. Steht VOR dem Video, damit
    /// Google einen Cache-Hit erkennt. Portiert von AI Studio, Wortlaut von Vertex selbst übernommen.
    /// </summary>
    private static string GetStaticPromptBeginning(int partNumber) {
        string s = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n" +
                   "<context_and_parameters>\n" +
                   "IMPORTANT: The System Instructions (System Prompt) contain the absolute rules, syntax specifications, and constraints for this transcription and MUST be followed strictly. The parameters below only specify details for this video fragment:\n\n" +
                   "<parameter name=\"merging_and_scope\">Do NOT attempt to merge the current part with the previous parts (i.e. do not try to fix the cut). Focus solely on transcribing this fragment as it is. As specified in the System Instructions, keep mathematical derivations and explanations self-contained and grouped within 'content' environments to preserve logical flow.</parameter>\n";
        if (partNumber != 1) {
            s += "<parameter name=\"segment_start\">\n" +
                 "1. Start the transcription EXACTLY where the audio begins in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction.\n" +
                 "2. Open this part as the System Instructions prescribe for a Part 2 or later: find the spot where this video begins in the previous part by its speech (it lies in the overlap, before that part's end), re-open the headings and any proof still open there, never a `content` block, and put the board leftovers into the `Initial Board State Leftovers` aiNote. However, you must still transcribe the spoken words exactly from where this new video segment begins.\n" +
                 "</parameter>\n";
        }
        return s;
    }

    /// <summary>
    /// [AI Context] Sends a lightweight handshake containing the system instruction (and, unlike
    /// AI Studio, NOT the explicit CachedContent reference — that cache is created later, in
    /// InitializeContextCachingAsync, after this setup-time warmup has already run) to activate Google's
    /// implicit prefix cache for the stable per-part preamble before the first real video request. Ported
    /// (2026-07-28) from AiStudioAutoExtractionSession.PrimePrefixCacheAsync, simplified to a
    /// single-shot handshake (no batched-history variant, since Vertex has no equivalent batching config).
    /// [Human] Wärme-Handshake für Vertex (portiert von AI Studio, vereinfacht auf einen einzelnen
    /// Handshake ohne History-Batching).
    /// </summary>
    private async Task<bool> PrimePrefixCacheAsync() {
        Ui.Info("Starte initialen Handshake-Roundtrip, um den stabilen Prompt-Anfang bei Google im impliziten Cache zu aktivieren...", "Cache-Warming");

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = 100
        };

        var sysParts = new List<Part>();
        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) sysParts.Add(new() { Text = _systemInstructionText });
        if (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0) {
            sysParts.AddRange(_historyParts.Where(p => p.FileData == null && p.InlineData == null && !string.IsNullOrEmpty(p.Text)));
        }
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        // [AI Context] The same thinking config as Part 1 (level/budget and IncludeThoughts), like the
        // AI Studio twin: a bit-identical request config is what lets the prefix cache hit. Before, the
        // handshake sent none at all. DisableThinkingDuringWarmUp=true overrides to ThinkingBudget=0
        // (opt-in, finding F9). The handshake's own thoughts are dropped below.
        // [Human] Gleiche Thinking-Konfiguration wie Part 1, damit der Warmup-Prefix gecacht wird.
        if (ModelCapabilities.SupportsThinking(_config.CurrentModel)) {
            if (_config.DisableThinkingDuringWarmUp) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 0 };
                Ui.Detail("Handshake läuft ohne Thinking (DisableThinkingDuringWarmUp = true).", "Cache-Warming");
            } else {
                requestConfig.ThinkingConfig = ThinkingConfigFactory.Build(
                    _config.CurrentModel, _config.ThinkingBudget, _config.ThinkingLevel, IncludesThoughts);
            }
        }

        string handshakeText = $"[Cache-Warming Handshake] System instruction and instructions loaded. Please acknowledge with exactly: '[AI-Model: {_config.CurrentModel}] Handshake confirmed. Ready.'";

        var warmupParts = new List<Part>();
        if (_config.SendDummyFileDuringTranscription) {
            string dummyReferenceBlock = $"<reference_context file=\"part0.tex\">\n{PrefixCacheAnchor.LoadPrefixCacheAnchorText()}\n</reference_context>\n\n";
            warmupParts.Add(new() { Text = dummyReferenceBlock + GetStaticPromptBeginning(1) });
        } else {
            warmupParts.Add(new() { Text = GetStaticPromptBeginning(1) });
        }
        warmupParts.Add(new() { Text = handshakeText });

        var pingContent = new List<Content> {
            new() { Role = "user", Parts = warmupParts }
        };

        // [AI Context] Count tokens before request so that token count is visible even if a Quota Error occurs.
        // IMPORTANT: CountTokensAsync itself may consume input tokens against the same Free-Tier quota
        // as GenerateContent, so this call is wrapped with retry to prevent exhausting the token
        // budget mid-count and causing an immediate 429 on the subsequent GenerateContent request.
        // SkipTokenCountingDuringWarmUp=true skips this block entirely to conserve quota budget.
        if (!_config.SkipTokenCountingDuringWarmUp) {
            try {
                var warmupContents = new List<Content>();
                if (requestConfig.SystemInstruction != null) warmupContents.Add(requestConfig.SystemInstruction);
                warmupContents.AddRange(pingContent);
                int backoff = _config.CountTokensRetryBackoffSeconds > 0 ? _config.CountTokensRetryBackoffSeconds : 20;
                var counted = await ApiRetryPolicy.ExecuteWithRetryAsync(
                    () => _client.Models.CountTokensAsync(_config.CurrentModel, warmupContents),
                    maxRetries: 8,
                    initialBackoff: backoff,
                    retryContext: "CountTokens Warmup",
                    highDemandDelay: _config.HighDemandDelaySeconds > 0 ? _config.HighDemandDelaySeconds : null);
                int totalToks = counted?.TotalTokens ?? 0;
                int estNew = _lastWarmupInputTokens > 0 ? Math.Max(0, totalToks - _lastWarmupInputTokens) : totalToks;
                Ui.Info($"[Warmup Request] Neu dazugekommene Tokens: {estNew:N0} | Total Prompt: {totalToks:N0} Tokens", "Tokens");
            }
            catch (Exception countEx) {
                Ui.Detail($"[Exception gefangen] {countEx.Describe()}");
            }
        }

        try {
            string responseText = "";
            int inputTokens = 0, outputTokens = 0, cachedTokens = 0;
            var usage = new UsageReport();

            bool success = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, pingContent, requestConfig),
                onChunkReceived: async (chunk) => {
                    string txt = ResponseParts.Split(chunk).Answer;
                    responseText += txt;
                    usage.Absorb(chunk.UsageMetadata);
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) inputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) outputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) cachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                    }
                    await Task.CompletedTask;
                },
                cancellationToken: CancellationToken.None,
                retryContext: "Cache-Warming Handshake",
                highDemandDelay: _config.HighDemandDelaySeconds > 0 ? _config.HighDemandDelaySeconds : null
            );

            if (success) {
                int freshTokens = Math.Max(0, inputTokens - cachedTokens);
                _sessionTotalInputTokens += inputTokens;
                _sessionTotalOutputTokens += outputTokens;
                _sessionTotalCachedTokens += cachedTokens;

                int newlyAdded = cachedTokens > 0 ? Math.Max(0, inputTokens - cachedTokens) : (_lastWarmupInputTokens > 0 ? Math.Max(0, inputTokens - _lastWarmupInputTokens) : freshTokens);
                _lastWarmupInputTokens = inputTokens;

                Ui.Success("Handshake erfolgreich.", "Cache-Warming");
                if (!string.IsNullOrWhiteSpace(responseText)) {
                    Ui.Detail($"[Gemini Antwort] {responseText.Trim()}");
                }
                Ui.Info(usage.Describe($"[Warmup Tokens] Neu dazugekommen: {newlyAdded:N0} | Total Prompt: {inputTokens:N0} | Gecacht: {cachedTokens:N0} | Output: {outputTokens:N0}"), "Tokens");

                int delay = _config.RateLimitDelaySeconds > 0 ? _config.RateLimitDelaySeconds : 130;
                Ui.Detail($"Warte {delay} Sekunden (Token Refill)...", "Rate-Limit");
                await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
                return true;
            } else {
                Ui.Warn($"Cache-Warming Handshake abgebrochen oder fehlgeschlagen.", "Cache-Warming");
                return false;
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Cache-Warming Handshake fehlgeschlagen: {ex.Describe()}. Fahre trotzdem fort.", "Cache-Warming");
            int delay = _config.RateLimitDelaySeconds > 0 ? _config.RateLimitDelaySeconds : 130;
            Ui.Detail($"Warte {delay} Sekunden (Token Refill nach Handshake)...", "Rate-Limit");
            await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
        }
        return false;
    }
}
