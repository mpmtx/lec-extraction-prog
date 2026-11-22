using LectureExtraction.Cli.Commands;
using LectureExtraction.ConsoleUi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>Small wording helpers of the console output, as a log reader sees them.</summary>
public class OutputTextTests {
    [Theory]
    [InlineData("Warte auf Rate-Limits (Token Refill)...", "Warte auf Rate-Limits (Token Refill) (64s)")]
    [InlineData("Server-Verarbeitung", "Warte 64s: Server-Verarbeitung")]
    [InlineData("Warte 64 Sekunden nach Datei-Aktivierung...", "Warte 64 Sekunden nach Datei-Aktivierung")]
    public void A_wait_line_says_Warte_once(string message, string expected) {
        Assert.Equal(expected, InteractiveDelay.DescribeWait(64, message));
    }

    [Theory]
    [InlineData(@"D:\out\02-18-2026-wednesday-week1-Analysis_II", "02-18-2026-wednesday-week1-Analysis_II-all-offset-copy-1", "02-18-2026-wednesday-week1-Analysis_II.pdf")]
    [InlineData(@"D:\out\05-12-25-tuesday", "05-12-25-tuesday", "05-12-25-tuesday.pdf")]
    [InlineData(@"D:\some\other-folder", "lecture", "lecture.pdf")]
    public void The_finished_pdf_is_named_after_the_lecture_folder(string folder, string baseName, string expected) {
        Assert.Equal(expected, LectureExtraction.Refinement.LatexRefinementSession.FinalPdfFileName(folder, baseName));
    }

    [Fact]
    public void A_step_file_is_shown_without_the_lecture_name_its_row_already_gives() {
        string lecture = "02-18-2026-wednesday-week1-Analysis_II";

        Assert.Equal("step2-…-all-offset-merged.tex", FinishCommand.ShortFileName($@"D:\out\{lecture}\step2-{lecture}-all-offset-merged.tex", lecture));
    }

    [Theory]
    [InlineData(@"D:\out\step1-02-18-2026-wednesday-week1-Analysis_II-all-offset-copy-1.tex", "02-18-2026-wednesday-week1-Analysis_II")]
    [InlineData(@"D:\out\step2-02-18-2026-wednesday-week1-Analysis_II-all-offset-copy-1-offset-merged-copy1.tex", "02-18-2026-wednesday-week1-Analysis_II")]
    [InlineData(@"D:\out\step4-05-12-25-tuesday-offset-final.tex", "05-12-25-tuesday")]
    [InlineData(@"D:\out\step1-10-28-2026-wednesday-week7-Algebra_I.tex", "10-28-2026-wednesday-week7-Algebra_I")]
    [InlineData(@"D:\out\step3-10-28-2026-wednesday-week7-Algebra_I-speech_refined.tex", "10-28-2026-wednesday-week7-Algebra_I")]
    [InlineData(@"D:\out\step4-10-28-2026-wednesday-week7-Algebra_I-final.tex", "10-28-2026-wednesday-week7-Algebra_I")]
    public void Clean_base_name_strips_copy_and_offset_suffixes_in_any_order(string path, string expected) {
        Assert.Equal(expected, LectureExtraction.Refinement.LatexRefinementSession.GetCleanBaseName(path));
    }

    [Fact]
    public void Single_pass_refinement_drops_offset_infix_while_multipart_keeps_it() {
        Assert.False(LectureExtraction.Refinement.LatexRefinementSession.UsesOffsetFileNaming("step1-lec.tex", partsCount: 1));
        Assert.False(LectureExtraction.Refinement.LatexRefinementSession.UsesOffsetFileNaming("step1-02-18-2026-all_about_rings.tex", partsCount: 1));
        Assert.False(LectureExtraction.Refinement.LatexRefinementSession.UsesOffsetFileNaming("step3-lec-speech_refined.tex", partsCount: 1));
        Assert.False(LectureExtraction.Refinement.LatexRefinementSession.UsesOffsetFileNaming("step4-lec-final.tex", partsCount: 1));

        Assert.True(LectureExtraction.Refinement.LatexRefinementSession.UsesOffsetFileNaming("step1-lec-all-offset.tex", partsCount: 2));
        Assert.True(LectureExtraction.Refinement.LatexRefinementSession.UsesOffsetFileNaming("step2-lec-offset-merged.tex", partsCount: 1));
        Assert.True(LectureExtraction.Refinement.LatexRefinementSession.UsesOffsetFileNaming("step1-lec-all.tex", partsCount: 1));

        Assert.Equal("step3-lec-speech_refined.tex", LectureExtraction.Refinement.LatexRefinementSession.StepOutputFileName("step3", "lec", "speech_refined", useOffsetNaming: false));
        Assert.Equal("step4-lec-final.tex", LectureExtraction.Refinement.LatexRefinementSession.StepOutputFileName("step4", "lec", "final", useOffsetNaming: false));
        Assert.Equal("step5-lec-last_try1.tex", LectureExtraction.Refinement.LatexRefinementSession.StepOutputFileName("step5", "lec", "last_try1", useOffsetNaming: false));
        Assert.Equal("step3-lec-offset-speech_refined.tex", LectureExtraction.Refinement.LatexRefinementSession.StepOutputFileName("step3", "lec", "speech_refined", useOffsetNaming: true));

        Assert.Contains("complete single-part LaTeX document", LectureExtraction.Refinement.LatexRefinementSession.BuildSpeechRefinementIntroPrompt(isSinglePart: true));
        Assert.Contains("current merged LaTeX document", LectureExtraction.Refinement.LatexRefinementSession.BuildSpeechRefinementIntroPrompt(isSinglePart: false));
    }
}
