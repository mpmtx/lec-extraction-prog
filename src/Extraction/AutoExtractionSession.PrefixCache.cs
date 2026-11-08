using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Globalization;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Latex;
using LectureExtraction.Media;
using LectureExtraction.Refinement;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Implicit prefix-cache priming: loads the dummy-part0.tex anchor and sends
/// warm-up handshakes (single-shot or staged/batched) to pre-fill Google's implicit prefix cache before
/// real video processing begins. Split out of the session (Phase 4.5).
/// Member Index:
/// - WarmUpWithBatchedHistoryAsync: Staged cache warming by grouping history files into batches.
/// - PrimePrefixCacheAsync: Session warm-up handshake (system instruction + dummy anchor).
/// - PrimePrefixCacheForResumedPartAsync: Extra handshake when a video's first sent part is 2+ (resume).
/// - SendWarmupHandshakeAsync: Sends one handshake, records usage, waits for the token refill.
/// [Human] Der Cache-Warmup-Teil der Session: dummy-part0.tex laden und Warmup-Handshakes senden.
/// </summary>
public partial class AutoExtractionSession {
    /// <summary>
    /// [AI Context] Performs staged cache warming: splits history files into batches and sends
    /// incremental warm-up handshakes between each batch to pre-fill Google's implicit prefix cache.
    /// Each batch appends its text files to _systemInstructionText and uploads non-text files.
    /// [Human] Gestaffeltes Cache-Warming: History wird in Batches aufgeteilt, nach jedem Batch
    /// wird ein Handshake gesendet, um den Google-Cache schrittweise aufzubauen.
    /// </summary>
    private async Task<bool> WarmUpWithBatchedHistoryAsync(List<string> historyFiles, string? commonBase) {
        // Grouped by subfolder of training-history (the history list itself holds files, not folders)
        var batches = HistoryFileResolver.GroupHistoryFilesByTopLevelSubfolder(
            historyFiles, [Stage1PromptFiles.HistoryRoot], _config.HistoryBatchCount);

        int systemInstructionDelay = Math.Max(0, _config.SystemInstructionDelaySeconds);
        int historyBatchDelay = Math.Max(0, _config.HistoryRateLimitDelaySeconds);

        Ui.Info($"Starte gestaffeltes Cache-Warming für System Instruction + History in {batches.Count} Batch(es) (BaseDelay: {systemInstructionDelay}s, HistoryDelay: {historyBatchDelay}s)...", "SystemInstruction-Warmup");

        // Step 0: Optionally warm up base system instruction before adding history
        if (!_config.MergeSystemInstructionAndFirstHistoryBatch) {
            Ui.Blank();
            Ui.Step("Cache-Warming Schritt 0: Warmup für Basis System Instruction");
            if (!await PrimePrefixCacheAsync(systemInstructionDelay, includeDummyPart0: false, stepLabel: "Schritt 0")) return false;
        } else {
            Ui.Info($"Überspringe separaten Warmup & Wartezeit ({systemInstructionDelay}s) für Basis System Instruction (wird mit erstem Batch vereint)...", "Cache-Warming");
        }

        // Process each history batch: append files, then warm up
        for (int batchIndex = 0; batchIndex < batches.Count; batchIndex++) {
            var (batchLabel, batchFiles) = batches[batchIndex];
            bool isLastBatch = batchIndex == batches.Count - 1;

            Ui.Blank();
            string dest = _config.LoadHistoryIntoSystemInstruction ? "System Instruction" : "User Prompt";
            Ui.Step($"Cache-Warming Schritt {batchIndex + 1}/{batches.Count}: Lade History-Batch '{batchLabel}' ({batchFiles.Count} Datei(en)) in {dest}");

            // [AI Context] If LoadHistoryIntoSystemInstruction is false, we append to _historyParts instead. 
            // The logic below seamlessly supports MergeSystemInstructionAndFirstHistoryBatch = true: 
            // Step 0 is skipped above, and right here in batchIndex 0 we populate the first history parts 
            // into the User Prompt. When PrimePrefixCacheAsync is called below, it sends both the base 
            // System Instruction AND the first User Prompt history batch in the very first HTTP request, 
            // warming them both up together perfectly.
            // [Human] Fügt den Text entweder an die System Instruction oder die User-Message an.
            var batchBuilder = new System.Text.StringBuilder();
            var loaded = await SystemInstructionTextBuilder.AppendHistoryFilesAsync(
                batchFiles, batchBuilder, commonBase, _attachmentHandler, uploadTextFiles: !_config.InlineHistory);
            AddLoadedHistory(batchBuilder.ToString(), loaded);

            bool shouldSendHandshake = true;
            
            // [AI Context] Free-Tier Quota Protection: If a user configures many batches (e.g. 7), sending a 
            // handshake for every single batch means the cumulative token count sent across all requests 
            // easily exceeds 1.5 million tokens. This triggers the 'generate_content_free_tier_input_token_count' 
            // quota limit. By merging consecutive batches in pairs (skipping every second handshake), 
            // we drastically reduce the total tokens consumed during warmup while still building the cache.
            // [Human] Verhindert, dass durch zu viele Handshakes das Token-Limit des Free-Tiers gesprengt wird.
            if (_config.MergeAllConsecutiveHistoryBatches && !isLastBatch) {
                int pairingStart = _config.MergeSystemInstructionAndFirstHistoryBatch ? 1 : 0;
                if (batchIndex >= pairingStart && (batchIndex - pairingStart) % 2 == 0) {
                    shouldSendHandshake = false;
                }
            }

            if (shouldSendHandshake) {
                // [AI Context] If MergeSystemInstructionAndFirstHistoryBatch is active, the first batch
                // logically represents the combined "system instruction + first history" round, so the
                // SystemInstructionDelaySeconds (not HistoryRateLimitDelaySeconds) should apply.
                bool isFirstMergedBatch = _config.MergeSystemInstructionAndFirstHistoryBatch && batchIndex == 0;
                int batchDelay = isFirstMergedBatch ? systemInstructionDelay : historyBatchDelay;
                string stepLabel = $"Schritt {batchIndex + 1}/{batches.Count}";
                if (!await PrimePrefixCacheAsync(batchDelay, includeDummyPart0: isLastBatch, stepLabel: stepLabel)) return false;
            } else {
                Ui.Info($"Überspringe Handshake & Wartezeit ({historyBatchDelay}s) für Batch '{batchLabel}' (wird mit dem nächsten Batch vereint)...", "Cache-Warming");
            }
        }

        Ui.Detail($"History-Warming abgeschlossen. Max-Frisch-Tokens in einem Schritt: {_sessionMaxFreshTokens:N0}", "Tokens");
        return true;
    }

