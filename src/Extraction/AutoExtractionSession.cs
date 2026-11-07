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
using Spectre.Console;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Orchestrates the fully automated transcription pipeline, on AI Studio or Vertex.
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// What differs between the two platforms sits behind <see cref="IExtractionBackend"/>; until
/// 2026-10-15 each had its own session class (AiStudioAutoExtractionSession, VertexAutoExtractionSession).
/// Split into partial classes:
/// - AutoExtractionSession.cs (core pipeline, file batching, YouTube transcription)
/// - AutoExtractionSession.Generation.cs (per-segment upload, request building, streaming)
/// - AutoExtractionSession.PrefixCache.cs (implicit prefix cache warming & history loading)
/// Member Index:
/// - StartAsync: Validates folders, prompts mode selection (batch, single, youtube), and begins execution.
/// - RunAsync: Ensures session setup then processes files.
/// - ProcessFilesAsync: Producer/consumer loop for MP4 video segment extraction.
/// - ProcessYouTubeTasksAsync: YouTube video download and transcription pipeline.
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos,
/// für AI Studio und Vertex.
/// </summary>
public partial class AutoExtractionSession(IExtractionBackend backend, IAutoExtractionConfig config, AttachmentUploader attachmentHandler, SessionLogger sessionLogger, LatexRefinementSessionConfig latexRefinementConfig) : IYouTubeTranscriptionHost {
    private readonly IExtractionBackend _backend = backend;
    private readonly Client _client = backend.Client;
    private readonly IAutoExtractionConfig _config = config;
    private readonly AttachmentUploader _attachmentHandler = attachmentHandler;
    private readonly SessionLogger _sessionLogger = sessionLogger;
    private readonly LatexRefinementSessionConfig _latexRefinementConfig = latexRefinementConfig;
    private string _systemInstructionText = "";
    // [AI Context] Cached payloads to avoid redundant uploads and API calls across multiple video chunks.
    private readonly List<Part> _historyParts = [];
    // [AI Context] History text files uploaded under InlineHistory=false (header Part + file link each); always in the user turn.
    private readonly List<Part> _uploadedHistoryParts = [];
    // [AI Context] Stores the acknowledged history prompt and the model's confirmation, statically prepended to all subsequent API calls.
    private readonly List<Content> _sessionPreamble = [];
    private bool _historyWasLoaded = false;
    // Every request this session sent, warm-ups and debug roundtrip included; printed in verbose mode.
    private TokenUsage _sessionUsage;
    private int _sessionMaxFreshTokens = 0;
    private int _lastWarmupInputTokens = 0;

    /// <summary>
    /// [AI Context] Read-only preamble text placed before reference_context blocks in the user-turn.
    /// Instructs the model to treat previous .tex outputs as read-only reference, not as content to rewrite.
    /// Used identically in warm-up handshakes and real video extraction requests.
    /// [Human] Einleitungstext für die Referenz-Kontextblöcke – wird sowohl im Warm-up als auch in der echten Extraktion verwendet.
    /// </summary>
    private static readonly string ReferenceContextPreambleRules =
        "IMPORTANT CONTEXT WARNING: Below is the LaTeX output generated from previous parts of this lecture.\n" +
        "You must treat this strictly as READ-ONLY reference material. It is provided ONLY so you know what has already been transcribed " +
        "and can correctly reference existing labels (e.g. \\ref{...}) if the professor refers back to previous theorems or equations.\n\n" +
        "CRITICAL RULES:\n" +
        "1. DO NOT rewrite, summarize, or continue transcribing this previous text.\n" +
        "2. Your SOLE task is to transcribe the new attached video segment verbatim.\n" +
        "3. Treat these context files as read-only and focus entirely on the new video fragment.\n";

    // [AI Context] Rule 4 exists only while StripCommentsFromReferenceContext is in effect. The stripped
    // copies must not read as a style example: the system instructions require comments and `hidden`
    // checkpoints, and a model that sees thousands of tokens without any would drift towards omitting them.
    private const string StrippedCommentsRule =
        "4. To save tokens, all LaTeX comments and all `hidden` environments have been removed from these context files. " +
        "That applies to these reference copies only: it does NOT mean you may omit them. Your own transcription must still contain " +
        "every comment and every `hidden` environment the system instructions require.\n";

    // Rule 4 with a hedge when a ReferenceSlimming profile keeps some header comment lines.
    private string StrippedCommentsRuleText =>
        _config.ReferenceSlimming.PrecedingPart.KeepCommentPrefixes.Length > 0 || _config.ReferenceSlimming.OlderParts.KeepCommentPrefixes.Length > 0
            ? StrippedCommentsRule.Replace("all LaTeX comments and", "all LaTeX comments (except possibly a few header comment lines) and")
            : StrippedCommentsRule;

    /// <summary>
    /// [AI Context] Whether inlined reference context is stripped of comments in this session. The same
    /// answer decides the preamble's rule 4, so warm-up and part requests keep one identical prefix.
    /// [Human] Ob die eingebetteten Kontext-Dateien ohne Kommentare gesendet werden.
    /// </summary>
    private bool StripsReferenceContextComments =>
        _config.StripCommentsFromReferenceContext && _config.InlinePrecedingLecTexParts;

    // [AI Context] Rule 5 exists only while a ReferenceSlimming profile in use removes something, for the
    // same reason as rule 4: the slimmed copies must not read as a style example for speech blocks, boxes
    // or figures. Its text names exactly what each profile removes (ReferenceContextSlimmer.RuleText).

    /// <summary>Whether older inlined preceding parts are slimmed in this session (decides rule 5 too).</summary>
    private bool SlimsOlderReferenceParts =>
        _config.SlimOlderReferenceParts && _config.InlinePrecedingLecTexParts;

    private string ReferenceContextPreamble =>
        ReferenceContextPreambleRules
        + (StripsReferenceContextComments ? StrippedCommentsRuleText : "")
        + (_config.InlinePrecedingLecTexParts
            ? ReferenceContextSlimmer.RuleText(SlimsOlderReferenceParts ? _config.ReferenceSlimming.OlderParts : null,
                _config.ReferenceSlimming.PrecedingPart, StripsReferenceContextComments)
            : "")
        + "\n";

