using System;
using System.Text.Json.Serialization;
using LectureExtraction.Extraction;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Configuration DTO for unattended batch processing using AI Studio endpoints.
/// Composes shared building blocks (ApiKeyProfile, WorkspacePaths, GenerationParameters, ModelSelection).
/// The prompt files are not configured here: Stage1PromptFiles lists them.
/// [Human] Konfiguration für den automatisierten Extraktions-Modus mit dem kostenlosen AI Studio.
/// </summary>
public class AiStudioAutoExtractionConfig : IAutoExtractionConfig {
    public ApiKeyProfile ApiKey { get; set; } = new();
    public WorkspacePaths Paths { get; set; } = new();
    public GenerationParameters Generation { get; set; } = new() {
        Temperature = 0.36f,
        TopP = 0.8f,
        TopK = 10,
        MaxOutputTokens = 65535
    };
    public ModelSelection ModelSelection { get; set; } = new();

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ActiveApiProfile { get => ApiKey.ActiveProfile; set => ApiKey.ActiveProfile = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] AiStudioApiKeyEnvNames { get => ApiKey.EnvNames; set => ApiKey.EnvNames = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string SourceFolder { get => Paths.SourceFolder; set => Paths.SourceFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] PredefinedSourceFolders { get => Paths.PredefinedSourceFolders; set => Paths.PredefinedSourceFolders = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string TargetFolder { get => Paths.TargetFolder; set => Paths.TargetFolder = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string LogFolder { get => Paths.LogFolder; set => Paths.LogFolder = value; }
    // Stage 1's prompt files are compiled in (Stage1PromptFiles), not read from the JSON. Get-only on purpose:
    // the config binder ignores [JsonIgnore] and would set (or empty) any property with a setter.
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] SystemInstructionPaths => Stage1PromptFiles.SystemInstructionPaths;
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] HistoryPreloadPaths => Stage1PromptFiles.HistoryPaths;

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float Temperature { get => Generation.Temperature; set => Generation.Temperature = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public float TopP { get => Generation.TopP; set => Generation.TopP = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int TopK { get => Generation.TopK; set => Generation.TopK = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int MaxOutputTokens { get => Generation.MaxOutputTokens; set => Generation.MaxOutputTokens = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int? ThinkingBudget { get => Generation.ThinkingBudget; set => Generation.ThinkingBudget = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string? ThinkingLevel { get => Generation.ThinkingLevel; set => Generation.ThinkingLevel = value; }

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string[] Model { get => ModelSelection.Available; set => ModelSelection.Available = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int CurrentModelIndex { get => ModelSelection.CurrentIndex; set => ModelSelection.CurrentIndex = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string CurrentModel { get => ModelSelection.Current; set => ModelSelection.Current = value; }

    public double SpeedMultiplier { get; set; } = 1.0;
    public bool GenerateOffsetFiles { get; set; } = true;
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;
    public bool InlineHistoryImages { get; set; } = true;
    public int HistoryBatchCount { get; set; } = 0;
    public bool DebugSendReferenceFile { get; set; } = true;
    public bool SendDummyFileDuringTranscription { get; set; } = true;
    public bool InlinePrecedingLecTexParts { get; set; } = true;

    /// <summary>
    /// [AI Context] true (default): the history's text files are inlined as text, in the system instruction or
    /// the user turn as LoadHistoryIntoSystemInstruction says. false: each is uploaded as a text/plain file and
    /// linked from the start of the user turn, because the system instruction rejects file links (gemini-cache-probe
    /// e13, 500 Internal error). Uploaded files do not count at the free tier's per-minute gate, inline text does.
    /// A file whose upload fails is inlined where it would have gone. Images are not affected.
    /// [Human] Lädt die History-Textdateien hoch, statt sie einzubetten (zählt nicht ans Minutenlimit).
    /// </summary>
    public bool InlineHistory { get; set; } = true;

    /// <summary>
    /// [AI Context] Strips LaTeX comments and `hidden` environments from the inlined reference context
    /// (preceding parts and the dummy anchor), and adds a rule to the reference-context preamble saying
    /// so - and that the model's own output must still contain them. Inline path only: uploaded .tex
    /// references are sent as they are.
    /// [Human] Entfernt Kommentare und hidden-Umgebungen aus den eingebetteten Kontext-Dateien.
    /// </summary>
    public bool StripCommentsFromReferenceContext { get; set; } = true;

    /// <summary>
    /// [AI Context] Slims every inlined preceding part except the most recent one, which stays complete,
    /// as ReferenceSlimming says (see ReferenceContextSlimmer). Inline path only. Off by default; an
    /// experiment from 2026-10-16 to bring late parts under the free tier's per-minute token limit.
    /// [Human] Kürzt ältere Vorgänger-Teile im Kontext, der letzte bleibt ganz.
    /// </summary>
    public bool SlimOlderReferenceParts { get; set; } = false;

    /// <summary>
    /// [AI Context] What SlimOlderReferenceParts removes: environments, emptied environments, commands.
    /// The class defaults reproduce the 2026-10-16 behaviour (speech bodies and TikZ only).
    /// [Human] Was beim Kürzen älterer Teile entfernt wird.
    /// </summary>
    public ReferenceSlimmingConfig ReferenceSlimming { get; set; } = new();
    public bool DebugHelloRoundtrip { get; set; } = false;
    public bool VerboseConsoleOutput { get; set; } = false;
    public bool GenerateAudioFile { get; set; } = true;
    public bool GoIntoLatexRefinement { get; set; } = true;
    public bool UseChosenModelForRestOfPipeline { get; set; } = true;
    public NumberOfParts NumberOfParts { get; set; } = NumberOfParts.Auto;
    public int OverlapSeconds { get; set; } = 180;
    public bool CreateLogFiles { get; set; } = true;
    public bool EnableParallelFileUploads { get; set; } = true;
    public double? GoogleVideoFps { get; set; }
    public bool UseGoogleSearch { get; set; } = false;
    public string FfmpegPreset { get; set; } = "fast";
    public int VideoPartDelaySeconds { get; set; } = 130;
    public int SystemInstructionDelaySeconds { get; set; } = 65;
    public int FileActivationDelaySeconds { get; set; } = 130;
    public int VideoUploadTimeoutSeconds { get; set; } = 240;
    public int VideoUploadMaxRetries { get; set; } = 10;
    public bool MergeSystemInstructionAndFirstHistoryBatch { get; set; } = false;
    public bool MergeAllConsecutiveHistoryBatches { get; set; } = false;
    public bool SendDummyFileWithEachWarmUpRound { get; set; } = false;
    public int HistoryRateLimitDelaySeconds { get; set; } = 65;
    public bool EnableImplicitPrefixCacheWarmup { get; set; } = true;

    /// <summary>
    /// [AI Context] Delay in seconds when the model returns a "high demand" error (HTTP 503 / RESOURCE_EXHAUSTED).
    /// Defaults to 180 seconds (3 minutes) if unspecified, or can be set to e.g. 64 seconds.
    /// [Human] Wartezeit in Sekunden, wenn das Modell überlastet ist ("high demand").
    /// </summary>
    public int HighDemandDelaySeconds { get; set; } = 180;

    /// <summary>
    /// [AI Context] While the internet is down (the Google host does not resolve or has no route), seconds between two
    /// probes (a bare TCP connect to port 443, no request, no quota). Defaults to 15.
    /// [Human] Abstand der Verbindungsproben bei Internetausfall, in Sekunden.
    /// </summary>
    public int NetworkPollIntervalSeconds { get; set; } = 15;

    /// <summary>
    /// [AI Context] How long to wait for the Google host to become reachable before the request fails like any exhausted retry.
    /// The failed attempts do not count against the retry limit. Defaults to 30.
    /// [Human] So lange wird auf die Internetverbindung gewartet, in Minuten.
    /// </summary>
    public int NetworkMaxWaitMinutes { get; set; } = 30;

    /// <summary>
    /// [AI Context] Seconds to wait before retrying after a broken connection that is not an outage (reset,
    /// "Error while copying content to a stream"). Unlike an outage this counts as an attempt. Defaults to 60.
    /// [Human] Wartezeit nach einem Verbindungsabbruch, in Sekunden.
    /// </summary>
    public int ConnectionErrorDelaySeconds { get; set; } = 60;

    /// <summary>
    /// [AI Context] Upper bound on requests per video part, the first one included. Each continue
    /// re-sends the video, so this caps the cost of a part whose model never says "Video complete".
    /// [Human] Maximale Anzahl Requests pro Videoteil (inkl. Continue-Anfragen).
    /// </summary>
    public int MaxContinueRequests { get; set; } = 15;

    /// <summary>
    /// [AI Context] When true, the informational CountTokensAsync call before each cache-warming
    /// handshake is skipped entirely. CountTokensAsync may consume input tokens against the same
    /// Free-Tier quota as GenerateContent; skipping it saves quota budget at the cost of losing the
    /// "Voraussichtlich NEU zu berechnen" preview line (the actual token counts still appear in the
    /// UsageMetadata of the GenerateContent response).
    /// [Human] Überspringt den CountTokens-Aufruf vor jedem Warmup-Handshake. Spart Quota-Budget,
    /// aber die Vorab-Token-Anzeige entfällt.
    /// </summary>
    public bool SkipTokenCountingDuringWarmUp { get; set; } = false;

    /// <summary>
    /// [AI Context] Initial backoff in seconds when a CountTokensAsync call hits a 429 quota error
    /// during cache warming. Lower values (e.g. 20) are fine when quota pressure is low; set to
    /// 60–65 defensively when running many batches on the Free Tier to let the per-minute window clear.
    /// Only relevant when SkipTokenCountingDuringWarmUp is false.
    /// [Human] Wartezeit in Sekunden beim ersten 429-Retry des CountTokens-Aufrufs.
    /// </summary>
    public int CountTokensRetryBackoffSeconds { get; set; } = 20;

    /// <summary>
    /// [AI Context] Sends <c>ThinkingBudget = 0</c> on the cache-warming handshake. The handshake asks
    /// the model to echo one fixed sentence, so it has nothing to reason about - but unlike the real
    /// generation path it sets no ThinkingConfig at all today, meaning it runs at the model's default
    /// thinking behaviour and can bill reasoning tokens for a request that needs none (finding F9).
    ///
    /// <para>Defaults to <c>false</c>: this is a live, paid request, and "Thinking level is not
    /// supported" is a real error mode for some models (ApiRetryPolicy filters for that message), so
    /// the change is opt-in until one run's reported Denk-Tokens show whether it is worth making.
    /// The flag is ignored for models that do not support thinking at all.</para>
    /// [Human] Schaltet das "Nachdenken" beim Cache-Warming-Handshake ab. Standardmässig aus, bis
    /// eine echte Messung zeigt, dass es sich lohnt.
    /// </summary>
    public bool DisableThinkingDuringWarmUp { get; set; } = false;

    /// <summary>
    /// [AI Context] If true, the session will perform history loading, system instruction setup,
    /// and prefix cache warming, then stop before video processing starts (used for debugging handshakes).
    /// [Human] Wenn true, führt die Session nur das Laden der System Instructions & den Cache-Warmup durch und beendet sich dann ohne Videoextraktion (für Debugzwecke).
    /// </summary>
    public bool OnlyDoWarmUp { get; set; } = false;
    public YouTubeTranscriptionTask[] YouTubeTasks { get; set; } = [];

    /// <summary>
    /// [AI Context] Thought summaries for Stage 1, one file per part. The cache-warming handshake sends
    /// the same IncludeThoughts value so its request config stays identical to Part 1's, but its
    /// thoughts are not logged: it only echoes a fixed sentence.
    /// [Human] Gedanken-Zusammenfassungen für die Transkription (eine Datei pro Teil).
    /// </summary>
    public ThoughtSummaryConfig ThoughtSummaries { get; set; } = new();
}