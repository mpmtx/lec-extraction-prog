using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Latex;
using LectureExtraction.Media;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] Post-processing pipeline that takes sequentially extracted LaTeX chunks and deterministically merges them into a single, cohesive document.
/// [Human] Der letzte Schritt in der Pipeline. Fügt die überlappenden LaTeX-Fragmente nahtlos zu einem kompilierbaren PDF zusammen.
/// </summary>
public partial class LatexRefinementSession {
    private readonly Client _client;
    private readonly LatexRefinementSessionConfig _config;
    private readonly string? _singleFilePathToProcess;
    private readonly string[]? _multipleFilesToProcess;
    private readonly IAutoExtractionConfig? _extractionConfig;
    private readonly int _overlapSeconds;
    private readonly string? _audioFilePath;
    private List<Part>? _preUploadedAudioAttachments;

    /// <summary>
    /// [AI Context] One constructor, taking a <see cref="RefinementOptions"/> that names which of the
    /// pipeline's three input modes the caller wants. The four telescoping constructors this
    /// replaces differed only in how many of these fields they set to null.
    /// [Human] Ein Konstruktor; was verarbeitet wird, beschreibt <see cref="RefinementOptions"/>.
    /// </summary>
    public LatexRefinementSession(Client client, RefinementOptions options) {
        _client = client;
        _config = options.Config;
        _singleFilePathToProcess = options.SingleFilePath;
        _multipleFilesToProcess = options.MultipleFilePaths;
        _extractionConfig = options.ExtractionConfig;
        _overlapSeconds = options.OverlapSeconds ?? options.ExtractionConfig?.OverlapSeconds ?? 180;
        _audioFilePath = options.AudioFilePath;
        _preUploadedAudioAttachments = options.PreUploadedAudioAttachments;
    }

    /// <summary>
    /// [AI Context] Entry point for the refinement pipeline. Validates dependencies and starts the execution if prerequisites are met.
    /// [Human] Startet die Refinement-Pipeline, prüft aber vorher, ob die Ziel-Ordner und Audio-Dateien überhaupt vorhanden sind.
    /// </summary>
    /// <returns>false if a step failed or the input is missing; true when every enabled step succeeded,
    /// or when refinement is switched off by configuration and deliberately did nothing.</returns>
    public async Task<bool> StartAsync() {
        if (!_config.Enabled) {
            Ui.Info("LaTeX Refinement ist in der Konfiguration deaktiviert. Überspringe die Ausführung.", "LaTeX Refinement");
            return true;
        }

        if ((_singleFilePathToProcess != null || _multipleFilesToProcess != null) && _extractionConfig != null) {
            int initialPartsCount = _multipleFilesToProcess != null
                ? _multipleFilesToProcess.Length
                : _singleFilePathToProcess != null
                    ? ResolvePartsCount([_singleFilePathToProcess])
                    : 1;
            bool requiresOffsetAndAudio = initialPartsCount > 1 && _audioFilePath != null;

            if (!_extractionConfig.GoIntoLatexRefinement || (requiresOffsetAndAudio && (!_extractionConfig.GenerateOffsetFiles || !_extractionConfig.GenerateAudioFile))) {
                Ui.Warn("LaTeX Refinement übersprungen.", "LaTeX Refinement");
                Ui.Detail("Grund: Die Voraussetzungen in AutoExtractionConfig sind nicht erfüllt.");
                return true;
            }

            if (_singleFilePathToProcess != null && !System.IO.File.Exists(_singleFilePathToProcess)) {
                Ui.Warn($"LaTeX Refinement übersprungen. Die Zieldatei fehlt: {_singleFilePathToProcess}", "LaTeX Refinement");
                return false;
            }

            if (_audioFilePath == null || !System.IO.File.Exists(_audioFilePath)) {
                Ui.Info($"Ausführung erfolgt ohne Audio-Datei (Pfad: {_audioFilePath ?? "null"}).", "LaTeX Refinement");
            }
        }

        Ui.Step("Starte LaTeX Refinement Pipeline");

        // [AI Context] Reset HasJustUploaded when starting the pipeline so that any background audio upload
        // or prior extraction steps don't suppress the initial 130-second token refill timer.
        AttachmentUploader.HasJustUploaded = false;

        return await ExecutePipelineAsync();
    }

