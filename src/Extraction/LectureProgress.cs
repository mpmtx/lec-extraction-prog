using System;
using System.IO;
using System.Linq;

namespace LectureExtraction.Extraction;

/// <summary>The stage a lecture folder needs next, in pipeline order.</summary>
public enum LectureStage {
    /// <summary>No .tex at all; the video has not been transcribed in this folder.</summary>
    NotStarted,
    /// <summary>Some parts exist but not the combined file - transcription stopped partway.</summary>
    Transcribe,
    /// <summary>Refinement step 1: merge the parts and correct timestamps.</summary>
    Merge,
    /// <summary>Refinement step 2: speech refinement.</summary>
    Speech,
    /// <summary>Refinement step 3: final polish.</summary>
    Polish,
    /// <summary>Only the PDF is missing.</summary>
    Pdf,
    Done
}

/// <summary>
/// [AI Context] What one lecture output folder holds and which stage comes next, read from the file
/// names the pipeline writes (see docs/cli/pipeline.md). Shared by `status` and `finish`, so the
/// report and the action cannot disagree. Each refinement step's input is the newest file with that
/// step's prefix, re-run copies (-copy-N) included: step N reads the stepN-* file. A failed step's
/// "-truncated.tex" fragment never counts: the step is redone from its own input.
/// [Human] Was ein Vorlesungsordner enthält und welcher Schritt als Nächstes fällig ist.
/// </summary>
public sealed record LectureProgress(
    string Folder,
    int PartsCount,
    string? CombinedTex,
    string? MergedTex,
    string? SpeechTex,
    string? FinalTex,
    string? Pdf,
    long TmpBytes) {

    public string Lecture => Path.GetFileName(Folder);

    public LectureStage NextStage =>
        Pdf != null ? LectureStage.Done :
        FinalTex != null ? LectureStage.Pdf :
        SpeechTex != null ? LectureStage.Polish :
        MergedTex != null ? LectureStage.Speech :
        CombinedTex != null ? LectureStage.Merge :
        PartsCount > 0 ? LectureStage.Transcribe :
        LectureStage.NotStarted;

    /// <summary>The file the next refinement step or the PDF reads; null for transcription and when done.</summary>
    public string? NextInput => NextStage switch {
        LectureStage.Merge => CombinedTex,
        LectureStage.Speech => MergedTex,
        LectureStage.Polish => SpeechTex,
        LectureStage.Pdf => FinalTex,
        _ => null
    };

    /// <summary>German label of the furthest stage reached, for tables.</summary>
    public string ReachedLabel => NextStage switch {
        LectureStage.Done => "PDF fertig",
        LectureStage.Pdf => "Schritt 3 (Polish)",
        LectureStage.Polish => "Schritt 2 (Speech)",
        LectureStage.Speech => "Schritt 1 (Merge)",
        LectureStage.Merge => "transkribiert",
        LectureStage.Transcribe => PartsCount == 1 ? "1 Teil" : $"{PartsCount} Teile",
        _ => "leer"
    };

    /// <summary>German label of the next stage, for tables.</summary>
    public static string StageLabel(LectureStage stage) => stage switch {
        LectureStage.NotStarted => "nicht begonnen",
        LectureStage.Transcribe => "Transkription fortsetzen",
        LectureStage.Merge => "Schritt 1 (Merge)",
        LectureStage.Speech => "Schritt 2 (Speech)",
        LectureStage.Polish => "Schritt 3 (Polish)",
        LectureStage.Pdf => "PDF",
        _ => "-"
    };

    /// <summary>Reads one lecture output folder.</summary>
    public static LectureProgress Scan(string folder) {
        string lecture = Path.GetFileName(folder);

        int partsCount = Directory.GetFiles(folder, "step1-*-part*.tex")
            .Select(Path.GetFileName)
            .Count(f => !f!.Contains("-offset", StringComparison.OrdinalIgnoreCase));

        // Refinement reads the offset-corrected combined file when there is one (GenerateOffsetFiles).
        string? combined = Newest(folder, "step1-*-all-offset*.tex") ?? Newest(folder, "step1-*-all*.tex");

        string? pdf = Directory.GetFiles(folder, "*.pdf")
            .FirstOrDefault(f => Path.GetFileName(f).Equals($"{lecture}.pdf", StringComparison.OrdinalIgnoreCase))
            ?? Directory.GetFiles(folder, "step4-*-final*.pdf").FirstOrDefault()
            ?? Directory.GetFiles(folder, "step5-*-last_try*.pdf").FirstOrDefault();
        if (pdf != null && new FileInfo(pdf).Length == 0) {
            pdf = null;
        }

        string tmp = Path.Combine(folder, "tmp");
        long tmpBytes = 0;
        if (Directory.Exists(tmp)) {
            try {
                tmpBytes = new DirectoryInfo(tmp).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return new LectureProgress(
            folder,
            partsCount,
            combined,
            Newest(folder, "step2-*merged*.tex"),
            Newest(folder, "step3-*speech_refined*.tex"),
            Newest(folder, "step4-*final*.tex"),
            pdf,
            tmpBytes);
    }

    /// <summary>
    /// Whether <paramref name="folder"/> looks like a lecture output folder (as opposed to a parent of
    /// them, or an unrelated folder such as rerun-logs).
    /// </summary>
    public static bool IsLectureFolder(string folder) =>
        Directory.Exists(Path.Combine(folder, "tmp"))
        || Directory.GetFiles(folder, "step1-*.tex").Length > 0
        || Directory.GetFiles(folder, "*.pdf").Length > 0;

    /// <summary>
    /// The newest file matching <paramref name="pattern"/>, skipping the "-main.tex" standalone copies
    /// a compile writes beside a step's output (preamble included): they are no step's input. Also
    /// skips the "-truncated.tex" fragment a refinement step saves when its answer broke off: the step
    /// failed, so the fragment neither proves it finished nor feeds the next one. On 2026-11-02 'finish'
    /// planned step 3 from a step3-...-speech_refined-truncated.tex, which would have lost the end of
    /// the lecture.
    /// </summary>
    private static string? Newest(string folder, string pattern) =>
        Directory.GetFiles(folder, pattern)
            .Where(f => !f.EndsWith("-main.tex", StringComparison.OrdinalIgnoreCase)
                && !f.EndsWith("-truncated.tex", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
}
