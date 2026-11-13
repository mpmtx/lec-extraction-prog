using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] The model-interaction half of the refinement session: resolving each step's system
/// instruction from disk, creating and reusing the Vertex context cache, assembling the request
/// config, streaming the response back, dumping the prompt log, and computing the expected
/// structural counts used to sanity-check the result.
///
/// <para>Split out of LatexRefinementSession (Phase 11), leaving that file with the pipeline and
/// the three refinement steps. The boundary is "what one step does when it talks to Gemini" versus
/// "which steps run, in what order, over which files" - the former changes when the API or caching
/// strategy changes, the latter when the pipeline shape does.</para>
/// [Human] Die Modell-Hälfte der Refinement-Session: System Instruction laden, Context-Cache,
/// Request-Konfiguration, Streaming und Prompt-Log. Aus der Hauptdatei herausgelöst.
/// </summary>
public partial class LatexRefinementSession {

    private static async Task<string> ResolveSystemInstructionTextAsync(RefinementStepConfig stepConfig) {
        string systemInstructionText = "";
        if (stepConfig.SystemInstructionPaths != null && stepConfig.SystemInstructionPaths.Length > 0) {
            Ui.Info("Folgende System-Instruktionen sind konfiguriert:", "LaTeX Refinement");
            var resolved = HistoryFileResolver.ResolveHistoryFiles(stepConfig.SystemInstructionPaths, "SystemInstructionPath");
            FileTreeRenderer.PrintFileTree(resolved);
            foreach (var path in resolved) {
                if (System.IO.File.Exists(path)) {
                    Ui.Info($"Lade System-Instruktion: {path}");
                    string relPath = Path.GetFileName(path);
                    systemInstructionText += $"******\n------\n******\nHere is the file `{relPath}`:\n\n" + await System.IO.File.ReadAllTextAsync(path) + "\n\n";
                }
                else {
                    Ui.Warn($"System-Instruktion nicht gefunden und übersprungen: {path}");
                }
            }
            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;
        }
        return systemInstructionText;
    }

    private async Task<string?> EnsureContextCacheAsync(BackendParameters backendParams, string systemInstructionText, string outputFileName, string cacheStateFileName) {
        string? cacheName = null;
        if (_config.UseVertex && backendParams.UseContextCaching && !string.IsNullOrWhiteSpace(systemInstructionText)) {
            string checksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText);
            var savedState = ContextCacheStateManager.LoadState(cacheStateFileName);
            bool match = ContextCacheStateManager.MatchesConfig(
                savedState,
                backendParams.CurrentModel,
                backendParams.Temperature,
                backendParams.TopP,
                backendParams.TopK,
                ModelCapabilities.ResolveMaxOutputTokens(backendParams.CurrentModel, backendParams.MaxOutputTokens),
                backendParams.ThinkingBudget,
                ModelCapabilities.ResolveThinkingLevel(backendParams.CurrentModel, backendParams.ThinkingLevel),
                checksum
            );
            if (match && await ContextCacheStateManager.IsValidRemoteAsync(_client, savedState.CacheName!)) {
                cacheName = savedState.CacheName;
                Ui.Info($"Bestehender Google Kontext-Cache geladen: {cacheName}", "Cache");
            }
            else {
                if (!string.IsNullOrEmpty(savedState.CacheName)) {
                    await ContextCacheStateManager.DeleteRemoteAsync(_client, savedState.CacheName);
                }
                Ui.Info("Erstelle neuen Google Kontext-Cache...", "Cache");
                cacheName = await CreateContextCacheAsync(backendParams, systemInstructionText, outputFileName, checksum, cacheStateFileName, isRecreate: false);
            }
        }
        else if (_config.UseVertex && !backendParams.UseContextCaching) {
            var sState = ContextCacheStateManager.LoadState(cacheStateFileName);
            if (!string.IsNullOrEmpty(sState.CacheName)) {
                await ContextCacheStateManager.DeleteRemoteAsync(_client, sState.CacheName);
                ContextCacheStateManager.ClearState(cacheStateFileName);
            }
        }

