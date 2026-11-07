using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.Media;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The command to run first. It reports what a run would do - which videos, how many segments, how
/// many billable requests, under which model and key profile - and calls nothing. A wrong
/// <c>--folder</c>, an unset API key or a stale resume window is far cheaper to find here than
/// halfway through a paid batch.
/// </summary>
public static class PlanCommand {
    public static readonly Option<string?> Video = new("--video") {
        Description = "A single video file to work on."
    };

    public static readonly Option<string?> Folder = new("--folder") {
        Description = "Work on every .mp4 in this folder. Defaults to the configured source folder."
    };

    public static readonly Option<string?> From = new("--from") {
        Description = "Skip videos before this one (matched on the file name), keeping chronological order."
    };

    public static readonly Option<double?> ResumeWindow = new("--resume-window") {
        Description = "Hours a finished .tex part stays reusable. Default 2, matching the pipeline's own constant."
    };

    public static readonly Option<bool> Resume = new("--resume") {
        Description = "Reuse existing .tex parts regardless of age (equivalent to an unlimited --resume-window)."
    };

    public static readonly Option<int?> Limit = new("--limit") {
        Description = "Process at most this many videos in chronological order."
    };

    public static readonly Option<bool> Force = new("--force") {
        Description = "Ignore existing .tex parts and re-request every segment."
    };

    public static Command Build() {
        var command = new Command("plan", "Report what a run would do - videos, segments, requests - without calling the API.") {
            Video, Folder, From, Limit, ResumeWindow, Resume, Force,
            ExtractionOptions.Out, ExtractionOptions.Parts, ExtractionOptions.Overlap,
            ExtractionOptions.Speed, ExtractionOptions.Model, ExtractionOptions.Profile,
            ExtractionOptions.VideoFps, ExtractionOptions.SkipTokenCounting, ExtractionOptions.FileActivationDelay
        };

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);

            if (!TryResolveVideos(parseResult, config, out var videos, out int failure)) {
                return failure;
            }

            double resumeWindow = parseResult.GetValue(Resume)
                ? 87600.0
                : (parseResult.GetValue(ResumeWindow) ?? ExtractionPlanner.DefaultResumeWindowHours);

            var plan = ExtractionPlanner.Build(
                config,
                videos,
                resumeWindow,
                parseResult.GetValue(Force),
                await ExtractionPlanner.ProbeDurationsIfAutoAsync(config, videos));

            CliOutput.Payload(context, plan, () => Render(plan));