    /// <summary>
    /// [AI Context] Sends a lightweight handshake request containing the System Instruction to the backend.
    /// This warms up Google's implicit prefix cache and enforces a token refill delay
    /// before heavy video processing begins, preventing Quota Errors and ensuring high cache hits.
    /// [Human] Wärme-Handshake: Sendet ein kleines Signal an Google, damit die KI die System Instruction vorab in den impliziten Cache laedt.
    /// </summary>
    private async Task<bool> PrimePrefixCacheAsync(int? customDelay = null, bool includeDummyPart0 = false, string stepLabel = "") {
        string stepPrefix = string.IsNullOrEmpty(stepLabel) ? "" : $"[{stepLabel}] ";
        Ui.Info($"Starte initialen Handshake-Roundtrip {stepPrefix}um die System Instruction bei Google im impliziten Cache zu aktivieren...", "Cache-Warming");

        // [AI Context] Prefix-consistency rule: include dummy-part0.tex only when the system instruction
        // is COMPLETE (includeDummyPart0=true, i.e. last history batch or single-shot warmup). Intermediate
        // batches have a partial system instruction that cannot produce a cache hit for Part-1 anyway, so
        // sending the dummy there wastes ~4500 tokens with zero cache benefit.
        // SendDummyFileWithEachWarmUpRound=true overrides to always send (debugging/testing aid).
        // [Human] Dummy nur beim letzten Handshake (vollständige Sys-Instruction), sonst Tokenverbrauch ohne Nutzen.
        bool shouldIncludeDummy = _config.SendDummyFileDuringTranscription
            && ((_config.DebugSendReferenceFile && includeDummyPart0) || _config.SendDummyFileWithEachWarmUpRound);
        List<Part> warmupParts = [];

        // [AI Context] When LoadHistoryIntoSystemInstruction is false, the history resides in the User Message.
        // We prepend it here to ensure the exact same token sequence is sent during the warmup handshakes
        // as will be sent during the actual video generation request, ensuring a perfect prefix match.
        // Uploaded history files (InlineHistory=false) are in the User Message either way.
        warmupParts.AddRange(HistoryUserTurnParts());

        if (shouldIncludeDummy) {
            warmupParts.Add(new Part { Text = ComposeReferenceContextText("", 1) });
        } else if (includeDummyPart0) {
            warmupParts.Add(new Part { Text = GetStaticPromptBeginning(1) });
        }
        warmupParts.Add(new Part { Text = HandshakeText() });

        return await SendWarmupHandshakeAsync([new() { Role = "user", Parts = warmupParts }], customDelay, stepLabel);
    }

