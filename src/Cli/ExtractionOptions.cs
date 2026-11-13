using System.CommandLine;
using LectureExtraction.Configuration;

namespace LectureExtraction.Cli;

/// <summary>
/// The knobs several commands share, declared once so <c>media segment</c>, <c>plan</c> and
/// <c>run</c> cannot drift into different spellings or different defaults for the same setting.
/// A null option means "leave the configured value alone" - the CLI overrides configuration, it
/// does not replace it.
/// </summary>
public static class ExtractionOptions {
    public static readonly Option<string?> Out = new("--out") {
        Description = "Target folder for the produced files. Defaults to the configured target folder."
    };

    public static readonly Option<string?> Parts = new("--parts") {
        Description = "Number of overlapping segments to split into (positive integer or 'auto')."
    };

    public static readonly Option<int?> Overlap = new("--overlap") {
        Description = "Overlap between segments, in seconds."
    };

    public static readonly Option<double?> Speed = new("--speed") {
        Description = "Playback speed multiplier applied during preprocessing."
    };

    public static readonly Option<string?> Preset = new("--preset") {
        Description = "FFmpeg encoding preset (e.g. fast, medium, slow)."
    };

    public static readonly Option<string?> Model = new("--model") {
        Description = "Model id to use for this run."
    };

    public static readonly Option<string?> Profile = new("--profile") {
        Description = "API-key profile index (0-4) or name (e.g. latex-refinement, automated-content-extraction) to use for this run."
    };

    public static readonly Option<double?> VideoFps = new("--video-fps", "--google-video-fps", "--fps") {
        Description = "Video sampling frame rate for Google AI Studio (e.g. 0.25, 0.3333333, 1)."
    };

    public static readonly Option<bool?> SkipTokenCounting = new("--skip-token-counting") {
        Description = "Skip pre-flight CountTokensAsync calls to preserve Free Tier token quota budget."
    };

    public static readonly Option<int?> FileActivationDelay = new("--file-activation-delay") {
        Description = "Seconds to wait after file activation in AI Studio (default 0 for maximum prefix cache freshness)."
    };

    public static readonly Option<int?> MaxOutputTokens = new("--max-output-tokens") {
        Description = "Maximum output token budget per generation (e.g. 65536, 200000, 300000)."
    };

    public static readonly Option<string?> ThinkingLevel = new("--thinking-level") {
        Description = "Thinking level for reasoning models (e.g. MINIMAL, LOW, MEDIUM, HIGH, XHIGH)."
    };

    /// <summary>
    /// Applies whichever overrides were supplied. Config writeback is off by default in CLI mode,
    /// so these change the in-memory config for this run only unless --save-config was passed.
    /// </summary>
    public static void Apply(AiStudioAutoExtractionConfig config, ParseResult parseResult) {
        string? target = parseResult.GetValue(Out);
        if (!string.IsNullOrWhiteSpace(target)) {
            config.TargetFolder = System.IO.Path.GetFullPath(target);
        }

        if (parseResult.GetValue(Parts) is string partsRaw && !string.IsNullOrWhiteSpace(partsRaw)) {
            if (NumberOfParts.TryParse(partsRaw, out var parts)) {
                config.NumberOfParts = parts;
            }
        }

        if (parseResult.GetValue(Overlap) is int overlap) {
            config.OverlapSeconds = overlap;
        }

        if (parseResult.GetValue(Speed) is double speed) {
            config.SpeedMultiplier = speed;
        }

        string? preset = parseResult.GetValue(Preset);
        if (!string.IsNullOrWhiteSpace(preset)) {
            config.FfmpegPreset = preset;
        }

        string? model = parseResult.GetValue(Model);
        if (!string.IsNullOrWhiteSpace(model)) {
            config.CurrentModel = model;
        }

        if (parseResult.GetValue(Profile) is string profileRaw && !string.IsNullOrWhiteSpace(profileRaw)) {
            config.ActiveApiProfile = GoogleAi.ApiKeyProfileResolver.ResolveIndex(profileRaw, config.AiStudioApiKeyEnvNames, config.ActiveApiProfile);
        }

        if (parseResult.GetValue(VideoFps) is double videoFps) {
            config.GoogleVideoFps = videoFps;
        }

        if (parseResult.GetValue(SkipTokenCounting) is bool skipTokens) {
            config.SkipTokenCountingDuringWarmUp = skipTokens;
        }

        if (parseResult.GetValue(FileActivationDelay) is int activationDelay) {
            config.FileActivationDelaySeconds = activationDelay;
        }

        if (parseResult.GetValue(MaxOutputTokens) is int maxOutputTokens && maxOutputTokens > 0) {
            config.MaxOutputTokens = maxOutputTokens;
        }

        if (parseResult.GetValue(ThinkingLevel) is string thinkingLevel && !string.IsNullOrWhiteSpace(thinkingLevel)) {
            config.ThinkingLevel = thinkingLevel.Trim().ToUpperInvariant();
        }

        ThoughtOptions.Apply(config, parseResult);
    }
}
