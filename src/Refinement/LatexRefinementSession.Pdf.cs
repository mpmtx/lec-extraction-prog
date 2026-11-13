using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.Extraction.Model;
using LectureExtraction.GoogleAi;
using LectureExtraction.Latex;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] The PDF half of the refinement session: compiling the merged LaTeX with local
/// pdflatex, formatting its log, cleaning up the wrapper/helper files it leaves behind, and the two
/// repair paths taken when compilation fails - one that asks the model to fix the LaTeX, one that
/// delegates to an external Antigravity agent.
///
/// <para>Split out of LatexRefinementSession (Phase 11), which was 1461 lines. This is a coherent,
/// self-contained concern: it runs after the AI refinement steps are finished and touches none of
/// their logic, so work on the refinement steps never needs to read it and vice versa. Kept as a
/// `partial` rather than a standalone type because these methods read the session's configuration
/// and client fields directly; turning that into constructor injection is a separate design
/// question from getting the file down to a readable size.</para>
/// [Human] Die PDF-Hälfte der Refinement-Session: Kompilierung, Log-Aufbereitung, Aufräumen und die
/// beiden Reparaturwege bei Fehlern. Aus der 1461-Zeilen-Datei herausgelöst.
/// </summary>
public partial class LatexRefinementSession {

    /// <summary>
    /// [AI Context] Compiles the final merged LaTeX file into a PDF using local pdflatex. Uses a wrapper file to inject the preamble.
    /// [Human] Baut das fertige LaTeX-Skript mithilfe einer Preamble (Design-Vorlage) zu einem PDF zusammen.
    /// </summary>
    private async Task<bool> CompilePdfAsync(string finalTexFile, string baseName, string targetFolder, string stepPrefix = "step4", bool allowRetryOnFailure = true, bool autoFixApplied = false) {
        if (!System.IO.File.Exists(finalTexFile)) {
            Ui.Error($"Kann PDF nicht generieren: {finalTexFile} existiert nicht.", "LaTeX Refinement");
            return false;
        }

        LectureLanguage language = LectureLanguageDetector.Detect(await System.IO.File.ReadAllTextAsync(finalTexFile));
        string preamblePath = _config.PdfCompilation?.ResolvePreamblePath(language) ?? "pdf-preamble.tex";
        if (!System.IO.File.Exists(preamblePath)) {
            Ui.Warn($"Preamble-Datei ({preamblePath}) nicht gefunden. Überspringe PDF-Generierung.", "LaTeX Refinement");
            return false;
        }
        Ui.Info($"Preamble: {preamblePath} (Sprache: {language})");

        try {
            if (System.IO.File.Exists(finalTexFile)) {
                string bodyContent = await System.IO.File.ReadAllTextAsync(finalTexFile);
                if (bodyContent.Contains("\\begin{document}", StringComparison.OrdinalIgnoreCase) || bodyContent.Contains("\\end{document}", StringComparison.OrdinalIgnoreCase)) {
                    int beginDocIdx = bodyContent.IndexOf("\\begin{document}", StringComparison.OrdinalIgnoreCase);
                    int endDocIdx = bodyContent.IndexOf("\\end{document}", StringComparison.OrdinalIgnoreCase);
                    if (beginDocIdx >= 0 && endDocIdx > beginDocIdx) {
                        beginDocIdx += "\\begin{document}".Length;
                        bodyContent = bodyContent[beginDocIdx..endDocIdx].Trim();
                    }
                    else if (beginDocIdx >= 0 && endDocIdx < 0) {
                        beginDocIdx += "\\begin{document}".Length;
                        bodyContent = bodyContent[beginDocIdx..].Trim();
                    }
                    else if (endDocIdx >= 0 && beginDocIdx < 0) {
                        bodyContent = bodyContent[..endDocIdx].Trim();
                    }
                    bodyContent = DocumentTagsRegex().Replace(bodyContent, "").Trim();
                    await System.IO.File.WriteAllTextAsync(finalTexFile, bodyContent);
                    Ui.Info($"Verbleibende \\begin{{document}} / \\end{{document}} Tags aus {Path.GetFileName(finalTexFile)} entfernt.");
                }
            }

            string preambleText = await System.IO.File.ReadAllTextAsync(preamblePath);
            string finalFileName = Path.GetFileName(finalTexFile);

            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperFileName = $"{inputBaseName}-main.tex";
            string wrapperPath = Path.Combine(targetFolder, wrapperFileName);

            string wrapperContent = preambleText + "\n\\begin{document}\n\n" +
                                    $"\\input{{{finalFileName}}}\n\n" +
                                    "\\end{document}\n";

            await System.IO.File.WriteAllTextAsync(wrapperPath, wrapperContent);
            Ui.Info($"Wrapper-Datei erstellt: {wrapperPath}");

            var (success, log) = await LatexToolkit.CompilePdfAsync(wrapperPath);

            string logContent = FormatLatexLog(log, success);
            string logPath = Path.Combine(targetFolder, $"{stepPrefix}-compile-log.txt");
            await System.IO.File.WriteAllTextAsync(logPath, logContent);

            if (success) {
                Ui.Success($"PDF erfolgreich erstellt im Zielordner: {targetFolder}");

                string compiledPdfPath = Path.ChangeExtension(wrapperPath, ".pdf");
                PublishPdf(compiledPdfPath, Path.Combine(targetFolder, inputBaseName + ".pdf"), targetFolder, baseName, isFinal: stepPrefix == "step4");

                CleanupHelperFiles(targetFolder, finalTexFile, true);

                if (logContent.Contains("⚠️ WARNING:")) {
                    Ui.Warn($"Es gab LaTeX-Warnungen während der Kompilation. Details in: {stepPrefix}-compile-log.txt");
                }
                return true;
            }
            else {
                Ui.Error($"Fehler bei der PDF-Generierung. Protokoll gespeichert in: {logPath}");

                // The backup holds the text as it was before any repair; after our own fixes the
                // original is already saved, and a second backup would only store the fixed text.
                if (allowRetryOnFailure && !autoFixApplied) {
                    BackupBeforeRepair(targetFolder, inputBaseName, finalTexFile, Path.ChangeExtension(wrapperPath, ".pdf"));
                }

                CleanupHelperFiles(targetFolder, finalTexFile, false);
                if (allowRetryOnFailure) {
                    // Our own rule-based fixes first (HIGHLY EXPERIMENTAL, opt-in): free, instant, and aimed
                    // at the error classes the AI repair kept failing on. Only if they change nothing, or the
                    // result still fails, does the AI repair below get its turn.
                    if (!autoFixApplied && _config.PdfCompilation?.ExperimentalLatexAutoFix == true && await TryApplyAutoFixesAsync(finalTexFile, inputBaseName)) {
                        Ui.Info("[EXPERIMENTELL] Kompiliere erneut mit den automatischen LaTeX-Korrekturen...", "LaTeX Auto-Fix");
                        return await CompilePdfAsync(finalTexFile, baseName, targetFolder, stepPrefix, allowRetryOnFailure: true, autoFixApplied: true);
                    }

                    string originalBodyTex = await System.IO.File.ReadAllTextAsync(finalTexFile);

                    // The AI repair gets our scanner's findings with line numbers next to the compile log,
                    // since pdflatex reports only its first fatal error, often far from the cause.
                    string logWithFindings = AppendStructuralFindings(logContent, originalBodyTex);

                    if (_config.PdfCompilation?.UseAntiGravityAgent == true) {
                        Ui.Warn("PDF-Kompilierung fehlgeschlagen. Starte sofort interaktive Reparatur über AntiGravity...", "AntiGravity Agent Mode");
                        return await RunExternalAgentRepairLoopAsync(finalTexFile, baseName, targetFolder, preambleText, logWithFindings);
                    }

                    int maxRounds = _config.PdfCompilation?.MaxFixRounds ?? 3;
                    if (maxRounds <= 0) return false;

                    string currentBodyTex = originalBodyTex;
                    string currentLog = logWithFindings;
                    bool anyRoundSucceeded = false;

                    for (int round = 1; round <= maxRounds; round++) {
                        Ui.Step($"Starte Reparatur-Runde {round} von {maxRounds}...", "AI PDF Fix Loop");

                        var (roundSuccess, candidateBody, candidateLog) = await TryRepairFailedPdfBuildAsync(preambleText, originalBodyTex, currentBodyTex, currentLog, baseName, targetFolder, round);

                        if (roundSuccess) {
                            anyRoundSucceeded = true;
                            break;
                        }

                        // A rejected or empty answer leaves the input as it was, so the next round retries it.
                        if (candidateBody != null) {
                            currentBodyTex = candidateBody;
                            currentLog = candidateLog != null ? AppendStructuralFindings(candidateLog, candidateBody) : currentLog;
                        }
                    }

                    if (anyRoundSucceeded) {
                        return true;
                    }

                    return false;
                }
                return false;
            }
        }
        catch (Exception ex) {
            Ui.Error($"Unerwarteter Fehler bei der PDF-Generierung: {ex.Describe()}", "LaTeX Refinement");
            return false;
        }
    }

