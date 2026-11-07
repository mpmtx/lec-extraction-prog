using System;
using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The whole pipeline, headless. Video in, segments, LaTeX, refinement and PDF out.
///
/// <para>There is no orchestration here, because the pipeline already chains: a successful
/// extraction constructs a <c>LatexRefinementSession</c> and awaits it, which runs steps 1-3 and
/// then the PDF. So this command supplies arguments and picks an entry point; the sequencing is
/// the pipeline's own. <c>--stop-after extract</c> works by turning that chaining off rather than
/// by reimplementing it.</para>
///
/// <para>This is the first command that can spend money, so it refuses to start when the plan says
/// the key does not resolve, and <c>--dry-run</c> prints exactly what it would do.</para>
/// </summary>
public static class RunCommand {
    internal static readonly Option<string?> StopAfter = new("--stop-after") {
        Description = "Stop after a stage: extract (skip refinement and PDF) or full (default)."
    };

    public static readonly Option<bool> CleanTmp = new("--clean-tmp") {
        Description = "Delete intermediate tmp/ directory upon successful completion."
    };

    public static Command Build(bool chainRefinement, string name, string description) {
        var command = new Command(name, description) {
            PlanCommand.Video, PlanCommand.Folder, PlanCommand.From, PlanCommand.Limit,
            PlanCommand.ResumeWindow, PlanCommand.Resume, PlanCommand.Force, CleanTmp,
            ExtractionOptions.Out, ExtractionOptions.Parts, ExtractionOptions.Overlap,
            ExtractionOptions.Speed, ExtractionOptions.Preset,
            ExtractionOptions.Model, ExtractionOptions.Profile,
            ExtractionOptions.VideoFps, ExtractionOptions.SkipTokenCounting, ExtractionOptions.FileActivationDelay,
            ThoughtOptions.Thoughts
        };

        if (chainRefinement) {
            command.Add(StopAfter);
        }

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);

            if (!PlanCommand.TryResolveVideos(parseResult, config, out var videos, out int failure)) {
                return failure;
            }

            // `extract run` never chains; `run --stop-after extract` chooses not to.
            bool stopAfterExtract = !chainRefinement
                || string.Equals(parseResult.GetValue(StopAfter), "extract", StringComparison.OrdinalIgnoreCase);
            config.GoIntoLatexRefinement = !stopAfterExtract;

            double resumeWindow = parseResult.GetValue(PlanCommand.Resume)
                ? 87600.0
                : (parseResult.GetValue(PlanCommand.ResumeWindow) ?? ExtractionPlanner.DefaultResumeWindowHours);
            bool force = parseResult.GetValue(PlanCommand.Force);
            var plan = ExtractionPlanner.Build(config, videos, resumeWindow, force,
                await ExtractionPlanner.ProbeDurationsIfAutoAsync(config, videos));

            if (context.DryRun) {
                CliOutput.Payload(context, new { dryRun = true, plan },
                    () => Ui.Info($"{plan.VideoCount} Video(s), {plan.PendingRequests} offene Anfrage(n). Nichts ausgeführt (--dry-run)."));
                return ExitCodes.Success;
            }

            if (!plan.ApiKeyResolves) {
                Ui.Error($"Der API-Key '{plan.ApiKeyEnvName}' (Profil {plan.ApiKeyProfile}) ist nicht gesetzt.", name);
                return ExitCodes.Configuration;
            }

            if (videos.Count == 0) {
                Ui.Warn("Keine Videos zu verarbeiten.", name);
                return ExitCodes.Success;
            }

            // --force is expressed by widening the reuse window to nothing, which is the same knob
            // the pipeline reads; there is no separate "ignore the cache" path to keep in sync.
            SessionCostLedger.Reset();
            var session = BuildSession(config, force ? 0 : resumeWindow, parseResult);

            bool allSucceeded = await session.RunAsync([.. videos]);