    /// <summary>
    /// [AI Context] Orchestrates the 4-step pipeline: Merge, Speech Refinement, Final Format, and PDF Compilation.
    /// [Human] Steuert die einzelnen Schritte (Zusammenfügen, Sprach-Korrektur, Finale Formatierung und PDF-Erstellung).
    /// </summary>
    /// <returns>false if any enabled step failed - also the ones after which the pipeline carries on.</returns>
    private async Task<bool> ExecutePipelineAsync() {
        string[] currentFiles;
        string targetFolder;
        string baseName;

        if (_multipleFilesToProcess != null && _multipleFilesToProcess.Length > 0) {
            currentFiles = _multipleFilesToProcess;
            targetFolder = Path.GetDirectoryName(currentFiles[0]) ?? _config.TargetFolder;
            baseName = GetCleanBaseName(currentFiles[0]);
        }
        else if (_singleFilePathToProcess != null) {
            currentFiles = [_singleFilePathToProcess];
            targetFolder = Path.GetDirectoryName(_singleFilePathToProcess) ?? _config.TargetFolder;
            baseName = GetCleanBaseName(_singleFilePathToProcess);
        }
        else {
            string sourceFolder = _config.SourceFolder;
            if (!Directory.Exists(sourceFolder)) {
                Ui.Error("Ordner nicht gefunden. Bitte prüfe den SourceFolder in der Konfiguration.", "LaTeX Refinement");
                return false;
            }
            currentFiles = Directory.GetFiles(sourceFolder, "*.tex");
            if (currentFiles.Length == 0) return false;
            targetFolder = string.IsNullOrWhiteSpace(_config.TargetFolder) ? sourceFolder : _config.TargetFolder;
            baseName = "refined_output";
        }

        bool allStepsSucceeded = true;

        // Step 1: Merge and Timestamp Control
        int partsCount = ResolvePartsCount(currentFiles);
        if (_config.Step1MergeAndTimestamp.Enabled) {
            Ui.Step("LaTeX Refinement - Schritt 1: Merge & Zeitstempel-Abgleich");
            if (partsCount <= 1) {
                Ui.Info($"NumberOfParts = {partsCount} (<= 1). Ein Merger ist nicht erforderlich. Überspringe Schritt 1.");
            }
            else {
                string? step1Output = await MergeSegmentsAndAlignTimestampsAsync(currentFiles, _audioFilePath, baseName, targetFolder, partsCount);
                if (step1Output == null) {
                    Ui.Error("Schritt 1 (Merge) fehlgeschlagen. Breche Pipeline ab.", "LaTeX Refinement");
                    return false;
                }
                currentFiles = [step1Output];
            }
        }

        // Step 2: Speech Refinement
        if (_config.Step2SpeechRefinement.Enabled) {
            Ui.Step("LaTeX Refinement - Schritt 2: Textkorrektur & Grammatik-Polishing");
            string? step2Output = await RefineAgainstSpeechAsync(currentFiles[0], _audioFilePath, baseName, targetFolder);
            if (step2Output == null) {
                Ui.Error("Schritt 2 (Speech Refinement) fehlgeschlagen. Breche Pipeline ab.", "LaTeX Refinement");
                return false;
            }
            currentFiles = [step2Output];
        }

        // Step 3: Last Refinement
        if (_config.Step3LastRefinement.Enabled) {
            Ui.Step("LaTeX Refinement - Schritt 3: Endprüfung & Validierung");
            Ui.Info("Führe Probe-Kompilierung des aktuellen Dokuments aus...");
            bool alreadyCompiles = await CompilePdfAsync(currentFiles[0], baseName, targetFolder, "step3-precheck", allowRetryOnFailure: false);

            string compileLogPath = Path.Combine(targetFolder, "step3-precheck-compile-log.txt");
            string compileLog = System.IO.File.Exists(compileLogPath) ? await System.IO.File.ReadAllTextAsync(compileLogPath) : "";

            if (alreadyCompiles) {
                Ui.Info("Probe-Kompilierung erfolgreich! Keine Syntaxfehler vorhanden. Gebe diese Info an Schritt 3 weiter.");
            }
            else {
                Ui.Info("Probe-Kompilierung meldet Syntaxfehler. Gebe das Fehlerprotokoll an Schritt 3 weiter zur Korrektur.");
            }

            // [AI Context] Clean up temporary test-compile helper files (aux, log, out, toc, wrapper tex, precheck log)
            // while preserving the successfully generated PDF from the speech refinement stage.
            CleanupPrecheckFiles(targetFolder, currentFiles[0], "step3-precheck", alreadyCompiles);

            Ui.Info("Starte finalen Durchlauf für Schritt 3 (Last Refinement)...");
            var finalOutput = await ApplyFinalPolishAsync(currentFiles[0], baseName, targetFolder, alreadyCompiles, compileLog);
            if (finalOutput == null) {
                Ui.Error("Schritt 3 (Last Refinement) fehlgeschlagen.", "LaTeX Refinement");
                allStepsSucceeded = false;
            }
            else {
                currentFiles = [finalOutput];
            }
        }

        // Step 4: PDF Compilation
        // UseAntiGravityAgent only picks how a failed compile is repaired, not whether step 4 runs:
        // treating it as an enabler made every single-step run (--step 1, the menu's "only step N")
        // compile an intermediate file and start a paid repair loop on it.
        if (_config.PdfCompilation?.Enabled == true) {
            Ui.Step("LaTeX Refinement - Schritt 4: PDF Generierung & Validierung");
            if (!await CompilePdfAsync(currentFiles[0], baseName, targetFolder)) {
                allStepsSucceeded = false;
            }
        }

        if (allStepsSucceeded) {
            Ui.Success("LaTeX Refinement Pipeline erfolgreich abgeschlossen!", "LaTeX Refinement");
        }
        else {
            Ui.Warn("LaTeX Refinement Pipeline mit Fehlern abgeschlossen (siehe oben).", "LaTeX Refinement");
        }

        return allStepsSucceeded;
    }