    /// <summary>
    /// [AI Context] HIGHLY EXPERIMENTAL. Runs LatexAutoFixer over the .tex in place and reports each
    /// change with an [EXPERIMENTELL] tag and where the original is. Returns true when something changed,
    /// i.e. a recompile is worth trying. Called after BackupBeforeRepair, so -orig.tex holds the input.
    /// [Human] HOCHGRADIG EXPERIMENTELL: wendet die eigenen LaTeX-Korrekturen an und meldet jede Änderung.
    /// </summary>
    private static async Task<bool> TryApplyAutoFixesAsync(string texFile, string inputBaseName) {
        string tex = await System.IO.File.ReadAllTextAsync(texFile);
        var result = LatexAutoFixer.Apply(tex);
        if (!result.Changed) {
            Ui.Info("[EXPERIMENTELL] Keine automatisch behebbaren LaTeX-Fehler gefunden.", "LaTeX Auto-Fix");
            return false;
        }

        await System.IO.File.WriteAllTextAsync(texFile, result.Text);
        Ui.Warn($"[EXPERIMENTELL] {result.Changes.Count} automatische Korrektur(en) in {Path.GetFileName(texFile)} - das Original liegt als {inputBaseName}-orig.tex daneben:", "LaTeX Auto-Fix");
        foreach (string change in result.Changes) {
            Ui.Detail(change);
        }
        return true;
    }