    /// <summary>
    /// [AI Context] How recently a part's .tex must have been written for it to be reused instead
    /// of re-requested. Was a hardcoded 2 hours on <c>VideoProcessingState</c> with no way to reach
    /// it, which made it the most expensive invisible constant here: a batch retried after the
    /// window lapsed silently re-bought every part it had already paid for. Still 2 hours by
    /// default, so the interactive run is unchanged. <see cref="PartReuse"/> measures it on the
    /// newest of the part's file and its -copy-N siblings.
    /// [Human] Wie alt eine bereits erzeugte .tex-Datei sein darf, um wiederverwendet zu werden.
    /// </summary>
    public TimeSpan ResumeWindow { get; init; } = TimeSpan.FromHours(2);

    /// <summary>
    /// [AI Context] Non-interactive entry point: process exactly these files and report whether
    /// every one succeeded. This is what <see cref="StartAsync"/> reaches after its mode menu has
    /// chosen a file list, exposed directly so the CLI can supply that list as arguments instead.
    /// Both paths run identical code from here down; the menu is the only difference.
    /// [Human] Einstiegspunkt ohne Menü: verarbeitet genau die übergebenen Dateien.
    /// </summary>
    /// <returns>false if any file failed, so a caller can report partial success.</returns>
    public async Task<bool> RunAsync(IReadOnlyList<string> files) {
        if (!PrepareWorkspace()) return false;
        if (files.Count == 0) {
            Ui.Info("Keine Dateien ausgewählt.");
            return true;
        }

        // Leftovers of a crashed run go first; the finally clears this run's uploads even on an exception.
        await _backend.CleanUpRemoteFilesAsync();
        try {
            if (!await EnsureSessionSetupAsync()) return false;
            if (_config.OnlyDoWarmUp) {
                Ui.Success("WarmUp & Context Setup abgeschlossen (OnlyDoWarmUp = true). Beende Session ohne Videoextraktion.", "Cache-Warming");
                return true;
            }
            return await ProcessFilesAsync([.. files]);
        }
        finally {
            await _backend.CleanUpRemoteFilesAsync();
        }
    }

    /// <summary>
    /// [AI Context] Non-interactive YouTube entry point, the counterpart of <see cref="RunAsync"/> for
    /// <c>lecx youtube</c>. Same cleanup around it as menu entry 3; the source folder is not needed,
    /// since the model reads the video from its URL.
    /// [Human] YouTube-Aufgaben ohne Menü ausführen.
    /// </summary>
    public async Task<bool> RunYouTubeAsync(IReadOnlyList<YouTubeTranscriptionTask> tasks) {
        string targetFolder = ExtractionHelpers.OutputRoot(_config);
        Directory.CreateDirectory(targetFolder);

        await _backend.CleanUpRemoteFilesAsync();
        try {
            return await new YouTubeTaskRunner(_config, this).RunAsync(tasks);
        }
        finally {
            await _backend.CleanUpRemoteFilesAsync();
        }
    }

    /// <summary>
    /// [AI Context] Validates the source folder and settles the target folder, which defaults to a
    /// subfolder of the source. Shared by the interactive and headless entry points so they cannot
    /// disagree about where output lands.
    /// [Human] Prüft den Quellordner und legt den Zielordner fest.
    /// </summary>
    private bool PrepareWorkspace() {
        if (!Directory.Exists(_config.SourceFolder)) {
            Ui.Error($"Quellordner nicht gefunden: {_config.SourceFolder}");
            return false;
        }

        string targetFolder = ExtractionHelpers.OutputRoot(_config);
        if (!Directory.Exists(targetFolder)) {
            Directory.CreateDirectory(targetFolder);
        }

        if (_config.HighDemandDelaySeconds > 0) {
            ApiRetryPolicy.DefaultHighDemandDelaySeconds = _config.HighDemandDelaySeconds;
        }
        NetworkAvailability.Configure(_config.NetworkPollIntervalSeconds, _config.NetworkMaxWaitMinutes);
        if (_config.ConnectionErrorDelaySeconds > 0) {
            ApiRetryPolicy.ConnectionErrorDelaySeconds = _config.ConnectionErrorDelaySeconds;
        }

        return true;
    }

    /// <summary>
    /// [AI Context] Entry point that validates the source/target directories and checks filename formats.
    /// [Human] Bereitet die Session vor: Prüft Ordner, warnt bei falschen Dateinamen (wichtig für die chronologische Sortierung) und lädt History/System-Prompt hoch.
    /// </summary>
    public async Task StartAsync() {
        if (!PrepareWorkspace()) {
            return;
        }

        Ui.Step($"Automatisierte Extraktion ({_backend.Name})");
        Ui.Detail($"Quelle (Source): {_config.SourceFolder}");
        Ui.Detail($"Ziel (Target):   {ExtractionHelpers.OutputRoot(_config)}");
        _backend.DescribeEndpoint();

        string[] videoFilesToProcess = ExtractionHelpers.DeduplicatePreferCompressed(Directory.GetFiles(_config.SourceFolder, "*.mp4")).ToArray();
        foreach (var videoFile in videoFilesToProcess) {
            var dateInfo = VideoDateParser.Parse(videoFile);
            if (!dateInfo.IsValid) {
                Ui.Warn($"Video entspricht nicht dem Datums-/Wochen-Namensschema: {Path.GetFileName(videoFile)}", "AutoExtraction");
                Ui.Detail("Erwartetes Format z.B.: 02-16-2026-monday-week1-Analysis_II.mp4 oder week1-02-16-2026-montag.mp4");
            }
        }

        // The mode menu loops: every branch below can be backed out of, and landing here again is
        // what "back" from the branch means. Only "Zurück" leaves the session.
        while (true) {
            var choice = Ui.Select("Modus auswählen:", [
                ("1) 🎬 Einzelnes Video auswählen und konvertieren", ExtractionMode.SingleVideo),
                ("2) 🚀 Alle Videos im Quellordner konvertieren (Standard)", ExtractionMode.AllVideos),
                ("3) 📺 YouTube-Video transkribieren", ExtractionMode.YouTube)
            ], backLabel: "4) 🚪 Abbrechen / Zurück");

            if (!choice.IsValue) return;

            switch (choice.Value) {
                case ExtractionMode.SingleVideo: {
                    var files = FileSelectionPrompt.SelectSingleFile(_config.SourceFolder);
                    if (files.Length > 0) {
                        await RunAsync(files);
                        return;
                    }
                    break;
                }

                case ExtractionMode.YouTube:
                    await _backend.CleanUpRemoteFilesAsync();
                    try {
                        await new YouTubeTaskRunner(_config, this).RunAsync();
                    }
                    finally {
                        await _backend.CleanUpRemoteFilesAsync();
                    }
                    return;

                default: {
                    var files = VideoBatchSelector.SelectAndFilterVideosForBatch(_config.SourceFolder);
                    if (files.Length > 0) {
                        await RunAsync(files);
                        return;
                    }
                    break;
                }
            }
        }
    }