    /// <summary>
    /// [AI Context] How many video parts the input was transcribed from - what step 1 tells the model
    /// and what decides whether a merge is needed at all. Several part files are counted directly. A
    /// single combined file is asked first, through its "% --- TEIL n" separators: the configured
    /// NumberOfParts is wrong for it whenever it is "auto" (resolving that needs the video's length,
    /// which is not known here), has changed since the extraction, or is absent because refinement
    /// runs standalone - and a combined file read as "1 part" silently skips the merge and timestamp
    /// alignment.
    /// [Human] Ermittelt, aus wie vielen Video-Teilen die Eingabe besteht - bevorzugt aus der Datei selbst.
    /// </summary>
    private int ResolvePartsCount(string[] inputFiles) {
        if (inputFiles.Length != 1) {
            return inputFiles.Length;
        }

        int fromDocument = System.IO.File.Exists(inputFiles[0])
            ? TexDocumentWriter.CountParts(System.IO.File.ReadAllText(inputFiles[0]))
            : 0;

        return fromDocument > 0 ? fromDocument : _extractionConfig?.NumberOfParts.FixedParts ?? 1;
    }
    /// <summary>
    /// [AI Context] Step 1: Merges overlapping LaTeX chunks. If an audio file is provided, its metadata is attached to align timestamps correctly.
    /// [Human] Schritt 1: Führt die einzelnen Video-Teile zusammen. Nutzt (falls vorhanden) die Audio-Spur, um kaputte Zeitstempel zu korrigieren.
    /// </summary>
    private async Task<string?> MergeSegmentsAndAlignTimestampsAsync(string[] inputFiles, string? audioFilePath, string baseName, string targetFolder, int partsCount) {
        if (inputFiles.Length == 0) return null;
        int overlapMin = _overlapSeconds / 60;

        string audioLengthStr = "unknown";
        string partTimestampsStr = "";

        bool audioExists = audioFilePath != null && System.IO.File.Exists(audioFilePath);

        List<Part> audioParts = [];
        if (audioExists) {
            double dur = await FfmpegToolkit.GetVideoDurationAsync(audioFilePath!);
            TimeSpan t = TimeSpan.FromSeconds(dur);
            audioLengthStr = $"{t.Hours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

            // Calculate expected timestamps for each part
            int overlapSec = _overlapSeconds;
            double segmentLength = (dur + (partsCount - 1) * overlapSec) / partsCount;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < partsCount; i++) {
                double start = i * (segmentLength - overlapSec);
                double end = start + segmentLength;
                if (end > dur) end = dur;

                TimeSpan tStart = TimeSpan.FromSeconds(start);
                TimeSpan tEnd = TimeSpan.FromSeconds(end);
                sb.AppendLine($"- Part {i + 1}: {tStart.Hours:D2}:{tStart.Minutes:D2}:{tStart.Seconds:D2} - {tEnd.Hours:D2}:{tEnd.Minutes:D2}:{tEnd.Seconds:D2}");
            }
            partTimestampsStr = sb.ToString();

            if (_config.Step1MergeAndTimestamp.AttachAudio) {
                if (_preUploadedAudioAttachments != null && _preUploadedAudioAttachments.Count > 0) {
                    Ui.Info("Verwende parallel im Hintergrund hochgeladene Audio-Datei.", "Step 1");
                    audioParts.AddRange(_preUploadedAudioAttachments);
                    AttachmentUploader.HasJustUploaded = false;
                }
                else {
                    var handler = new AttachmentUploader(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "",
                        fileActivationDelaySeconds: _config.Step1MergeAndTimestamp.RateLimitDelaySeconds);
                    var (success, attached) = await handler.UploadFilesAsync([audioFilePath!]);
                    if (success) {
                        audioParts.AddRange(attached);
                        Ui.Info($"Audio-Datei erfolgreich verarbeitet: {audioFilePath}", "Step 1");
                        _preUploadedAudioAttachments = attached;
                    }
                }
            }
        }

        bool audioAttached = audioExists && _config.Step1MergeAndTimestamp.AttachAudio;
        string outputFileName = $"step2-{baseName}-offset-merged.tex";
        string? result;