    /// <summary>
    /// [AI Context] Whether the first part a session sends for a video gets the resumed-part warm-up:
    /// it is part 2 or later, its predecessors are on disk, they are inlined into the prompt (not
    /// uploaded as files, which that text does not contain), and warm-up is enabled at all.
    /// [Human] Entscheidet, ob vor dem ersten gesendeten Teil (Teil 2+) ein Resume-Warm-up nötig ist.
    /// </summary>
    public static bool ShouldWarmUpResumedPart(IAutoExtractionConfig config, int partNumber, int precedingPartsOnDisk) =>
        partNumber > 1
        && precedingPartsOnDisk > 0
        && config.EnableImplicitPrefixCacheWarmup
        && config.DebugSendReferenceFile
        && config.InlinePrecedingLecTexParts;

    /// <summary>
    /// [AI Context] One extra warm-up round for a video whose first part sent in this session is part 2
    /// or later - the earlier parts were reused from disk (resume, see ResumeWindow). The session warm-up
    /// only primed system instruction + dummy anchor, so without this round the part's inlined
    /// predecessors (~10-15k tokens each) all arrive cold in one request, on top of the video. In a run
    /// that starts at part 1 they enter the cache one part at a time instead. The handshake sends exactly
    /// the text Part that request will open with (built by ComposeReferenceContextText), minus the video,
    /// right before it. Only meaningful for the inline path: uploaded .tex references are not part of
    /// that text.
    /// [Human] Zusätzliches Cache-Warming, wenn ein Video erst ab Teil 2+ transkribiert wird (die
    /// vorherigen Teile kommen von der Festplatte): wärmt die eingebetteten Vorgänger-Teile vor.
    /// </summary>
    private async Task PrimePrefixCacheForResumedPartAsync(int partNumber, IReadOnlyList<string> previousTexFiles) {
        string stepLabel = $"Resume Part {partNumber}";
        Ui.Info($"[{stepLabel}] Teil 1-{partNumber - 1} stammen von der Festplatte - zusätzlicher Handshake, damit die eingebetteten Teile vor dem Video-Request im impliziten Cache liegen:", "Cache-Warming");

        List<Part> warmupParts = HistoryUserTurnParts();
        warmupParts.Add(new Part { Text = ComposeReferenceContextText(await BuildInlinedPrecedingPartsAsync(previousTexFiles), partNumber) });
        warmupParts.Add(new Part { Text = HandshakeText() });

        // _sessionPreamble precedes the user turn in the real request too (BuildGenerationRequestAsync).
        List<Content> contents = [.. _sessionPreamble, new() { Role = "user", Parts = warmupParts }];

        // An optimization only: if it fails, the part request still runs, just with a colder cache.
        if (!await SendWarmupHandshakeAsync(contents, customDelay: null, stepLabel)) {
            Ui.Warn($"[{stepLabel}] Zusätzlicher Handshake fehlgeschlagen - Teil {partNumber} wird trotzdem gesendet.", "Cache-Warming");
        }
    }

