using System;
using System.CommandLine;
using System.IO;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The refinement stages over an existing <c>.tex</c>: steps 1-3, and step 4 (PDF) on its own.
///
/// <para>Both commands go through the same <c>LatexRefinementSession</c> the interactive path uses,
/// and select stages the same way it does - by toggling the <c>Enabled</c> flags before starting.
/// Nothing about the pipeline's sequencing is reimplemented here.</para>
///
/// <para>The extraction config is deliberately <b>not</b> passed. It is what gates refinement on
/// <c>GoIntoLatexRefinement</c>, <c>GenerateOffsetFiles</c> and <c>GenerateAudioFile</c>, which are
/// prerequisites of "refinement as the tail of an extraction". A caller naming a .tex file
/// explicitly has already made that decision.</para>
/// </summary>
public static class RefineCommands {
    private static readonly Option<string> Tex = new("--tex") {
        Description = "The .tex file to work on.",
        Required = true
    };

    private static readonly Option<int?> Step = new("--step") {
        Description = "Run a single step: 1 (merge/timestamps), 2 (speech), 3 (final polish). Omit for all three."
    };

    private static readonly Option<bool> ThroughEnd = new("--through-end") {
        Description = "With --step, continue through the remaining steps and compile the PDF."
    };

    private static readonly Option<string?> Audio = new("--audio") {
        Description = "Audio track used to correct timestamps in step 1."
    };

    private static readonly Option<int?> FixLoop = new("--fix-loop") {
        Description = "Rounds of AI repair to attempt on a failed compile. 0 makes the command free."
    };

    public static Command BuildRefine() {
        var group = new Command("refine", "LaTeX refinement steps 1-3 over an existing .tex.");
        var run = new Command("run", "Merge, polish and validate a .tex file.") { Tex, Step, ThroughEnd, Audio, ExtractionOptions.Overlap, ExtractionOptions.Profile, ExtractionOptions.Model, ExtractionOptions.MaxOutputTokens, ExtractionOptions.ThinkingLevel, ThoughtOptions.Thoughts };

        run.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            string tex = parseResult.GetValue(Tex)!;

            if (!File.Exists(tex)) {
                Ui.Error($".tex file not found: '{tex}'", "refine");
                return ExitCodes.Usage;
            }

            int? step = parseResult.GetValue(Step);
            if (step is < 1 or > 3) {
                Ui.Error("--step must be 1, 2 or 3.", "refine");
                return ExitCodes.Usage;
            }

            var config = ConfigLoader<LatexRefinementSessionConfig>.Load();
            var key = ApplyOverrides(config, parseResult);
            if (key.Error != null) {
                Ui.Error(key.Error, "refine");
                return ExitCodes.Usage;
            }
            string? audio = ResolveAudio(parseResult, tex);
            bool throughEnd = parseResult.GetValue(ThroughEnd);

            // Step 1 tells the model where each part starts, which needs the overlap the segments
            // were cut with. A standalone run has no extraction session to ask, so it reads the same
            // configured value the extraction used, unless --overlap says otherwise.
            int overlapSeconds = parseResult.GetValue(ExtractionOptions.Overlap)
                ?? ((AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig))).OverlapSeconds;

            // "4" is the interactive menu's spelling of "the whole pipeline"; reusing it keeps the
            // stage selection in one place rather than duplicating the flag arithmetic.
            string stepChoice = step?.ToString() ?? "4";

            if (context.DryRun) {
                CliOutput.Payload(context, new {
                    dryRun = true, tex = Path.GetFullPath(tex), step = stepChoice, throughEnd,
                    audio, overlapSeconds, backend = config.UseVertex ? "vertex" : "aistudio", model = StepModel(config),
                    apiKeyProfile = TableProfile(key.EnvName), apiKeyEnvName = key.EnvName, apiKeyResolves = key.Resolves
                }, () => Ui.Info($"Würde {(step == null ? "die komplette Pipeline" : $"Schritt {step}")} auf {Path.GetFileName(tex)} anwenden."));
                return ExitCodes.Success;
            }

            if (!key.Resolves) {
                Ui.Error($"Der API-Key '{key.EnvName}' (Profil {TableProfile(key.EnvName)}) ist nicht gesetzt.", "refine");
                return ExitCodes.Configuration;
            }

