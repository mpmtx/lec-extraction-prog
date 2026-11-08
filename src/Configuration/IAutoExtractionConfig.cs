namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] Everything AutoExtractionSession, VideoSegmentProducer, YouTubeTaskRunner and the
/// refinement hand-off read from an extraction config, whichever backend it belongs to. What only
/// one backend has (AI Studio's API-key profiles and upload timeouts, Vertex's endpoint, GCS bucket
/// and explicit context cache) stays on the concrete class and is read by that backend.
/// [Human] Alles, was die Extraktions-Session aus einer Konfiguration liest - unabhängig vom Backend.
/// </summary>
public interface IAutoExtractionConfig {
    double SpeedMultiplier { get; }
    bool GoIntoLatexRefinement { get; }
    bool UseChosenModelForRestOfPipeline { get; }
    bool GenerateOffsetFiles { get; }
    bool GenerateAudioFile { get; }
    NumberOfParts NumberOfParts { get; }
    int OverlapSeconds { get; }
    string SourceFolder { get; set; }
    string[] PredefinedSourceFolders { get; }
    string TargetFolder { get; set; }
    string LogFolder { get; }
    bool CreateLogFiles { get; }
    double? GoogleVideoFps { get; }
    bool UseGoogleSearch { get; }
    YouTubeTranscriptionTask[] YouTubeTasks { get; }
    string FfmpegPreset { get; }
    bool DebugSendReferenceFile { get; }
    bool SendDummyFileDuringTranscription { get; }
    bool InlinePrecedingLecTexParts { get; }
    bool StripCommentsFromReferenceContext { get; }
    bool SlimOlderReferenceParts { get; }
    ReferenceSlimmingConfig ReferenceSlimming { get; }
    bool VerboseConsoleOutput { get; }
    bool OnlyDoWarmUp { get; }
    bool DebugHelloRoundtrip { get; }
    int HighDemandDelaySeconds { get; }
    int NetworkPollIntervalSeconds { get; }
    int NetworkMaxWaitMinutes { get; }
    int ConnectionErrorDelaySeconds { get; }
    int MaxContinueRequests { get; }
    bool EnableParallelFileUploads { get; }
    ThoughtSummaryConfig ThoughtSummaries { get; }

    // Model and generation parameters
    string[] Model { get; }
    int CurrentModelIndex { get; set; }
    string CurrentModel { get; set; }
    GenerationParameters Generation { get; }
    float Temperature { get; }
    float TopP { get; }
    int TopK { get; }
    int MaxOutputTokens { get; }
    int? ThinkingBudget { get; }
    string? ThinkingLevel { get; }

    // System instruction and history: Stage 1's compiled-in file lists (Stage1PromptFiles), never a folder
    string[] SystemInstructionPaths { get; }
    string[] HistoryPreloadPaths { get; }
    bool LoadHistoryIntoSystemInstruction { get; }
    bool InlineHistory { get; }
    int HistoryBatchCount { get; }

    // Pacing: the wait between parts and continues, and the warm-up delays. 0 means no wait.
    int VideoPartDelaySeconds { get; }
    int SystemInstructionDelaySeconds { get; }
    int HistoryRateLimitDelaySeconds { get; }

    // Implicit prefix-cache warm-up
    bool EnableImplicitPrefixCacheWarmup { get; }
    bool MergeSystemInstructionAndFirstHistoryBatch { get; }
    bool MergeAllConsecutiveHistoryBatches { get; }
    bool SendDummyFileWithEachWarmUpRound { get; }
    bool SkipTokenCountingDuringWarmUp { get; }
    int CountTokensRetryBackoffSeconds { get; }
    bool DisableThinkingDuringWarmUp { get; }
}