    /// <summary>
    /// [AI Context] Appends LatexDiagnostics' findings (line numbers for unclosed environments, surplus
    /// braces, unclosed inline math) to a compile log before it goes to an AI repair, and prints them.
    /// Read-only, so it runs whether or not the experimental auto-fix is on.
    /// [Human] Hängt die Strukturbefunde mit Zeilennummern an das Compile-Log für die KI-Reparatur an.
    /// </summary>
    private static string AppendStructuralFindings(string compileLog, string bodyTex) {
        var findings = LatexDiagnostics.Analyze(bodyTex);
        if (findings.Count == 0) return compileLog;

        Ui.Info($"{findings.Count} Strukturbefund(e) gehen mit an die KI-Reparatur:", "LaTeX Diagnose");
        foreach (string finding in findings) {
            Ui.Detail(finding);
        }
        return compileLog + LatexDiagnostics.FormatForPrompt(findings);
    }

    /// <summary>
    /// [AI Context] Saves the document as it was before a repair run touches it: the .tex and the
    /// partial PDF pdflatex left behind. The first backup is <c>-orig</c>; a later run with a
    /// different text gets <c>-orig2</c>, <c>-orig3</c>, ... so an earlier original is never
    /// overwritten (the Antigravity path replaces the .tex with its fix), and a .tex and .pdf with
    /// the same suffix always belong together.
    /// [Human] Sichert LaTeX und Teil-PDF vor der Reparatur, ohne frühere Sicherungen zu überschreiben.
    /// </summary>
    private static void BackupBeforeRepair(string targetFolder, string inputBaseName, string finalTexFile, string compiledPdfPath) {
        try {
            string currentTex = System.IO.File.ReadAllText(finalTexFile);
            for (int n = 1; ; n++) {
                string suffix = n == 1 ? "-orig" : $"-orig{n}";
                string texPath = Path.Combine(targetFolder, inputBaseName + suffix + ".tex");
                if (System.IO.File.Exists(texPath)) {
                    if (System.IO.File.ReadAllText(texPath) == currentTex) {
                        return; // this exact version is already backed up, with its PDF
                    }
                    continue;
                }

                System.IO.File.WriteAllText(texPath, currentTex);
                Ui.Info($"Originales LaTeX vor der Reparatur gesichert unter: {Path.GetFileName(texPath)}");
                if (System.IO.File.Exists(compiledPdfPath)) {
                    string pdfPath = Path.ChangeExtension(texPath, ".pdf");
                    System.IO.File.Copy(compiledPdfPath, pdfPath, true);
                    Ui.Info($"Teil-PDF vor der Reparatur gesichert unter: {Path.GetFileName(pdfPath)}");
                }
                return;
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Sicherung vor der Reparatur fehlgeschlagen: {ex.Describe()}", "LaTeX Refinement");
        }
    }

    /// <summary>
    /// [AI Context] Whether a repaired LaTeX body may replace the original. "It compiles" cannot see
    /// the dangerous failure: a repair that dropped half the lecture compiles fine. So the candidate
    /// must keep at least 80% of the original's length and 90% of its speech and content blocks.
    /// [Human] Prüft, ob eine Reparatur das Original ersetzen darf (nicht deutlich kürzer, keine fehlenden Blöcke).
    /// </summary>
    public static bool IsSafeRepairReplacement(string original, string candidate, out string reason) {
        if (candidate.Length < original.Length * 0.8) {
            reason = $"Die Reparatur ist deutlich kürzer als das Original ({candidate.Length:N0} statt {original.Length:N0} Zeichen).";
            return false;
        }

        int originalSpeech = SpokenCleanRegex().Count(original);
        int originalContent = MathStrokeRegex().Count(original);
        int candidateSpeech = SpokenCleanRegex().Count(candidate);
        int candidateContent = MathStrokeRegex().Count(candidate);
        if (candidateSpeech < originalSpeech * 0.9 || candidateContent < originalContent * 0.9) {
            reason = $"In der Reparatur fehlen Blöcke ({candidateSpeech}/{originalSpeech} speech, {candidateContent}/{originalContent} content).";
            return false;
        }

        reason = "";
        return true;
    }

    private static void CleanupPrecheckFiles(string targetFolder, string finalTexFile, string stepPrefix, bool compilationSuccess = true) {
        try {
            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperBase = $"{inputBaseName}-main";
            string[] extensions = compilationSuccess 
                ? [".tex", ".pdf", ".log", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"]
                : [".pdf", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"];
            foreach (var ext in extensions) {
                string filePath = Path.Combine(targetFolder, wrapperBase + ext);
                if (System.IO.File.Exists(filePath)) {
                    System.IO.File.Delete(filePath);
                }
            }
            if (compilationSuccess) {
                string precheckLogPath = Path.Combine(targetFolder, $"{stepPrefix}-compile-log.txt");
                if (System.IO.File.Exists(precheckLogPath)) {
                    System.IO.File.Delete(precheckLogPath);
                }
            }
            // Keep the clean PDF if compilation succeeded so the user has the PDF from the speech refinement stage;
            // only clean it up if compilation failed.
            if (!compilationSuccess) {
                string cleanPdfPath = Path.Combine(targetFolder, inputBaseName + ".pdf");
                if (System.IO.File.Exists(cleanPdfPath)) {
                    System.IO.File.Delete(cleanPdfPath);
                }
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Konnte temporäre Precheck-Dateien nicht vollständig bereinigen: {ex.Describe()}", "LaTeX Refinement");
        }
    }

    /// <summary>
    /// [AI Context] Copies a compiled PDF (the wrapper's output, which CleanupHelperFiles deletes) to
    /// where it is read. The deliverable goes to one place only, &lt;lecture&gt;.pdf: it used to be copied
    /// beside the step's .tex as well, so every finished lecture held two identical PDFs. A precheck
    /// compile (step 3) is no deliverable and keeps its copy beside its own file.
    /// [Human] Legt das kompilierte PDF ab - das fertige PDF nur einmal, als &lt;Vorlesung&gt;.pdf.
    /// </summary>
    private static void PublishPdf(string compiledPdfPath, string stepPdfPath, string targetFolder, string baseName, bool isFinal) {
        if (!System.IO.File.Exists(compiledPdfPath)) return;

        if (!isFinal) {
            System.IO.File.Copy(compiledPdfPath, stepPdfPath, true);
            Ui.Info($"PDF kopiert zu: {Path.GetFileName(stepPdfPath)}");
            return;
        }

        string finalPdfPath = Path.Combine(targetFolder, FinalPdfFileName(targetFolder, baseName));
        System.IO.File.Copy(compiledPdfPath, finalPdfPath, true);
        Ui.Info($"Finales PDF: {Path.GetFileName(finalPdfPath)}");
    }

    private static void CleanupHelperFiles(string targetFolder, string finalTexFile, bool compilationSuccess = true) {
        try {
            string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
            string wrapperBase = $"{inputBaseName}-main";
            string[] extensions = compilationSuccess 
                ? [".tex", ".pdf", ".log", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"]
                : [".pdf", ".aux", ".out", ".toc", ".fls", ".fdb_latexmk", ".synctex.gz"];
            foreach (var ext in extensions) {
                string filePath = Path.Combine(targetFolder, wrapperBase + ext);
                if (System.IO.File.Exists(filePath)) {
                    System.IO.File.Delete(filePath);
                }
            }
        }
        catch (Exception ex) {
            Ui.Warn($"Hilfsdateien für {Path.GetFileName(finalTexFile)} konnten nicht vollständig bereinigt werden: {ex.Describe()}");
        }
    }

    private static string FormatLatexLog(string rawLog, bool success) {
        var lines = rawLog.Split('\n');
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("==========================================");
        sb.AppendLine($" LaTeX Compilation Log - {(success ? "SUCCESS" : "FAILED")}");
        sb.AppendLine("==========================================");
        sb.AppendLine();

        bool inError = false;
        int errorCount = 0;
        int warningCount = 0;

        foreach (var line in lines) {
            string tLine = line.Trim();
            if (tLine.StartsWith("! ") || tLine.StartsWith("Runaway argument?")) {
                sb.AppendLine();
                string errMsg = tLine.StartsWith("! ") ? tLine[2..] : tLine;
                sb.AppendLine("❌ ERROR: " + errMsg);
                inError = true;
                errorCount++;
            }
            else if (tLine.StartsWith("l.")) {
                sb.AppendLine("   Line: " + tLine);
                inError = false;
            }
            else if (tLine.Contains("Warning:", StringComparison.OrdinalIgnoreCase)) {
                sb.AppendLine("⚠️ WARNING: " + tLine);
                inError = false;
                warningCount++;
            }
            else if (tLine.StartsWith("Overfull") || tLine.StartsWith("Underfull")) {
                // Ignore layout noise
            }
            else if (inError && !string.IsNullOrWhiteSpace(tLine)) {
                sb.AppendLine("   " + tLine); // Continuation of error message
            }
            else if (string.IsNullOrWhiteSpace(tLine)) {
                inError = false;
            }
        }

        sb.Insert(0, $"Summary: {errorCount} Errors, {warningCount} Warnings\n\n");

        // If we failed but couldn't parse the errors cleanly, append raw log so nothing is lost
        if (!success && errorCount == 0) {
            sb.AppendLine("\n--- Raw Output (Could not format cleanly) ---");
            sb.AppendLine(rawLog);
        }

        return sb.ToString();
    }

    /// <returns>Success, plus the candidate body and its compile log for the next round; both null when the
    /// answer was empty or rejected, so the next round retries the same input.</returns>
    private async Task<(bool Success, string? CandidateBody, string? CandidateLog)> TryRepairFailedPdfBuildAsync(
        string preambleText, string originalBodyTex, string failedBodyTex, string compileLog, string baseName, string targetFolder, int roundNumber = 1) {
        Ui.Step($"Schritt 4 Retry: PDF LaTeX Fix (-final-attempt, Runde #{roundNumber})", "LaTeX Fix");
        BackendParameters backendParams = _config.UseVertex ? _config.Step3LastRefinement.Vertex : _config.Step3LastRefinement.AiStudio;

        string promptText = $"<pdf_latex_compile_log>\n{compileLog}\n</pdf_latex_compile_log>\n\n" +
                            $"<current_latex_preamble note=\"FOR REFERENCE ONLY - DO NOT OUTPUT THIS PREAMBLE OR INVENT YOUR OWN\">\n{preambleText}\n</current_latex_preamble>\n\n" +
                            $"<latex_document_body_to_fix>\n{failedBodyTex}\n</latex_document_body_to_fix>\n\n" +
                            "Please fix the LaTeX compilation errors in the DOCUMENT BODY above. Note that the compile log error output might be incomplete or truncated, so carefully inspect the entire body for any potential LaTeX syntax errors, unescaped characters, or broken math environments.\n\n" +
                            "CRITICAL INSTRUCTION: DO NOT output the preamble! Even if certain LaTeX packages or libraries seem to be missing or undefined, DO NOT output any preamble, \\documentclass, or \\usepackage declarations! You MUST ONLY output the corrected LaTeX code that belongs between \\begin{document} and \\end{document} (do NOT include \\begin{document} and \\end{document} tags themselves). To save tokens, output ONLY the fixed document body content. Use the preamble above strictly as reference so you do not invent your own preamble. Output the WHOLE body: change only what the compile log reports, and copy everything else unchanged, including every \\begin{hidden} ... \\end{hidden} block (the figures' plans) and every % comment line.";

        var promptParts = new List<Part> {
            new() { Text = promptText },
            new() { Text = "\n\nCRITICAL INSTRUCTION: When you have completely finished writing your response and there is nothing left to output, you MUST append the exact text '% [SYSTEM] Refinement complete' on a new line at the very end of your response. This is mandatory for the system to know you are done." }
        };

        var history = new List<Content> { new() { Role = "user", Parts = promptParts } };

        var requestConfig = new GenerateContentConfig {
            Temperature = backendParams.Temperature,
            TopP = backendParams.TopP,
            TopK = backendParams.TopK,
            MaxOutputTokens = ModelCapabilities.ResolveMaxOutputTokens(backendParams.CurrentModel, backendParams.MaxOutputTokens),
            AutomaticContinuation = false
        };

        var thoughtSettings = _config.PdfCompilation?.ThoughtSummaries;
        requestConfig.ThinkingConfig = ThinkingConfigFactory.Build(
            backendParams.CurrentModel, backendParams.ThinkingBudget, backendParams.ThinkingLevel, thoughtSettings?.Enabled == true);

        string noPreambleFileName = $"step5-{baseName}-offset-last_try{roundNumber}.tex";
        string standaloneFileName = $"step5-{baseName}-offset-last_try{roundNumber}-main.tex";
        string retryLogPath = Path.Combine(targetFolder, $"compile-log-step5-last_try{roundNumber}.txt");

        // The round's compile log is its existing log, but it is written only after the answer has
        // been compiled, so the thoughts are appended to it then rather than per request. A round
        // that never compiles (empty or rejected answer) still gets that file, holding only them.
        var thoughts = new ThoughtLog(thoughtSettings, $"pdf-repair-{roundNumber}", $"{TexDocumentWriter.PdfRepairStep}, round {roundNumber}",
            backendParams.CurrentModel, Path.Combine(targetFolder, noPreambleFileName), existingLogPath: null, existingLogWrittenLater: true);

        var (fullResponseText, usage) = await StreamFixResponseAsync(history, requestConfig, backendParams, standaloneFileName, thoughts);

        if (!string.IsNullOrEmpty(fullResponseText)) {
            string cleanedText = LatexResponseCleaner.CleanLatexResponse(fullResponseText);

            // Version ohne Preamble und komplett bereinigt von \begin{document} / \end{document}
            string bodyOnlyText = cleanedText;
            int beginDocIdx = cleanedText.IndexOf("\\begin{document}", StringComparison.OrdinalIgnoreCase);
            int endDocIdx = cleanedText.IndexOf("\\end{document}", StringComparison.OrdinalIgnoreCase);
            if (beginDocIdx >= 0 && endDocIdx > beginDocIdx) {
                beginDocIdx += "\\begin{document}".Length;
                bodyOnlyText = cleanedText[beginDocIdx..endDocIdx].Trim();
            }
            else if (beginDocIdx >= 0 && endDocIdx < 0) {
                beginDocIdx += "\\begin{document}".Length;
                bodyOnlyText = cleanedText[beginDocIdx..].Trim();
            }
            else if (endDocIdx >= 0 && beginDocIdx < 0) {
                bodyOnlyText = cleanedText[..endDocIdx].Trim();
            }
            bodyOnlyText = DocumentTagsRegex().Replace(bodyOnlyText, "").Trim();

            if (!IsSafeRepairReplacement(originalBodyTex, bodyOnlyText, out string rejectReason)) {
                string rejectedPath = Path.Combine(targetFolder, $"step5-{baseName}-offset-last_try{roundNumber}-rejected.tex");
                await System.IO.File.WriteAllTextAsync(rejectedPath, bodyOnlyText);
                Ui.Error($"Fix-Versuch #{roundNumber} verworfen: {rejectReason} Gespeichert unter: {Path.GetFileName(rejectedPath)}");
                await thoughts.AppendAllToAsync(retryLogPath);
                return (false, null, null);
            }

            // The saved candidate carries the repaired document's header stack under its own layer;
            // the body that gets compiled and handed to the next round stays as the model wrote it.
            string noPreamblePath = Path.Combine(targetFolder, noPreambleFileName);
            string repairHeader = TexDocumentWriter.BuildRefinementHeader(
                $"{TexDocumentWriter.PdfRepairStep}, round {roundNumber}", noPreambleFileName, null, usage, backendParams.CurrentModel, backendParams.Generation,
                Path.GetFileName(thoughts.FilePath));
            await System.IO.File.WriteAllTextAsync(noPreamblePath, TexDocumentWriter.StackLayers(
                repairHeader, TexDocumentWriter.SplitHeaderLayers(originalBodyTex).Layers, TexDocumentWriter.SplitHeaderLayers(bodyOnlyText).Body));
            Ui.Info($"Gefixte LaTeX-Datei (Versuch #{roundNumber}) gespeichert unter: {noPreamblePath}");

            string standaloneContent = preambleText + "\n\\begin{document}\n\n" + bodyOnlyText + "\n\n\\end{document}\n";
            string standalonePath = Path.Combine(targetFolder, standaloneFileName);
            await System.IO.File.WriteAllTextAsync(standalonePath, standaloneContent);
            Ui.Info($"Gefixte LaTeX-Datei (Versuch #{roundNumber}, mit Preamble) gespeichert unter: {standalonePath}");

            InteractiveDelay.LastGenerationCompletionTimeUtc = DateTime.UtcNow;

            Ui.Info($"Starte PDF-Kompilierung für step5 (Fix-Versuch #{roundNumber})...");
            var (retrySuccess, retryLog) = await LatexToolkit.CompilePdfAsync(standalonePath);
            string retryLogContent = FormatLatexLog(retryLog, retrySuccess);
            await System.IO.File.WriteAllTextAsync(retryLogPath, retryLogContent);
            await thoughts.AppendAllToAsync(retryLogPath);

            if (retrySuccess) {
                Ui.Success($"PDF erfolgreich im Fix-Versuch #{roundNumber} (step5) erstellt: {targetFolder}");
                PublishPdf(Path.ChangeExtension(standalonePath, ".pdf"), Path.ChangeExtension(noPreamblePath, ".pdf"), targetFolder, baseName, isFinal: true);
                CleanupHelperFiles(targetFolder, noPreamblePath, true);
                return (true, bodyOnlyText, retryLogContent);
            }
            else {
                Ui.Error($"Auch Fix-Versuch #{roundNumber} konnte das PDF nicht fehlerfrei kompilieren. Log in: {retryLogPath}");
                CleanupHelperFiles(targetFolder, noPreamblePath, false);
                return (false, bodyOnlyText, retryLogContent);
            }
        }
        await thoughts.AppendAllToAsync(retryLogPath);
        return (false, null, null);
    }

    private async Task<(string Text, TokenUsage Usage)> StreamFixResponseAsync(List<Content> history, GenerateContentConfig requestConfig, BackendParameters backendParams, string outputFileName, ThoughtLog thoughts) {
        string providerName = _config.UseVertex ? "Vertex AI" : "Google AI Studio";
        // As in StreamAndCollectAsync: 0 means no pacing wait, while the retry backoff keeps 130 s.
        int fixDelay = _config.Step3LastRefinement?.RateLimitDelaySeconds ?? 130;
        int retryBackoff = fixDelay > 0 ? fixDelay : 130;

        var result = await GenerationLoop.RunAsync(history,
            (h, continuationToken) => {
                ApiKeyQuotaTracker.RecordClientRequest(_client, backendParams.CurrentModel, outputFileName);
                var hopConfig = GenerationLoop.PrepareHopConfig(requestConfig, backendParams.CurrentModel, continuationToken);
                return _client.Models.GenerateContentStreamAsync(backendParams.CurrentModel, h, hopConfig);
            },
            thoughts,
            new GenerationLoopOptions {
                MaxContinueRequests = _config.Step3LastRefinement?.MaxContinueRequests ?? 10,
                SupportsLongDecode = ModelCapabilities.SupportsLongDecode(backendParams.CurrentModel),
                BeforeRequestAsync = () => WaitOutRateLimitAsync(fixDelay,
                    "vor dem PDF-Fix-Request...", "Warte auf Rate-Limits (Token-Refill Schutz vor PDF-Fix-Request)..."),
                AnnounceRequest = (request, max) =>
                    Ui.Info($"Sende PDF-Fix-Anfrage an {providerName} ({backendParams.CurrentModel}) (Request {request}/{max})...", "API"),
                RetryContext = outputFileName,
                InitialBackoff = retryBackoff,
                FirstChunkTimeout = AppConfig.FirstChunkTimeout,
                HighDemandDelay = retryBackoff,
                CancelledAsWarning = true,
                EmptyAnswerRetries = 3,
                IsComplete = text => text.Contains(RefinementCompleteMarker, StringComparison.OrdinalIgnoreCase),
                BudgetStopSuffix = "Breche den PDF-Fix ab.",
                BuildContinuePrompt = response => {
                    string continuePrompt = GenerationLoop.CutShortPrompt(response,
                        "Please \"continue\" exactly where you left off. Start typing the VERY NEXT CHARACTER that would come after your last output. Do not repeat anything you already wrote. Just print the very next character.",
                        _config.ContinuePromptEchoCharacters);
                    Ui.Info("Unerwartetes Ende der Antwort. Bereite Continue-Prompt vor...", "PDF-Fix");
                    return continuePrompt;
                },
                WaitBeforeContinueAsync = () => {
                    if (fixDelay <= 0) return Task.FromResult(true);
                    Ui.Detail($"Warte {fixDelay} Sekunden vor der Fortsetzung...", "Timer");
                    return InteractiveDelay.SmartDelayAsync(fixDelay, "Warte auf Rate-Limits (Token Refill)...");
                },
            });
        // A fix that broke off mid-answer is no fix; compiling the fragment would only waste a round.
        if (!result.IsUsable && result.Text.Length > 0) {
            Ui.Error($"PDF-Fix-Antwort unvollständig ({result.End}); dieser Fix-Versuch wird verworfen.", "PDF-Fix");
            return ("", result.Usage);
        }
        return (result.Text, result.Usage);
    }

    private async Task<bool> RunExternalAgentRepairLoopAsync(string finalTexFile, string baseName, string targetFolder, string preambleText, string initialLog) {
        int maxRounds = _config.PdfCompilation?.MaxFixRounds ?? 3;
        if (maxRounds <= 0) maxRounds = 1;

        string? envVarName = (_config.AiStudioApiKeyEnvNames != null && _config.AiStudioApiKeyEnvNames.Length > _config.AiStudioActiveApiProfile)
            ? _config.AiStudioApiKeyEnvNames[_config.AiStudioActiveApiProfile]
            : "API_KEY";

        string? apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envVarName);
        if (string.IsNullOrEmpty(apiKey)) {
            Ui.Error($"Antigravity Agent benötigt einen gültigen API-Key in der Umgebungsvariable '{envVarName}'.");
            return false;
        }

        // CompilePdfAsync has already backed up the original (-orig.tex / -orig.pdf) before calling this.
        string inputBaseName = Path.GetFileNameWithoutExtension(finalTexFile);
        string originalTex = await System.IO.File.ReadAllTextAsync(finalTexFile);

        using var httpClient = new System.Net.Http.HttpClient();
        httpClient.Timeout = TimeSpan.FromMinutes(20);
        httpClient.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

        string currentCandidatePath = finalTexFile;
        string currentLog = initialLog;

        for (int round = 1; round <= maxRounds; round++) {
            Ui.Step($"Starte Reparatur-Runde {round} von {maxRounds}...", "Antigravity Agent API");

            string? fixedLatex = await CallAntiGravityAgentAsync(httpClient, currentCandidatePath, currentLog);
            if (string.IsNullOrWhiteSpace(fixedLatex)) {
                Ui.Error($"Agent konnte in Runde {round} keine verwertbare Korrektur liefern. Breche Reparatur ab.");
                return false;
            }

            string tryTexFileName = $"{inputBaseName}-try{round}.tex";
            string tryTexPath = Path.Combine(targetFolder, tryTexFileName);

            if (!IsSafeRepairReplacement(originalTex, fixedLatex, out string rejectReason)) {
                // Keep it for inspection, but never compile or promote it; the next round retries the previous input.
                string rejectedPath = Path.Combine(targetFolder, $"{inputBaseName}-try{round}-rejected.tex");
                await System.IO.File.WriteAllTextAsync(rejectedPath, fixedLatex);
                Ui.Error($"Reparatur aus Runde {round} verworfen: {rejectReason} Gespeichert unter: {Path.GetFileName(rejectedPath)}");
                continue;
            }

            await System.IO.File.WriteAllTextAsync(tryTexPath, fixedLatex);
            Ui.Info($"Reparierte Version (Runde {round}) gespeichert unter: {tryTexFileName}");

            // the preamble CompilePdfAsync chose for this lecture's language
            string preamble = preambleText;

            string tryWrapperFileName = $"{inputBaseName}-try{round}-main.tex";
            string tryWrapperPath = Path.Combine(targetFolder, tryWrapperFileName);
            string tryWrapperContent = preamble + "\n\\begin{document}\n\n" + $"\\input{{{tryTexFileName}}}\n\n" + "\\end{document}\n";
            await System.IO.File.WriteAllTextAsync(tryWrapperPath, tryWrapperContent);

            var (success, log) = await LatexToolkit.CompilePdfAsync(tryWrapperPath);
            string logContent = FormatLatexLog(log, success);
            string tryLogFileName = $"compile-log-{inputBaseName}-try{round}.txt";
            string tryLogPath = Path.Combine(targetFolder, tryLogFileName);
            await System.IO.File.WriteAllTextAsync(tryLogPath, logContent);

            string compiledPdfPath = Path.ChangeExtension(tryWrapperPath, ".pdf");
            if (System.IO.File.Exists(compiledPdfPath)) {
                string tryPdfPath = Path.Combine(targetFolder, $"{inputBaseName}-try{round}.pdf");
                System.IO.File.Copy(compiledPdfPath, tryPdfPath, true);
                Ui.Info($"Try-PDF gespeichert: {Path.GetFileName(tryPdfPath)}");
            }

            if (success) {
                Ui.Success($"PDF durch Antigravity Agent erfolgreich (Runde {round}/{maxRounds}) generiert!", "LatexToolkit");

                // Overwrite finalTexFile with the winning fix
                System.IO.File.Copy(tryTexPath, finalTexFile, true);
                Ui.Success($"Finale Datei aktualisiert mit erfolgreichem Fix: {Path.GetFileName(finalTexFile)}");

                PublishPdf(compiledPdfPath, Path.Combine(targetFolder, inputBaseName + ".pdf"), targetFolder, baseName, isFinal: true);

                // Cleanup only temporary wrapper auxiliary files, preserving -try{round}.tex, -try{round}.pdf, and logs
                CleanupHelperFiles(targetFolder, tryTexPath, true);
                return true;
            }
            else {
                Ui.Warn($"PDF-Generierung für Versuch #{round} fehlgeschlagen. Details in: {tryLogFileName}", "Antigravity Agent API");
                CleanupHelperFiles(targetFolder, tryTexPath, false);

                // Setup candidate and log for next round
                currentCandidatePath = tryTexPath;
                currentLog = AppendStructuralFindings(logContent, fixedLatex);
            }
        }

        Ui.Error($"Maximale Anzahl an Antigravity-Reparaturrunden ({maxRounds}) erreicht. PDF konnte nicht generiert werden.");
        return false;
    }

    private static async Task<string?> CallAntiGravityAgentAsync(System.Net.Http.HttpClient httpClient, string candidateTexFile, string logContent) {
        string candidateFileName = Path.GetFileName(candidateTexFile);
        string currentLatexContent = await System.IO.File.ReadAllTextAsync(candidateTexFile);

        string prompt = $@"We are trying to compile a LaTeX document, but pdflatex encountered errors.
You are the Antigravity Agent. Please fix the LaTeX code.

The preamble is managed by a wrapper script. Do not write the preamble, only output the fixed content for the body file.

### Error Log
```text
{logContent}
```

### Current File Contents (`{candidateFileName}`)
```latex
{currentLatexContent}
```

Please return the fully corrected contents of `{candidateFileName}` inside a ```latex code block. DO NOT use \begin{{document}} or \end{{document}}.";

        var payload = new {
            agent = "antigravity-preview-05-2026",
            environment = "remote",
            input = prompt
        };

        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(payload);
        var content = new System.Net.Http.StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        Ui.Info("Kontaktiere Google Cloud (v1beta/interactions) für automatische Korrektur...", "Antigravity Agent API");

        try {
            var response = await httpClient.PostAsync("https://generativelanguage.googleapis.com/v1beta/interactions", content);
            string responseBody = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode) {
                Ui.Error($"Antigravity Agent API Aufruf fehlgeschlagen: {response.StatusCode} - {ExceptionExtensions.CleanMessage(responseBody)}");
                return null;
            }

            string agentOutput = AntigravityResponseParser.ExtractText(responseBody);
            if (string.IsNullOrWhiteSpace(agentOutput)) {
                Ui.Error("Antigravity Agent Response enthielt keinen verwertbaren Text (kein output_text / keine Antwort-Schritte).");
                return null;
            }

            string cleanedText = LatexResponseCleaner.CleanLatexResponse(agentOutput);

            if (!cleanedText.Contains('\\') || cleanedText.Length < 100) {
                Ui.Error("Antigravity Agent lieferte keinen gültigen LaTeX-Code zurück.");
                return null;
            }

            Ui.Success($"Agent hat LaTeX-Korrektur erfolgreich empfangen ({cleanedText.Length:N0} Zeichen).", "Antigravity Agent API");
            return cleanedText;
        }
        catch (Exception ex) {
            Ui.Error($"Exception bei Antigravity Agent API: {ex.Describe()}");
            return null;
        }
    }


}