            RefinementUiHelper.ApplyStepSelection(config, stepChoice, throughEnd);
            bool completed = await RefinementUiHelper.RunRefinementAsync(config, null, Path.GetFullPath(tex), audio, overlapSeconds);

            CliOutput.Payload(context, new { tex = Path.GetFullPath(tex), step = stepChoice, throughEnd, completed },
                () => { /* The session reports each stage as it runs. */ });

            // A failed step is logged and the pipeline carries on or stops - either way a caller
            // reading only the exit code has to learn that the output is not what it asked for.
            return completed ? ExitCodes.Success : ExitCodes.Partial;
        });

        group.Add(run);
        return group;
    }

    public static Command BuildPdf() {
        var group = new Command("pdf", "PDF compilation (step 4).");
        var compile = new Command("compile", "Compile a .tex to PDF, optionally with the AI repair loop.") { Tex, FixLoop, ExtractionOptions.Profile, ExtractionOptions.Model, ThoughtOptions.Thoughts };

        compile.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            string tex = parseResult.GetValue(Tex)!;

            if (!File.Exists(tex)) {
                Ui.Error($".tex file not found: '{tex}'", "pdf");
                return ExitCodes.Usage;
            }

            var config = ConfigLoader<LatexRefinementSessionConfig>.Load();
            var key = ApplyOverrides(config, parseResult);
            if (key.Error != null) {
                Ui.Error(key.Error, "pdf");
                return ExitCodes.Usage;
            }

            // Every generation step off, PDF on: the session then runs step 4 alone.
            config.Step1MergeAndTimestamp.Enabled = false;
            config.Step2SpeechRefinement.Enabled = false;
            config.Step3LastRefinement.Enabled = false;
            config.PdfCompilation ??= new();
            config.PdfCompilation.Enabled = true;

            // The repair loop is what makes this command billable - it sends the failed document
            // and its log to the model. --fix-loop 0 keeps the command purely local.
            int? fixLoop = parseResult.GetValue(FixLoop);
            if (fixLoop is int rounds) {
                config.PdfCompilation.MaxFixRounds = rounds;
                if (rounds == 0) {
                    config.PdfCompilation.UseAntiGravityAgent = false;
                }
            }

            if (context.DryRun) {
                CliOutput.Payload(context, new {
                    dryRun = true, tex = Path.GetFullPath(tex),
                    fixLoopRounds = config.PdfCompilation.MaxFixRounds, model = StepModel(config),
                    apiKeyProfile = TableProfile(key.EnvName), apiKeyEnvName = key.EnvName, apiKeyResolves = key.Resolves
                }, () => Ui.Info($"Würde {Path.GetFileName(tex)} kompilieren (AI-Reparatur: {config.PdfCompilation.MaxFixRounds} Runden)."));
                return ExitCodes.Success;
            }

            string fullTex = Path.GetFullPath(tex);
            bool succeeded = await RefinementUiHelper.RunRefinementAsync(config, null, fullTex, null);

            string targetFolder = Path.GetDirectoryName(fullTex) ?? config.TargetFolder;
            string finalPdfPath = Path.Combine(targetFolder, Refinement.LatexRefinementSession.FinalPdfFileName(targetFolder, Refinement.LatexRefinementSession.GetCleanBaseName(fullTex)));
            string stepPdfPath = Path.ChangeExtension(fullTex, ".pdf");
            string pdfPath = File.Exists(finalPdfPath) ? finalPdfPath : stepPdfPath;
            bool produced = succeeded && File.Exists(pdfPath);

            CliOutput.Payload(context, new { tex = fullTex, pdf = produced ? pdfPath : null, produced },
                () => { /* CompilePdfAsync reports the path it wrote. */ });

            return produced ? ExitCodes.Success : ExitCodes.Unexpected;
        });

        group.Add(compile);
        return group;
    }

    /// <summary>
    /// Applies --profile and --model to this run only and reports which key that selects. Refinement
    /// keys come from their own list in the refinement config, so the extraction's profile check does
    /// not cover them; a caller that hits a rate limit can move to another key, and one that hits
    /// "high demand" on a model can move to another model, without editing config.
    /// </summary>
    private static (string EnvName, bool Resolves, string? Error) ApplyOverrides(LatexRefinementSessionConfig config, ParseResult parseResult) {
        if (parseResult.GetValue(ExtractionOptions.Profile) is string profileRaw && !string.IsNullOrWhiteSpace(profileRaw)) {
            // A number is the profile table's (config profiles), not an index into this config's own list.
            var profileTable = ((AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig))).AiStudioApiKeyEnvNames;
            int index = ApiKeyProfileResolver.ResolveIndexInStageList(profileRaw, profileTable, config.AiStudioApiKeyEnvNames);
            if (index < 0) {
                string wanted = int.TryParse(profileRaw.Trim(), out int number) ? ApiKeyProfileResolver.Resolve(number, profileTable) : profileRaw;
                return (wanted, false, $"Der Key '{wanted}' (--profile {profileRaw}) fehlt in AiStudioApiKeyEnvNames von LatexRefinementSessionConfig.json.");
            }
            config.AiStudioActiveApiProfile = index;
        }

        // Every step, because the PDF repair runs on step 3's settings and --step picks which one runs.
        if (parseResult.GetValue(ExtractionOptions.Model) is string model && !string.IsNullOrWhiteSpace(model)) {
            foreach (var step in new[] { config.Step1MergeAndTimestamp, config.Step2SpeechRefinement, config.Step3LastRefinement }) {
                (config.UseVertex ? step.Vertex : step.AiStudio).CurrentModel = model;
            }
        }

        if (parseResult.GetValue(ExtractionOptions.MaxOutputTokens) is int maxOutputTokens && maxOutputTokens > 0) {
            foreach (var step in new[] { config.Step1MergeAndTimestamp, config.Step2SpeechRefinement, config.Step3LastRefinement }) {
                (config.UseVertex ? step.Vertex : step.AiStudio).MaxOutputTokens = maxOutputTokens;
            }
        }

        if (parseResult.GetValue(ExtractionOptions.ThinkingLevel) is string thinkingLevel && !string.IsNullOrWhiteSpace(thinkingLevel)) {
            string normalized = thinkingLevel.Trim().ToUpperInvariant();
            foreach (var step in new[] { config.Step1MergeAndTimestamp, config.Step2SpeechRefinement, config.Step3LastRefinement }) {
                (config.UseVertex ? step.Vertex : step.AiStudio).ThinkingLevel = normalized;
            }
        }

        ThoughtOptions.Apply(config, parseResult);

        string envName = RefinementUiHelper.ResolveRefinementKeyEnvName(config);
        return (envName, config.UseVertex || GoogleAiClientBuilder.IsApiKeyPresent(envName), null);
    }

    /// <summary>
    /// The key's number in the profile table (config profiles), -1 if it has none. The refinement
    /// config's own index is not shown: in another order it named a different key's number.
    /// </summary>
    private static int TableProfile(string envName) =>
        ApiKeyProfileResolver.ResolveIndex(envName,
            ((AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig))).AiStudioApiKeyEnvNames, -1);

    /// <summary>The model the final step (and with it the PDF repair) would use - what --model sets.</summary>
    private static string StepModel(LatexRefinementSessionConfig config) =>
        (config.UseVertex ? config.Step3LastRefinement.Vertex : config.Step3LastRefinement.AiStudio).CurrentModel;

    /// <summary>
    /// Uses an explicit --audio, otherwise looks for the audio track the extraction writes beside
    /// the .tex. Step 1 works without it; the timestamps are simply less accurate.
    /// </summary>
    private static string? ResolveAudio(ParseResult parseResult, string tex) {
        string? explicitAudio = parseResult.GetValue(Audio);
        if (!string.IsNullOrWhiteSpace(explicitAudio)) {
            return File.Exists(explicitAudio) ? Path.GetFullPath(explicitAudio) : null;
        }

        string folder = Path.GetDirectoryName(Path.GetFullPath(tex)) ?? ".";
        foreach (string candidate in Directory.GetFiles(folder, "*_audio.aac")) {
            return candidate;
        }

        return null;
    }
}