        if (audioAttached) {
            var round1Parts = new List<Part>();
            string round1Prompt = $"Here is the combined .tex file to process. It holds {partsCount} parts of one lecture video, each starting at its `% --- TEIL n ---` marker; consecutive parts overlap by {overlapMin} minutes. " +
                                  (string.IsNullOrEmpty(partTimestampsStr) ? "" : $"\nExpected total duration timestamps for each part:\n{partTimestampsStr}\n(Note: These timestamps represent the total chronological span of each video part, NOT the span of a single `speech` block!)\n\n") +
                                  "Please acknowledge you have read it. I will provide the audio file and final merge instructions in the next round.";
            round1Parts.Add(new Part { Text = round1Prompt });
            foreach (var file in inputFiles) {
                Ui.Info($"Lese Eingabedatei für Merge: {Path.GetFileName(file)}", "Step 1");
                string content = TexDocumentWriter.StripHeadersForModel(await System.IO.File.ReadAllTextAsync(file));
                round1Parts.Add(new Part { Text = $"<input_file name=\"{Path.GetFileName(file)}\">\n{content}\n</input_file>" });
            }

            List<Content> history = [];
            history.Add(new Content { Role = "user", Parts = round1Parts });
            history.Add(new Content { Role = "model", Parts = [new Part { Text = "Understood. I have read the .tex files and noted the expected timestamps. I am ready for the audio file and the merge instructions." }] });

            var round2Parts = new List<Part>();
            round2Parts.AddRange(audioParts);
            string round2Prompt = $"Here is the generated audio file. The actual audio length is exactly {audioLengthStr} (00:00:00 - {audioLengthStr}).\n\n" +
                                  $"The timestamps of every part are already shifted to lecture time. Use the audio to align the timestamps around each seam between two parts, and to correct a timestamp elsewhere only where it is clearly broken; leave all other timestamps as they are. The last `speech` block must end at {audioLengthStr}. Please perform the merge according to the system instructions.";
            round2Parts.Add(new Part { Text = round2Prompt });

            history.Add(new Content { Role = "user", Parts = round2Parts });

            Ui.Info("Verwende Multi-Turn-Struktur für Schritt 1 (Simulation von Audio + Textsegmenten).", "Step 1");
            result = await RunRefinementStepAsync(_config.Step1MergeAndTimestamp, TexDocumentWriter.MergeStep, "merge", inputFiles[0], history, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep1);
        }
        else {
            var parts = new List<Part>();
            string promptText = "Here is the combined file with all the offset parts together. " +
                                $"It holds {partsCount} parts of one lecture video, each starting at its `% --- TEIL n ---` marker; consecutive parts overlap by {overlapMin} minutes. " +
                                $"The actual audio/lecture length is roughly {audioLengthStr} (00:00:00 - {audioLengthStr}).\n\n" +
                                (string.IsNullOrEmpty(partTimestampsStr) ? "" : $"Expected total duration timestamps for each part:\n{partTimestampsStr}\n(Note: These timestamps represent the total chronological span of each video part, NOT the span of a single `speech` block!)\n\n") +
                                "Important: Since no audio file is attached, the timestamps in subsequent parts have already been pre-adjusted to global lecture time. Please eliminate redundant overlapping blocks at the part seams and only fix timestamps that look completely out of order or severely broken across boundaries. Otherwise, trust and preserve the existing pre-calibrated timestamps.";
            parts.Add(new Part { Text = promptText });
            foreach (var file in inputFiles) {
                Ui.Info($"Lese Eingabedatei für Merge: {Path.GetFileName(file)}", "Step 1");
                string content = TexDocumentWriter.StripHeadersForModel(await System.IO.File.ReadAllTextAsync(file));
                parts.Add(new Part { Text = $"<input_file name=\"{Path.GetFileName(file)}\">\n{content}\n</input_file>" });
            }
            AttachmentUploader.HasJustUploaded = false;
            result = await RunRefinementStepAsync(_config.Step1MergeAndTimestamp, TexDocumentWriter.MergeStep, "merge", inputFiles[0], parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep1);
        }

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    private async Task<string?> RefineAgainstSpeechAsync(string inputFile, string? audioFilePath, string baseName, string targetFolder) {
        bool audioAttached = _config.Step2SpeechRefinement.AttachAudio && audioFilePath != null && System.IO.File.Exists(audioFilePath);
        var audioParts = new List<Part>();

        if (audioAttached) {
            if (_preUploadedAudioAttachments != null && _preUploadedAudioAttachments.Count > 0) {
                Ui.Info("Verwende parallel im Hintergrund hochgeladene Audio-Datei.", "Step 2");
                audioParts.AddRange(_preUploadedAudioAttachments);
                AttachmentUploader.HasJustUploaded = false;
            }
            else {
                var handler = new AttachmentUploader(_client, targetFolder, [targetFolder], !_config.UseVertex, _config.UseVertex ? _config.VertexGcsBucketName : "",
                    fileActivationDelaySeconds: _config.Step2SpeechRefinement.RateLimitDelaySeconds);
                var (success, attached) = await handler.UploadFilesAsync([audioFilePath!]);
                if (success) {
                    audioParts.AddRange(attached);
                    Ui.Info($"Audio-Datei erfolgreich verarbeitet: {audioFilePath}", "Step 2");
                    _preUploadedAudioAttachments = attached;
                }
                else {
                    audioAttached = false;
                }
            }
        }

        Ui.Info($"Lese Eingabedatei für Textkorrektur: {Path.GetFileName(inputFile)}", "Step 2");
        string content = TexDocumentWriter.StripHeadersForModel(await System.IO.File.ReadAllTextAsync(inputFile));
        string outputFileName = $"step3-{baseName}-offset-speech_refined.tex";
        string? result;

        if (audioAttached && audioParts.Count > 0) {
            var round1Parts = new List<Part> {
                new() { Text = "Here is the current merged LaTeX document (.tex file) to process. Please read and internalize the entire document structure, including all math containers, equations, and `speech` blocks. Please acknowledge that you have read it. I will provide the audio file and speech refinement instructions in the next round." },
                new() { Text = $"<input_tex name=\"{Path.GetFileName(inputFile)}\">\n{content}\n</input_tex>" }
            };

            List<Content> history = [
                new() { Role = "user", Parts = round1Parts },
                new() { Role = "model", Parts = [new Part { Text = "Understood. I have read the complete LaTeX document and internalized all mathematical structures, formulas, timestamps, and `speech` environments. I will refine the spoken text inside the `speech` environments and leave everything else as it is, apart from the edits the system instructions allow. I am ready for the audio file." }] }
            ];

            var round2Parts = new List<Part>();
            round2Parts.AddRange(audioParts);
            round2Parts.Add(new Part { Text = "Here is the lecture audio file. Please listen to the audio carefully and refine the text inside the `speech` environments to fix any transcription, word choice, or grammatical errors according to the system instructions. Outside `speech`, change only what the system instructions allow. Output only the refined LaTeX code." });
            history.Add(new Content { Role = "user", Parts = round2Parts });

            Ui.Info("Verwende Multi-Turn-Struktur für Schritt 2 (Simulation von Text-Dokument + Audio-Refinement).", "Step 2");
            AttachmentUploader.HasJustUploaded = false;
            result = await RunRefinementStepAsync(_config.Step2SpeechRefinement, TexDocumentWriter.SpeechStep, "speech", inputFile, history, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep2, restoreNonSpeech: true);
        }
        else {
            var parts = new List<Part> {
                new() { Text = "Please refine the text inside the `speech` environments according to the system instructions. Outside `speech`, change only what the system instructions allow." },
                new() { Text = $"<input_tex>\n{content}\n</input_tex>" }
            };
            AttachmentUploader.HasJustUploaded = false;
            result = await RunRefinementStepAsync(_config.Step2SpeechRefinement, TexDocumentWriter.SpeechStep, "speech", inputFile, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep2, restoreNonSpeech: true);
        }

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    private async Task<string?> ApplyFinalPolishAsync(string inputFile, string baseName, string targetFolder, bool alreadyCompiles, string compilerFeedbackLog) {
        List<Part> parts = [new() { Text = "Perform the final refinement and formatting pass on this document according to the system instructions." }];
        if (alreadyCompiles) {
            parts.Add(new Part { Text = "<compiler_status>\nThe input LaTeX document ALREADY COMPILES successfully without any LaTeX errors! Please preserve its valid syntax and structure while performing any final textual/typographical refinements according to the system instructions.\n</compiler_status>" });
        }
        else if (!string.IsNullOrWhiteSpace(compilerFeedbackLog)) {
            parts.Add(new Part { Text = $"<compiler_error_feedback>\nWhen attempting to compile the input LaTeX document with pdflatex, the following errors and log messages were produced:\n\n{compilerFeedbackLog}\n\nPlease analyze and fix these LaTeX syntax/compilation errors during this final refinement pass.\n</compiler_error_feedback>" });
        }

        Ui.Info($"Lese Eingabedatei für Formatierung: {Path.GetFileName(inputFile)}", "Step 3");
        string content = TexDocumentWriter.StripHeadersForModel(await System.IO.File.ReadAllTextAsync(inputFile));
        parts.Add(new Part { Text = $"<input_tex>\n{content}\n</input_tex>" });

        string outputFileName = $"step4-{baseName}-offset-final.tex";
        AttachmentUploader.HasJustUploaded = false;
        var result = await RunRefinementStepAsync(_config.Step3LastRefinement, TexDocumentWriter.FinalStep, "final", inputFile, parts, targetFolder, outputFileName, ContextCacheStateManager.StateFileLatexStep3);

        if (_config.UseVertex) {
            await CleanupBucketAsync();
        }

        return result;
    }

    private async Task<string?> RunRefinementStepAsync(RefinementStepConfig stepConfig, string stepLabel, string thoughtStageKey, string inputFile, List<Part> userPromptParts, string targetOutputFolder, string outputFileName, string cacheStateFileName, bool restoreNonSpeech = false) {
        var finalPromptParts = new List<Part>(userPromptParts);
        var history = new List<Content> { new() { Role = "user", Parts = finalPromptParts } };
        return await RunRefinementStepAsync(stepConfig, stepLabel, thoughtStageKey, inputFile, history, targetOutputFolder, outputFileName, cacheStateFileName, restoreNonSpeech);
    }

    /// <param name="stepLabel">The step name heading this step's header layer (a <see cref="TexDocumentWriter"/> constant).</param>
    /// <param name="thoughtStageKey">The short stage name its thoughts file carries (<c>merge</c>, <c>speech</c>, <c>final</c>), what <c>lecx thoughts --stage</c> matches.</param>
    /// <param name="inputFile">The file the step works on: its header layers and per-part blocks are carried into the output.</param>
    /// <param name="restoreNonSpeech">Step 2 only: everything outside the speech blocks is taken from <paramref name="inputFile"/> (<see cref="NonSpeechGuard"/>).</param>
    private async Task<string?> RunRefinementStepAsync(RefinementStepConfig stepConfig, string stepLabel, string thoughtStageKey, string inputFile, List<Content> history, string targetOutputFolder, string outputFileName, string cacheStateFileName, bool restoreNonSpeech = false) {
        BackendParameters backendParams = _config.UseVertex ? stepConfig.Vertex : stepConfig.AiStudio;

        string systemInstructionText = await ResolveSystemInstructionTextAsync(stepConfig);
        string? cacheName = await EnsureContextCacheAsync(backendParams, systemInstructionText, outputFileName, cacheStateFileName);
        var requestConfig = BuildStepRequestConfig(backendParams, cacheName, systemInstructionText, stepConfig.ThoughtSummaries?.Enabled == true);

        // The step's existing log is the prompt log written just below; the thoughts file sits beside
        // the output under the name the step intends, even if the output itself later gets "-copyN".
        var thoughts = new ThoughtLog(stepConfig.ThoughtSummaries, thoughtStageKey, stepLabel, backendParams.CurrentModel,
            Path.Combine(targetOutputFolder, outputFileName), Path.Combine(targetOutputFolder, $"{outputFileName}-prompt-log.md"));

        var lastUserMsg = history.LastOrDefault(c => c.Role == "user");
        if (lastUserMsg != null && lastUserMsg.Parts != null) {
            lastUserMsg.Parts.Add(new Part { Text = "\n\nCRITICAL INSTRUCTION: When you have completely finished writing your response and there is nothing left to output, you MUST append the exact text '% [SYSTEM] Refinement complete' on a new line at the very end of your response. This is mandatory for the system to know you are done." });
        }

        await DumpPromptLogAsync(history, systemInstructionText, targetOutputFolder, outputFileName);
        var (expectedSpokenClean, expectedMathStroke) = ComputeExpectedStructuralCounts(history);

        int totalInputLength = CountInputLatexLength(history);

        var (fullResponseText, usage, usable) =
            await StreamAndCollectAsync(stepConfig, backendParams, history, requestConfig, outputFileName, thoughts);

        // IMPORTANT: a step whose stream failed for good (or was cancelled, or kept answering blank) holds
        // only the text before the failure. On 2026-10-16 such a fragment of step 2 (43 of 68 speech blocks,
        // no completion marker) passed the 60 % integrity check below and step 3 polished it. It is kept as a
        // truncated file to look at, never as the step's result.
        // [Human] Bricht ein Schritt mitten in der Antwort ab, ist der Text unvollständig: er wird nur als
        // abgeschnittene Datei gesichert, nicht als Ergebnis weitergegeben.
        if (!usable && !string.IsNullOrEmpty(fullResponseText)) {
            Ui.Error($"Antwort unvollständig: die Anfrage ist endgültig fehlgeschlagen, bevor der Abschlussmarker kam ({fullResponseText.Length:N0} Zeichen). Der Schritt gilt als fehlgeschlagen.", "Refinement");
            await SaveTruncatedResponseAsync(targetOutputFolder, outputFileName, fullResponseText);
            return null;
        }

        if (!string.IsNullOrEmpty(fullResponseText)) {
            if (expectedSpokenClean > 0 || expectedMathStroke > 0) {
                int actualSpokenClean = SpokenCleanRegex().Count(fullResponseText);
                int actualMathStroke = MathStrokeRegex().Count(fullResponseText);

                int minExpectedSpoken = (int)(expectedSpokenClean * 0.6);
                int minExpectedMath = (int)(expectedMathStroke * 0.6);

                if (actualSpokenClean < minExpectedSpoken || actualMathStroke < minExpectedMath) {
                    Ui.Error($"SILENT TRUNCATION DETECTED! Erwartet: ~{expectedSpokenClean} speech / ~{expectedMathStroke} content, Erhalten: {actualSpokenClean} speech / {actualMathStroke} content.", "Refinement");
                    await SaveTruncatedResponseAsync(targetOutputFolder, outputFileName, fullResponseText);
                    return null;
                }
                else {
                    Ui.Detail($"Structural Integrity Verified: {actualSpokenClean}/{expectedSpokenClean} speech, {actualMathStroke}/{expectedMathStroke} content.", "Refinement");
                }
            }
            else if (totalInputLength > 1000 && fullResponseText.Length < totalInputLength * 0.25) {
                Ui.Error($"SILENT TRUNCATION DETECTED! LaTeX-Eingabe: ~{totalInputLength:N0} Zeichen, Erhalten: nur {fullResponseText.Length:N0} Zeichen.", "Refinement");
                await SaveTruncatedResponseAsync(targetOutputFolder, outputFileName, fullResponseText);
                return null;
            }
        }

        if (!string.IsNullOrEmpty(fullResponseText)) {
            if (!Directory.Exists(targetOutputFolder)) Directory.CreateDirectory(targetOutputFolder);
            string outPath = Path.Combine(targetOutputFolder, outputFileName);

            if (System.IO.File.Exists(outPath)) {
                string fileNameWithoutExt = Path.GetFileNameWithoutExtension(outputFileName);
                string ext = Path.GetExtension(outputFileName);
                int copyIndex = 1;
                while (System.IO.File.Exists(outPath)) {
                    outPath = Path.Combine(targetOutputFolder, $"{fileNameWithoutExt}-copy-{copyIndex}{ext}");
                    copyIndex++;
                }
                outputFileName = Path.GetFileName(outPath);
            }

            // The header is the C#'s, not the model's: this step's layer goes on top of the layers the
            // input carried, and any layers the model echoed at the top of its answer are dropped.
            // Each part's block goes back under every separator the model kept.
            string inputText = System.IO.File.Exists(inputFile) ? await System.IO.File.ReadAllTextAsync(inputFile) : "";
            var (inheritedLayers, _) = TexDocumentWriter.SplitHeaderLayers(inputText);
            var (_, body) = TexDocumentWriter.SplitHeaderLayers(LatexResponseCleaner.CleanLatexResponse(fullResponseText));
            body = TexDocumentWriter.RestorePartHeaders(body, TexDocumentWriter.ExtractPartHeaders(inputText));

            // IMPORTANT: step 2 re-emits the whole document and makes copy slips outside `speech` that no
            // prompt wording stops (2026-11-07: "(R1) --" retyped as "(V1) --" in a tikzpicture). Everything
            // outside the speech blocks therefore comes back from the input, apart from the flag lines and
            // student-quote splits the prompt allows. The input body goes through the same end-tag repair as
            // the answer did, so restoring never brings back what the cleaner fixed.
            // [Human] Schritt 2 darf nur speech-Blöcke ändern: alles andere wird aus der Eingabe übernommen.
            List<string>? headerNotes = null;
            if (restoreNonSpeech) {
                var (_, inputBody) = TexDocumentWriter.SplitHeaderLayers(inputText);
                int lineOffset = LinesBeforeBody(inputText, inputBody);
                var guard = NonSpeechGuard.Apply(inputBody.FixMalformedEndTags(), body);
                NonSpeechGuard.Report(guard, lineOffset, "Step 2");
                body = guard.Body;
                headerNotes = [NonSpeechGuard.HeaderNote(guard, lineOffset)];
            }

            string stepHeader = TexDocumentWriter.BuildRefinementHeader(
                stepLabel, outputFileName, Path.GetFileName(inputFile), usage, backendParams.CurrentModel, backendParams.Generation,
                Path.GetFileName(thoughts.FilePath), headerNotes);

            await System.IO.File.WriteAllTextAsync(outPath, TexDocumentWriter.StackLayers(stepHeader, inheritedLayers, body));
            Ui.Success($"Ergebnis gespeichert unter: {outPath}", "Refinement");

            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

            return outPath;
        }
        else {
            Ui.Error("Beim Refinement ist ein Fehler aufgetreten oder der Vorgang wurde abgebrochen.", "Refinement");
            return null;
        }
    }
    private Task CleanupBucketAsync() => GcsWorkspace.PurgeAsync(_config.VertexGcsBucketName);

    /// <summary>
    /// [AI Context] Lines of <paramref name="fileText"/> above <paramref name="body"/>, the suffix
    /// <see cref="TexDocumentWriter.SplitHeaderLayers"/> returned: added to a body line number it gives
    /// the line in the file.
    /// </summary>
    private static int LinesBeforeBody(string fileText, string body) {
        string normalized = fileText.Replace("\r\n", "\n");
        int bodyStart = Math.Max(0, normalized.Length - body.Length);
        return normalized.AsSpan(0, bodyStart).Count('\n');
    }

    /// <summary>
    /// [AI Context] Characters of LaTeX the step was given: the &lt;input_file&gt; / &lt;input_tex&gt;
    /// payloads only. Instructions and compiler logs around them are not something the answer
    /// reproduces, so counting them made the length check fire on complete answers.
    /// </summary>
    private static int CountInputLatexLength(List<Content> history) {
        int length = 0;
        foreach (var part in history.Where(c => c.Role == "user").SelectMany(c => c.Parts ?? [])) {
            if (string.IsNullOrEmpty(part.Text)) continue;
            foreach (System.Text.RegularExpressions.Match match in InputPayloadRegex().Matches(part.Text)) {
                length += match.Groups[1].Length;
            }
        }
        return length;
    }

    /// <summary>
    /// [AI Context] A response rejected as truncated was still paid for; keep it next to the would-be
    /// output so it can be inspected or salvaged instead of being thrown away.
    /// [Human] Speichert eine als abgeschnitten erkannte Antwort, statt sie zu verwerfen.
    /// </summary>
    private static async Task SaveTruncatedResponseAsync(string targetOutputFolder, string outputFileName, string responseText) {
        try {
            Directory.CreateDirectory(targetOutputFolder);
            string path = Path.Combine(targetOutputFolder, Path.GetFileNameWithoutExtension(outputFileName) + "-truncated.tex");
            await System.IO.File.WriteAllTextAsync(path, LatexResponseCleaner.CleanLatexResponse(responseText));
            Ui.Info($"Abgeschnittene Antwort gesichert unter: {path}", "Refinement");
        }
        catch (Exception ex) {
            Ui.Warn($"Abgeschnittene Antwort konnte nicht gesichert werden: {ex.Describe()}", "Refinement");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<input_(?:file|tex)\b[^>]*>(.*?)</input_(?:file|tex)>", System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex InputPayloadRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"-copy-?\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex TrailingCopySuffixRegex();

    /// <summary>
    /// [AI Context] Name of the finished PDF the pipeline copies out for the reader. It is the lecture
    /// folder's name whenever the base name starts with it: a base name derived from a re-run's files
    /// keeps tails like "-all-offset-copy-1" (GetCleanBaseName strips only a trailing "-all"), and on
    /// 2026-10-15 the deliverable came out as "<lecture>-all-offset-copy-1.pdf". The intermediate step
    /// files keep using the base name, so their names do not change.
    /// [Human] Dateiname des fertigen PDFs: der Name des Vorlesungsordners.
    /// </summary>
    public static string FinalPdfFileName(string targetFolder, string baseName) {
        string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(targetFolder));
        bool isLectureFolder = folderName.Length > 0 && baseName.StartsWith(folderName, StringComparison.OrdinalIgnoreCase);
        return (isLectureFolder ? folderName : baseName) + ".pdf";
    }

    public static string GetCleanBaseName(string filePath) {
        string name = Path.GetFileNameWithoutExtension(filePath);
        if (name.Length > 6 && name.StartsWith("step", StringComparison.OrdinalIgnoreCase) && char.IsDigit(name[4]) && name[5] == '-') {
            name = name[6..];
        }
        string[] suffixes = ["-merged", "-speech_refined", "-final", "-last-fix", "-last-fix-standalone", "-final-attempt", "-final-main", "-last_try-main", "-last_try", "-offset", "-all"];
        bool stripped;
        do {
            stripped = false;
            string withoutCopy = TrailingCopySuffixRegex().Replace(name, "");
            if (withoutCopy.Length != name.Length) {
                name = withoutCopy;
                stripped = true;
            }
            foreach (var suffix in suffixes) {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                    name = name[..^suffix.Length];
                    stripped = true;
                }
            }
        } while (stripped);

        int partIdx = name.IndexOf("-part", StringComparison.OrdinalIgnoreCase);
        if (partIdx >= 0) {
            name = name[..partIdx];
        }
        return ExtractionHelpers.StripVideoExtensions(name);
    }

    // `speech` and `content` since the environment rename; the old names still count, for older output.
    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{(?:speech|spoken-clean)\}")]
    private static partial System.Text.RegularExpressions.Regex SpokenCleanRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{(?:content|math-stroke)\}")]
    private static partial System.Text.RegularExpressions.Regex MathStrokeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"\\begin\{document\}|\\end\{document\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DocumentTagsRegex();
}