            // A plan whose key does not resolve is still a valid plan, but the run it describes
            // cannot happen - so it reports the configuration problem rather than success.
            return plan.ApiKeyResolves ? ExitCodes.Success : ExitCodes.Configuration;
        });

        return command;
    }

    /// <summary>
    /// Resolves --video / --folder / the configured source folder into an ordered file list.
    /// Shared with <c>run</c>, so both commands select the same videos from the same arguments.
    /// </summary>
    public static bool TryResolveVideos(
        ParseResult parseResult,
        AiStudioAutoExtractionConfig config,
        out IReadOnlyList<string> videos,
        out int failureExitCode) {

        videos = [];
        failureExitCode = ExitCodes.Success;

        string? single = parseResult.GetValue(Video);
        if (!string.IsNullOrWhiteSpace(single)) {
            if (!File.Exists(single)) {
                Ui.Error($"Video not found: '{single}'", "plan");
                failureExitCode = ExitCodes.Usage;
                return false;
            }
            string fullPath = Path.GetFullPath(single);
            string resolvedPath = ExtractionHelpers.ResolveCompressedVariantIfExists(fullPath);
            if (!string.Equals(fullPath, resolvedPath, StringComparison.OrdinalIgnoreCase)) {
                Ui.Info($"Verwende vorhandene komprimierte Datei: '{Path.GetFileName(resolvedPath)}' anstelle von '{Path.GetFileName(fullPath)}'", "plan");
            }
            config.SourceFolder = Path.GetDirectoryName(resolvedPath) ?? config.SourceFolder;
            videos = [resolvedPath];
            return true;
        }

        string folder = parseResult.GetValue(Folder) is string requested && !string.IsNullOrWhiteSpace(requested)
            ? requested
            : config.SourceFolder;

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) {
            Ui.Error($"Source folder not found: '{folder}'", "plan");
            failureExitCode = ExitCodes.Usage;
            return false;
        }

        config.SourceFolder = Path.GetFullPath(folder);
        var rawVideos = Directory.GetFiles(config.SourceFolder, "*.mp4");
        var deduplicated = ExtractionHelpers.DeduplicatePreferCompressed(rawVideos);
        var ordered = deduplicated.OrderChronologically().ToList();

        string? from = parseResult.GetValue(From);
        if (!string.IsNullOrWhiteSpace(from)) {
            int index = ordered.FindIndex(video => Path.GetFileName(video).Contains(from, StringComparison.OrdinalIgnoreCase));
            if (index < 0) {
                Ui.Error($"No video in '{config.SourceFolder}' matches --from '{from}'.", "plan");
                failureExitCode = ExitCodes.Usage;
                return false;
            }
            ordered = [.. ordered.Skip(index)];
        }

        int? limit = parseResult.GetValue(Limit);
        if (limit.HasValue && limit.Value > 0) {
            ordered = [.. ordered.Take(limit.Value)];
        }

        if (ordered.Count == 0) {
            Ui.Warn($"No .mp4 files in '{config.SourceFolder}'.", "plan");
        }

        videos = ordered;
        return true;
    }

    private static void Render(ExtractionPlan plan) {
        Ui.Header("Plan");
        Ui.Table("Lauf", [
            ("Backend", plan.Backend),
            ("Modell", plan.Model),
            ("API-Key", $"Profil {plan.ApiKeyProfile} ({plan.ApiKeyEnvName}) — {(plan.ApiKeyResolves ? "gesetzt" : "FEHLT")}"),
            ("Quelle", plan.SourceFolder),
            ("Ziel", plan.TargetFolder),
            ("Videos", plan.VideoCount.ToString()),
            ("Segmente je Video", $"{plan.SegmentsPerVideo?.ToString() ?? "auto, nach Videolänge"} ({plan.OverlapSeconds}s Overlap, {plan.SpeedMultiplier}x{(plan.GoogleVideoFps.HasValue ? $", {plan.GoogleVideoFps.Value:0.###} FPS" : "")})"),
            ("Offene Anfragen", plan.PendingRequests.ToString()),
            ("Wiederverwendbar", $"{plan.ResumableSegments} (Fenster: {(plan.ResumeWindowHours >= 87600 ? "unbegrenzt, --resume" : $"{plan.ResumeWindowHours}h")})"),
            ("Refinement danach", plan.RefinementFollows ? "ja" : "nein")
        ]);

        // The folder's stage beside the parts: a video whose parts are all there may still be anywhere
        // in refinement, and 'run' would then merge again (copy-1 names, paid twice) where 'finish'
        // continues from the step it stopped at.
        int continuedElsewhere = 0;
        Ui.Grid("Videos", ["Vorlesung", "Teile", "Offen", "Stand im Ordner"], plan.Videos.Select(video => {
            string folder = Path.Combine(plan.TargetFolder, ExtractionHelpers.ComputeOutputFolderName(video.FileName));
            var progress = Directory.Exists(folder) ? LectureProgress.Scan(folder) : null;
            if (progress?.NextStage is LectureStage.Merge or LectureStage.Speech or LectureStage.Polish or LectureStage.Pdf or LectureStage.Done) {
                continuedElsewhere++;
            }
            return (IReadOnlyList<string>)[
                LectureLabel.Short(video.FileName),
                video.SegmentCount.ToString(),
                video.PendingSegments == 0 ? "-" : video.PendingSegments.ToString(),
                progress == null ? "-" : progress.NextStage == LectureStage.Done ? "PDF fertig" : progress.ReachedLabel
            ];
        }).ToList());
        if (continuedElsewhere > 0) {
            Ui.Detail($"{continuedElsewhere} Video(s) sind schon im Refinement oder fertig: 'run' würde sie neu zusammenführen; 'lecx finish' setzt beim offenen Schritt fort.");
        }

        foreach (string warning in plan.Warnings) {
            Ui.Warn(warning, "plan");
        }

        if (!plan.ApiKeyResolves) {
            Ui.Error($"Der API-Key '{plan.ApiKeyEnvName}' ist nicht gesetzt — dieser Lauf würde scheitern.", "plan");
        }
    }
}