    // [AI Context] The handshake text is explicitly phrased to suppress any content generation:
    // - No video or media is attached — the system instruction's processing rules do not apply.
    // - The model must ONLY echo the acknowledgement string, nothing else.
    private string HandshakeText() =>
        $"""[Cache-Warming Handshake] This is a technical warmup request to pre-fill the implicit prefix cache (on {_backend.ProviderName} servers). No video, audio, or any other media file is attached to this request — the lecture extraction instructions in the system instruction are therefore NOT applicable and must NOT be executed. Do not generate any LaTeX, summaries, or content of any kind. Please acknowledge receipt with exactly this string and nothing else: '[AI-Model: {_config.CurrentModel}] Handshake confirmed. Ready.'""";

    /// <summary>
    /// [AI Context] Sends one warm-up handshake (the given contents under the session's system
    /// instruction and generation config), records its usage, then waits for the token refill.
    /// Shared by the session warm-up and the resumed-part warm-up.
    /// [Human] Sendet einen Warm-up-Handshake, verbucht die Tokens und wartet auf den Token-Refill.
    /// </summary>
    private async Task<bool> SendWarmupHandshakeAsync(List<Content> pingContent, int? customDelay, string stepLabel) {
        string stepPrefix = string.IsNullOrEmpty(stepLabel) ? "" : $"[{stepLabel}] ";

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
        };

        var sysParts = GetValidSystemInstructionParts();
        if (sysParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
        }

        // [AI Context] ThinkingConfig is built with the same model-specific logic as BuildGenerationRequestAsync
        // so that the warmup handshake uses a bit-identical GenerateContentConfig — which is a prerequisite
        // for Google's implicit prefix cache to produce a hit on the real Part-1 request.
        // DisableThinkingDuringWarmUp=true overrides to ThinkingBudget=0 (opt-in, see finding F9).
        // [Human] Gleiche Thinking-Konfiguration wie Part 1+, damit der Warmup-Prefix gecacht wird.
        if (ModelCapabilities.SupportsThinking(_config.CurrentModel)) {
            if (_config.DisableThinkingDuringWarmUp) {
                requestConfig.ThinkingConfig = new ThinkingConfig { ThinkingBudget = 0 };
                Ui.Detail("Handshake läuft ohne Thinking (DisableThinkingDuringWarmUp = true).", "Cache-Warming");
            } else {
                // IncludeThoughts too, for the same reason; the handshake's own thoughts are dropped below.
                requestConfig.ThinkingConfig = ThinkingConfigFactory.Build(
                    _config.CurrentModel, _config.ThinkingBudget, _config.ThinkingLevel, IncludesThoughts);
            }
        }

