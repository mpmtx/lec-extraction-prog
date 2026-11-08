using System;
using System.Text.Json.Serialization;
using LectureExtraction.Extraction;

namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Configuration for the enterprise Vertex AI tier.
/// Composes shared building blocks (VertexEndpoint, WorkspacePaths, GenerationParameters, ModelSelection, ContextCacheSettings).
/// The prompt files are not configured here: Stage1PromptFiles lists them.
/// [Human] Konfiguration für den professionellen Google Cloud Modus. Erfordert ein eingerichtetes Rechnungskonto und Cloud Storage.
/// </summary>
public class VertexAutoExtractionConfig : IAutoExtractionConfig {
    public VertexEndpoint Endpoint { get; set; } = new();
    public WorkspacePaths Paths { get; set; } = new();
    public GenerationParameters Generation { get; set; } = new() {
        Temperature = 0.35f,
        TopP = 0.95f,
        TopK = 40,
        MaxOutputTokens = 65535
    };
    public ModelSelection ModelSelection { get; set; } = new();
    public ContextCacheSettings ContextCaching { get; set; } = new() {
        Enabled = true,
        Minutes = 45,
        IncrementMinutes = 45,
        MinimumRemainingMinutes = 15
    };

    // Delegating properties for backward compatibility
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string ProjectId { get => Endpoint.ProjectId; set => Endpoint.ProjectId = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string Location { get => Endpoint.Location; set => Endpoint.Location = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public string GcsBucketName { get => Endpoint.GcsBucketName; set => Endpoint.GcsBucketName = value; }
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

    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public bool UseContextCaching { get => ContextCaching.Enabled; set => ContextCaching.Enabled = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingMinutes { get => ContextCaching.Minutes; set => ContextCaching.Minutes = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingIncrementMinutes { get => ContextCaching.IncrementMinutes; set => ContextCaching.IncrementMinutes = value; }
    [JsonIgnore] [Newtonsoft.Json.JsonIgnore] public int ContextCachingMinimumRemainingMinutes { get => ContextCaching.MinimumRemainingMinutes; set => ContextCaching.MinimumRemainingMinutes = value; }

    public double SpeedMultiplier { get; set; } = 1.2;
    public bool GenerateOffsetFiles { get; set; } = true;
    public bool LoadHistoryIntoSystemInstruction { get; set; } = false;
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
    public int RateLimitDelaySeconds { get; set; } = 130;
    public bool EnableImplicitPrefixCacheWarmup { get; set; } = false;

    /// <summary>
    /// [AI Context] The shared session's wait between parts, between continues and after a warm-up
    /// handshake. Vertex keeps its own key for it, RateLimitDelaySeconds, so existing JSON stays valid.
    /// [Human] Wartezeit zwischen Teilen und Continue-Anfragen; in Vertex heisst sie RateLimitDelaySeconds.
    /// </summary>
    int IAutoExtractionConfig.VideoPartDelaySeconds => RateLimitDelaySeconds;

    /// <summary>
    /// [AI Context] Runs the pre-merge VertexAutoExtractionSession instead of AutoExtractionSession with
    /// the Vertex backend. The old class is kept unchanged as the reference implementation: neither
    /// Vertex path has had a real run since the 2026-10-15 merge, so the first test can compare them.
    /// [Human] Nutzt die alte Vertex-Session statt der zusammengeführten (zum Vergleichen beim ersten Test).
    /// </summary>
    public bool UseLegacyVertexSession { get; set; } = false;

    // The settings below came with the merge into AutoExtractionSession (2026-10-15). They mean the
    // same as on AiStudioAutoExtractionConfig and start from its defaults.
    public bool StripCommentsFromReferenceContext { get; set; } = true;
    public bool SlimOlderReferenceParts { get; set; } = false;
    public ReferenceSlimmingConfig ReferenceSlimming { get; set; } = new();
    public bool DebugHelloRoundtrip { get; set; } = false;
    public int HistoryBatchCount { get; set; } = 0;
    public int SystemInstructionDelaySeconds { get; set; } = 65;
    public int HistoryRateLimitDelaySeconds { get; set; } = 65;
    public bool MergeSystemInstructionAndFirstHistoryBatch { get; set; } = false;
    public bool MergeAllConsecutiveHistoryBatches { get; set; } = false;
    public bool SendDummyFileWithEachWarmUpRound { get; set; } = false;

    /// <summary>
    /// [AI Context] Vertex counterpart: delay in seconds when the model returns a "high demand" error.
    /// Defaults to 180 seconds (3 minutes) if unspecified, or can be configured to e.g. 64 seconds.
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
    /// [AI Context] Vertex counterpart: skips the informational CountTokensAsync call before each
    /// cache-warming handshake. See AI Studio config for full rationale.
    /// [Human] Überspringt den CountTokens-Aufruf vor jedem Warmup-Handshake.
    /// </summary>
    public bool SkipTokenCountingDuringWarmUp { get; set; } = false;

    /// <summary>
    /// [AI Context] Vertex counterpart: initial backoff in seconds for CountTokensAsync 429 retries.
    /// See AI Studio config for full rationale.
    /// [Human] Wartezeit in Sekunden beim ersten 429-Retry des CountTokens-Aufrufs.
    /// </summary>
    public int CountTokensRetryBackoffSeconds { get; set; } = 20;

    /// <summary>
    /// [AI Context] Vertex counterpart of the AI Studio flag: sends <c>ThinkingBudget = 0</c> on the
    /// cache-warming handshake, which has one fixed sentence to echo and nothing to reason about.
    /// Opt-in for the same reason - it is a live paid request and some models reject the field.
    /// [Human] Schaltet das "Nachdenken" beim Cache-Warming-Handshake ab (standardmässig aus).
    /// </summary>
    public bool DisableThinkingDuringWarmUp { get; set; } = false;

    /// <summary>
    /// [AI Context] Vertex counterpart of the AI Studio flag: stops session after history loading,
    /// system instruction setup, and prefix cache warming without extracting video files.
    /// [Human] Wenn true, führt die Session nur das Laden der System Instructions & den Cache-Warmup durch und beendet sich dann ohne Videoextraktion (für Debugzwecke).
    /// </summary>
    public bool OnlyDoWarmUp { get; set; } = false;
    public YouTubeTranscriptionTask[] YouTubeTasks { get; set; } = [];

    /// <summary>
    /// [AI Context] Vertex counterpart of the AI Studio setting: thought summaries for Stage 1, one file
    /// per part. The warm-up handshake sends the same IncludeThoughts value so its request config stays
    /// identical to Part 1's, but drops its thoughts; the history acknowledgement never requests them.
    /// [Human] Gedanken-Zusammenfassungen für die Transkription (eine Datei pro Teil).
    /// </summary>
    public ThoughtSummaryConfig ThoughtSummaries { get; set; } = new();
}