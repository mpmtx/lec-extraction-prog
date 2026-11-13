using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// Runs several videos at once, one child process per worker.
///
/// <para><b>Processes, not threads, and not by preference.</b> The pipeline keeps its rate-limit
/// pacing in <c>InteractiveDelay.LastGenerationCompletionTimeUtc</c> - a single process-wide static
/// clock, written after every generation and read to decide how long to wait. Two sessions inside
/// one process would each read the other's timestamp, destroying exactly the independent pacing
/// that separate API-key profiles exist to provide. <c>AttachmentUploader.HasJustUploaded</c>,
/// <c>SessionCostLedger</c> and the single console are the same story. Separate processes get
/// separate clocks, separate ledgers and separate rate-limit budgets for free.</para>
///
/// <para>This command therefore contains <b>no pipeline logic at all</b>. It shards the video list,
/// spawns <c>run</c> children, tees their output to per-worker logs and aggregates their exit
/// codes. Sharding is by whole video, so no two workers ever touch the same output folder or
/// <c>tmp</c> directory.</para>
/// </summary>
public static class BatchCommand {
    private static readonly Option<string> Workers = new("--workers") {
        Description = "Worker specs separated by commas, e.g. profile=1:model=gemini-3.5-flash,profile=2. "
                    + "Each worker becomes one process with its own API-key profile and rate-limit budget.",
        Required = true
    };

    private static readonly Option<string?> LogFolder = new("--log-folder") {
        Description = "Where per-worker logs are written. Defaults to a 'batch-logs' folder under the target."
    };