        // [AI Context] Count tokens before request so that token count is visible even if a Quota Error occurs.
        // IMPORTANT: CountTokensAsync itself may consume input tokens against the same Free-Tier quota
        // (generate_content_free_tier_input_token_count) as GenerateContent. Therefore it is wrapped
        // in ExecuteWithRetryAsync so that a 429 quota error during token counting is retried with
        // proper backoff — preventing a situation where CountTokens succeeds but the subsequent
        // GenerateContent fails immediately because the token budget was already exhausted by counting.
        // SkipTokenCountingDuringWarmUp=true skips this block entirely to conserve quota budget.
        long? countedTokens = null;
        Ui.Info($"{(string.IsNullOrEmpty(stepLabel) ? "[Warmup Request]" : $"[{stepLabel}]")} " +
                MinuteGateEstimate.Describe(requestConfig.SystemInstruction, pingContent), "Tokens");
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
                    retryContext: $"CountTokens {stepLabel}",
                    highDemandDelay: _config.HighDemandDelaySeconds > 0 ? _config.HighDemandDelaySeconds : null);
                int totalToks = counted?.TotalTokens ?? 0;
                countedTokens = totalToks > 0 ? totalToks : null;
                int estNew = _lastWarmupInputTokens > 0 ? Math.Max(0, totalToks - _lastWarmupInputTokens) : totalToks;
                string prefix = string.IsNullOrEmpty(stepLabel) ? "[Warmup Request]" : $"[{stepLabel}]";
                Ui.Info($"{prefix} Voraussichtlich NEU zu berechnen: {estNew:N0} | Total Prompt: {totalToks:N0}", "Tokens");
            }
            catch (Exception countEx) {
                Ui.Detail($"[Exception gefangen] {countEx.Describe()}");
                // [AI Context] Even when CountTokens fails after all retries (e.g. network outage), display
                // the last known total so the user always sees a token count line before the actual generate request.
                string lastKnown = _lastWarmupInputTokens > 0 ? $"{_lastWarmupInputTokens:N0}" : "unbekannt";
                string prefix = string.IsNullOrEmpty(stepLabel) ? "[Warmup Request]" : $"[{stepLabel}]";
                Ui.Info($"{prefix} Token-Zählung nicht verfügbar. Letzter bekannter Total-Prompt: {lastKnown} (zzgl. neuer Batch)", "Tokens");
            }
        }

        try {
            string responseText = "";
            var usage = new UsageReport();

            bool success = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                streamFactory: () => {
                    ApiKeyQuotaTracker.RecordClientRequest(_client, _config.CurrentModel, string.IsNullOrEmpty(stepLabel) ? "Warmup Handshake" : stepLabel);
                    return _client.Models.GenerateContentStreamAsync(_config.CurrentModel, pingContent, requestConfig);
                },
                onChunkReceived: async (chunk) => {
                    // The answer only: with IncludeThoughts on, Parts[0] can be a thought summary.
                    string txt = ResponseParts.Split(chunk).Answer;
                    responseText += txt;
                    usage.Absorb(chunk.UsageMetadata);
                    await Task.CompletedTask;
                },
                cancellationToken: CancellationToken.None,
                retryContext: "Cache-Warming Handshake",
                highDemandDelay: _config.HighDemandDelaySeconds > 0 ? _config.HighDemandDelaySeconds : null,
                // The counted size lets the retry policy stop when a per-minute token limit is smaller than the handshake.
                requestInputTokens: countedTokens
            );

            if (success) {
                int inputTokens = usage.PromptTokens, outputTokens = usage.CandidateTokens, cachedTokens = usage.CachedTokens;
                int freshTokens = Math.Max(0, inputTokens - cachedTokens);
                _sessionUsage += new TokenUsage(inputTokens, outputTokens, cachedTokens);
                _sessionMaxFreshTokens = Math.Max(_sessionMaxFreshTokens, freshTokens);

                int newlyAdded = cachedTokens > 0 ? Math.Max(0, inputTokens - cachedTokens) : (_lastWarmupInputTokens > 0 ? Math.Max(0, inputTokens - _lastWarmupInputTokens) : freshTokens);
                _lastWarmupInputTokens = inputTokens;

                Ui.Success($"Handshake {stepPrefix}erfolgreich.", "Cache-Warming");
                if (!string.IsNullOrWhiteSpace(responseText)) {
                    Ui.Detail($"[Gemini Antwort] {responseText.Trim()}");
                }

                // IMPORTANT: "Gecacht" and with it "NEU berechnet" are Google's unreliable cache figure (UsageReport.CachedTokens), not facts.
                string caller = $"NEU berechnet (laut Google): {freshTokens:N0} | Gecacht (laut Google): {cachedTokens:N0} | Total Prompt: {inputTokens:N0} | Output: {outputTokens:N0}";
                string tag = string.IsNullOrEmpty(stepLabel) ? "[Warmup Result]" : $"[{stepLabel}]";
                Ui.Info(usage.Describe(caller, tag).TrimStart(), "Tokens");

                int delay = customDelay ?? _config.VideoPartDelaySeconds;
                if (delay > 0) {
                    Ui.Detail($"Warte {delay} Sekunden (Token Refill)...", "Rate-Limit");
                    await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
                }
                return true;
            } else {
                Ui.Warn($"Cache-Warming Handshake {stepPrefix}abgebrochen oder fehlgeschlagen.", "Cache-Warming");
                return false;
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Cache-Warming Handshake fehlgeschlagen: {ex.Describe()}. Das Setup wird abgebrochen.", "Cache-Warming");
            int delay = customDelay ?? _config.VideoPartDelaySeconds;
            if (delay > 0) {
                Ui.Detail($"Warte {delay} Sekunden (Token Refill nach Handshake)...", "Rate-Limit");
                await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Handshake...");
            }
        }
        return false;
    }
}
