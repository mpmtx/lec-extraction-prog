using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// [AI Context] 'lecx finish': brings every started lecture to a PDF from wherever it stopped. It reads
/// each folder through <see cref="LectureProgress"/> (the scan 'status' shows) and runs the one
/// existing command that continues it - 'run --resume' while parts are missing, 'refine run --step N
/// --through-end' on the right step file, 'pdf compile' when only the PDF is left. Nothing about the
/// pipeline's sequencing is reimplemented: each lecture's action is parsed and invoked as if typed.
///
/// <para>Before this, finishing a folder meant reading 'status', finding the newest step file by hand
/// and picking the matching command. Picking 'run' after a refinement failure paid for the merge
/// twice and renamed every later file (-copy-1), which docs/cli/agents.md warned about.</para>
/// [Human] Führt jede begonnene Vorlesung ab dem Schritt fort, an dem sie stehen geblieben ist.
/// </summary>
public static class FinishCommand {
    private static readonly Option<string?> Source = new("--source") {
        Description = "Folder holding the videos, for lectures whose transcription is incomplete. Defaults to the configured source folder, then the parent of the output folder."
    };

    private static readonly Option<int?> Limit = new("--limit") {
        Description = "Finish at most this many lectures, in chronological order."
    };

    public static Command Build() {
        var command = new Command("finish", "Continue every started lecture from where it stopped, through to the PDF. Paid; --dry-run shows the commands it would run.") {
            StatusCommand.Video, StatusCommand.Folder, Source, Limit, RunCommand.CleanTmp,
            ExtractionOptions.Profile, ExtractionOptions.Model,
            ExtractionOptions.MaxOutputTokens, ExtractionOptions.ThinkingLevel,
            ThoughtOptions.Thoughts
        };

        command.SetAction(async (parseResult, cancellationToken) => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));

            var folders = LectureFolders.Resolve(parseResult.GetValue(StatusCommand.Video), parseResult.GetValue(StatusCommand.Folder), config);
            var sourceDirs = SourceDirectories(parseResult.GetValue(Source), config, folders);
            var actions = PlanActions(folders.Select(LectureProgress.Scan), lecture => FindVideo(lecture, sourceDirs), PassThroughArguments(parseResult));
            if (parseResult.GetValue(Limit) is int limit) {
                actions = [.. actions.Take(Math.Max(0, limit))];
            }

            if (actions.Count == 0) {
                CliOutput.Payload(context, new { actions = Array.Empty<object>() },
                    () => Ui.Info("Keine begonnene, unfertige Vorlesung gefunden. ('lecx run' beginnt neue.)", "finish"));
                return ExitCodes.Success;
            }

            if (context.DryRun) {
                CliOutput.Payload(context, new { dryRun = true, actions = actions.Select(Describe) }, () => RenderPlan(actions, withCommands: true));
                return ExitCodes.Success;
            }

            RenderPlan(actions, withCommands: false);
            var results = new List<(FinishAction Action, int ExitCode, LectureProgress After)>();
            for (int i = 0; i < actions.Count; i++) {
                var action = actions[i];
                Ui.Step($"[{i + 1}/{actions.Count}] {LectureLabel.Short(action.Lecture)}: {LectureProgress.StageLabel(action.Stage)}", "finish");

                int exitCode = action.Arguments == null
                    ? ExitCodes.Usage
                    : await InvokeAsync(action.Arguments);
                if (action.Arguments == null) {
                    Ui.Error(action.Problem!, "finish");
                }

                var after = LectureProgress.Scan(action.Folder);
                if (after.NextStage == LectureStage.Done && parseResult.GetValue(RunCommand.CleanTmp)) {
                    DeleteTmp(action.Folder);
                }
                results.Add((action, exitCode, after));

                // A missing key or an unanswerable prompt will fail every following lecture the same way.
                if (exitCode is ExitCodes.Configuration or ExitCodes.UnattendedPrompt) {
                    Ui.Error($"Abbruch nach {LectureLabel.Short(action.Lecture)} (Exit {exitCode}); die übrigen Vorlesungen würden genauso scheitern.", "finish");
                    break;
                }
                if (cancellationToken.IsCancellationRequested) {
                    break;
                }
            }

