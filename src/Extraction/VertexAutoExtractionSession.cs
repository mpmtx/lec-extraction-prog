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
/// [AI Context] Orchestrates the fully automated transcription pipeline for Vertex AI.
/// Combines local FFmpeg preprocessing (producer) with Gemini API sequential extraction (consumer).
/// Since 2026-10-15 the default Vertex path is AutoExtractionSession with VertexExtractionBackend; this
/// pre-merge class is kept unchanged as the reference and runs when UseLegacyVertexSession is true,
/// until a real Vertex run shows which of the two to keep.
/// Split into partial classes:
/// - VertexAutoExtractionSession.cs (core pipeline, file batching, YouTube transcription)
/// - VertexAutoExtractionSession.Generation.cs (per-segment upload, request building, streaming)
/// - VertexAutoExtractionSession.PrefixCache.cs (implicit prefix cache warming &amp; history loading)
/// - VertexAutoExtractionSession.ContextCache.cs (Vertex-only explicit CachedContent API)
/// Member Index:
/// - StartAsync: Validates folders, prompts mode selection (batch, single, youtube), and begins execution.
/// - SetupContextAndProcessAsync: Ensures system instructions/preamble are loaded then processes files.
/// - ProcessPreparedVideoAsync: Producer/consumer loop for MP4 video segment extraction.
/// - ProcessYouTubeTasksAsync: YouTube video download and transcription pipeline.
/// [Human] Die Hauptklasse für die automatisierte Verarbeitung eines ganzen Ordners voller Vorlesungsvideos.
/// </summary>
public partial class VertexAutoExtractionSession(Client client, VertexAutoExtractionConfig config, AttachmentUploader attachmentHandler, SessionLogger sessionLogger, LatexRefinementSessionConfig latexRefinementConfig) {
    public static readonly string[] AvailableModels = [
        "gemini-3.8-flash",
        "gemini-3.7-flash",
        "gemini-3.6-flash",
        "gemini-3.5-flash",
        "gemini-3-flash-preview"
    ];

    private readonly Client _client = client;
    private readonly VertexAutoExtractionConfig _config = config;
    private readonly AttachmentUploader _attachmentHandler = attachmentHandler;
    private readonly SessionLogger _sessionLogger = sessionLogger;
    private readonly LatexRefinementSessionConfig _latexRefinementConfig = latexRefinementConfig;
    private string _systemInstructionText = "";
    // [AI Context] Cached payloads to avoid redundant uploads and API calls across multiple video chunks.
    private readonly List<Part> _historyParts = [];
    // [AI Context] Stores the acknowledged history prompt and the model's confirmation, statically prepended to all subsequent API calls.
    private readonly List<Content> _sessionPreamble = [];
    private bool _historyWasLoaded = false;
    private int _sessionTotalInputTokens = 0;
    private int _sessionTotalOutputTokens = 0;
    private int _sessionTotalCachedTokens = 0;
    private int _lastWarmupInputTokens = 0;
    // [AI Context] Active Google Cloud Context Cache resource name if caching is active.
    private string? _cachedContentName = null;