    private enum ExtractionMode { AllVideos, SingleVideo, YouTube }

    public async Task<bool> EnsureSessionSetupAsync() {
        // --- Phase 1: Load System Instruction text (and the history) from disk (if not already loaded) ---
        if (string.IsNullOrEmpty(_systemInstructionText)) {
            if (!await TryLoadSystemInstructionWithHistoryAsync()) return false;
        }

        // --- Phase 2: Finalize session setup (logging, debug roundtrip) ---
        InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;
        _sessionLogger.SetSessionMetadata(!string.IsNullOrEmpty(_systemInstructionText), _historyWasLoaded);
        _sessionLogger.InitializeSession();

        if (_config.CreateLogFiles) {
            string logDest = !string.IsNullOrWhiteSpace(_sessionLogger.CurrentSessionLogPath)
                ? _sessionLogger.CurrentSessionLogPath
                : _config.LogFolder;
            await ExtractionHelpers.LogSystemInstructionDumpAsync(logDest, _systemInstructionText, [.. _historyParts, .. _uploadedHistoryParts]);
        }

        await _sessionLogger.LogSessionSetupAsync();

        if (_config.DebugHelloRoundtrip) {
            var roundtrip = await DebugRoundtripRunner.RunAsync(_client, _config, GetValidSystemInstructionParts());
            if (!roundtrip.Succeeded) return false;

            // The runner reports usage rather than mutating these itself, so the session stays the
            // single writer of its own counters and preamble.
            _sessionUsage += roundtrip.Usage;
            _sessionMaxFreshTokens = Math.Max(_sessionMaxFreshTokens, roundtrip.Usage.Fresh);
            _sessionPreamble.Add(roundtrip.UserTurn!);
            _sessionPreamble.Add(roundtrip.ModelTurn!);

            int delay = _config.VideoPartDelaySeconds;
            if (delay > 0) {
                Ui.Detail($"Warte {delay}s (Token Refill) nach Debug 'Hello' Roundtrip...");
                await InteractiveDelay.SmartDelayAsync(delay, "Warte auf Token-Refill nach Debug Roundtrip...");
            }
            Ui.Success("'Hello' Roundtrip erfolgreich.");
        }

        // Vertex only: the explicit cache holds the finished system instruction and history, so it is
        // created once both are loaded (and deleted here if caching was switched off).
        if (_backend.ContextCache != null) {
            await _backend.ContextCache.InitializeAsync(_systemInstructionText, _historyParts);
        }

        return true;
    }

    /// <summary>
    /// [AI Context] Resolves system instruction files from disk, builds the instruction text,
    /// and optionally loads history files into the system instruction (with batched or bulk warm-up).
    /// Returns false only if the user declines to load, which is not an error.
    /// [Human] Lädt die System-Instruction-Dateien und (optional) History-Dateien ein.
    /// </summary>
    private async Task<bool> TryLoadSystemInstructionWithHistoryAsync() {
        if (_config.SystemInstructionPaths == null || _config.SystemInstructionPaths.Length == 0) return true;

        Ui.Info("Folgende System Instruction-Dateien sind konfiguriert:");
        var resolvedInstructionFiles = HistoryFileResolver.ResolveHistoryFiles(_config.SystemInstructionPaths, "SystemInstructionPath");

        if (resolvedInstructionFiles.Count == 0) {
            Ui.Warn("Keine System Instruction-Dateien gefunden oder konfiguriert.");
            return true;
        }

        FileTreeRenderer.PrintFileTree(resolvedInstructionFiles, _config.VerboseConsoleOutput);

        // Optionally resolve history files that will be merged into the system instruction
        List<string> historyFilesForSystemInstruction = [];
        bool shouldLoadHistory = !_historyWasLoaded;
        if (shouldLoadHistory) {
            historyFilesForSystemInstruction = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths, "HistoryPreloadPath");
            if (historyFilesForSystemInstruction.Count > 0) {
                Ui.Info("Folgende Dateien sind als History konfiguriert:");
                FileTreeRenderer.PrintFileTree(historyFilesForSystemInstruction, _config.VerboseConsoleOutput);
            }
        }

        // Ask user for confirmation. Only a person at the keyboard is asked: declining means paying
        // to transcribe without the instructions, so an unattended run has exactly one sensible
        // answer and loads them, rather than failing on the question or needing --yes (which would
        // accept every other default along with it).
        string confirmPrompt = shouldLoadHistory && historyFilesForSystemInstruction.Count > 0
            ? "System Instructions und History laden?"
            : "System Instructions laden?";
        if (Ui.PromptSource.IsInteractive && !Ui.Confirm(confirmPrompt, true)) {
            Ui.Warn("System Instructions wurden vom Benutzer nicht geladen.");
            return true;
        }

        // Determine common base for relative path display
        var allPathsForBaseResolution = new List<string>(resolvedInstructionFiles);
        if (shouldLoadHistory && historyFilesForSystemInstruction.Count > 0) {
            allPathsForBaseResolution.AddRange(historyFilesForSystemInstruction);
        }
        string? commonBase = FileTreeRenderer.FindCommonBaseDirectory(allPathsForBaseResolution);

        // Build the system instruction text from the instruction files
        string instructionText = await SystemInstructionTextBuilder.BuildAsync(resolvedInstructionFiles, historyFilesForSystemInstruction, commonBase, _config.VerboseConsoleOutput);
        _systemInstructionText = instructionText;

        // If there is history to load, process it via WarmUpWithBatchedHistoryAsync
        if (shouldLoadHistory && historyFilesForSystemInstruction.Count > 0) {
            if (_config.EnableImplicitPrefixCacheWarmup) {
                if (!await WarmUpWithBatchedHistoryAsync(historyFilesForSystemInstruction, commonBase)) return false;
            } else {
                // If warmup is disabled, load history files directly into session state without API handshakes
                Ui.Info("Lade History-Dateien direkt in den Session-Zustand (Warmup deaktiviert)...");
                var batchBuilder = new System.Text.StringBuilder();
                var loaded = await SystemInstructionTextBuilder.AppendHistoryFilesAsync(
                    historyFilesForSystemInstruction, batchBuilder, commonBase, _attachmentHandler, _config.VerboseConsoleOutput, uploadTextFiles: !_config.InlineHistory);
                AddLoadedHistory(batchBuilder.ToString(), loaded);
            }

            _historyWasLoaded = true;
        } else {
            if (_config.EnableImplicitPrefixCacheWarmup && !await PrimePrefixCacheAsync(includeDummyPart0: true)) return false;
        }

