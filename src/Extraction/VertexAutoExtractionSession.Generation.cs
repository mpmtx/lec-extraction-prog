using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] The per-segment generation half of the Vertex session, split out to mirror
/// AiStudioAutoExtractionSession.Generation.cs member-for-member - the two are twins and drift
/// between them is the single most expensive defect class in this codebase.
/// Member Index:
/// - UploadSegmentAndBuildPromptAsync: Uploads one video segment and builds its dynamic prompt.
/// - TranscribeSegmentToLatexAsync: Runs one segment through the model to LaTeX.
/// - BuildGenerationRequestAsync: Assembles the request config and history for one segment.
/// - BuildReferenceContextPreamble: The read-only warning introducing the preceding parts' LaTeX.
/// - BuildPreviousTexReferenceBlockAsync: Inlines preceding .tex parts as reference context.
/// - StreamAndCollectAsync: Streams the response, handles continuations and token accounting.
/// [Human] Der Generierungs-Teil der Vertex-Session, aufgeteilt wie beim AI-Studio-Zwilling.
/// </summary>
public partial class VertexAutoExtractionSession {
    private async Task<SegmentUpload> UploadSegmentAndBuildPromptAsync(string partFile, int partNumber, int totalParts, string originalFileName, double fullOriginalVideoDuration) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        string weekday = dateInfo.WeekdayEnglish ?? dateInfo.Weekday ?? "Unknown";

        double partDurationSeconds = await FfmpegToolkit.GetVideoDurationAsync(partFile);
        string durationString = ExtractionHelpers.FormatDurationForPrompt(partDurationSeconds);
        string fullDurationString = ExtractionHelpers.FormatDurationForPrompt(fullOriginalVideoDuration);

        string dateMetadata = partNumber == 1
            ? $"The lecture being transcribed is from {dateContext}. Please note that the exact date, day of the week ({weekday}), and week number ({dateInfo.WeekInfo ?? "N/A"}) are important metadata since this is part 1 of the lecture."
            : $"The lecture took place on {dateContext} (Day of the week: {weekday}). This is not so important since this is part {partNumber} of the lecture.";

        string prompt =
            $"<parameter name=\"lecture_metadata\">{dateMetadata}</parameter>\n" +
            $"<parameter name=\"source_video\">You must transcribe the video attachment named `{Path.GetFileName(partFile)}` verbatim according to the system instructions. Ensure you transcribe every single spoken word up to the very last second of the video, even if it cuts off mid-sentence.</parameter>\n" +
            $"<parameter name=\"segment_info\">You are currently transcribing Part {partNumber} of {totalParts} from this lecture. This specific video segment is exactly {durationString} long; that is the value for `% End of the video`. For context only, the whole lecture, of which this segment is one part, runs {fullDurationString}; never use that number for a timestamp.</parameter>\n" +
            $"<parameter name=\"duration_and_timestamps\">Do NOT calculate any time offset for the 'speech' environment. Start at 00:00:00 and ensure the final timestamp in your very last 'speech' block perfectly matches the segment length ({durationString}).</parameter>\n" +
            "</context_and_parameters>";

        var (uploadSuccess, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach \"{partFile}\" | {prompt}");
        if (!uploadSuccess || attachmentParts.Count == 0) return new SegmentUpload(false, null, []);

