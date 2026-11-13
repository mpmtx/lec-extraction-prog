using System;
using System.CommandLine;
using System.IO;
using System.Text.RegularExpressions;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// Menu entry 3 (YouTube) without the menu. The model reads the video straight from its URL, one
/// request per timestamp fragment; there is no download, no FFmpeg and no upload, so there is also no
/// resume: a rerun requests every fragment again. The fragments are the menu's own
/// (<see cref="YouTubeTaskPrompt.BuildFragments"/>), built from <c>--duration</c> because nothing
/// here can ask YouTube how long the video is.
/// </summary>
public static partial class YouTubeCommand {
    public static readonly Option<string> Url = new("--url") {
        Description = "YouTube link (watch?v=, youtu.be/ or shorts/). Playlist parameters are dropped.",
        Required = true
    };

    public static readonly Option<string> Duration = new("--duration") {
        Description = "Length of the video: minutes (90) or HH:MM:SS / MM:SS.",
        Required = true
    };

    public static readonly Option<string?> Name = new("--name") {
        Description = "Output name; the folder becomes <out>/step1-<name>/. Default youtube-<video id>."
    };

    public static readonly Option<int?> Fragments = new("--parts") {
        Description = "Number of fragments. Default: one per started 45 minutes."
    };

    public static Command Build() {
        var command = new Command("youtube", "Transcribe a YouTube video from its URL: fragments -> LaTeX -> refinement -> PDF.") {
            Url, Duration, Name, Fragments,
            ExtractionOptions.Out, ExtractionOptions.Overlap,
            ExtractionOptions.Model, ExtractionOptions.Profile,
            ExtractionOptions.MaxOutputTokens, ExtractionOptions.ThinkingLevel,
            RunCommand.StopAfter, ThoughtOptions.Thoughts
        };

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);

            string rawUrl = parseResult.GetValue(Url) ?? "";
            string? videoId = ExtractVideoId(rawUrl);
            if (videoId == null) {
                Ui.Error($"Keine YouTube-Video-ID in '{rawUrl}' gefunden.", "youtube");
                return ExitCodes.Usage;
            }

            double totalSeconds = YouTubeTaskPrompt.ParseDuration(parseResult.GetValue(Duration) ?? "");
            if (totalSeconds <= 0) {
                Ui.Error("Ungültige --duration (Minuten, MM:SS oder HH:MM:SS).", "youtube");
                return ExitCodes.Usage;
            }

            int overlap = config.OverlapSeconds > 0 ? config.OverlapSeconds : 180;
            int parts = parseResult.GetValue(Fragments) ?? YouTubeTaskPrompt.DefaultPartCount(totalSeconds);
            if (parts < 1) {
                Ui.Error("--parts muss mindestens 1 sein.", "youtube");
                return ExitCodes.Usage;
            }

            string name = parseResult.GetValue(Name) is string n && !string.IsNullOrWhiteSpace(n) ? n.Trim() : $"youtube-{videoId}";
            var task = new YouTubeTranscriptionTask {
                VideoUrl = $"https://www.youtube.com/watch?v={videoId}",
                OutputName = name,
                Fragments = YouTubeTaskPrompt.BuildFragments(totalSeconds, parts, overlap)
            };

            bool stopAfterExtract = string.Equals(parseResult.GetValue(RunCommand.StopAfter), "extract", StringComparison.OrdinalIgnoreCase);
            config.GoIntoLatexRefinement = !stopAfterExtract;

            string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);
            bool keyResolves = GoogleAiClientBuilder.ResolveApiKeyByName(envName) != null;
            string folderName = name.StartsWith("step1-", StringComparison.OrdinalIgnoreCase) ? name : "step1-" + name;
            string outputFolder = Path.Combine(ExtractionHelpers.OutputRoot(config), folderName);

            var plan = new {
                url = task.VideoUrl,
                outputFolder,
                model = config.CurrentModel,
                apiKeyProfile = config.ActiveApiProfile,
                apiKeyEnvName = envName,
                apiKeyResolves = keyResolves,
                refinement = !stopAfterExtract,
                fragments = task.Fragments
            };

            if (context.DryRun) {
                CliOutput.Payload(context, new { dryRun = true, plan }, () => {
                    Ui.Info($"{task.VideoUrl} -> {outputFolder} ({config.CurrentModel}, Profil {config.ActiveApiProfile})");
                    YouTubeTaskPrompt.ShowFragments(task.Fragments);
                    Ui.Info($"{task.Fragments.Count} Fragment-Anfrage(n){(stopAfterExtract ? "" : " + Refinement")}. Nichts ausgeführt (--dry-run).");
                });
                return ExitCodes.Success;
            }

            if (!keyResolves) {
                Ui.Error($"Der API-Key '{envName}' (Profil {config.ActiveApiProfile}) ist nicht gesetzt.", "youtube");
                return ExitCodes.Configuration;
            }

            YouTubeTaskPrompt.ShowFragments(task.Fragments);
            SessionCostLedger.Reset();
            var session = RunCommand.BuildSession(config, 0, parseResult);
            bool allSucceeded = await session.RunYouTubeAsync([task]);

            var summary = new {
                plan.url,
                outputFolder,
                fragments = task.Fragments.Count,
                generationRequests = SessionCostLedger.GenerationRequests,
                supportRequests = SessionCostLedger.SupportRequests,
                retriedAttempts = SessionCostLedger.RetriedAttempts,
                refinementRan = !stopAfterExtract,
                allSucceeded
            };
            CliOutput.Payload(context, summary, () => Ui.Table("Aufwand dieser Sitzung", SessionCostLedger.Summary()));

            return allSucceeded ? ExitCodes.Success : ExitCodes.Partial;
        });

        return command;
    }

    /// <summary>The 11-character video id of a watch?v=, youtu.be/, shorts/, embed/ or live/ link, or a bare id.</summary>
    public static string? ExtractVideoId(string url) {
        var match = VideoIdRegex().Match(url.Trim());
        return match.Success ? match.Groups["id"].Value : null;
    }

    [GeneratedRegex(@"(?:[?&]v=|youtu\.be/|/shorts/|/embed/|/live/|^)(?<id>[A-Za-z0-9_-]{11})(?![A-Za-z0-9_-])")]
    private static partial Regex VideoIdRegex();
}