        Ui.Success("System Instructions und History erfolgreich geladen.");
        return true;
    }



    /// <summary>
    /// [AI Context] Puts one loaded history batch where LoadHistoryIntoSystemInstruction says: its inlined
    /// text and media into the system instruction or the user turn's history parts. Uploaded text files
    /// (InlineHistory=false) always go to the user turn, because the system instruction rejects file links
    /// (gemini-cache-probe e13). A batch whose text files were all uploaded leaves no empty text Part behind.
    /// [Human] Legt einen geladenen History-Batch in System Instruction oder User-Turn ab.
    /// </summary>
    private void AddLoadedHistory(string batchText, SystemInstructionTextBuilder.LoadedHistory loaded) {
        if (_config.LoadHistoryIntoSystemInstruction) {
            _systemInstructionText += batchText;
            _historyParts.AddRange(loaded.MediaParts);
        } else {
            if (_config.InlineHistory || batchText.Length > 0) {
                _historyParts.Add(new Part { Text = batchText });
            }
            _historyParts.AddRange(loaded.MediaParts);
        }
        _uploadedHistoryParts.AddRange(loaded.UploadedTextParts);
    }

    /// <summary>
    /// [AI Context] The history that opens every user turn, in the same order for the part requests and all
    /// warm-up handshakes so their prefixes agree: the uploaded history files (with their notice), then, if
    /// LoadHistoryIntoSystemInstruction is false, the inlined history.
    /// [Human] Die History am Anfang jedes User-Turns (Request und Warm-ups gleich).
    /// </summary>
    private List<Part> HistoryUserTurnParts() {
        var parts = new List<Part>();
        if (_uploadedHistoryParts.Count > 0) {
            parts.Add(new Part { Text = SystemInstructionTextBuilder.UploadedHistoryNotice });
            parts.AddRange(_uploadedHistoryParts);
        }
        if (!_config.LoadHistoryIntoSystemInstruction) {
            parts.AddRange(_historyParts);
        }
        return parts;
    }

    private List<Part> GetValidSystemInstructionParts() =>
        BuildSystemInstructionParts(_systemInstructionText, _historyParts, _config.LoadHistoryIntoSystemInstruction, _backend.SystemInstructionAcceptsFiles);

    /// <summary>
    /// [AI Context] The system instruction's parts: its text, then the history when it is loaded into
    /// the system instruction. A backend whose system instruction takes text only (Vertex) keeps the
    /// history's files out; they reach the model through its explicit context cache instead.
    /// [Human] Die Teile der System Instruction; bei Vertex ohne History-Dateien.
    /// </summary>
    public static List<Part> BuildSystemInstructionParts(string systemInstructionText, IReadOnlyList<Part> historyParts, bool loadHistoryIntoSystemInstruction, bool acceptsFiles) {
        var sysParts = new List<Part>();
        if (!string.IsNullOrWhiteSpace(systemInstructionText)) sysParts.Add(new() { Text = systemInstructionText });
        if (loadHistoryIntoSystemInstruction && historyParts.Count > 0) {
            sysParts.AddRange(acceptsFiles
                ? historyParts
                : historyParts.Where(p => p.FileData == null && p.InlineData == null && !string.IsNullOrEmpty(p.Text)));
        }
        return sysParts;
    }

    /// <summary>
    /// [AI Context] Returns the static, per-partNumber prefix of the user-turn prompt.
    /// This text is deterministic and placed BEFORE the video payload in every request, so it forms a
    /// stable, growing cache prefix that the warm-up can pre-activate in the same token order.
    /// partNumber == 1  → no segment_start parameter (matches the warm-up dummy turn exactly).
    /// partNumber  > 1  → adds the segment_start parameter for mid-lecture continuity.
    /// [Human] Gibt den immer gleichen statischen Anfang des Prompts zurück. Steht VOR dem Video, damit Google einen Cache-Hit erkennt.
    /// </summary>
    private static string GetStaticPromptBeginning(int partNumber) {
        string s = "Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n" +
                   "<context_and_parameters>\n" +
                   "IMPORTANT: The System Instructions (System Prompt) contain the absolute rules, syntax specifications, and constraints for the lecture transcription and MUST be followed strictly. The parameters below specify details for this video fragment:\n\n" +
                   "<parameter name=\"merging_and_scope\">Do NOT attempt to merge the current part with the previous parts (i.e. do not try to fix the cut). Focus solely on transcribing this fragment as it is. As specified in the System Instructions, keep mathematical derivations and explanations self-contained and grouped within 'content' environments to preserve logical flow.</parameter>\n";
        if (partNumber != 1) {
            s += "<parameter name=\"segment_start\">\n" +
                 "1. Start the transcription EXACTLY where the audio begins in this specific video segment, even if it is mid-sentence. Do not attempt to reconstruct the beginning of the sentence from the previous context, and do not perform any overlap correction.\n" +
                 "2. If the previous part ended in the middle of an environment (like a `proofWrapper`, `proof`, or `content`), you MUST logically continue that environment in this part (e.g., start with `\\begin{proofWrapper}` or `\\begin{content}` if the professor is still doing the proof/derivation). However, you must still transcribe the spoken words exactly from where this new video segment begins.\n" +
                 "</parameter>\n";
        }
        return s;
    }


    /// <summary>
    /// [AI Context] Executes the batch processing workflow.
    /// Uses System.Threading.Channels to run FFmpeg processing in the background (Producer) while Gemini processes chunks sequentially (Consumer), maximizing hardware and API throughput.
    /// [Human] Das asynchrone Fließband: FFmpeg bereitet Videos im Hintergrund vor, während Gemini sie der Reihe nach abarbeitet.
    /// </summary>
    /// <returns>
    /// false if any file failed. The interactive path only prints this, but an unattended caller
    /// needs it in the exit status - a batch that transcribed four videos and failed the fifth is
    /// neither success nor failure.
    /// </returns>
    private async Task<bool> ProcessFilesAsync(string[] videoFilesToProcess) {
        // Chronologisch aufsteigend sortieren anhand des Dateinamens und der Woche
        videoFilesToProcess = [.. videoFilesToProcess.OrderChronologically()];

        // [AI Context] We use a bounded channel (capacity 1) to synchronize the FFmpeg Producer task and the Gemini Consumer task.
        // This allows FFmpeg to prepare the *next* video while Gemini is waiting for the API to process the *current* video, maximizing throughput.
        // [Human] Wir nutzen einen 'Kanal' (Channel), um FFmpeg (Videobearbeitung) und Gemini (KI-Analyse) parallel laufen zu lassen.
        // Während die KI das erste Video analysiert, schneidet FFmpeg im Hintergrund schon das zweite. Das spart enorm Zeit!
        var preparedVideoQueue = Channel.CreateBounded<PreparedVideo>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

        // 1. PRODUCER: FFmpeg läuft unsichtbar in einem eigenen Hintergrund-Task
        using var producerCancellation = new CancellationTokenSource();
        var videoPreparationTask = Task.Run(() => VideoSegmentProducer.RunAsync(videoFilesToProcess, preparedVideoQueue.Writer, _config, producerCancellation.Token));

        // 2. CONSUMER: Unser Haupt-Thread schnappt sich die Videos vom Fließband, sobald sie da sind
        // [AI Context] Awaits tasks from the bounded channel. This guarantees Gemini processes chunks strictly sequentially while FFmpeg works ahead.
        bool anyVideoFailed = false;

        try {
            await foreach (var (file, fileSpecificOutputFolder, _, partsWithTimes, _, fullOriginalVideoDuration) in preparedVideoQueue.Reader.ReadAllAsync()) {
                bool success = await ProcessPreparedVideoAsync(file, fileSpecificOutputFolder, partsWithTimes, fullOriginalVideoDuration);
                if (!success) anyVideoFailed = true;
            }
        }
        finally {
            // [AI Context] A consumer that threw leaves the producer blocked on the full channel; cancelling
            // releases it. After a normal drain the producer has already finished and this is a no-op.
            producerCancellation.Cancel();
            try {
                // Warten, bis der Producer-Task sauber beendet wurde (fängt Fehler ab)
                await videoPreparationTask;
            }
            catch (OperationCanceledException ex) when (producerCancellation.IsCancellationRequested) {
                Ui.Detail($"FFmpeg-Producer nach Abbruch des Verbrauchers gestoppt: {ex.Describe()}", "FFmpeg Producer");
            }
            catch (Exception ex) {
                Ui.Error($"FFmpeg-Producer fehlgeschlagen; die restlichen Videos wurden nicht vorbereitet: {ex.Describe()}", "FFmpeg Producer");
                anyVideoFailed = true;
            }
        }

        if (anyVideoFailed) {
            Ui.Warn("Batch-Verarbeitung mit Fehlern abgeschlossen (einige Dateien wurden abgebrochen).", "AutoExtraction");
        }
        else {
            Ui.Success("Batch-Verarbeitung vollständig und fehlerfrei abgeschlossen!", "AutoExtraction");
        }

        return !anyVideoFailed;
    }

    /// <summary>
    /// [AI Context] Processes one already-split video end to end: sequentially extracts LaTeX from
    /// each part via the Gemini API (with resume-from-disk caching, parallel pre-uploads, and rate-limit
    /// pacing), writes the combined document, and launches LatexRefinementSession. Extracted from the
    /// former single ~300-line ProcessFilesAsync consumer-loop body — one call per video. Further split
    /// (Phase 4.5) into TranscribeSegmentsAsync (the per-part loop) and FinalizeVideoOutputAsync (combined
    /// document + refinement launch), sharing state via VideoProcessingState since the loop mutates several
    /// values across iterations (pending upload/rate-limit tasks, accumulated text and tokens).
    /// [Human] Verarbeitet ein bereits gesplittetes Video vollständig: extrahiert LaTeX Teil für Teil
    /// über die Gemini-API, schreibt das Gesamtdokument und startet das Refinement.
    /// </summary>
    /// <returns>false if any part failed and the file's processing was aborted (an error condition the caller reports as "hasErrors").</returns>
    private async Task<bool> ProcessPreparedVideoAsync(string file, string fileSpecificOutputFolder, IReadOnlyList<VideoSegment> partsWithTimes, double fullOriginalVideoDuration) {
        // Ensure the file-specific output folder exists before starting processing
        if (!Directory.Exists(fileSpecificOutputFolder)) {
            Directory.CreateDirectory(fileSpecificOutputFolder);
        }

        Ui.Step($"[Gemini Consumer] Starte API-Extraktion für {Path.GetFileName(file)}");

        // Handle Audio File Generation
        var (refinementClient, refinementConfig) = PrepareRefinementConfig();
        var state = new VideoProcessingState(ComputeBaseName(file), new AudioTrackExtractor(file, fileSpecificOutputFolder)) {
            RefinementClient = refinementClient,
            RefinementConfig = refinementConfig
        };

        await TranscribeSegmentsAsync(state, file, fileSpecificOutputFolder, partsWithTimes, fullOriginalVideoDuration);

        if (state.FileProcessingSuccess && !await FinalizeVideoOutputAsync(state, file, fileSpecificOutputFolder, partsWithTimes.Count)) {
            // The .tex parts are on disk and stay reusable, but the refinement that follows them
            // failed - the video is not done, and a caller reading the exit code must see that.
            return false;
        }

        return state.FileProcessingSuccess;
    }

    /// <summary>
    /// [AI Context] Strips the -speed-N-compressed / -compressed suffixes FFmpeg preprocessing adds and
    /// ensures the step1- prefix, giving the stable base name used for every part/combined output file.
    /// [Human] Baut den Basisnamen für die Ausgabedateien aus dem Videodateinamen.
    /// </summary>
    private static string ComputeBaseName(string file) => ExtractionHelpers.ComputeTexBaseName(file);

    /// <summary>
    /// [AI Context] Initializes refinementClient early because the parallel audio upload task
    /// (pendingAudioUploadTask) needs to upload the audio to the EXACT SAME Google Cloud Project / API Key
    /// that LatexRefinementSession will use. Otherwise, LatexRefinementSession gets a ClientError: "You do
    /// not have permission to access the File". Clones the refinement config so runtime overrides (disabling
    /// step 1 for single-part videos or applying extraction model parameters) never mutate the loaded configuration.
    /// [Human] Löst den Client für das Refinement auf und übernimmt bei Bedarf die gewählten Modell-Parameter auf einer Kopie der Konfiguration.
    /// </summary>
    private (Client? Client, LatexRefinementSessionConfig? Config) PrepareRefinementConfig() {
        if (!_config.GoIntoLatexRefinement) return (null, null);

        var copy = _latexRefinementConfig.Clone();
        bool useVertex = _backend.RefinementUsesVertex;
        copy.UseVertex = useVertex;
        if (!_config.NumberOfParts.IsAuto && (int)_config.NumberOfParts <= 1 && copy.Step1MergeAndTimestamp != null) {
            Ui.Info($"NumberOfParts = {_config.NumberOfParts} (<= 1). Deaktiviere Schritt 1 (Merger) für die LatexRefinementSession.", "AutoExtraction");
            copy.Step1MergeAndTimestamp.Enabled = false;
        }
        if (_config.UseChosenModelForRestOfPipeline) {
            Ui.Info($"UseChosenModelForRestOfPipeline = true. Übernehme Modell '{_config.CurrentModel}' und Parameter im Arbeitsspeicher für das Refinement...", "AutoExtraction");
            ApplyChosenModelToRefinement(copy, _config, useVertex);
        }
        try {
            return (_backend.CreateRefinementClient(copy), copy);
        }
        catch (InvalidOperationException ex) {
            Ui.Warn($"Refinement-Client konnte nicht erstellt werden: {ex.Describe()}. Das anschließende Refinement wird übersprungen.", "AutoExtraction");
            return (null, null);
        }
    }

    /// <summary>
    /// [AI Context] Copies the extraction's model and generation parameters onto every refinement
    /// step - onto the parameter set the refinement will read (Vertex or AI Studio), in memory only.
    /// [Human] Übernimmt Modell und Parameter der Extraktion für alle Refinement-Schritte.
    /// </summary>
    public static void ApplyChosenModelToRefinement(LatexRefinementSessionConfig refinement, IAutoExtractionConfig extraction, bool useVertex) {
        foreach (var step in new[] { refinement.Step1MergeAndTimestamp, refinement.Step2SpeechRefinement, refinement.Step3LastRefinement }) {
            var target = step == null ? null : useVertex ? step.Vertex : step.AiStudio;
            if (target == null) continue;
            target.CurrentModel = extraction.CurrentModel;
            target.Temperature = extraction.Temperature;
            target.TopP = extraction.TopP;
            target.TopK = extraction.TopK;
            target.MaxOutputTokens = extraction.MaxOutputTokens;
            target.ThinkingBudget = extraction.ThinkingBudget;
            target.ThinkingLevel = extraction.ThinkingLevel;
        }
    }

    /// <summary>
    /// [AI Context] Holds the mutable state that ProcessPreparedVideoAsync's former single loop body
    /// threads across iterations (pending pre-upload/rate-limit tasks, accumulated output text and token
    /// totals) plus the per-video values TranscribeSegmentsAsync and FinalizeVideoOutputAsync both need.
    /// Extracted (Phase 4.5) purely so the loop and the finalization step could become separate methods
    /// without a 6+ parameter/ref-parameter list.
    /// [Human] Der pro Video gemeinsam genutzte, veränderliche Zustand für Transkription und Abschluss.
    /// </summary>
    private sealed class VideoProcessingState(string baseName, AudioTrackExtractor audioTrackExtractor) {
        public readonly string BaseName = baseName;
        public readonly AudioTrackExtractor AudioTrackExtractor = audioTrackExtractor;
        public readonly List<string> GeneratedTexFiles = [];
        public readonly System.Text.StringBuilder FullOutputTextRaw = new(); // Stores text as is, no timestamp adjustment
        public readonly System.Text.StringBuilder FullOutputTextOffsetted = new(); // Stores text with timestamps adjusted by partStartTimeSeconds
        public TokenUsage FileTotalTokens;
        public readonly List<string> PartSummaries = []; // one line per part, for the combined header
        public bool FileProcessingSuccess = true;
        public bool AnyPartSentToApi; // false while every part so far came from disk (resume)
        public Task<SegmentUpload>? PendingVideoUploadTask;
        public Task<List<Part>>? PendingAudioUploadTask;
        public Client? RefinementClient;
        public LatexRefinementSessionConfig? RefinementConfig;
    }

    /// <summary>
    /// [AI Context] Sequentially extracts LaTeX for every part of one video (resume-from-disk caching,
    /// parallel pre-uploads, rate-limit pacing), mutating VideoProcessingState as it goes. Sets
    /// state.FileProcessingSuccess = false and returns early on the first unrecoverable part failure,
    /// mirroring the former inline loop's break semantics exactly.
    /// [Human] Extrahiert LaTeX Teil für Teil für ein Video.
    /// </summary>
    private async Task TranscribeSegmentsAsync(VideoProcessingState state, string file, string fileSpecificOutputFolder, IReadOnlyList<VideoSegment> partsWithTimes, double fullOriginalVideoDuration) {
        for (int i = 0; i < partsWithTimes.Count; i++) {
            string safePartPath = partsWithTimes[i].FilePath;
            double partStartTimeSeconds = partsWithTimes[i].StartTimeSeconds;
            string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-part{i + 1}.tex");

            Ui.Step($"Verarbeite Teil {i + 1}/{partsWithTimes.Count}: {Path.GetFileName(safePartPath)}");
            string? reusablePartPath = PartReuse.FindReusablePart(fileSpecificOutputFolder, state.BaseName, i + 1, ResumeWindow);
            if (reusablePartPath != null) {
                Ui.Info($"Vorhandene LaTeX-Datei gefunden: {Path.GetFileName(reusablePartPath)}. Überspringe API-Extraktion für diesen Teil.", "Resume");
                string existingTex = await System.IO.File.ReadAllTextAsync(reusablePartPath);
                state.GeneratedTexFiles.Add(reusablePartPath);

                // The part keeps the header it was written with - its own model, date and tokens - so
                // a reused part is described as what it is, not as part of this run.
                var (cachedHeader, cachedBody) = TexDocumentWriter.SplitHeaderLayers(existingTex);
                string cachedSummary = TexDocumentWriter.SummarizePartHeader(cachedHeader);
                state.PartSummaries.Add(cachedSummary.Length > 0 ? $"{cachedSummary} | aus Cache" : "aus Cache");
                string cachedSeparator = TexDocumentWriter.BuildPartSeparator(i + 1, cachedHeader, fromCache: true);
                state.FullOutputTextRaw.Append(cachedSeparator).Append(cachedBody); // For raw output
                if (_config.GenerateOffsetFiles) {
                    state.FullOutputTextOffsetted.Append(cachedSeparator).Append(LatexTimestampAdjuster.AdjustTimestamps(cachedBody, partStartTimeSeconds)); // For offsetted output
                }
                state.AudioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);
                continue;
            }

            SegmentTranscript segmentTranscript;

            bool uploadSuccess;
            string? parsedPrompt;
            List<Part> attachmentParts;

            Task<SegmentUpload> uploadTask;

            if (_config.EnableParallelFileUploads && state.PendingVideoUploadTask != null) {
                Ui.Info($"Nutze im Hintergrund bereits hochgeladenes Video für Teil {i + 1}...", "Pre-Upload");
                uploadTask = state.PendingVideoUploadTask;
                state.PendingVideoUploadTask = null;
            }
            else {
                uploadTask = UploadSegmentAndBuildPromptAsync(safePartPath, i + 1, partsWithTimes.Count, file, fullOriginalVideoDuration);
            }

            SegmentUpload upload = await uploadTask;
            (uploadSuccess, parsedPrompt, attachmentParts) = (upload.Succeeded, upload.Prompt, upload.Attachments);
            if (!uploadSuccess) {
                Ui.Error($"Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                state.FileProcessingSuccess = false;
                break;
            }

            state.AudioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);

            if (_backend.ContextCache != null) {
                await _backend.ContextCache.EnsureValidAsync(_systemInstructionText, _historyParts);
            }

            if (i > 0) {
                int rateLimitDelay = _config.VideoPartDelaySeconds; // 0 waits for nothing
                double secondsSinceLastGen = (DateTime.UtcNow - InteractiveDelay.LastGenerationCompletionTimeUtc).TotalSeconds;
                if (secondsSinceLastGen < rateLimitDelay && !InteractiveDelay.IsInSmartDelay) {
                    int waitRemaining = (int)Math.Ceiling(rateLimitDelay - secondsSinceLastGen);
                    if (waitRemaining > 0) {
                        Ui.Detail($"Warte verbleibende {waitRemaining} Sekunden vor dem nächsten Videoteil, um API-Limits zu schonen...", "Rate-Limit & Quota");
                        if (!await InteractiveDelay.SmartDelayAsync(waitRemaining, "Warte auf Rate-Limits (Token Refill)...")) {
                            state.FileProcessingSuccess = false;
                            break;
                        }
                    }
                }
            }

            // If EnableParallelFileUploads is enabled, start pre-uploading the next part (or the audio file if this is the last part) while Gemini processes the current part.
            if (_config.EnableParallelFileUploads) {
                if (i + 1 < partsWithTimes.Count) {
                    // The same rule the next iteration applies, so a part that will be reused is not
                    // uploaded and a stale one that will be transcribed again is.
                    if (PartReuse.FindReusablePart(fileSpecificOutputFolder, state.BaseName, i + 2, ResumeWindow) == null) {
                        Ui.Info($"Starte parallelen Video-Upload für nächsten Teil ({i + 2}/{partsWithTimes.Count}) im Hintergrund...", "Pre-Upload");
                        state.PendingVideoUploadTask = UploadSegmentAndBuildPromptAsync(partsWithTimes[i + 1].FilePath, i + 2, partsWithTimes.Count, file, fullOriginalVideoDuration);
                    }
                    else {
                        state.PendingVideoUploadTask = null;
                    }
                }
                else if (i == partsWithTimes.Count - 1 && _config.GenerateAudioFile && _config.GoIntoLatexRefinement) {
                    state.PendingAudioUploadTask = Task.Run(async () => {
                        if (state.AudioTrackExtractor.PendingTask != null) {
                            await state.AudioTrackExtractor.PendingTask;
                        }
                        string audioPath = FindAudioFile(fileSpecificOutputFolder, file);
                        if (System.IO.File.Exists(audioPath)) {
                            Ui.Info($"Starte parallelen Audio-Upload für LaTeX Refinement im Hintergrund ({Path.GetFileName(audioPath)})...", "Pre-Upload");
                            var handler = _backend.CreateAudioUploader(state.RefinementClient, fileSpecificOutputFolder);
                            var (audioUploadOk, attached) = await handler.UploadFilesAsync([audioPath]);
                            if (audioUploadOk) return attached;
                        }
                        return [];
                    });
                }
            }

            // Every part before this one was reused from disk: the run starts mid-video, so its inlined
            // predecessors have never been sent in this session and get one warm-up round of their own.
            if (!state.AnyPartSentToApi && ShouldWarmUpResumedPart(_config, i + 1, state.GeneratedTexFiles.Count)) {
                await PrimePrefixCacheForResumedPartAsync(i + 1, state.GeneratedTexFiles);
            }
            state.AnyPartSentToApi = true;

            segmentTranscript = await TranscribeSegmentToLatexAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, state.GeneratedTexFiles, targetPartPath);
            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

            state.FileTotalTokens += segmentTranscript.Usage;

            if (!string.IsNullOrWhiteSpace(segmentTranscript.LatexBody)) {
                string cleanTex = LatexResponseCleaner.CleanLatexResponse(segmentTranscript.LatexBody);

                // One header per part, written into the part file and repeated under the part's
                // separator in the combined document, so each part's settings travel with its text.
                string partHeader = TexDocumentWriter.BuildPartHeader(
                    partNumber: i + 1,
                    totalParts: partsWithTimes.Count,
                    sourcePartFileName: Path.GetFileName(safePartPath),
                    partStartTimeSeconds: partStartTimeSeconds,
                    usage: segmentTranscript.Usage,
                    model: _config.CurrentModel,
                    generation: _config.Generation,
                    thoughtsFileName: Path.GetFileName(segmentTranscript.ThoughtsFile));
                state.PartSummaries.Add(TexDocumentWriter.SummarizePartHeader(partHeader));

                string partSeparator = TexDocumentWriter.BuildPartSeparator(i + 1, partHeader, fromCache: false);
                state.FullOutputTextRaw.Append(partSeparator).Append(cleanTex);
                if (_config.GenerateOffsetFiles) {
                    state.FullOutputTextOffsetted.Append(partSeparator).Append(LatexTimestampAdjuster.AdjustTimestamps(cleanTex, partStartTimeSeconds));
                }

                string uniqueTargetPartPath = ExtractionHelpers.ResolveNonClashingTexPath(targetPartPath);
                await System.IO.File.WriteAllTextAsync(uniqueTargetPartPath, partHeader + "\n" + cleanTex);

                if (_config.GenerateOffsetFiles) {
                    string offsettedPartContent = LatexTimestampAdjuster.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                    string targetPartPathOffset = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-part{i + 1}-offset.tex");
                    string uniqueTargetPartPathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetPartPathOffset);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetPartPathOffset, partHeader + "\n" + offsettedPartContent);
                    Ui.Success($"Offset-korrigierter Teil gespeichert unter: {Path.GetFileName(uniqueTargetPartPathOffset)}");
                }
                state.GeneratedTexFiles.Add(uniqueTargetPartPath);
            }
            else {
                Ui.Error($"Die Verarbeitung von Teil {i + 1} für '{Path.GetFileName(file)}' ist fehlgeschlagen. Breche die Verarbeitung für diese Datei ab.");
                state.FileProcessingSuccess = false;
                // [AI Context] Retain successfully generated parts so subsequent runs can resume without wasting tokens or re-computing.
                break;
            }
        }

        await ObserveAbandonedPreUploadAsync(state);
    }

    /// <summary>
    /// [AI Context] A part that failed ends the loop while the next part's pre-upload may still run.
    /// Awaiting it surfaces its failure (it would otherwise be an unobserved task) and lets it finish
    /// before the session's remote cleanup, so the file it uploads is deleted with the rest.
    /// [Human] Wartet auf einen nicht mehr benötigten Vorab-Upload, damit Fehler sichtbar werden.
    /// </summary>
    private static async Task ObserveAbandonedPreUploadAsync(VideoProcessingState state) {
        if (state.PendingVideoUploadTask is not { } abandoned) return;
        state.PendingVideoUploadTask = null;
        try {
            await abandoned;
        }
        catch (Exception ex) {
            Ui.Warn($"Nicht mehr benötigter Vorab-Upload fehlgeschlagen: {ex.Describe()}", "Pre-Upload");
        }
    }

    /// <returns>The refinement's result: false if one of its steps failed. true when it succeeded or
    /// was not meant to run (GoIntoLatexRefinement off).</returns>
    private async Task<bool> FinalizeVideoOutputAsync(VideoProcessingState state, string file, string fileSpecificOutputFolder, int totalParts) {
        string targetFilePath = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-all.tex");
        string targetFilePathOffset = Path.Combine(fileSpecificOutputFolder, $"{state.BaseName}-all-offset.tex");

        string Header(string outputPath) => TexDocumentWriter.BuildCombinedHeader(
            outputFileName: Path.GetFileName(outputPath),
            sourceFileName: Path.GetFileName(file),
            totalParts: totalParts,
            runUsage: state.FileTotalTokens,
            model: _config.CurrentModel,
            generation: _config.Generation,
            partSummaries: state.PartSummaries);

        string uniqueTargetFilePath = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePath);
        await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, Header(uniqueTargetFilePath) + state.FullOutputTextRaw.ToString());
        Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {uniqueTargetFilePath}", "AutoExtraction");

        string refinementTargetFile = uniqueTargetFilePath;

        if (_config.GenerateOffsetFiles) {
            string uniqueTargetFilePathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePathOffset);
            await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, Header(uniqueTargetFilePathOffset) + state.FullOutputTextOffsetted.ToString());
            Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das offset-korrigierte Dokument liegt hier: {uniqueTargetFilePathOffset}", "AutoExtraction");
            refinementTargetFile = uniqueTargetFilePathOffset;
        }

        if (state.AudioTrackExtractor.PendingTask != null) {
            Ui.Info($"Warte auf Abschluss der parallelen Audio-Extraktion für {Path.GetFileName(file)}, da das Refinement diese benötigt...", "AutoExtraction");
            await state.AudioTrackExtractor.PendingTask;
        }

        List<Part>? preUploadedAudioParts = null;
        if (_config.EnableParallelFileUploads && state.PendingAudioUploadTask != null) {
            Ui.Info("Warte auf Abschluss des parallelen Audio-Uploads...", "AutoExtraction");
            preUploadedAudioParts = await state.PendingAudioUploadTask;
        }

        string audioFilePath = FindAudioFile(fileSpecificOutputFolder, file);

        Ui.Step($"Starte automatischen Refinement-Prozess für die {(_config.GenerateOffsetFiles ? "offset-korrigierte " : "")}Datei...");
        var refinementSession = new LatexRefinementSession(
            state.RefinementClient ?? _client,
            RefinementOptions.ForFile(state.RefinementConfig ?? _latexRefinementConfig, refinementTargetFile, _config, audioFilePath, preUploadedAudioParts));

        AttachmentUploader.HasJustUploaded = false;
        return await refinementSession.StartAsync();
    }

    /// <summary>
    /// [AI Context] The audio track the refinement reads: the newest .aac in the video's output folder,
    /// else the name AudioTrackExtractor would give it (which may not exist yet).
    /// [Human] Die Audiodatei für das Refinement: die neueste .aac im Ausgabeordner.
    /// </summary>
    private static string FindAudioFile(string fileSpecificOutputFolder, string videoFile) =>
        Directory.GetFiles(fileSpecificOutputFolder, "*.aac").OrderByDescending(aacFile => System.IO.File.GetLastWriteTime(aacFile)).FirstOrDefault()
        ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(videoFile) + "_audio.aac");

    public async Task<bool> RefineYouTubeOutputAsync(string combinedTexPath, int totalParts) {
        if (!_config.GoIntoLatexRefinement) return true;

        var (refinementClient, refinementConfig) = PrepareRefinementConfig();
        var targetConfig = refinementConfig ?? _latexRefinementConfig;
        if (totalParts <= 1 && targetConfig.Step1MergeAndTimestamp != null) {
            Ui.Info($"YouTube-Video hat {totalParts} Teil (<= 1). Überspringe Schritt 1 (Merge) und führe Speech Refinement + Last Refinement aus.", "YouTube Mode");
            targetConfig.Step1MergeAndTimestamp.Enabled = false;
        }

        Ui.Step("Starte automatischen Refinement-Prozess für die YouTube-Transkription...", "YouTube Mode");
        var refinementSession = new LatexRefinementSession(
            refinementClient ?? _client,
            RefinementOptions.ForFile(targetConfig, combinedTexPath, _config));

        AttachmentUploader.HasJustUploaded = false;
        return await refinementSession.StartAsync();
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SegmentCompleteRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VideoCompleteRegex();
}