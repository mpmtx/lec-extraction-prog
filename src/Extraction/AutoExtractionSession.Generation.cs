using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Latex;
using LectureExtraction.Media;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Everything the extraction session does for a single video segment once FFmpeg has
/// produced it: uploading the segment and building its prompt, assembling the generation request
/// (system instruction, history, reference context, thinking config), the optional per-part token
/// diagnostic, and streaming the response back while accumulating usage.
///
/// <para>Split out of the session (Phase 11), mirroring the same cut made in
/// LatexRefinementSession. The boundary is per-segment model interaction versus batch
/// orchestration - the pipeline half decides which videos and parts get processed and in what
/// order, this half decides what one request looks like.</para>
/// [Human] Alles, was für einen einzelnen Video-Teil passiert: Upload, Prompt- und Request-Aufbau,
/// Token-Diagnose und Streaming. Vom Batch-Ablauf getrennt.
/// </summary>
public partial class AutoExtractionSession {

    private async Task<SegmentUpload> UploadSegmentAndBuildPromptAsync(string partFile, int partNumber, int totalParts, string originalFileName, double fullOriginalVideoDuration) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        double partDurationSeconds = await FfmpegToolkit.GetVideoDurationAsync(partFile);
        string durationString = ExtractionHelpers.FormatDurationForPrompt(partDurationSeconds);
        string fullDurationString = ExtractionHelpers.FormatDurationForPrompt(fullOriginalVideoDuration);

        string dateMetadata = LectureMetadataText(dateInfo, dateContext, partNumber);
        string singlePartCheckpointNote = totalParts == 1
            ? " Since this is a complete single-part video (Part 1 of 1), do NOT emit a trailing `\\begin{aiNote}[Final Video Cut-off \\& Board State Checkpoint]` at the end of the document."
            : "";

        string prompt =
            $"<parameter name=\"lecture_metadata\">{dateMetadata}</parameter>\n" +
            $"<parameter name=\"source_video\">You must transcribe the video attachment named `{Path.GetFileName(partFile)}` verbatim according to the system instructions. Ensure you transcribe every single spoken word up to the very last second of the video, even if it cuts off mid-sentence.</parameter>\n" +
            $"<parameter name=\"segment_info\">You are currently transcribing Part {partNumber} of {totalParts} from this lecture. This specific video segment is exactly {durationString} long; that is the value for `% End of the video`. For context only, the whole lecture, of which this segment is one part, runs {fullDurationString}; never use that number for a timestamp.{singlePartCheckpointNote}</parameter>\n" +
            $"<parameter name=\"duration_and_timestamps\">Do NOT calculate any time offset for the 'speech' environment. Start at 00:00:00 and ensure the final timestamp in your very last 'speech' block perfectly matches the segment length ({durationString}).</parameter>\n" +
            "</context_and_parameters>";

        var (uploadSuccess, attachmentParts) = await _attachmentHandler.UploadFilesAsync([partFile]);
        if (!uploadSuccess || attachmentParts.Count == 0) return new SegmentUpload(false, null, []);

