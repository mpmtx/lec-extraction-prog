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
/// step's prefix, re-run copies (-copy-N) included: step N reads the stepN-* file. Files written beside
/// a step's output never count (<see cref="IsSideFile"/>): after a failed step's "-truncated.tex"
/// fragment, the step is redone from its own input.
/// [Human] Was ein Vorlesungsordner enthält und welcher Schritt als Nächstes fällig ist.
/// </summary>
public sealed partial record LectureProgress(
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
            ?? Directory.GetFiles(folder, "step4-*-final*.pdf").FirstOrDefault(f => !IsSideFile(f))
            ?? Directory.GetFiles(folder, "step5-*-last_try*.pdf").FirstOrDefault(f => !IsSideFile(f));
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

    /// <summary>The newest file matching <paramref name="pattern"/> that is a step's output, not a <see cref="IsSideFile"/>.</summary>
    private static string? Newest(string folder, string pattern) =>
        Directory.GetFiles(folder, pattern)
            .Where(f => !IsSideFile(f))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    /// <summary>
    /// [AI Context] Whether a file only sits beside a step's output although its name matches the step's
    /// pattern, so it is neither the next step's input nor proof the step finished:
    /// <list type="bullet">
    /// <item>"-main": the standalone copy a compile writes (preamble included).</item>
    /// <item>"-truncated": the fragment a refinement step saves when its answer broke off; the step failed.
    /// On 2026-11-02 'finish' planned step 3 from a step3-...-speech_refined-truncated.tex, which would
    /// have lost the end of the lecture.</item>
    /// <item>"-orig", "-origN": the backup taken before a PDF repair (with the failed compile's partial
    /// PDF). Its text is the step's file as it was then, and it is written after it, so it was the newest:
    /// 'finish' planned 'pdf compile' on grundstrukturen 04-21-25's backup, and a repair would then have
    /// edited the backup.</item>
    /// <item>"-tryN", "-tryN-rejected": the Antigravity repair's candidates. A winning one is copied over
    /// the step's file and published, so one left over failed or was refused.</item>
    /// </list>
    /// [Human] Dateien neben einem Schrittergebnis, die selbst keines sind.
    /// </summary>
    private static bool IsSideFile(string path) => SideFileRegex().IsMatch(Path.GetFileName(path));

    [System.Text.RegularExpressions.GeneratedRegex(@"-(main|truncated|orig\d*|try\d+(-rejected)?)\.(tex|pdf)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SideFileRegex();
}