            int succeeded = results.Count(r => r.After.NextStage == LectureStage.Done);
            CliOutput.Payload(context,
                new {
                    finished = succeeded,
                    attempted = results.Count,
                    planned = actions.Count,
                    results = results.Select(r => new { lecture = r.Action.Lecture, stage = r.Action.Stage.ToString(), exitCode = r.ExitCode, nowAt = r.After.NextStage.ToString() })
                },
                () => {
                    Ui.Grid("Ergebnis", ["Vorlesung", "Gestartet bei", "Exit", "Jetzt"], results.Select(r => (IReadOnlyList<string>)[
                        LectureLabel.Short(r.Action.Lecture),
                        LectureProgress.StageLabel(r.Action.Stage),
                        r.ExitCode.ToString(),
                        r.After.NextStage == LectureStage.Done ? "PDF fertig" : $"als Nächstes {LectureProgress.StageLabel(r.After.NextStage)}"
                    ]));
                });

            if (succeeded == actions.Count) return ExitCodes.Success;
            return succeeded > 0 ? ExitCodes.Partial : results.Select(r => r.ExitCode).FirstOrDefault(c => c != ExitCodes.Success, ExitCodes.Partial);
        });

        return command;
    }

    /// <summary>
    /// [AI Context] The command that continues one lecture, for every started, unfinished one, in
    /// chronological order. Pure, so the stage-to-command mapping is testable without running anything.
    /// [Human] Welcher Befehl jede unfertige Vorlesung fortsetzt.
    /// </summary>
    /// <param name="findVideo">Finds the source video of a lecture folder name, or null.</param>
    /// <param name="passThrough">Options appended to every command (profile, model, thoughts, config dir).</param>
    public static List<FinishAction> PlanActions(IEnumerable<LectureProgress> lectures, Func<string, string?> findVideo, IReadOnlyList<string> passThrough) {
        var actions = new List<FinishAction>();
        foreach (var p in lectures
            .OrderBy(p => Media.VideoDateParser.SortDate(p.Lecture))
            .ThenBy(p => p.Lecture, StringComparer.OrdinalIgnoreCase)) {
            string[]? args;
            string? problem = null;
            switch (p.NextStage) {
                case LectureStage.Done:
                case LectureStage.NotStarted:
                    continue;
                case LectureStage.Transcribe: {
                    string? video = findVideo(p.Lecture);
                    if (video == null) {
                        args = null;
                        problem = $"Kein Video zu '{p.Lecture}' gefunden (--source angeben).";
                        break;
                    }
                    // --resume reuses every finished part however old it is; run then chains into refinement.
                    args = ["run", "--video", video, "--resume", "--out", Path.GetDirectoryName(p.Folder)!];
                    break;
                }
                case LectureStage.Merge:
                    args = ["refine", "run", "--tex", p.NextInput!];
                    break;
                case LectureStage.Speech:
                    args = ["refine", "run", "--tex", p.NextInput!, "--step", "2", "--through-end"];
                    break;
                case LectureStage.Polish:
                    args = ["refine", "run", "--tex", p.NextInput!, "--step", "3", "--through-end"];
                    break;
                default:
                    args = ["pdf", "compile", "--tex", p.NextInput!];
                    break;
            }
            actions.Add(new FinishAction(p.Lecture, p.Folder, p.NextStage, p.ReachedLabel, p.NextInput,
                args == null ? null : [.. args, .. passThrough], problem));
        }
        return actions;
    }

    /// <summary>A command line as it would be typed, for the plan and the log.</summary>
    public static string CommandLine(IEnumerable<string> arguments) =>
        "lecx " + string.Join(" ", arguments.Select(a => a.Contains(' ') || a.Length == 0 ? $"\"{a}\"" : a));

    private static object Describe(FinishAction a) => new {
        lecture = a.Lecture, folder = a.Folder, reached = a.Reached, stage = a.Stage.ToString(), input = a.Input,
        command = a.Arguments == null ? null : CommandLine(a.Arguments), problem = a.Problem
    };

    private static void RenderPlan(IReadOnlyList<FinishAction> actions, bool withCommands) {
        Ui.Grid($"lecx finish: {actions.Count} Vorlesung(en)", ["Vorlesung", "Stand", "Als Nächstes", "Eingabe"], actions.Select(a => (IReadOnlyList<string>)[
            LectureLabel.Short(a.Lecture),
            a.Reached,
            LectureProgress.StageLabel(a.Stage),
            a.Input != null ? ShortFileName(a.Input, a.Lecture) : a.Problem ?? "Video (Teile werden wiederverwendet)"
        ]));
        foreach (var a in actions.Where(a => withCommands && a.Arguments != null)) {
            Ui.Detail(CommandLine(a.Arguments!));
        }
    }

    /// <summary>Deletes a finished lecture's tmp/ (FFmpeg segments); a later run would cut them again if needed.</summary>
    private static void DeleteTmp(string lectureFolder) {
        string tmp = Path.Combine(lectureFolder, "tmp");
        if (!Directory.Exists(tmp)) return;
        try {
            long bytes = new DirectoryInfo(tmp).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            Directory.Delete(tmp, recursive: true);
            Ui.Detail($"tmp/ gelöscht ({CleanCommand.FormatBytes(bytes)})", "finish");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Ui.Warn($"tmp/ von {Path.GetFileName(lectureFolder)} konnte nicht gelöscht werden: {ex.Describe()}", "finish");
        }
    }

    /// <summary>A step file's name with the lecture name elided ("step2-…-all-offset-merged.tex"); the row names the lecture.</summary>
    public static string ShortFileName(string path, string lecture) {
        string name = Path.GetFileName(path);
        return lecture.Length > 0 ? name.Replace(lecture, "…", StringComparison.OrdinalIgnoreCase) : name;
    }

    private static async System.Threading.Tasks.Task<int> InvokeAsync(string[] arguments) {
        Ui.Detail(CommandLine(arguments), "finish");
        try {
            var parsed = CliBootstrapper.BuildRootCommand().Parse(arguments);
            if (parsed.Errors.Count > 0) {
                foreach (var error in parsed.Errors) Ui.Error(error.Message, "finish");
                return ExitCodes.Usage;
            }
            return await parsed.InvokeAsync(new InvocationConfiguration { EnableDefaultExceptionHandler = false });
        }
        catch (UnattendedPromptException ex) {
            Ui.Error(ex.Message, "finish");
            return ExitCodes.UnattendedPrompt;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) {
            // One lecture's crash should not cost the others their turn.
            Ui.Error($"Unerwarteter Fehler: {ex.Describe()}", "finish");
            return ExitCodes.Unexpected;
        }
    }

    /// <summary>The options of this invocation that every per-lecture command should see too.</summary>
    private static List<string> PassThroughArguments(ParseResult parseResult) {
        var args = new List<string>();
        void Add(string name, string? value) {
            if (!string.IsNullOrWhiteSpace(value)) args.AddRange([name, value]);
        }
        Add("--profile", parseResult.GetValue(ExtractionOptions.Profile));
        Add("--model", parseResult.GetValue(ExtractionOptions.Model));
        Add("--max-output-tokens", parseResult.GetValue(ExtractionOptions.MaxOutputTokens)?.ToString());
        Add("--thinking-level", parseResult.GetValue(ExtractionOptions.ThinkingLevel));
        Add("--thoughts", parseResult.GetValue(ThoughtOptions.Thoughts));
        Add("--config-dir", parseResult.GetValue(CliOptions.ConfigDir));
        if (parseResult.GetValue(CliOptions.Yes)) args.Add("--yes");
        if (parseResult.GetValue(CliOptions.Quiet)) args.Add("--quiet");
        return args;
    }

    private static List<string> SourceDirectories(string? explicitSource, IAutoExtractionConfig config, IReadOnlyList<string> lectureFolders) {
        if (!string.IsNullOrWhiteSpace(explicitSource)) return [Path.GetFullPath(explicitSource)];
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(config.SourceFolder)) dirs.Add(Path.GetFullPath(config.SourceFolder));
        // <source>/extracted_output/<lecture>: the videos are two levels up from a lecture folder.
        foreach (string folder in lectureFolders) {
            string? parent = Path.GetDirectoryName(Path.GetDirectoryName(folder));
            if (parent != null && !dirs.Contains(parent, StringComparer.OrdinalIgnoreCase)) dirs.Add(parent);
        }
        return dirs;
    }

    private static string? FindVideo(string lecture, IReadOnlyList<string> sourceDirs) {
        foreach (string dir in sourceDirs.Where(Directory.Exists)) {
            string? match = ExtractionHelpers.DeduplicatePreferCompressed(Directory.GetFiles(dir, "*.mp4"))
                .FirstOrDefault(v => string.Equals(ExtractionHelpers.ComputeOutputFolderName(v), lecture, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }
}

/// <summary>What 'finish' runs for one lecture; Arguments is null when it cannot continue (Problem says why).</summary>
public sealed record FinishAction(string Lecture, string Folder, LectureStage Stage, string Reached, string? Input, string[]? Arguments, string? Problem);