    /// <summary>
    /// [AI Context] Entry point that validates the source/target directories and checks filename formats.
    /// [Human] Bereitet die Session vor: Prüft Ordner, warnt bei falschen Dateinamen (wichtig für die chronologische Sortierung) und lädt History/System-Prompt hoch.
    /// </summary>
    public async Task StartAsync() {
        if (_config.HighDemandDelaySeconds > 0) {
            ApiRetryPolicy.DefaultHighDemandDelaySeconds = _config.HighDemandDelaySeconds;
        }
        NetworkAvailability.Configure(_config.NetworkPollIntervalSeconds, _config.NetworkMaxWaitMinutes);
        if (_config.ConnectionErrorDelaySeconds > 0) {
            ApiRetryPolicy.ConnectionErrorDelaySeconds = _config.ConnectionErrorDelaySeconds;
        }

        if (!Directory.Exists(_config.SourceFolder)) {
            Ui.Error($"Quellordner nicht gefunden: {_config.SourceFolder}");
            return;
        }

        // If no specific target folder is provided in config, create one inside the source folder.
        if (string.IsNullOrWhiteSpace(_config.TargetFolder)) {
            _config.TargetFolder = Path.Combine(_config.SourceFolder, "extracted_output");
        }

        if (!Directory.Exists(_config.TargetFolder)) {
            Directory.CreateDirectory(_config.TargetFolder);
        }

        Ui.Step("Automatisierte Extraktion (Vertex AI)");
        Ui.Detail($"Quelle (Source): {_config.SourceFolder}");
        Ui.Detail($"Ziel (Target):   {_config.TargetFolder}");
        if (!string.IsNullOrWhiteSpace(_config.ProjectId)) {
            Ui.Detail($"API-Projekt:     {_config.ProjectId} ({_config.Location})");
        }

        string[] videoFilesToProcess = ExtractionHelpers.DeduplicatePreferCompressed(Directory.GetFiles(_config.SourceFolder, "*.mp4")).ToArray();
        foreach (var videoFile in videoFilesToProcess) {
            var dateInfo = VideoDateParser.Parse(videoFile);
            if (!dateInfo.IsValid) {
                Ui.Warn($"Video entspricht nicht dem Datums-/Wochen-Namensschema: {Path.GetFileName(videoFile)}", "AutoExtraction");
                Ui.Detail("Erwartetes Format z.B.: 02-16-2026-monday-week1-Analysis_II.mp4 oder week1-02-16-2026-montag.mp4");
            }
        }

        // Same loop as the AI Studio twin: backing out of a branch returns to this menu, and only
        // "Zurück" leaves the session.
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
                        await SetupContextAndProcessAsync(files);
                        return;
                    }
                    break;
                }

                case ExtractionMode.YouTube:
                    await ProcessYouTubeTasksAsync();
                    return;

                default: {
                    var files = VideoBatchSelector.SelectAndFilterVideosForBatch(_config.SourceFolder);
                    if (files.Length > 0) {
                        await SetupContextAndProcessAsync(files);
                        return;
                    }
                    break;
                }
            }
        }
    }

    private enum ExtractionMode { AllVideos, SingleVideo, YouTube }

    /// <summary>
    /// [AI Context] Core initialization routine before batch processing. Loads system instructions and pre-warms the model context with attachments.
    /// [Human] Lädt die System-Instruktionen und die Historie hoch, bevor die eigentliche Video-Verarbeitung startet.
    /// </summary>
    private async Task SetupContextAndProcessAsync(string[] files) {
        if (files == null || files.Length == 0) {
            Ui.Warn("Keine Dateien ausgewählt.");
            return;
        }

        // [AI Context] Clean up the bucket at the very beginning to remove any leftovers from previous crashes.
        await CleanupBucketAsync();

        try {
            if (!await EnsureSessionSetupAsync()) return;
            if (_config.OnlyDoWarmUp) {
                Ui.Success("WarmUp & Context Setup abgeschlossen (OnlyDoWarmUp = true). Beende Session ohne Videoextraktion.", "Cache-Warming");
                return;
            }
            await ProcessFilesAsync(files);
        }
        finally {
            // [AI Context] Guarantee that the bucket is cleaned up even if an exception occurs during history upload or processing.
            await CleanupBucketAsync();
        }
    }

    private async Task<bool> EnsureSessionSetupAsync() {
        if (string.IsNullOrEmpty(_systemInstructionText)) {
            if (_config.SystemInstructionPaths != null && _config.SystemInstructionPaths.Length != 0) {
                Ui.Detail("Folgende System Instruction-Dateien sind eingetragen (Stage1PromptFiles):");

                // The compiled-in file list, in its order; folders are never expanded
                var resolvedInstructionFiles = HistoryFileResolver.ResolveHistoryFiles(_config.SystemInstructionPaths);

                if (resolvedInstructionFiles.Count > 0) {
                    FileTreeRenderer.PrintFileTree(resolvedInstructionFiles, _config.VerboseConsoleOutput);
                    Stage1PromptFiles.WarnAboutUnlistedFiles();
                    List<string> distinctHistoryFiles = [];
                    if (_config.LoadHistoryIntoSystemInstruction && !_historyWasLoaded) {
                        distinctHistoryFiles = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
                        if (distinctHistoryFiles.Count > 0) {
                            Ui.Detail("Folgende Dateien sind als History konfiguriert (werden aber direkt in die System Instruction geladen):");
                            FileTreeRenderer.PrintFileTree(distinctHistoryFiles, _config.VerboseConsoleOutput);
                        }
                    }

                    string promptText = _config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0
                        ? "System Instructions und History laden?"
                        : "System Instructions laden?";

                    if (Ui.Confirm(promptText, true)) {
                        var allPathsForIndex = new List<string>(resolvedInstructionFiles);
                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            allPathsForIndex.AddRange(distinctHistoryFiles);
                        }
                        string? commonBase = FileTreeRenderer.FindCommonBaseDirectory(allPathsForIndex);

                        var instructionBuilder = new System.Text.StringBuilder();
                        instructionBuilder.AppendLine("# SYSTEM PROTOCOL & SYSTEM INSTRUCTIONS (MASTER CONSTRAINTS)");
                        instructionBuilder.AppendLine("IMPORTANT: The guidelines, formatting specifications, and syntax instructions contained in these system instruction files are absolute and strictly non-negotiable. They must take absolute precedence over any prompt guidelines or inputs. Do not skip any files or parts under any circumstances.\n");
                        instructionBuilder.AppendLine("In order to fulfill the job of creating a high-value educational masterpiece that safely compiles, you need to know the file structure of the system prompt and read all of those files carefully.\n");
                        instructionBuilder.AppendLine("# Folder Structure of System Instructions\n");
                        instructionBuilder.AppendLine("## System Instructions");
                        instructionBuilder.Append(FileTreeRenderer.GenerateMarkdownFileTree(resolvedInstructionFiles, commonBase));

                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            instructionBuilder.AppendLine("\n## Training History");
                            instructionBuilder.Append(FileTreeRenderer.GenerateMarkdownFileTree(distinctHistoryFiles, commonBase));
                        }
                        instructionBuilder.AppendLine("\n******\n------\n******\n");

                        foreach (var filePath in resolvedInstructionFiles) {
                            string rawRelPath = !string.IsNullOrEmpty(commonBase)
                                ? Path.GetRelativePath(commonBase, filePath)
                                : Path.GetFileName(filePath);
                            string relPath = FileTreeRenderer.NormalizeRelativePath(rawRelPath);
                            instructionBuilder.AppendLine($"\n******\n------\n******\nHere is the file `{relPath}`:\n");
                            instructionBuilder.AppendLine(await System.IO.File.ReadAllTextAsync(filePath));
                            Ui.Info($"System Instruction geladen: {relPath}");
                        }
                        _systemInstructionText = instructionBuilder.ToString();

                        if (_config.LoadHistoryIntoSystemInstruction && distinctHistoryFiles.Count > 0) {
                            Ui.Info("Lade History-Dateien für System Instruction ein...");
                            string fileList = string.Join(", ", distinctHistoryFiles.Select(p => $"\"{p}\""));
                            var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}", true, commonBase);
                            if (success && attachmentParts.Count > 0) {
                                _historyParts.AddRange(attachmentParts);
                                _historyWasLoaded = true;
                                Ui.Info("Dateien erfolgreich eingelesen und in die System Instruction eingebunden.");
                            }
                            else {
                                Ui.Error("Einige oder alle History-Dateien konnten nicht eingelesen werden.");
                            }
                        }
                    }
                }
                else {
                    Ui.Warn("Keine System Instruction-Dateien gefunden oder konfiguriert.");
                }
            }
        }

        if (!_historyWasLoaded) {
            var distinctFiles = HistoryFileResolver.ResolveHistoryFiles(_config.HistoryPreloadPaths);
            if (distinctFiles.Count > 0) {
                Ui.Detail("Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:");
                FileTreeRenderer.PrintFileTree(distinctFiles, _config.VerboseConsoleOutput);
                string promptText = _config.LoadHistoryIntoSystemInstruction
                    ? "Sollen diese Dateien als System Instructions hochgeladen werden? (LoadHistoryIntoSystemInstruction = true)"
                    : "Sollen diese Dateien als History geladen und für die Session hochgeladen werden?";

                if (Ui.Confirm(promptText, true)) {
                    if (_config.LoadHistoryIntoSystemInstruction) {
                        Ui.Info("Lade Dateien als System Instructions hoch (dies kann einen Moment dauern)...");
                    }
                    else {
                        Ui.Info("Lade History-Dateien für die Session hoch (dies kann einen Moment dauern)...");
                    }
                    string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
                    var (success, _, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync($"attach {fileList}", _config.LoadHistoryIntoSystemInstruction);
                    if (success && attachmentParts.Count > 0) {
                        _historyParts.AddRange(attachmentParts);
                        _historyWasLoaded = true;
                        if (_config.LoadHistoryIntoSystemInstruction) {
                            Ui.Info("Dateien erfolgreich hochgeladen und werden in die System Instruction eingebunden (Acknowledge wird übersprungen).");
                        }
                        else {
                            Ui.Info("History-Dateien erfolgreich hochgeladen und für die Session zwischengespeichert.");
                            if (!await SendHistoryHandshakeAsync(fileList)) return false;
                        }
                    }
                    else {
                        Ui.Error("Einige oder alle History-Dateien konnten nicht hochgeladen werden.");
                    }
                }
            }
        }

        // [AI Context] Reset the rate-limit timer to now: session setup (loading system instructions and history)
        // can take significant time; the 150s guard will count from here and enforce a proper gap before the first API call.
        InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

        _sessionLogger.SetSessionMetadata(!string.IsNullOrEmpty(_systemInstructionText), _historyWasLoaded);
        _sessionLogger.InitializeSession();

        if (_config.CreateLogFiles) {
            string logDest = !string.IsNullOrWhiteSpace(_sessionLogger.CurrentSessionLogPath)
                ? _sessionLogger.CurrentSessionLogPath
                : _config.LogFolder;
            await ExtractionHelpers.LogSystemInstructionDumpAsync(logDest, _systemInstructionText, _historyParts);
        }

        await _sessionLogger.LogSessionSetupAsync();

        // [AI Context] Implicit prefix-cache warm-up, ported from AiStudioAutoExtractionSession. Runs once
        // here, before InitializeContextCachingAsync creates Vertex's explicit CachedContent -- the two
        // mechanisms are independent and can both be active.
        if (_config.EnableImplicitPrefixCacheWarmup) {
            if (!await PrimePrefixCacheAsync()) return false;
        }

        return true;
    }

    private async Task ProcessYouTubeTasksAsync() {
        List<YouTubeTranscriptionTask> tasksToProcess = [];

        if (_config.YouTubeTasks != null && _config.YouTubeTasks.Length > 0) {
            Ui.Info($"[YouTube Mode] Es wurden {_config.YouTubeTasks.Length} Aufgabe(n) in der Konfiguration gefunden.");
            if (!Ui.Confirm("Möchtest du diese Aufgaben ausführen?", true)) {
                var interactiveTask = YouTubeTaskPrompt.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
                if (interactiveTask != null) {
                    tasksToProcess.Add(interactiveTask);
                }
            }
            else {
                tasksToProcess.AddRange(_config.YouTubeTasks);
            }
        }
        else {
            Ui.Info("[YouTube Mode] Keine vorgegebenen YouTube-Aufgaben in der Konfiguration gefunden.");
            var interactiveTask = YouTubeTaskPrompt.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
            if (interactiveTask != null) {
                tasksToProcess.Add(interactiveTask);
            }
        }

        if (tasksToProcess.Count == 0) {
            Ui.Info("Keine YouTube-Aufgaben zum Verarbeiten.");
            return;
        }

        Ui.Step($"[YouTube Mode] Starte Transkription für {tasksToProcess.Count} YouTube-Video(s)...");

        await CleanupBucketAsync();
        try {
            if (!await EnsureSessionSetupAsync()) return;

            if (_config.UseContextCaching) {
                await InitializeContextCachingAsync();
            }

            foreach (var task in tasksToProcess) {
                if (string.IsNullOrWhiteSpace(task.VideoUrl)) continue;

                string baseName = string.IsNullOrWhiteSpace(task.OutputName) ? "youtube-lecture" : task.OutputName;
                if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
                    baseName = "step1-" + baseName;
                }

                string fileSpecificOutputFolder = Path.Combine(_config.TargetFolder, baseName);
                if (!Directory.Exists(fileSpecificOutputFolder)) {
                    Directory.CreateDirectory(fileSpecificOutputFolder);
                }

                Ui.Step($"[YouTube Consumer] Starte API-Extraktion für URL: {task.VideoUrl} ({baseName})");
                List<string> generatedTexFiles = [];
                string fullOutputTextRaw = "";

                for (int i = 0; i < task.Fragments.Count; i++) {
                    var frag = task.Fragments[i];
                    int partNum = i + 1;
                    Ui.Step($"Verarbeite Fragment {partNum}/{task.Fragments.Count}: {frag.StartTime} bis {frag.EndTime} ({frag.PartTitle})");

                    string parsedPrompt = YouTubeTaskRunner.BuildFragmentPrompt(frag, partNum, task.Fragments.Count);

                    var attachmentParts = new List<Part> {
                        Part.FromUri(task.VideoUrl, "video/mp4")
                    };

                    string texOutput = (await TranscribeSegmentToLatexAsync(
                        task.VideoUrl, partNum, baseName, parsedPrompt, attachmentParts, generatedTexFiles,
                        Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{partNum}.tex")
                    )).LatexBody;

                    if (!string.IsNullOrWhiteSpace(texOutput)) {
                        string cleanTex = LatexResponseCleaner.CleanLatexResponse(texOutput);
                        fullOutputTextRaw += $"\n\n% --- TEIL {partNum}: {frag.StartTime}-{frag.EndTime} ({frag.PartTitle}) ---\n" + cleanTex;

                        string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{partNum}.tex");
                        string partContent = cleanTex;
                        if (!partContent.StartsWith("% Startzeit:") && !partContent.StartsWith("% Zeitstempel:")) {
                            partContent = $"% Startzeit: {frag.StartTime} | Ende: {frag.EndTime}\n\n" + partContent;
                        }
                        await System.IO.File.WriteAllTextAsync(targetPartPath, partContent);
                        generatedTexFiles.Add(targetPartPath);
                        Ui.Success($"Teildatei gespeichert unter: {targetPartPath}");
                    }
                }

                if (!string.IsNullOrWhiteSpace(fullOutputTextRaw)) {
                    string combinedPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}.tex");
                    await System.IO.File.WriteAllTextAsync(combinedPath, fullOutputTextRaw.Trim());
                    Ui.Success($"Zusammengeführte YouTube-Transkription gespeichert unter: {combinedPath}");
                }
            }
        }
        finally {
            await CleanupBucketAsync();
        }
    }


    private async Task<bool> SendHistoryHandshakeAsync(string loadedFiles = "") {
        var historyPromptParts = new List<Part>(_historyParts) {
            new() { Text = $"Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {_config.CurrentModel}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards." }
        };
        var userContent = new Content { Role = "user", Parts = historyPromptParts };

        _sessionPreamble.Add(userContent);

        var requestConfig = new GenerateContentConfig {
            Temperature = _config.Temperature,
            TopP = _config.TopP,
            TopK = _config.TopK,
            MaxOutputTokens = _config.MaxOutputTokens
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
                if (nonText.Count > 0 && _sessionPreamble.Count == 0) {
                    _sessionPreamble.Add(new Content { Role = "user", Parts = nonText });
                }
            }
            if (sysParts.Count > 0) {
                requestConfig.SystemInstruction = new Content { Role = "system", Parts = sysParts };
            }
        }
        // Never with thought summaries: this answer becomes a model turn of the session preamble.
        requestConfig.ThinkingConfig = ThinkingConfigFactory.Build(_config.CurrentModel, _config.ThinkingBudget, _config.ThinkingLevel);

        Ui.Step($"Warte auf Bestätigung der History von {_config.CurrentModel}...");
        int initialBackoff = _config.RateLimitDelaySeconds > 0 ? _config.RateLimitDelaySeconds : 45;
        bool success = false;
        bool cancelled = false;
        string fullResponse = "";
        int finalInputTokens = 0;
        int finalOutputTokens = 0;
        int finalCachedTokens = 0;
        var usage = new UsageReport();

        using var cancelScope = new ConsoleCancelScope();
        try {
            // [AI Context] Same retry policy as every other generation call (bounded attempts, backoff,
            // network pause); this loop used to be a hand-written copy of it.
            success = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                streamFactory: () => _client.Models.GenerateContentStreamAsync(_config.CurrentModel, _sessionPreamble, requestConfig),
                onChunkReceived: chunk => {
                    string txt = ResponseParts.Split(chunk).Answer;
                    Ui.Raw(txt);
                    fullResponse += txt;
                    usage.Absorb(chunk.UsageMetadata);
                    if (chunk.UsageMetadata != null) {
                        if (chunk.UsageMetadata.PromptTokenCount.HasValue) finalInputTokens = chunk.UsageMetadata.PromptTokenCount.Value;
                        if (chunk.UsageMetadata.CandidatesTokenCount.HasValue) finalOutputTokens = chunk.UsageMetadata.CandidatesTokenCount.Value;
                        if (chunk.UsageMetadata.CachedContentTokenCount.HasValue) finalCachedTokens = chunk.UsageMetadata.CachedContentTokenCount.Value;
                    }
                    return Task.CompletedTask;
                },
                cancellationToken: cancelScope.Token,
                maxRetries: 10,
                initialBackoff: initialBackoff,
                retryContext: "History Bestätigung",
                onRetry: () => {
                    fullResponse = "";
                    finalInputTokens = 0;
                    finalOutputTokens = 0;
                    finalCachedTokens = 0;
                    usage = new UsageReport();
                },
                highDemandDelay: _config.HighDemandDelaySeconds > 0 ? _config.HighDemandDelaySeconds : null);
            cancelled = !success;
        }
        catch (Exception ex) {
            Ui.Error($"Der Fehler konnte nicht durch einen automatischen Retry behoben werden: {ex.Describe()}");
        }

        if (cancelled) {
            Ui.Info("Bestätigung abgebrochen.");
        }
        if (success) {
            _sessionTotalInputTokens += finalInputTokens;
            _sessionTotalOutputTokens += finalOutputTokens;
            _sessionTotalCachedTokens += finalCachedTokens;
            Ui.Detail(usage.Describe($"Total Prompt: {finalInputTokens:N0} | Gecacht: {finalCachedTokens:N0} | Frisch: {(Math.Max(0, finalInputTokens - finalCachedTokens)):N0} | Output: {finalOutputTokens:N0}", "[Request Tokens]      "));
            Ui.Detail($"[Session Total Tokens] Total Prompt: {_sessionTotalInputTokens:N0} | Gecacht: {_sessionTotalCachedTokens:N0} | Frisch: {(Math.Max(0, _sessionTotalInputTokens - _sessionTotalCachedTokens)):N0} | Output: {_sessionTotalOutputTokens:N0}");
        }

        if (success && !string.IsNullOrWhiteSpace(fullResponse)) {
            _sessionPreamble.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
            string logMsg = $"[History Acknowledgment] Angehängte Dateien: {loadedFiles}\n\nPrompt:\n{historyPromptParts.Last().Text}";
            await _sessionLogger.LogChatAsync(logMsg, logMsg, _config.CurrentModel, fullResponse, "AutoExtractionSetup", finalInputTokens, finalOutputTokens, finalCachedTokens);
            return true;
        }
        else {
            Ui.Error("Konnte Bestätigung für History nicht erhalten. Breche Extraktion ab.");
            _sessionPreamble.Clear();
            _historyWasLoaded = false;
            return false;
        }
    }

    /// <summary>
    /// [AI Context] Executes the batch processing workflow.
    /// Uses System.Threading.Channels to run FFmpeg processing in the background (Producer) while Gemini processes chunks sequentially (Consumer), maximizing hardware and API throughput.
    /// [Human] Das asynchrone Fließband: FFmpeg bereitet Videos im Hintergrund vor, während Gemini sie der Reihe nach abarbeitet.
    /// </summary>
    private async Task ProcessFilesAsync(string[] videoFilesToProcess) {
        // Chronologisch aufsteigend sortieren anhand des Dateinamens und der Woche
        videoFilesToProcess = [.. videoFilesToProcess.OrderBy(videoFile => VideoDateParser.Parse(videoFile).Date).ThenBy(videoFile => VideoDateParser.Parse(videoFile).WeekNumber ?? int.MaxValue).ThenBy(videoFile => videoFile)];

        // [AI Context] We use a bounded channel (capacity 1) to synchronize the FFmpeg Producer task and the Gemini Consumer task.
        // This allows FFmpeg to prepare the *next* video while Gemini is waiting for the API to process the *current* video, maximizing throughput.
        // [Human] Wir nutzen einen 'Kanal' (Channel), um FFmpeg (Videobearbeitung) und Gemini (KI-Analyse) parallel laufen zu lassen.
        // Während die KI das erste Video analysiert, schneidet FFmpeg im Hintergrund schon das zweite. Das spart enorm Zeit!
        var preparedVideoQueue = Channel.CreateBounded<PreparedVideo>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });

        // 1. PRODUCER: FFmpeg läuft unsichtbar in einem eigenen Hintergrund-Task
        var videoPreparationTask = Task.Run(() => VideoSegmentProducer.RunAsync(videoFilesToProcess, preparedVideoQueue.Writer, _config));

        // 2. CONSUMER: Unser Haupt-Thread schnappt sich die Videos vom Fließband, sobald sie da sind
        // [AI Context] Awaits tasks from the bounded channel. This guarantees Gemini processes chunks strictly sequentially while FFmpeg works ahead.
        bool anyVideoFailed = false;
        bool isFirstVideo = true;

        await foreach (var (file, fileSpecificOutputFolder, _, partsWithTimes, _, fullOriginalVideoDuration) in preparedVideoQueue.Reader.ReadAllAsync()) {
            if (isFirstVideo) {
                isFirstVideo = false;
                Ui.Info("Erstes Video wurde gesplittet. Erstelle jetzt Google Cloud Context Cache...", "Optimierung");
                await InitializeContextCachingAsync();
            }

            bool success = await ProcessPreparedVideoAsync(file, fileSpecificOutputFolder, partsWithTimes, fullOriginalVideoDuration);
            if (!success) anyVideoFailed = true;
        }

        await videoPreparationTask;

        if (anyVideoFailed) {
            Ui.Warn("Batch-Verarbeitung mit Fehlern abgeschlossen (einige Dateien wurden abgebrochen).", "AutoExtraction");
        }
        else {
            Ui.Success("Batch-Verarbeitung vollständig und fehlerfrei abgeschlossen!", "AutoExtraction");
        }
    }

    private async Task<bool> ProcessPreparedVideoAsync(string file, string fileSpecificOutputFolder, IReadOnlyList<VideoSegment> partsWithTimes, double fullOriginalVideoDuration) {
        if (!Directory.Exists(fileSpecificOutputFolder)) {
            Directory.CreateDirectory(fileSpecificOutputFolder);
        }

        Ui.Step($"[Gemini Consumer] Starte API-Extraktion für {Path.GetFileName(file)}");
        List<string> generatedTexFiles = [];
        string baseName = Path.GetFileNameWithoutExtension(file);
        baseName = SpeedCompressedSuffixRegex().Replace(baseName, "");
        baseName = CompressedSuffixRegex().Replace(baseName, "");
        if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
            baseName = "step1-" + baseName;
        }
        string fullOutputTextRaw = "";
        string fullOutputTextOffsetted = "";
        TokenUsage fileTotalTokens = default;
        List<string> partSummaries = []; // one line per part, for the combined header
        bool fileProcessingSuccess = true;
        TimeSpan cacheDuration = TimeSpan.FromHours(2);
        var audioTrackExtractor = new AudioTrackExtractor(file, fileSpecificOutputFolder);

        Task<SegmentUpload>? pendingVideoUploadTask = null;
        Task<List<Part>>? pendingAudioUploadTask = null;

        for (int i = 0; i < partsWithTimes.Count; i++) {
            string safePartPath = partsWithTimes[i].FilePath;
            double partStartTimeSeconds = partsWithTimes[i].StartTimeSeconds;
            string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}.tex");

            Ui.Step($"Verarbeite Teil {i + 1}/{partsWithTimes.Count}: {Path.GetFileName(safePartPath)}");
            if (System.IO.File.Exists(targetPartPath) && (DateTime.Now - System.IO.File.GetLastWriteTime(targetPartPath)) <= cacheDuration) {
                Ui.Info($"Vorhandene LaTeX-Datei gefunden: {Path.GetFileName(targetPartPath)}. Überspringe API-Extraktion für diesen Teil.", "Resume");
                string existingTex = await System.IO.File.ReadAllTextAsync(targetPartPath);
                generatedTexFiles.Add(targetPartPath);

                var (cachedHeader, cachedBody) = TexDocumentWriter.SplitHeaderLayers(existingTex);
                string cachedSummary = TexDocumentWriter.SummarizePartHeader(cachedHeader);
                partSummaries.Add(cachedSummary.Length > 0 ? $"{cachedSummary} | aus Cache" : "aus Cache");
                string cachedSeparator = TexDocumentWriter.BuildPartSeparator(i + 1, cachedHeader, fromCache: true);
                fullOutputTextRaw += cachedSeparator + cachedBody;
                if (_config.GenerateOffsetFiles) {
                    fullOutputTextOffsetted += cachedSeparator + LatexTimestampAdjuster.AdjustTimestamps(cachedBody, partStartTimeSeconds);
                }
                audioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);
                continue;
            }

            SegmentTranscript segmentTranscript;
            bool uploadSuccess;
            string? parsedPrompt;
            List<Part> attachmentParts;

            Task<SegmentUpload> uploadTask;

            if (_config.EnableParallelFileUploads && pendingVideoUploadTask != null) {
                Ui.Info($"Nutze im Hintergrund bereits hochgeladenes Video für Teil {i + 1}...", "Pre-Upload");
                uploadTask = pendingVideoUploadTask;
            }
            else {
                uploadTask = UploadSegmentAndBuildPromptAsync(safePartPath, i + 1, partsWithTimes.Count, file, fullOriginalVideoDuration);
            }

            SegmentUpload upload = await uploadTask;
            (uploadSuccess, parsedPrompt, attachmentParts) = (upload.Succeeded, upload.Prompt, upload.Attachments);
            if (!uploadSuccess) {
                Ui.Error($"Upload für Teil {i + 1} fehlgeschlagen. Breche Datei ab.");
                fileProcessingSuccess = false;
                break;
            }

            audioTrackExtractor.EnsureStarted(_config.GenerateAudioFile);

            if (_config.UseContextCaching) {
                var cacheState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
                double remainingMin = ContextCacheStateManager.GetRemainingMinutes(cacheState);
                bool cacheValid = false;

                if (!string.IsNullOrEmpty(_cachedContentName) && remainingMin > 0) {
                    if (remainingMin < _config.ContextCachingMinimumRemainingMinutes) {
                        Ui.Info($"Nur noch {remainingMin:F1} min verbleibend (Schwellenwert: {_config.ContextCachingMinimumRemainingMinutes} min). Verlängere automatisch um {_config.ContextCachingIncrementMinutes} min...", "Cache");
                        var updatedState = await ContextCacheStateManager.ExtendCacheAsync(_client, cacheState, _config.ContextCachingIncrementMinutes, ContextCacheStateManager.StateFileVertex);
                        if (updatedState != null) {
                            Ui.Info($"Cache verlängert. Gültig bis {updatedState.ExpireTimeUtc.ToLocalTime():t}.", "Cache");
                            cacheValid = true;
                        }
                    }
                    else {
                        cacheValid = await ContextCacheStateManager.IsValidRemoteAsync(_client, _cachedContentName);
                    }
                }

                if (!cacheValid) {
                    Ui.Info("Kein gültiger Cache aktiv oder Cache abgelaufen. Erstelle neuen Google Kontext-Cache für diesen Teil...", "Cache");
                    ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
                    _cachedContentName = null;
                    await InitializeContextCachingAsync();
                }
            }

            if (_config.EnableParallelFileUploads) {
                if (i + 1 < partsWithTimes.Count) {
                    string nextTexPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 2}.tex");
                    if (!System.IO.File.Exists(nextTexPath)) {
                        Ui.Info($"Starte parallelen Video-Upload für nächsten Teil ({i + 2}/{partsWithTimes.Count}) im Hintergrund...", "Pre-Upload");
                        pendingVideoUploadTask = UploadSegmentAndBuildPromptAsync(partsWithTimes[i + 1].FilePath, i + 2, partsWithTimes.Count, file, fullOriginalVideoDuration);
                    }
                    else {
                        pendingVideoUploadTask = null;
                    }
                }
                else if (i == partsWithTimes.Count - 1 && _config.GenerateAudioFile && _config.GoIntoLatexRefinement) {
                    pendingAudioUploadTask = Task.Run(async () => {
                        if (audioTrackExtractor.PendingTask != null) {
                            await audioTrackExtractor.PendingTask;
                        }
                        var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
                        string audioPath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
                                           ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");
                        if (System.IO.File.Exists(audioPath)) {
                            Ui.Info($"Starte parallelen Audio-Upload für LaTeX Refinement im Hintergrund ({Path.GetFileName(audioPath)})...", "Pre-Upload");
                            var handler = new AttachmentUploader(_client, fileSpecificOutputFolder, [fileSpecificOutputFolder], false, _config.GcsBucketName);
                            var (s, _, attached) = await handler.ProcessAttachmentsAsync($"attach \"{audioPath}\"");
                            if (s) return attached;
                        }
                        return [];
                    });
                }
            }

            segmentTranscript = await TranscribeSegmentToLatexAsync(safePartPath, i + 1, file, parsedPrompt, attachmentParts, generatedTexFiles, targetPartPath);

            fileTotalTokens += segmentTranscript.Usage;

            if (!string.IsNullOrWhiteSpace(segmentTranscript.LatexBody)) {
                string cleanTex = LatexResponseCleaner.CleanLatexResponse(segmentTranscript.LatexBody);

                string partHeader = TexDocumentWriter.BuildPartHeader(
                    partNumber: i + 1,
                    totalParts: partsWithTimes.Count,
                    sourcePartFileName: Path.GetFileName(safePartPath),
                    partStartTimeSeconds: partStartTimeSeconds,
                    usage: segmentTranscript.Usage,
                    model: _config.CurrentModel,
                    generation: _config.Generation,
                    thoughtsFileName: Path.GetFileName(segmentTranscript.ThoughtsFile));
                partSummaries.Add(TexDocumentWriter.SummarizePartHeader(partHeader));

                string partSeparator = TexDocumentWriter.BuildPartSeparator(i + 1, partHeader, fromCache: false);
                fullOutputTextRaw += partSeparator + cleanTex;
                if (_config.GenerateOffsetFiles) {
                    fullOutputTextOffsetted += partSeparator + LatexTimestampAdjuster.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                }

                string uniqueTargetPartPath = ExtractionHelpers.ResolveNonClashingTexPath(targetPartPath);
                await System.IO.File.WriteAllTextAsync(uniqueTargetPartPath, partHeader + "\n" + cleanTex);

                if (_config.GenerateOffsetFiles) {
                    string offsettedPartContent = LatexTimestampAdjuster.AdjustTimestamps(cleanTex, partStartTimeSeconds);
                    string targetPartPathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{i + 1}-offset.tex");
                    string uniqueTargetPartPathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetPartPathOffset);
                    await System.IO.File.WriteAllTextAsync(uniqueTargetPartPathOffset, partHeader + "\n" + offsettedPartContent);
                    Ui.Success($"Offset-korrigierter Teil gespeichert unter: {Path.GetFileName(uniqueTargetPartPathOffset)}");
                }
                generatedTexFiles.Add(uniqueTargetPartPath);
            }
            else {
                Ui.Error($"Die Verarbeitung von Teil {i + 1} für '{Path.GetFileName(file)}' ist fehlgeschlagen. Breche die Verarbeitung für diese Datei ab.");
                fileProcessingSuccess = false;
                // [AI Context] Retain successfully generated parts so subsequent runs can resume without wasting tokens or re-computing.
                break;
            }
        }

        if (fileProcessingSuccess) {
            string targetFilePath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-all.tex");
            string targetFilePathOffset = Path.Combine(fileSpecificOutputFolder, $"{baseName}-all-offset.tex");

            string Header(string outputPath) => TexDocumentWriter.BuildCombinedHeader(
                outputFileName: Path.GetFileName(outputPath),
                sourceFileName: Path.GetFileName(file),
                totalParts: partsWithTimes.Count,
                runUsage: fileTotalTokens,
                model: _config.CurrentModel,
                generation: _config.Generation,
                partSummaries: partSummaries);

            string uniqueTargetFilePath = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePath);
            await System.IO.File.WriteAllTextAsync(uniqueTargetFilePath, Header(uniqueTargetFilePath) + fullOutputTextRaw);
            Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das komplette Dokument liegt hier: {uniqueTargetFilePath}", "AutoExtraction");

            string refinementTargetFile = uniqueTargetFilePath;

            if (_config.GenerateOffsetFiles) {
                string uniqueTargetFilePathOffset = ExtractionHelpers.ResolveNonClashingTexPath(targetFilePathOffset);
                await System.IO.File.WriteAllTextAsync(uniqueTargetFilePathOffset, Header(uniqueTargetFilePathOffset) + fullOutputTextOffsetted);
                Ui.Success($"Fertig mit {Path.GetFileName(file)}. Das offset-korrigierte Dokument liegt hier: {uniqueTargetFilePathOffset}", "AutoExtraction");
                refinementTargetFile = uniqueTargetFilePathOffset;
            }

            if (audioTrackExtractor.PendingTask != null) {
                Ui.Info($"Warte auf Abschluss der parallelen Audio-Extraktion für {Path.GetFileName(file)}, da das Refinement diese benötigt...", "AutoExtraction");
                await audioTrackExtractor.PendingTask;
            }

            if (_latexRefinementConfig != null) {
                _latexRefinementConfig.UseVertex = AppConfig.IsVertexAiEnabled;
                if (!_config.NumberOfParts.IsAuto && (int)_config.NumberOfParts <= 1) {
                    Ui.Info($"NumberOfParts = {_config.NumberOfParts} (<= 1). Deaktiviere Schritt 1 (Merger) für die LatexRefinementSession.", "AutoExtraction");
                    _latexRefinementConfig.Step1MergeAndTimestamp.Enabled = false;
                }
            }
            Client refinementClient = GoogleAiClientBuilder.BuildVertexClient(_latexRefinementConfig?.VertexProjectId ?? "", _latexRefinementConfig?.VertexLocation ?? "");

            var aacFiles = Directory.GetFiles(fileSpecificOutputFolder, "*.aac");
            string audioFilePath = aacFiles.OrderByDescending(f => System.IO.File.GetLastWriteTime(f)).FirstOrDefault()
                                   ?? Path.Combine(fileSpecificOutputFolder, Path.GetFileNameWithoutExtension(file) + "_audio.aac");

            List<Part>? preUploadedAudioParts = null;
            if (_config.EnableParallelFileUploads && pendingAudioUploadTask != null) {
                Ui.Info("Warte auf Abschluss des parallelen Audio-Uploads...", "AutoExtraction");
                preUploadedAudioParts = await pendingAudioUploadTask;
            }

            Ui.Step($"Starte automatischen Refinement-Prozess für die {(_config.GenerateOffsetFiles ? "offset-korrigierte " : "")}Datei...");
            var refinementSession = new LatexRefinementSession(
                refinementClient,
                RefinementOptions.ForFile(_latexRefinementConfig!, refinementTargetFile, _config, audioFilePath, preUploadedAudioParts));

            await refinementSession.StartAsync();
        }

        return fileProcessingSuccess;
    }


    /// <summary>
    /// [AI Context] Financial Guardrail: Ensures the cloud storage bucket is purged immediately after processing to prevent accumulating storage costs for massive temporary video files.
    /// </summary>
    private Task CleanupBucketAsync() => GcsWorkspace.PurgeAsync(_config.GcsBucketName);

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Segment\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SegmentCompleteRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\[(?:SYSTEM|AI-MODEL)\][^\r\n]*Video\s*complete", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex VideoCompleteRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{2,4}-)?\d{2}-\d{2}-(monday|tuesday|wednesday|thursday|friday|saturday|sunday|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)(?:-speed-\d+(?:\.\d+)?-compressed|-compressed)?\.[a-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FilenamePatternRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-speed-[\d\.]+-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SpeedCompressedSuffixRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-compressed$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CompressedSuffixRegex();

}