        if (!string.IsNullOrEmpty(cacheName) && backendParams.UseContextCaching && !string.IsNullOrWhiteSpace(systemInstructionText)) {
            var cacheState = ContextCacheStateManager.LoadState(cacheStateFileName);
            double remainingMin = ContextCacheStateManager.GetRemainingMinutes(cacheState);
            bool cacheValid = false;

            if (remainingMin > 0) {
                if (remainingMin < backendParams.ContextCachingMinimumRemainingMinutes) {
                    Ui.Info($"TTL knapp ({remainingMin:F1} min). Verlängere automatisch um {backendParams.ContextCachingIncrementMinutes} min...", "Cache");
                    var updatedState = await ContextCacheStateManager.ExtendCacheAsync(_client, cacheState, backendParams.ContextCachingIncrementMinutes, cacheStateFileName);
                    if (updatedState != null) {
                        Ui.Info($"Cache erfolgreich verlängert bis: {updatedState.ExpireTimeUtc.ToLocalTime():t}", "Cache");
                        cacheValid = true;
                    }
                }
                else {
                    cacheValid = await ContextCacheStateManager.IsValidRemoteAsync(_client, cacheName);
                }
            }

            if (!cacheValid) {
                Ui.Info("Cache abgelaufen oder ungültig. Erstelle neuen Google Kontext-Cache...", "Cache");
                ContextCacheStateManager.ClearState(cacheStateFileName);
                string checksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText);
                cacheName = await CreateContextCacheAsync(backendParams, systemInstructionText, outputFileName, checksum, cacheStateFileName, isRecreate: true);
            }
        }

        return cacheName;
    }

    /// <summary>
    /// [AI Context] Assembles the GenerateContentConfig for a refinement step: cached content or plain
    /// system instruction (mutually exclusive), plus thinking config (with thought summaries when the
    /// step's ThoughtSummaries asks for them).
    /// [Human] Baut die Anfrage-Konfiguration für einen Refinement-Schritt.
    /// </summary>
    private static GenerateContentConfig BuildStepRequestConfig(BackendParameters backendParams, string? cacheName, string systemInstructionText, bool includeThoughts) {
        var requestConfig = new GenerateContentConfig {
            Temperature = backendParams.Temperature,
            TopP = backendParams.TopP,
            TopK = backendParams.TopK,
            MaxOutputTokens = ModelCapabilities.ResolveMaxOutputTokens(backendParams.CurrentModel, backendParams.MaxOutputTokens),
            AutomaticContinuation = false
        };
        if (!string.IsNullOrEmpty(cacheName)) {
            requestConfig.CachedContent = cacheName;
        }
        else if (!string.IsNullOrWhiteSpace(systemInstructionText)) {
            // Simplified using target‑typed new for both the Content and Part objects.
            requestConfig.SystemInstruction = new() { Role = "system", Parts = [new() { Text = systemInstructionText }] };
        }

        requestConfig.ThinkingConfig = ThinkingConfigFactory.Build(
            backendParams.CurrentModel, backendParams.ThinkingBudget, backendParams.ThinkingLevel, includeThoughts);

        return requestConfig;
    }

    /// <summary>
    /// [AI Context] Dumps the full conversation history Gemini will read into a log file, for debugging.
    /// Failure here is non-fatal -- only the log write is skipped.
    /// [Human] Speichert den vollständigen Gemini-Prompt-Verlauf als Log-Datei (nur Diagnose).
    /// </summary>
    private static async Task DumpPromptLogAsync(List<Content> history, string systemInstructionText, string targetOutputFolder, string outputFileName) {
        try {
            var sbPrompt = new System.Text.StringBuilder();
            sbPrompt.AppendLine("# SYSTEM INSTRUCTION");
            sbPrompt.AppendLine(systemInstructionText);
            sbPrompt.AppendLine("\n---\n");

            foreach (var turn in history) {
                sbPrompt.AppendLine($"# ROLE: {turn.Role}");
                if (turn.Parts != null) {
                    foreach (var part in turn.Parts) {
                        sbPrompt.AppendLine(part.Text ?? "[Media Attachment]");
                    }
                }
                sbPrompt.AppendLine("\n---\n");
            }

            string promptDumpPath = Path.Combine(targetOutputFolder, $"{outputFileName}-prompt-log.md");
            await System.IO.File.WriteAllTextAsync(promptDumpPath, sbPrompt.ToString());
            Ui.Info($"Gemini-Prompt-Log gespeichert unter: {promptDumpPath}");
        }
        catch (Exception ex) {
            Ui.Warn($"Konnte Prompt-Log nicht speichern: {ex.Describe()}");
        }
    }

    private static (int ExpectedSpokenClean, int ExpectedMathStroke) ComputeExpectedStructuralCounts(List<Content> history) {
        int expectedSpokenClean = 0;
        int expectedMathStroke = 0;
        try {
            string allInputText = "";
            foreach (var turn in history) {
                if (turn.Parts != null) {
                    foreach (var part in turn.Parts) {
                        if (!string.IsNullOrEmpty(part.Text)) {
                            allInputText += part.Text + "\n";
                        }
                    }
                }
            }
            expectedSpokenClean = SpokenCleanRegex().Count(allInputText);
            expectedMathStroke = MathStrokeRegex().Count(allInputText);
            if (expectedSpokenClean > 0 || expectedMathStroke > 0) {
                Ui.Info($"Structural Integrity Tracker: Erwarte ca. {expectedSpokenClean}x speech und {expectedMathStroke}x content Blöcke im Output.");
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Structural Integrity Check übersprungen: {ex.Describe()}");
        }
        return (expectedSpokenClean, expectedMathStroke);
    }

    /// <summary>
    /// [AI Context] Streams one refinement step, sending continue requests until the completion
    /// marker arrives. The returned usage is the sum over every request the step needed - each
    /// continue request re-sends and re-bills the whole conversation - with thinking tokens kept
    /// apart from the visible output.
    /// [Human] Streamt einen Refinement-Schritt inkl. Continue-Anfragen und summiert deren Tokens.
    /// </summary>
    private async Task<(string FullResponseText, TokenUsage Usage, bool Usable)> StreamAndCollectAsync(
        RefinementStepConfig stepConfig, BackendParameters backendParams, List<Content> history, GenerateContentConfig requestConfig, string outputFileName, ThoughtLog thoughts) {
        string providerName = _config.UseVertex ? "Vertex AI" : "Google AI Studio";
        // RateLimitDelaySeconds paces the requests, where 0 means no wait, and also seeds the retry
        // backoff, which keeps its 130 s fallback: a failed request retried at once fails again.
        int rateLimitDelay = stepConfig.RateLimitDelaySeconds;
        int retryBackoff = rateLimitDelay > 0 ? rateLimitDelay : 130;

        var result = await GenerationLoop.RunAsync(history,
            (h, continuationToken) => {
                ApiKeyQuotaTracker.RecordClientRequest(_client, backendParams.CurrentModel, outputFileName);
                var hopConfig = GenerationLoop.PrepareHopConfig(requestConfig, backendParams.CurrentModel, continuationToken);
                return _client.Models.GenerateContentStreamAsync(backendParams.CurrentModel, h, hopConfig);
            },
            thoughts,
            new GenerationLoopOptions {
                MaxContinueRequests = stepConfig.MaxContinueRequests,
                SupportsLongDecode = ModelCapabilities.SupportsLongDecode(backendParams.CurrentModel),
                BeforeRequestAsync = () => WaitOutRateLimitAsync(rateLimitDelay,
                    "vor dem nächsten API-Aufruf...", "Warte auf Rate-Limits (Token-Refill Schutz vor API-Aufruf)..."),
                AnnounceRequest = (request, max) =>
                    Ui.Info($"Sende Anfrage an {providerName} ({backendParams.CurrentModel}) (Request {request}/{max})...", "API"),
                OnChunk = (chunk, text) => {
                    // The last chunk of every normal answer is empty with STOP; only other reasons say anything.
                    if (string.IsNullOrEmpty(text) && !thoughts.IsEnabled && chunk.Candidates != null && chunk.Candidates.Count > 0
                        && !string.Equals(ResponseParts.FinishReason(chunk), "STOP", StringComparison.OrdinalIgnoreCase)) {
                        Ui.Detail($"Empty text in chunk. FinishReason: {chunk.Candidates[0].FinishReason}", "DEBUG");
                    }
                },
                RetryContext = outputFileName,
                InitialBackoff = retryBackoff,
                FirstChunkTimeout = AppConfig.FirstChunkTimeout,
                HighDemandDelay = retryBackoff,
                CancelledAsWarning = true,
                ReportRequest = request => {
                    var usage = request.Usage;
                    // IMPORTANT: "Gecacht"/"Frisch" come from Google's cache figure, which is unreliable (hit or miss; UsageReport.CachedTokens).
                    Ui.Info(usage.Describe($"[Request {request.RequestNumber}] Total Prompt: {usage.PromptTokens:N0} | Gecacht: {usage.CachedTokens:N0} | Output: {usage.CandidateTokens:N0}", tag: "").TrimStart(), "Tokens");
                    if (usage.WasReported) {
                        Ui.Info(usage.DescribeOutputBudget(ModelCapabilities.ResolveMaxOutputTokens(backendParams.CurrentModel, backendParams.MaxOutputTokens)), "Tokens");
                    }
                },
                EmptyAnswerRetries = 3,
                IsComplete = text => text.Contains(RefinementCompleteMarker, StringComparison.OrdinalIgnoreCase),
                BudgetStopSuffix = "Breche dieses Refinement ab.",
                BuildContinuePrompt = BuildRefinementContinuePrompt,
                WaitBeforeContinueAsync = async () => {
                    if (rateLimitDelay <= 0) return true;
                    Ui.Detail($"Warte {rateLimitDelay} Sekunden (Token Refill)...", "Rate-Limit");
                    if (!await InteractiveDelay.SmartDelayAsync(rateLimitDelay, "Warte auf Rate-Limits (Token Refill)...")) {
                        Ui.Warn("Warten durch Benutzer abgebrochen.");
                        return false;
                    }
                    return true;
                },
            });
        return (result.Text, result.Usage, result.IsUsable);
    }

    /// <summary>The line the refinement and PDF-repair prompts ask the model to end its answer with.</summary>
    private const string RefinementCompleteMarker = "% [SYSTEM] Refinement complete";

    private string BuildRefinementContinuePrompt(string response) {
        // The step prompts demand raw LaTeX, so a trailing fence means the model wrapped its answer
        // anyway and probably believes it is done - the usual way the completion marker gets lost.
        bool closedBlock = response.TrimEnd().EndsWith("```");
        string continuePrompt = GenerationLoop.CutShortPrompt(response,
            "Please \"continue\" exactly where you left off. Start typing the VERY NEXT CHARACTER that would come after your last output. Do not repeat anything you already wrote. Output raw LaTeX only, without Markdown code fences. Do not open a new environment, and do not open new math delimiters if you were already inside one. Just print the very next character.",
            _config.ContinuePromptEchoCharacters);

        if (closedBlock) {
            continuePrompt += "\n\n[WARNING] Your output ended with a Markdown code fence (```), which the raw LaTeX output contract forbids, and without the '% [SYSTEM] Refinement complete' marker. If you have not finished refining the ENTIRE document, DO NOT just send the marker! Continue with the remaining content as raw LaTeX, without any code fence. If you have finished, output only the marker.";
        }

        Ui.Info("Unerwartetes Ende der Antwort. Bereite automatisierten 'Continue'-Prompt vor...", "Refinement");
        Ui.Detail($"Sende folgenden Continue-Prompt:\n{continuePrompt}");
        return continuePrompt;
    }

    /// <summary>
    /// [AI Context] Waits out what is left of <paramref name="delaySeconds"/> since the last
    /// generation finished, then clears HasJustUploaded. Runs before every refinement and PDF-repair
    /// request; false (the wait was cancelled) ends the stream loop.
    /// [Human] Wartet vor einer Anfrage den Rest der Rate-Limit-Pause ab.
    /// </summary>
    private static async Task<bool> WaitOutRateLimitAsync(int delaySeconds, string announcementEnd, string spinnerMessage) {
        double secondsSinceLastGen = (DateTime.UtcNow - InteractiveDelay.LastGenerationCompletionTimeUtc).TotalSeconds;
        if (secondsSinceLastGen < delaySeconds && !InteractiveDelay.IsInSmartDelay) {
            int waitRemaining = (int)Math.Ceiling(delaySeconds - secondsSinceLastGen);
            Ui.Detail($"Warte verbleibende {waitRemaining} Sekunden {announcementEnd}", "Rate-Limit & Quota");
            if (!await InteractiveDelay.SmartDelayAsync(waitRemaining, spinnerMessage)) {
                return false;
            }
        }
        AttachmentUploader.HasJustUploaded = false;
        return true;
    }

    private async Task<string?> CreateContextCacheAsync(BackendParameters backendParams, string systemInstructionText, string outputFileName, string checksum, string cacheStateFileName, bool isRecreate) {
        try {
            var cacheConfig = new CreateCachedContentConfig {
                SystemInstruction = new() { Role = "system", Parts = [new() { Text = systemInstructionText }] },
                DisplayName = $"latex-ref-{Path.GetFileNameWithoutExtension(outputFileName)}",
                Ttl = $"{backendParams.ContextCachingMinutes * 60}s"
            };
            var created = await _client.Caches.CreateAsync(backendParams.CurrentModel, cacheConfig);
            if (created != null && !string.IsNullOrEmpty(created.Name)) {
                string cacheName = created.Name;
                var newState = new ContextCacheState {
                    CacheName = cacheName,
                    Model = backendParams.CurrentModel,
                    Temperature = backendParams.Temperature,
                    TopP = backendParams.TopP,
                    TopK = backendParams.TopK,
                    MaxOutputTokens = ModelCapabilities.ResolveMaxOutputTokens(backendParams.CurrentModel, backendParams.MaxOutputTokens),
                    ThinkingBudget = backendParams.ThinkingBudget,
                    ThinkingLevel = ModelCapabilities.ResolveThinkingLevel(backendParams.CurrentModel, backendParams.ThinkingLevel),
                    SystemInstructionChecksum = checksum,
                    ExpireTimeUtc = DateTime.UtcNow.AddMinutes(backendParams.ContextCachingMinutes)
                };
                if (created.ExpireTime.HasValue) {
                    newState.ExpireTimeUtc = created.ExpireTime.Value.ToUniversalTime();
                }
                ContextCacheStateManager.SaveState(newState, cacheStateFileName);
                if (isRecreate) {
                    Ui.Success($"Google Kontext-Cache erfolgreich neu erstellt: {cacheName}");
                }
                else {
                    Ui.Success($"Google Kontext-Cache erfolgreich erstellt: {cacheName}");
                }
                return cacheName;
            }
        }
        catch (Exception ex) {
            Ui.Error($"Kontext-Caching fehlgeschlagen: {ex.Describe()}");
        }
        return null;
    }

}