        return new SegmentUpload(true, prompt, attachmentParts);
    }

    /// <summary>
    /// [AI Context] The lecture_metadata parameter: date, weekday and week from the video's file name.
    /// When the name states no weekday, the model is told so - derived from the date when there is
    /// one, otherwise explicitly unknown - instead of reading "day of the week (Unknown)" with no
    /// reason given and filling in a guess. A name that states its weekday gets the old text unchanged.
    /// [Human] Datums-Angaben für den Prompt; sagt Gemini, wenn der Wochentag nicht im Dateinamen steht.
    /// </summary>
    public static string LectureMetadataText(VideoDateParser.VideoDateInfo dateInfo, string dateContext, int partNumber) {
        string? weekday = dateInfo.WeekdayEnglish ?? dateInfo.Weekday;
        string weekdayNote = "";
        if (weekday == null) {
            if (dateInfo.Date != DateTime.MinValue) {
                weekday = dateInfo.Date.DayOfWeek.ToString();
                weekdayNote = $" The file name does not state the day of the week; {weekday} is derived from the date.";
            }
            else {
                weekday = "Unknown";
                weekdayNote = " The file name states neither the day of the week nor a date to derive it from, so the day of the week is unknown. Do not guess it: where the system instructions ask for it, say that it is unknown.";
            }
        }

        string weekInfo = dateInfo.WeekInfo ?? "N/A";
        return partNumber == 1
            ? $"The lecture being transcribed is from {dateContext}. Please note that the exact date, day of the week ({weekday}), and week number ({weekInfo}) are important metadata since this is part 1 of the lecture.{weekdayNote}"
            : $"The lecture took place on {dateContext} (Day of the week: {weekday}).{weekdayNote}";
    }

    /// <param name="partOutputPath">The part's .tex path; its thoughts file goes beside it. Without it no
    /// thoughts file is written, whatever ThoughtSummaries says.</param>
    public async Task<SegmentTranscript> TranscribeSegmentToLatexAsync(string partFile, int partNumber, string originalFileName, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles, string? partOutputPath = null) {
        var (requestConfig, history) = await BuildGenerationRequestAsync(partNumber, parsedPrompt, attachmentParts, previousTexFiles);

        int? promptTokens = await LogTokenCountsAsync(requestConfig, attachmentParts, history, previousTexFiles, partNumber);

        string logContext = $"[Part {partNumber}] {Path.GetFileName(originalFileName)}\n[Angehängtes Video]: {Path.GetFileName(partFile)}";
        if (previousTexFiles.Count > 0) {
            logContext += $"\n[Kontext-Dateien]: {string.Join(", ", previousTexFiles.Select(Path.GetFileName))}";
        }
        logContext += $"\n\n[Prompt]:\n{parsedPrompt ?? ""}";

        var thoughts = new ThoughtLog(_config.ThoughtSummaries, $"extract-part{partNumber}", $"{TexDocumentWriter.TranscriptionStep}, part {partNumber}",
            _config.CurrentModel, partOutputPath, _sessionLogger.ChatLogFilePath);
        return await StreamAndCollectAsync(requestConfig, history, partNumber, originalFileName, partFile, logContext, thoughts, promptTokens);
    }

    private async Task<(GenerateContentConfig RequestConfig, List<Content> History)> BuildGenerationRequestAsync(int partNumber, string? parsedPrompt, List<Part> attachmentParts, List<string> previousTexFiles) {
        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = ModelCapabilities.ResolveMaxOutputTokens(_config.CurrentModel, _config.MaxOutputTokens),
            AutomaticContinuation = false
        };

        // Vertex's explicit cache already holds the system instruction; the two are mutually exclusive.
        string? cachedContent = _backend.ContextCache?.Name;
        if (!string.IsNullOrEmpty(cachedContent)) {
            requestConfig.CachedContent = cachedContent;
        }
        else {
            var sysParts = GetValidSystemInstructionParts();
            if (sysParts.Count > 0) {
                requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
            }
        }

        requestConfig.ThinkingConfig = ThinkingConfigFactory.Build(
            _config.CurrentModel, _config.ThinkingBudget, _config.ThinkingLevel, IncludesThoughts);

        if (_config.UseGoogleSearch) {
            requestConfig.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        var userPromptParts = new List<Part>();
        
        // [AI Context] If LoadHistoryIntoSystemInstruction is false, we place the accumulated history 
        // at the very beginning of the User Message, BEFORE the static beginning and the video part.
        // This precise positioning is mandatory to perfectly align with the token sequence cached 
        // during the WarmUpWithBatchedHistoryAsync handshakes, guaranteeing a cache hit here.
        // Uploaded history files (InlineHistory=false) come first, in any case.
        userPromptParts.AddRange(HistoryUserTurnParts());

        var uploadedTexParts = new List<Part>();
        bool hasDummy = _config.SendDummyFileDuringTranscription;
        bool hasPreviousParts = previousTexFiles.Count > 0;
        if (_config.DebugSendReferenceFile && (hasDummy || hasPreviousParts)) {
            string precedingPartsText = "";
            if (hasPreviousParts) {
                if (_config.InlinePrecedingLecTexParts) {
                    Ui.Info("Bette folgende bereits generierte .tex-Dateien vor dem Video für optimales Prefix-Caching ein:", "Kontext");
                    precedingPartsText = await BuildInlinedPrecedingPartsAsync(previousTexFiles);
                } else {
                    var uploaded = await PrecedingTexReferences.UploadAsync(previousTexFiles, _attachmentHandler);
                    precedingPartsText = uploaded.ReferenceText;
                    uploadedTexParts.AddRange(uploaded.Parts);
                }
            }

            userPromptParts.Add(new Part { Text = ComposeReferenceContextText(precedingPartsText, partNumber) });
        } else {
            userPromptParts.Add(new Part { Text = GetStaticPromptBeginning(partNumber) });
        }

        // [AI Context] Uploaded .tex references sit between the text Part and the video, never inside the
        // text Part: splitting that Part around them would break the preamble+anchor prefix match too,
        // costing the anchor's cache benefit for no gain.
        // [Human] Hochgeladene .tex-Referenzen kommen nach dem Textblock und vor dem Video.
        userPromptParts.AddRange(uploadedTexParts);

        userPromptParts.AddRange(attachmentParts);

        if (!string.IsNullOrWhiteSpace(parsedPrompt)) {
            userPromptParts.Add(new Part { Text = parsedPrompt });
        }

        var history = new List<Content>();
        history.AddRange(_sessionPreamble);
        history.Add(new Content { Role = "user", Parts = userPromptParts });

        return (requestConfig, history);
    }

    /// <summary>
    /// [AI Context] One &lt;reference_context&gt; block. The dummy anchor, the inlined preceding parts and
    /// every warm-up handshake are built from this, so the warmed prefix and the real request agree
    /// byte for byte - the precondition for an implicit-cache hit.
    /// [Human] Ein &lt;reference_context&gt;-Block; von Request und Warm-up gemeinsam genutzt.
    /// </summary>
    private static string ReferenceContextBlock(string fileName, string content) =>
        $"<reference_context file=\"{fileName}\">\n{content}\n</reference_context>\n\n";

    private string DummyReferenceBlock() =>
        ReferenceContextBlock("part0.tex", PrepareReferenceContent(PrefixCacheAnchor.LoadPrefixCacheAnchorText()));

    /// <summary>
    /// [AI Context] Applies StripCommentsFromReferenceContext to one file's text. Every inlined file goes
    /// through here, so the preamble's rule 4 is true for all of them.
    /// [Human] Bereitet den Inhalt einer Kontext-Datei auf (ggf. ohne Kommentare).
    /// </summary>
    /// <param name="profile">The ReferenceSlimming profile for this file (OlderParts or PrecedingPart),
    /// or null for the dummy anchor. Runs after the comment stripping, which would otherwise remove the
    /// `% tokenOpt` markers again.</param>
    private string PrepareReferenceContent(string tex, ReferenceSlimProfile? profile = null) {
        string prepared = StripsReferenceContextComments ? LatexCommentStripper.StripCommentsAndHiddenBlocks(tex, profile?.KeepCommentPrefixes) : tex;
        return profile is null ? prepared : ReferenceContextSlimmer.Slim(prepared, profile);
    }

    /// <summary>
    /// [AI Context] Reads the preceding parts' .tex files and wraps each in a reference_context block,
    /// in part order.
    /// [Human] Liest die .tex-Dateien der vorherigen Teile und bettet sie als Referenzblöcke ein.
    /// </summary>
    private async Task<string> BuildInlinedPrecedingPartsAsync(IReadOnlyList<string> previousTexFiles) {
        var builder = new System.Text.StringBuilder();
        for (int i = 0; i < previousTexFiles.Count; i++) {
            string previousTexFile = previousTexFiles[i];
            string previousTexFileName = Path.GetFileName(previousTexFile);
            string content = await System.IO.File.ReadAllTextAsync(previousTexFile);
            // The most recent preceding part has its own profile (complete by default): the current part continues it.
            bool older = i < previousTexFiles.Count - 1;
            ReferenceSlimProfile? profile = older
                ? (SlimsOlderReferenceParts ? _config.ReferenceSlimming.OlderParts : null)
                : _config.ReferenceSlimming.PrecedingPart;
            string prepared = PrepareReferenceContent(content, profile);
            string what = profile is not null && profile.RemovesMoreThanComments()
                ? $"gekürzt ({(older ? "OlderParts" : "PrecedingPart")})"
                : "ohne Kommentare/hidden";
            Ui.Detail(prepared.Length == content.Length
                ? $"- {previousTexFileName}"
                : $"- {previousTexFileName} ({content.Length:N0} -> {prepared.Length:N0} Zeichen {what})");
            builder.Append(ReferenceContextBlock(previousTexFileName, prepared));
        }
        return builder.ToString();
    }

    /// <summary>
    /// [AI Context] The text Part that opens a part's user turn when reference context is sent:
    /// preamble, dummy anchor (if configured), the preceding parts' text, then the static prompt
    /// beginning. Shared by BuildGenerationRequestAsync and PrimePrefixCacheForResumedPartAsync, so the
    /// resumed-part warm-up caches exactly the prefix the real request will send.
    /// [Human] Der Textblock vor dem Video; gemeinsam für echten Request und Resume-Warm-up.
    /// </summary>
    private string ComposeReferenceContextText(string precedingPartsText, int partNumber) {
        var builder = new System.Text.StringBuilder(ReferenceContextPreamble);
        if (_config.SendDummyFileDuringTranscription) {
            builder.Append(DummyReferenceBlock());
        }
        builder.Append(precedingPartsText);
        builder.Append(GetStaticPromptBeginning(partNumber));
        return builder.ToString();
    }

    private async Task<int?> LogTokenCountsAsync(GenerateContentConfig requestConfig, List<Part> attachmentParts, List<Content> history, List<string> previousTexFiles, int partNumber) {
        // Local and free, so logged even when the CountTokens calls are skipped.
        Ui.Info($"[Part {partNumber} Prompt] {MinuteGateEstimate.Describe(requestConfig.SystemInstruction, history)}", "Tokens");
        if (_config.SkipTokenCountingDuringWarmUp) return null;
        int? countedPromptTokens = null;

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
            countedPromptTokens = promptToks > 0 ? promptToks : null;

            if (_config.VerboseConsoleOutput) {
                var userPromptParts = history[^1].Parts;
                bool hasDummy = _config.SendDummyFileDuringTranscription;
                bool hasPreviousParts = previousTexFiles.Count > 0;
                if (_config.DebugSendReferenceFile && (hasDummy || hasPreviousParts) && userPromptParts != null && userPromptParts.Count > 0 && !string.IsNullOrEmpty(userPromptParts[0].Text)) {
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
        return countedPromptTokens;
    }

    /// <summary>
    /// [AI Context] Whether Stage 1 requests thought summaries. The warm-up handshake reads it too, so
    /// its request config stays identical to the real part's.
    /// [Human] Ob die Transkription Gedanken-Zusammenfassungen anfordert.
    /// </summary>
    private bool IncludesThoughts => _config.ThoughtSummaries?.Enabled == true;

    private async Task<SegmentTranscript> StreamAndCollectAsync(GenerateContentConfig requestConfig, List<Content> history, int partNumber, string originalFileName, string partFile, string logContext, ThoughtLog thoughts, int? promptTokens) {
        var result = await GenerationLoop.RunAsync(history,
            (h, continuationToken) => {
                ApiKeyQuotaTracker.RecordClientRequest(_client, _config.CurrentModel, $"Part {partNumber}: {Path.GetFileNameWithoutExtension(originalFileName)}");
                var hopConfig = GenerationLoop.PrepareHopConfig(requestConfig, _config.CurrentModel, continuationToken);
                return _client.Models.GenerateContentStreamAsync(_config.CurrentModel, h, hopConfig);
            },
            thoughts,
            new GenerationLoopOptions {
                MaxContinueRequests = _config.MaxContinueRequests,
                RequestInputTokens = promptTokens,
                SupportsLongDecode = ModelCapabilities.SupportsLongDecode(_config.CurrentModel),
                FirstChunkTimeout = AppConfig.FirstChunkTimeout,
                AnnounceRequest = (request, max) =>
                    Ui.Step($"Sende Anfrage für Part {partNumber} an {_backend.ProviderName} ({_config.CurrentModel}) (Request {request}/{max})..."),
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
                    int delay = _config.VideoPartDelaySeconds;
                    if (delay <= 0) return true;
                    Ui.Detail($"Warte {delay} Sekunden vor der Fortsetzung, um API-Limits zu schonen...", "Timer");
                    if (!await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Rate-Limits (Token Refill)...")) {
                        Ui.Info("Warten durch Benutzer abgebrochen.");
                        return false;
                    }
                    return true;
                },
            });

        AttachmentUploader.HasJustUploaded = false;
        // IMPORTANT: a part whose stream failed for good is a fragment. Saved, resume would reuse it as finished
        // forever; returned empty, the part counts as failed and the next run requests it again.
        if (!result.IsUsable && result.Text.Length > 0) {
            Ui.Error($"Teil {partNumber} unvollständig ({result.End}): {result.Text.Length:N0} Zeichen werden verworfen, der Teil wird beim nächsten Lauf neu angefragt.", "AutoExtraction");
            return new SegmentTranscript("", result.Usage, thoughts.FilePath);
        }
        return new SegmentTranscript(result.Text, result.Usage, thoughts.FilePath);
    }

    /// <summary>
    /// [AI Context] A part request's token lines: the request, and in verbose mode the part and the
    /// session so far. Also adds the request to the session totals.
    /// [Human] Token-Zeilen einer Part-Anfrage; zählt sie zur Session-Summe.
    /// </summary>
    private void ReportPartRequest(GenerationRequest request, int partNumber) {
        var usage = request.Usage;
        _sessionUsage += new TokenUsage(usage.PromptTokens, usage.CandidateTokens, usage.CachedTokens);

        // IMPORTANT: "Gecacht"/"Frisch" come from Google's cache figure, which is unreliable (hit or miss; UsageReport.CachedTokens).
        int freshReqTokens = Math.Max(0, usage.PromptTokens - usage.CachedTokens);

        Ui.Info(usage.Describe($"[Part {partNumber} Request Response] Total Prompt (ganzer Prompt): {usage.PromptTokens:N0} | Gecacht: {usage.CachedTokens:N0} | Frisch: {freshReqTokens:N0} | Output: {usage.CandidateTokens:N0}", tag: "").TrimStart(), "Tokens");
        if (_config.VerboseConsoleOutput) {
            var part = request.Total;
            Ui.Detail($"[Part Total Tokens]    Total Prompt: {part.Input:N0} | Gecacht: {part.Cached:N0} | Frisch: {part.Fresh:N0} | Output: {part.Output:N0}");
            Ui.Detail($"[Session Total Tokens] Total Prompt: {_sessionUsage.Input:N0} | Gecacht: {_sessionUsage.Cached:N0} | Frisch: {_sessionUsage.Fresh:N0} | Output: {_sessionUsage.Output:N0}");
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