        return new SegmentUpload(true, parsedPrompt, attachmentParts);
    }

    /// <param name="partOutputPath">The part's .tex path; its thoughts file goes beside it. Without it no
    /// thoughts file is written, whatever ThoughtSummaries says.</param>
    private async Task<SegmentTranscript> TranscribeSegmentToLatexAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles, string? partOutputPath = null) {
        var (requestConfig, history) = await BuildGenerationRequestAsync(partFile, partNumber, parsedPrompt, attachmentParts, previousTexFiles);

        await LogTokenCountsAsync(requestConfig, attachmentParts, history, previousTexFiles, partNumber);

        string logContext = $"[Part {partNumber}] {Path.GetFileName(originalFileName)}\n[Angehängtes Video]: {Path.GetFileName(partFile)}";
        if (previousTexFiles.Count > 0) {
            logContext += $"\n[Kontext-Dateien]: {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
        }
        logContext += $"\n\n[Prompt]:\n{parsedPrompt ?? ""}";

        var thoughts = new ThoughtLog(_config.ThoughtSummaries, $"extract-part{partNumber}", $"{TexDocumentWriter.TranscriptionStep}, part {partNumber}",
            _config.CurrentModel, partOutputPath, _sessionLogger.ChatLogFilePath);
        return await StreamAndCollectAsync(requestConfig, history, partNumber, originalFileName, partFile, logContext, thoughts);
    }

    private async Task<(GenerateContentConfig RequestConfig, List<Content> History)> BuildGenerationRequestAsync(string partFile, int partNumber, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var userPromptParts = new List<Part>();

        var preVideoBuilder = new System.Text.StringBuilder();
        if (_config.EnableImplicitPrefixCacheWarmup && _config.SendDummyFileDuringTranscription) {
            preVideoBuilder.Append($"<reference_context file=\"part0.tex\">\n{PrefixCacheAnchor.LoadPrefixCacheAnchorText()}\n</reference_context>\n\n");
        }
        var uploadedTexParts = new List<Part>();
        if (_config.DebugSendReferenceFile && previousTexFiles.Count > 0) {
            if (_config.InlinePrecedingLecTexParts) {
                Ui.Info("Bette folgende bereits generierte .tex-Dateien vor dem Video für optimales Prefix-Caching ein:", "Kontext");
                preVideoBuilder.Append(await BuildPreviousTexReferenceBlockAsync(partFile, previousTexFiles));
            }
            else {
                // [AI Context] Upload mode replaces the append-at-the-end branch this used to have: the
                // reference text now sits in the same pre-video Part as the anchor (as on AI Studio), and
                // only the file references move, so the request has one stable shape either way.
                // [Human] Im Upload-Modus steht der Referenztext vor dem Video, nicht mehr am Ende.
                preVideoBuilder.Append(BuildReferenceContextPreamble(partFile));
                var uploaded = await PrecedingTexReferences.UploadAsync(previousTexFiles, _attachmentHandler);
                preVideoBuilder.Append(uploaded.ReferenceText);
                uploadedTexParts.AddRange(uploaded.Parts);
            }
        }
        preVideoBuilder.Append(GetStaticPromptBeginning(partNumber));
        userPromptParts.Add(new Part { Text = preVideoBuilder.ToString() });

        userPromptParts.AddRange(uploadedTexParts);

        userPromptParts.AddRange(attachmentParts);

        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        var history = new List<Content>();
        history.AddRange(_sessionPreamble);
        history.Add(new Content { Role = "user", Parts = userPromptParts });

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens,
            AutomaticContinuation = false
        };

        if (_config.UseGoogleSearch) {
            requestConfig.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        if (!string.IsNullOrEmpty(_cachedContentName)) {
            requestConfig.CachedContent = _cachedContentName;
        }
        else if (!string.IsNullOrWhiteSpace(_systemInstructionText) || (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0)) {
            var sysParts = new List<Part>();
            if (!string.IsNullOrWhiteSpace(_systemInstructionText)) sysParts.Add(new() { Text = _systemInstructionText });
            if (_config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0) {
                var textOnly = _historyParts.Where(p => p.FileData == null && p.InlineData == null && !string.IsNullOrEmpty(p.Text)).ToList();
                var nonText = _historyParts.Where(p => p.FileData != null || p.InlineData != null).ToList();
                sysParts.AddRange(textOnly);
                if (nonText.Count > 0 && history.Count == 0) {
                    history.Add(new Content { Role = "user", Parts = nonText });
                }
            }
            if (sysParts.Count > 0) {
                requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
            }
        }
        requestConfig.ThinkingConfig = ThinkingConfigFactory.Build(
            _config.CurrentModel, _config.ThinkingBudget, _config.ThinkingLevel, IncludesThoughts);

        return (requestConfig, history);
    }

    /// <summary>
    /// [AI Context] Whether Stage 1 requests thought summaries. The warm-up handshake reads it too, so
    /// its request config stays identical to the real part's (as in the AI Studio session).
    /// [Human] Ob die Transkription Gedanken-Zusammenfassungen anfordert.
    /// </summary>
    private bool IncludesThoughts => _config.ThoughtSummaries?.Enabled == true;

    /// <summary>
    /// [AI Context] The read-only warning that introduces the preceding parts' LaTeX, whether that
    /// LaTeX is inlined below it or attached as uploaded file references. Split out of
    /// BuildPreviousTexReferenceBlockAsync so the upload path can state the same rules without
    /// inlining anything; the wording is Vertex's own and stays byte-identical either way.
    /// [Human] Die Read-only-Warnung vor den Referenzdateien - gilt für eingebettete und hochgeladene.
    /// </summary>
    private static string BuildReferenceContextPreamble(string partFile) =>
        "IMPORTANT CONTEXT WARNING: Below is the LaTeX output generated from previous parts of this lecture.\n" +
        "You must treat this strictly as READ-ONLY reference material. It is provided ONLY so you know what has already been transcribed " +
        "and can correctly reference existing labels (e.g. \\ref{...}) if the professor refers back to previous theorems or equations.\n\n" +
        "CRITICAL RULES:\n" +
        "1. DO NOT rewrite, summarize, or continue transcribing this previous text.\n" +
        $"2. Your SOLE task is to transcribe the NEW attached video segment: `{Path.GetFileName(partFile)}`.\n" +
        "3. Treat these context files as read-only and focus entirely on the new video fragment.\n\n";

    private static async Task<string> BuildPreviousTexReferenceBlockAsync(string partFile, List<string> previousTexFiles) {
        var builder = new System.Text.StringBuilder(BuildReferenceContextPreamble(partFile));
        foreach (var texFile in previousTexFiles) {
            Ui.Detail($"- {Path.GetFileName(texFile)}");
            string content = await System.IO.File.ReadAllTextAsync(texFile);
            builder.Append($"<reference_context file=\"{Path.GetFileName(texFile)}\">\n{content}\n</reference_context>\n\n");
        }
        return builder.ToString();
    }

    private async Task LogTokenCountsAsync(GenerateContentConfig requestConfig, List<Part> attachmentParts, List<Content> history, List<string> previousTexFiles, int partNumber) {
        if (_config.SkipTokenCountingDuringWarmUp) return;

        try {
            var videoContents = new List<Content> { new() { Role = "user", Parts = attachmentParts } };
            // [AI Context] CountTokensAsync counts against the same Free-Tier quota as GenerateContent,
            // so all calls here are wrapped with retry to avoid exhausting the token budget mid-count.
            var videoCount = await ApiRetryPolicy.ExecuteWithRetryAsync(
                () => _client.Models.CountTokensAsync(_config.CurrentModel, videoContents),
                maxRetries: 8, initialBackoff: 20, retryContext: $"CountTokens Part {partNumber} (video)");
            int vidToks = videoCount?.TotalTokens ?? 0;

            var allContentsForCount = new List<Content>();
            if (requestConfig.SystemInstruction != null) allContentsForCount.Add(requestConfig.SystemInstruction);
            allContentsForCount.AddRange(history);

            var totalCount = await ApiRetryPolicy.ExecuteWithRetryAsync(
                () => _client.Models.CountTokensAsync(_config.CurrentModel, allContentsForCount),
                maxRetries: 8, initialBackoff: 20, retryContext: $"CountTokens Part {partNumber} (total)");
            int promptToks = totalCount?.TotalTokens ?? 0;
            Ui.Info($"[Part {partNumber} Prompt] Total Prompt-Tokens (ganzer Prompt): {promptToks:N0} (Video: {vidToks:N0} Tokens)", "Tokens");

            if (_config.VerboseConsoleOutput) {
                var userPromptParts = history[^1].Parts;
                bool hasDummy = _config.EnableImplicitPrefixCacheWarmup && _config.SendDummyFileDuringTranscription;
                bool hasPreviousParts = _config.DebugSendReferenceFile && previousTexFiles.Count > 0;
                if ((hasDummy || hasPreviousParts) && userPromptParts != null && userPromptParts.Count > 0 && !string.IsNullOrEmpty(userPromptParts[0].Text)) {
                    var texContents = new List<Content> { new() { Role = "user", Parts = [userPromptParts[0]] } };
                    var texCount = await ApiRetryPolicy.ExecuteWithRetryAsync(
                        () => _client.Models.CountTokensAsync(_config.CurrentModel, texContents),
                        maxRetries: 8, initialBackoff: 20, retryContext: $"CountTokens Part {partNumber} (tex)");
                    int texToks = texCount?.TotalTokens ?? 0;
                    string fileInfo;
                    if (hasDummy && hasPreviousParts && _config.InlinePrecedingLecTexParts) {
                        fileInfo = $"dummy-part0.tex + {previousTexFiles.Count} Datei(en): {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
                    } else if (hasDummy) {
                        fileInfo = "dummy-part0.tex";
                    } else {
                        fileInfo = $"{previousTexFiles.Count} Datei(en): {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
                    }
                    Ui.Detail($"- Inlined Kontext ({fileInfo}) Token: {texToks:N0}");
                }
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Fehler beim Zählen der Prompt-Token: [Exception gefangen] {ex.Describe()}", "Token-Analyse");
        }
    }

    private async Task<SegmentTranscript> StreamAndCollectAsync(GenerateContentConfig requestConfig, List<Content> history, int partNumber, string originalFileName, string partFile, string logContext, ThoughtLog thoughts) {
        var result = await GenerationLoop.RunAsync(history,
            h => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, h, requestConfig),
            thoughts,
            new GenerationLoopOptions {
                MaxContinueRequests = _config.MaxContinueRequests,
                AnnounceRequest = (request, max) =>
                    Ui.Step($"Sende Anfrage für Part {partNumber} an Vertex AI ({_config.CurrentModel}) (Request {request}/{max})..."),
                RetryContext = $"Teil {partNumber} von {Path.GetFileName(originalFileName)}",
                HighDemandDelay = _config.HighDemandDelaySeconds > 0 ? _config.HighDemandDelaySeconds : null,
                UnrecoverableErrorPrefix = "Der Fehler konnte nicht durch einen automatischen Retry behoben werden. Fahre mit nächstem Teil fort. Finaler Fehler: ",
                ShowGroundingSources = true,
                ReportRequest = request => ReportPartRequest(request, partNumber),
                // Thoughts first, so in chat_log.md the summary precedes the answer it led to.
                AfterRequestAsync = request => LogPartRequestAsync(request, partNumber, logContext),
                IsComplete = text => VideoCompleteRegex().IsMatch(text),
                BudgetStopSuffix = $"Breche diesen Teil ab ({partFile}).",
                BuildContinuePrompt = BuildPartContinuePrompt,
                WaitBeforeContinueAsync = async () => {
                    Ui.Detail("Warte 150 Sekunden vor der Fortsetzung...", "Timer");
                    if (!await InteractiveDelay.SmartDelayAsync(150, "Warte auf Fortsetzung (Sicherheits-Puffer)...")) {
                        Ui.Info("Warten durch Benutzer abgebrochen.");
                        return false;
                    }
                    return true;
                },
            });
        return new SegmentTranscript(result.Text, result.Usage, thoughts.FilePath);
    }

    /// <summary>
    /// [AI Context] A part request's token lines: the request, and in verbose mode the part and the
    /// session so far. Also adds the request to the session totals.
    /// [Human] Token-Zeilen einer Part-Anfrage; zählt sie zur Session-Summe.
    /// </summary>
    private void ReportPartRequest(GenerationRequest request, int partNumber) {
        var usage = request.Usage;
        _sessionTotalInputTokens += usage.PromptTokens;
        _sessionTotalOutputTokens += usage.CandidateTokens;
        _sessionTotalCachedTokens += usage.CachedTokens;

        int freshReqTokens = Math.Max(0, usage.PromptTokens - usage.CachedTokens);
        int freshSessTokens = Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens);

        Ui.Info(usage.Describe($"[Part {partNumber} Request Response] Total Prompt (ganzer Prompt): {usage.PromptTokens:N0} | Gecacht: {usage.CachedTokens:N0} | Frisch: {freshReqTokens:N0} | Output: {usage.CandidateTokens:N0}"), "Tokens");
        if (_config.VerboseConsoleOutput) {
            var part = request.Total;
            Ui.Detail($"[Part Total Tokens]    Total Prompt: {part.Input:N0} | Gecacht: {part.Cached:N0} | Frisch: {part.Fresh:N0} | Output: {part.Output:N0}");
            Ui.Detail($"[Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {freshSessTokens:N0} | Output: {_sessionTotalOutputTokens:N0}");
        }
    }

    /// <summary>Writes a part request to chat_log.md, under the prompt it answered.</summary>
    private Task LogPartRequestAsync(GenerationRequest request, int partNumber, string logContext) {
        string prompt = request.ContinuePrompt == null ? logContext : $"[Continue Prompt für Part {partNumber}]:\n{request.ContinuePrompt}";
        var usage = request.Usage;
        return _sessionLogger.LogChatAsync(prompt, prompt, _config.CurrentModel, request.Text, "AutoExtraction", usage.PromptTokens, usage.CandidateTokens, usage.CachedTokens);
    }

    /// <summary>
    /// [AI Context] A segment-complete marker asks for the next segment with a plain "Continue"; any
    /// other cut-off answer gets the full continue prompt quoting its tail.
    /// [Human] Continue-Prompt einer Part-Anfrage: "Continue" nach Segment-Ende, sonst mit Zitat.
    /// </summary>
    private static string BuildPartContinuePrompt(string response) {
        bool segmentComplete = SegmentCompleteRegex().IsMatch(response);
        string continuePrompt = segmentComplete ? "Continue" : GenerationLoop.CutShortPrompt(response,
            "Please \"continue\" exactly where you left off, as raw LaTeX without Markdown code fences, just continuing the text directly.");

        if (segmentComplete) Ui.Info("Segment-Limit erreicht. Sende 'Continue'...", "AutoExtraction");
        else Ui.Info("Unerwartetes Ende der Antwort. Bereite automatisierten 'Continue'-Prompt vor...", "AutoExtraction");

        Ui.Detail($"[Sende folgenden Continue-Prompt:]\n{continuePrompt}");
        return continuePrompt;
    }
}