    public static Command Build() {
        var command = new Command("batch", "Run several videos in parallel worker processes, each with its own model and API-key profile.") {
            Workers, LogFolder, RunCommand.StopAfter,
            PlanCommand.Folder, PlanCommand.From, PlanCommand.Limit, PlanCommand.ResumeWindow, PlanCommand.Force,
            ExtractionOptions.Out, ExtractionOptions.Parts, ExtractionOptions.Overlap,
            ExtractionOptions.Speed, ExtractionOptions.VideoFps,
            ExtractionOptions.SkipTokenCounting, ExtractionOptions.FileActivationDelay,
            ExtractionOptions.MaxOutputTokens, ExtractionOptions.ThinkingLevel,
            ThoughtOptions.Thoughts
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);

            if (!PlanCommand.TryResolveVideos(parseResult, config, out var videos, out int failure)) {
                return failure;
            }

            if (!TryParseWorkers(parseResult.GetValue(Workers)!, out var workers, out string? specError)) {
                Ui.Error(specError!, "batch");
                return ExitCodes.Usage;
            }

            if (videos.Count == 0) {
                Ui.Warn("Keine Videos zu verarbeiten.", "batch");
                return ExitCodes.Success;
            }

            var shards = Shard(videos, workers.Count);
            var plan = ExtractionPlanner.Build(config, videos, ExtractionPlanner.DefaultResumeWindowHours, force: false);

            // A collision means two workers would write the same output folder even though the
            // shards are disjoint by file - the reason sharding alone is not enough here.
            foreach (string warning in plan.Warnings) {
                Ui.Warn(warning, "batch");
            }

            string logFolder = parseResult.GetValue(LogFolder) is string requested && !string.IsNullOrWhiteSpace(requested)
                ? Path.GetFullPath(requested)
                : Path.Combine(plan.TargetFolder, "batch-logs");

            var assignments = workers
                .Select((worker, index) => new WorkerAssignment(index, worker, shards[index]))
                .Where(assignment => assignment.Videos.Count > 0)
                .ToList();

            if (context.DryRun) {
                CliOutput.Payload(context, new {
                    dryRun = true,
                    workers = assignments.Select(a => new { a.Index, a.Worker.Profile, a.Worker.Model, videos = a.Videos.Count }),
                    totalVideos = videos.Count,
                    logFolder
                }, () => RenderAssignments(assignments, logFolder));
                return ExitCodes.Success;
            }

            Directory.CreateDirectory(logFolder);
            RenderAssignments(assignments, logFolder);

            var results = await Task.WhenAll(assignments.Select(assignment =>
                RunWorkerAsync(assignment, parseResult, logFolder, plan.TargetFolder, cancellationToken)));

            var summary = new {
                workers = results,
                totalVideos = videos.Count,
                logFolder,
                allSucceeded = results.All(result => result.ExitCode == ExitCodes.Success)
            };

            CliOutput.Payload(context, summary, () => {
                Ui.Step("Ergebnis");
                foreach (var result in results) {
                    string state = result.ExitCode == ExitCodes.Success ? "OK" : $"Exit {result.ExitCode}";
                    Ui.Detail($"Worker {result.Index} (Profil {result.Profile}): {result.Videos} Video(s) — {state}");
                }
            });

            if (summary.allSucceeded) {
                return ExitCodes.Success;
            }

            // Any worker failing makes the batch partial - some videos have output and some do not.
            return results.All(result => result.ExitCode != ExitCodes.Success) ? ExitCodes.Unexpected : ExitCodes.Partial;
        });

        return command;
    }

    public sealed record WorkerSpec(int? Profile, string? Model);
    private sealed record WorkerAssignment(int Index, WorkerSpec Worker, IReadOnlyList<string> Videos);
    private sealed record WorkerResult(int Index, int? Profile, string? Model, int Videos, int ExitCode, string LogPath);

    /// <summary>
    /// Parses <c>profile=1:model=x,profile=2</c>. Kept strict rather than lenient: a typo that
    /// silently produced a worker on the default profile would double the load on one API key,
    /// which is the exact failure this command exists to avoid.
    /// </summary>
    public static bool TryParseWorkers(string spec, out List<WorkerSpec> workers, out string? error) {
        workers = [];
        error = null;

        foreach (string chunk in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
            int? profile = null;
            string? model = null;

            foreach (string field in chunk.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
                string[] pair = field.Split('=', 2);
                if (pair.Length != 2) {
                    error = $"'{field}' is not key=value. Expected e.g. profile=1:model=gemini-3.5-flash.";
                    return false;
                }

                switch (pair[0].ToLowerInvariant()) {
                    case "profile":
                        if (!int.TryParse(pair[1], out int parsed)) {
                            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
                            int resolved = ApiKeyProfileResolver.ResolveIndex(pair[1], config.AiStudioApiKeyEnvNames, -1);
                            if (resolved < 0) {
                                error = $"'{pair[1]}' is not a valid profile index or profile name.";
                                return false;
                            }
                            parsed = resolved;
                        }
                        profile = parsed;
                        break;

                    case "model":
                        model = pair[1];
                        break;

                    default:
                        error = $"Unknown worker field '{pair[0]}'. Known: profile, model.";
                        return false;
                }
            }

            workers.Add(new WorkerSpec(profile, model));
        }

        if (workers.Count == 0) {
            error = "No workers specified.";
            return false;
        }

        // Two workers on one profile share a rate-limit budget, which makes them slower than one.
        var duplicated = workers.Where(w => w.Profile != null)
                                .GroupBy(w => w.Profile)
                                .Where(group => group.Count() > 1)
                                .Select(group => group.Key!.Value)
                                .ToList();
        if (duplicated.Count > 0) {
            error = $"Profile {string.Join(", ", duplicated)} used by more than one worker; they would share one rate-limit budget. Give each worker its own profile.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Deals videos round-robin so the shards stay balanced when a folder holds a long tail of
    /// short lectures. Whole videos only - splitting one video across workers would put two
    /// processes in the same output folder.
    /// </summary>
    public static List<List<string>> Shard(IReadOnlyList<string> videos, int workerCount) {
        var shards = Enumerable.Range(0, workerCount).Select(_ => new List<string>()).ToList();
        for (int i = 0; i < videos.Count; i++) {
            shards[i % workerCount].Add(videos[i]);
        }
        return shards;
    }

    private static async Task<WorkerResult> RunWorkerAsync(
        WorkerAssignment assignment,
        ParseResult parseResult,
        string logFolder,
        string targetFolder,
        System.Threading.CancellationToken cancellationToken) {

        string logPath = Path.Combine(logFolder, $"worker-{assignment.Index}.log");
        int worstExitCode = ExitCodes.Success;

        await using var log = new StreamWriter(logPath, append: false);

        // One child per video rather than per shard: a worker's videos are sequential anyway, and
        // per-video children mean one failure does not abandon the rest of that worker's list.
        foreach (string video in assignment.Videos) {
            var arguments = BuildChildArguments(video, assignment.Worker, parseResult, targetFolder);
            await log.WriteLineAsync($"=== {Path.GetFileName(video)} ===");
            await log.WriteLineAsync($"$ {Environment.ProcessPath} {string.Join(' ', arguments)}");
            await log.FlushAsync(cancellationToken);

            int exitCode = await RunChildAsync(arguments, log, cancellationToken);
            await log.WriteLineAsync($"=== exit {exitCode} ===");
            await log.FlushAsync(cancellationToken);

            if (exitCode != ExitCodes.Success) {
                worstExitCode = exitCode;
            }
        }

        return new WorkerResult(assignment.Index, assignment.Worker.Profile, assignment.Worker.Model,
            assignment.Videos.Count, worstExitCode, logPath);
    }

    private static List<string> BuildChildArguments(string video, WorkerSpec worker, ParseResult parseResult, string targetFolder) {
        List<string> arguments = ["run", "--video", video];

        if (worker.Profile is int profile) {
            arguments.AddRange(["--profile", profile.ToString()]);
        }
        if (worker.Model is string model) {
            arguments.AddRange(["--model", model]);
        }

        // Pass through the knobs that change what a run does, so a child cannot silently use
        // different geometry from the batch that scheduled it.
        Forward(arguments, "--out", parseResult.GetValue(ExtractionOptions.Out) ?? targetFolder);
        Forward(arguments, "--parts", parseResult.GetValue(ExtractionOptions.Parts)?.ToString());
        Forward(arguments, "--overlap", parseResult.GetValue(ExtractionOptions.Overlap)?.ToString());
        Forward(arguments, "--speed", parseResult.GetValue(ExtractionOptions.Speed)?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Forward(arguments, "--video-fps", parseResult.GetValue(ExtractionOptions.VideoFps)?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Forward(arguments, "--skip-token-counting", parseResult.GetValue(ExtractionOptions.SkipTokenCounting)?.ToString());
        Forward(arguments, "--file-activation-delay", parseResult.GetValue(ExtractionOptions.FileActivationDelay)?.ToString());
        Forward(arguments, "--max-output-tokens", parseResult.GetValue(ExtractionOptions.MaxOutputTokens)?.ToString());
        Forward(arguments, "--thinking-level", parseResult.GetValue(ExtractionOptions.ThinkingLevel));
        Forward(arguments, "--resume-window", parseResult.GetValue(PlanCommand.ResumeWindow)?.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Forward(arguments, "--stop-after", parseResult.GetValue(RunCommand.StopAfter));
        Forward(arguments, "--thoughts", parseResult.GetValue(ThoughtOptions.Thoughts));

        // Without it a child would read the default config folder, not the one this batch was given.
        Forward(arguments, "--config-dir", parseResult.GetValue(CliOptions.ConfigDir));

        if (parseResult.GetValue(PlanCommand.Force)) {
            arguments.Add("--force");
        }

        // Children never persist configuration: several processes writing one *Config.json is the
        // collision --config-dir exists to prevent, and a batch must not rewrite the user's setup.
        arguments.Add("--json");
        return arguments;
    }

    private static void Forward(List<string> arguments, string name, string? value) {
        if (!string.IsNullOrWhiteSpace(value)) {
            arguments.AddRange([name, value]);
        }
    }

    private static async Task<int> RunChildAsync(List<string> arguments, StreamWriter log, System.Threading.CancellationToken cancellationToken) {
        var startInfo = new ProcessStartInfo {
            FileName = Environment.ProcessPath ?? "lec-extraction-prog",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments) {
            startInfo.ArgumentList.Add(argument);
        }

        try {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start returned null.");

            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            await log.WriteLineAsync(await stderr);
            await log.WriteLineAsync(await stdout);
            return process.ExitCode;
        }
        catch (Exception ex) {
            await log.WriteLineAsync($"[Exception gefangen] {ex.Describe()}");
            Ui.Error($"Worker-Prozess fehlgeschlagen: {ex.Describe()}", "batch");
            return ExitCodes.Unexpected;
        }
    }

    private static void RenderAssignments(List<WorkerAssignment> assignments, string logFolder) {
        Ui.Step($"{assignments.Count} Worker");
        foreach (var assignment in assignments) {
            string profile = assignment.Worker.Profile?.ToString() ?? "(Standard)";
            string model = assignment.Worker.Model ?? "(konfiguriert)";
            Ui.Detail($"Worker {assignment.Index}: Profil {profile}, Modell {model} — {assignment.Videos.Count} Video(s)");
        }
        Ui.Detail($"Logs: {logFolder}");
    }
}
