using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Latex;
using Spectre.Console;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Runs the YouTube transcription pipeline: resolves which tasks to process (configured
/// or interactively entered), then for each task sends every configured timestamp fragment to the
/// model as a <c>Part.FromUri</c> video reference and writes per-fragment and combined .tex output.
///
/// <para>Extracted from <c>AiStudioAutoExtractionSession</c> (Phase 11). It is a distinct feature
/// from the FFmpeg video pipeline - no local files, no splitting, no upload - and shares only the
/// two calls behind <see cref="IYouTubeTranscriptionHost"/>. Keeping it out of the session class
/// means work on the video pipeline never has to read it.</para>
/// [Human] Führt die YouTube-Transkription aus: pro Fragment eine Anfrage an das Modell, dann
/// Einzel- und Gesamtdatei schreiben. Aus der Session-Klasse herausgelöst.
/// </summary>
public sealed class YouTubeTaskRunner(IAutoExtractionConfig config, IYouTubeTranscriptionHost host) {
    private readonly IAutoExtractionConfig _config = config;
    private readonly IYouTubeTranscriptionHost _host = host;

    public Task RunAsync() => RunAsync(ResolveTasks());

    /// <summary>
    /// [AI Context] Runs exactly these tasks, with no prompt. The menu reaches it through
    /// <see cref="ResolveTasks"/>; <c>lecx youtube</c> builds the task from its flags and calls it directly.
    /// [Human] Führt genau diese Aufgaben ohne Rückfrage aus.
    /// </summary>
    /// <returns>false if the session could not be set up or any task lost a fragment or its refinement.</returns>
    public async Task<bool> RunAsync(IReadOnlyList<YouTubeTranscriptionTask> tasksToProcess) {
        if (tasksToProcess.Count == 0) {
            Ui.Info("Keine YouTube-Aufgaben zum Verarbeiten.");
            return true;
        }

        Ui.Step($"Starte Transkription für {tasksToProcess.Count} YouTube-Video(s)...", "YouTube Mode");

        if (!await _host.EnsureSessionSetupAsync()) return false;

        bool allSucceeded = true;
        foreach (var task in tasksToProcess) {
            if (string.IsNullOrWhiteSpace(task.VideoUrl)) continue;
            allSucceeded &= await TranscribeTaskAsync(task);
        }
        return allSucceeded;
    }

    /// <summary>
    /// [AI Context] Decides between the tasks declared in the JSON config and one entered
    /// interactively. Declining the configured tasks falls through to the interactive prompt, so
    /// "no" means "let me type a different URL" rather than "do nothing".
    /// [Human] Wählt zwischen den in der JSON konfigurierten Aufgaben und einer interaktiv
    /// eingegebenen.
    /// </summary>
    private List<YouTubeTranscriptionTask> ResolveTasks() {
        List<YouTubeTranscriptionTask> tasks = [];

        if (_config.YouTubeTasks != null && _config.YouTubeTasks.Length > 0) {
            Ui.Info($"Es wurden {_config.YouTubeTasks.Length} Aufgabe(n) in der Konfiguration gefunden.", "YouTube Mode");
            if (Ui.Confirm("Möchtest du diese Aufgaben ausführen?", true)) {
                tasks.AddRange(_config.YouTubeTasks);
                return tasks;
            }
        }
        else {
            Ui.Info("Keine vorgegebenen YouTube-Aufgaben in der Konfiguration gefunden.", "YouTube Mode");
        }

        var interactiveTask = YouTubeTaskPrompt.CreateInteractiveYouTubeTask(_config.OverlapSeconds);
        if (interactiveTask != null) {
            tasks.Add(interactiveTask);
        }
        return tasks;
    }

    private async Task<bool> TranscribeTaskAsync(YouTubeTranscriptionTask task) {
        string baseName = string.IsNullOrWhiteSpace(task.OutputName) ? "youtube-lecture" : task.OutputName;
        if (!baseName.StartsWith("step1-", StringComparison.OrdinalIgnoreCase)) {
            baseName = "step1-" + baseName;
        }

        string targetRoot = ExtractionHelpers.OutputRoot(_config);
        string fileSpecificOutputFolder = Path.Combine(targetRoot, baseName);
        if (!Directory.Exists(fileSpecificOutputFolder)) {
            Directory.CreateDirectory(fileSpecificOutputFolder);
        }

        Ui.Step($"Starte API-Extraktion für URL: {task.VideoUrl} ({baseName})", "YouTube Consumer");
        List<string> generatedTexFiles = [];
        string fullOutputTextRaw = "";
        int missingFragments = 0;

        for (int i = 0; i < task.Fragments.Count; i++) {
            var frag = task.Fragments[i];
            int partNum = i + 1;
            Ui.Step($"Verarbeite Fragment {partNum}/{task.Fragments.Count}: {frag.StartTime} bis {frag.EndTime} ({frag.PartTitle})");

            string parsedPrompt = BuildFragmentPrompt(frag, partNum, task.Fragments.Count);
            var attachmentParts = new List<Part> { Part.FromUri(task.VideoUrl, "video/mp4") };

            string targetPartPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}-part{partNum}.tex");
            string texOutput = (await _host.TranscribeSegmentToLatexAsync(
                task.VideoUrl, partNum, baseName, parsedPrompt, attachmentParts, generatedTexFiles, targetPartPath
            )).LatexBody;

            if (string.IsNullOrWhiteSpace(texOutput)) {
                missingFragments++;
                continue;
            }

            string cleanTex = LatexResponseCleaner.CleanLatexResponse(texOutput);
            fullOutputTextRaw += $"\n\n% --- TEIL {partNum}: {frag.StartTime}-{frag.EndTime} ({frag.PartTitle}) ---\n" + cleanTex;

            string partContent = cleanTex;
            if (!partContent.StartsWith("% Startzeit:") && !partContent.StartsWith("% Zeitstempel:")) {
                partContent = $"% Startzeit: {frag.StartTime} | Ende: {frag.EndTime}\n\n" + partContent;
            }
            await System.IO.File.WriteAllTextAsync(targetPartPath, partContent);
            generatedTexFiles.Add(targetPartPath);
            Ui.Success($"Teildatei gespeichert unter: {targetPartPath}");
        }

        if (string.IsNullOrWhiteSpace(fullOutputTextRaw)) return false;

        string combinedPath = Path.Combine(fileSpecificOutputFolder, $"{baseName}.tex");
        await System.IO.File.WriteAllTextAsync(combinedPath, fullOutputTextRaw.Trim());
        Ui.Success($"Zusammengeführte YouTube-Transkription gespeichert unter: {combinedPath}");
        if (missingFragments > 0) {
            Ui.Warn($"{missingFragments} von {task.Fragments.Count} Fragment(en) ohne Antwort; die Gesamtdatei ist lückenhaft.", "YouTube Mode");
        }
        bool refined = await _host.RefineYouTubeOutputAsync(combinedPath, task.Fragments.Count);
        return refined && missingFragments == 0;
    }

    /// <summary>
    /// [AI Context] Builds the per-fragment instruction. A YouTube task carries no date, so part 1 is
    /// told to date its <c>\youtubechapter</c> only from the video and never to guess; later parts are
    /// told not to repeat the chapter heading or the date, because a Part 2 or later opens no chapter
    /// (Stage 1, transcription-multi-part.md). When totalParts == 1, explicitly instructs the model to
    /// omit the trailing Final Video Cut-off &amp; Board State Checkpoint aiNote.
    /// [Human] Baut die Anweisung für ein Fragment. Teil 1: Datum nur aus dem Video, nie raten; spätere Teile: kein Datum.
    /// </summary>
    public static string BuildFragmentPrompt(YouTubeTimestampFragment frag, int partNum, int totalParts = 0) {
        string dateNotice = (partNum == 1)
            ? "This request gives no date: write `\\youtubechapter` with a `[Date]` only if the video states the date, and never guess one."
            : $"Since this is part {partNum}, do not repeat the `\\youtubechapter` heading or the date.";

        string singlePartNotice = (totalParts == 1)
            ? "\nSince this is a complete single-part video (Part 1 of 1), do NOT emit a trailing `\\begin{aiNote}[Final Video Cut-off \\& Board State Checkpoint]` at the end of the document."
            : "";

        return $"Please transcribe this lecture and extract all mathematical formulas into LaTeX according to the system instructions.\n\n[IMPORTANT INSTRUCTION FOR YOUTUBE VIDEO]:\nThis is part {partNum} ('{frag.PartTitle}') of the lecture. Please focus ONLY on transcribing and extracting the chosen video fragment starting at timestamp {frag.StartTime} and ending at timestamp {frag.EndTime}.\n{dateNotice}{singlePartNotice}";
    }
}