            if (allSucceeded && parseResult.GetValue(CleanTmp)) {
                foreach (string video in videos) {
                    string folderName = ExtractionHelpers.ComputeOutputFolderName(video);
                    string targetFolder = Path.Combine(ExtractionHelpers.OutputRoot(config), folderName);
                    string tmpPath = Path.Combine(targetFolder, "tmp");
                    if (Directory.Exists(tmpPath)) {
                        try {
                            Directory.Delete(tmpPath, recursive: true);
                            Ui.Detail($"Bereinigt (tmp): {folderName}/tmp", "clean-tmp");
                        }
                        catch (Exception ex) {
                            Ui.Warn($"Konnte tmp-Ordner nicht löschen: {ex.Describe()}", "clean-tmp");
                        }
                    }
                }
            }

            var summary = new {
                videos = plan.VideoCount,
                requestsPlanned = plan.PendingRequests,
                generationRequests = SessionCostLedger.GenerationRequests,
                supportRequests = SessionCostLedger.SupportRequests,
                retriedAttempts = SessionCostLedger.RetriedAttempts,
                timeWaitedSeconds = Math.Round(SessionCostLedger.TimeWaited.TotalSeconds, 1),
                refinementRan = !stopAfterExtract,
                targetFolder = plan.TargetFolder,
                allSucceeded
            };

            CliOutput.Payload(context, summary, () => Ui.Table("Aufwand dieser Sitzung", SessionCostLedger.Summary()));

            // Partial success is its own exit code: some videos produced .tex and others did not,
            // which the interactive app only ever reported as a scrolling warning.
            return allSucceeded ? ExitCodes.Success : ExitCodes.Partial;
        });

        return command;
    }

    internal static AutoExtractionSession BuildSession(AiStudioAutoExtractionConfig config, double resumeWindowHours, ParseResult parseResult) {
        string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);
        string apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName)
            ?? throw new InvalidOperationException($"Der API-Key für Profil {config.ActiveApiProfile} ('{envName}') ist nicht gesetzt.");
        Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);

        var attachmentHandler = new AttachmentUploader(
            client, config.SourceFolder, [config.SourceFolder], true, "",
            config.GoogleVideoFps, config.InlineHistoryImages, config.FileActivationDelaySeconds,
            config.VideoUploadTimeoutSeconds, config.VideoUploadMaxRetries) {
            ClientFactory = () => GoogleAiClientBuilder.BuildAiStudioClient(apiKey)
        };

        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var refinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();

        // The refinement continues on the extraction's key, found by name in its own list. --profile has
        // already picked that key from the profile table; reading --profile N as an index into the
        // refinement list chose another key wherever the two lists differ in order (2026-10-26:
        // --profile 4 ran the refinement on profile 0's key).
        int matchingRefinementProfile = ApiKeyProfileResolver.ResolveIndex(envName, refinementConfig.AiStudioApiKeyEnvNames, -1);
        if (matchingRefinementProfile >= 0) {
            refinementConfig.AiStudioActiveApiProfile = matchingRefinementProfile;
        }
        else if (parseResult.GetValue(ExtractionOptions.Profile) is string profileRaw && !string.IsNullOrWhiteSpace(profileRaw)) {
            Ui.Warn($"'{envName}' (--profile {profileRaw}) fehlt in AiStudioApiKeyEnvNames von LatexRefinementSessionConfig.json; das Refinement nutzt '{RefinementUiHelper.ResolveRefinementKeyEnvName(refinementConfig)}'.", "run");
        }

        // Only an explicit --model reaches the refinement steps. Without it, which model they use is
        // UseChosenModelForRestOfPipeline's decision (the session applies it when it chains).
        string? modelToUse = parseResult.GetValue(ExtractionOptions.Model);
        if (!string.IsNullOrWhiteSpace(modelToUse)) {
            foreach (var step in new[] { refinementConfig.Step1MergeAndTimestamp, refinementConfig.Step2SpeechRefinement, refinementConfig.Step3LastRefinement }) {
                if (step != null) {
                    (refinementConfig.UseVertex ? step.Vertex : step.AiStudio).CurrentModel = modelToUse;
                }
            }
        }

        ThoughtOptions.Apply(refinementConfig, parseResult);

        return new AutoExtractionSession(new AiStudioExtractionBackend(client, config), config, attachmentHandler, sessionLogger, refinementConfig) {
            ResumeWindow = TimeSpan.FromHours(resumeWindowHours)
        };
    }
}
