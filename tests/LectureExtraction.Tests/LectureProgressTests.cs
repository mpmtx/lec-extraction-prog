using System;
using System.IO;
using System.Linq;
using System.Threading;
using LectureExtraction.Cli.Commands;
using LectureExtraction.Extraction;
using Xunit;
using File = System.IO.File;

namespace LectureExtraction.Tests;

/// <summary>
/// Which stage a lecture folder needs next, read from the files the pipeline writes, and the command
/// 'lecx finish' runs for it. The mapping is what keeps finish from paying twice: re-running 'run'
/// after a refinement failure re-merges and renames every later file.
/// </summary>
public class LectureProgressTests : IDisposable {
    private const string Lecture = "02-18-2026-wednesday-week1-Analysis_II";
    private readonly string _root = Directory.CreateTempSubdirectory("lecture-progress-").FullName;
    private readonly string _folder;

    public LectureProgressTests() {
        _folder = Path.Combine(_root, Lecture);
        Directory.CreateDirectory(_folder);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Touch(string name) {
        File.WriteAllText(Path.Combine(_folder, name), "x");
        // Distinct write times, so "newest" is deterministic.
        Thread.Sleep(15);
    }

    [Fact]
    public void An_empty_folder_is_not_started() {
        Directory.CreateDirectory(Path.Combine(_folder, "tmp"));

        Assert.Equal(LectureStage.NotStarted, LectureProgress.Scan(_folder).NextStage);
    }

    [Fact]
    public void Parts_without_the_combined_file_continue_the_transcription() {
        Touch($"step1-{Lecture}-part1.tex");
        Touch($"step1-{Lecture}-part1-offset.tex");
        Touch($"step1-{Lecture}-part2.tex");

        var p = LectureProgress.Scan(_folder);

        Assert.Equal((LectureStage.Transcribe, 2), (p.NextStage, p.PartsCount));
        Assert.Null(p.NextInput);
    }

    [Fact]
    public void Each_refinement_step_reads_the_newest_file_with_its_prefix() {
        Touch($"step1-{Lecture}-all.tex");
        Touch($"step1-{Lecture}-all-offset.tex");
        Assert.Equal($"step1-{Lecture}-all-offset.tex", Path.GetFileName(LectureProgress.Scan(_folder).NextInput));

        Touch($"step1-{Lecture}-all-offset-copy-1.tex");
        Assert.Equal($"step1-{Lecture}-all-offset-copy-1.tex", Path.GetFileName(LectureProgress.Scan(_folder).NextInput));

        Touch($"step2-{Lecture}-all-offset-copy-1-offset-merged.tex");
        var p = LectureProgress.Scan(_folder);
        Assert.Equal(LectureStage.Speech, p.NextStage);
        Assert.Equal($"step2-{Lecture}-all-offset-copy-1-offset-merged.tex", Path.GetFileName(p.NextInput));
    }

    [Fact]
    public void A_compiles_standalone_main_copy_is_no_steps_input() {
        Touch($"step1-{Lecture}-all-offset.tex");
        Touch($"step4-{Lecture}-all-offset-final.tex");
        Touch($"step4-{Lecture}-all-offset-final-main.tex");

        var p = LectureProgress.Scan(_folder);

        Assert.Equal(LectureStage.Pdf, p.NextStage);
        Assert.Equal($"step4-{Lecture}-all-offset-final.tex", Path.GetFileName(p.NextInput));
    }

    [Fact]
    public void A_failed_steps_truncated_fragment_is_neither_its_output_nor_the_next_input() {
        // Information Security 02-29, 2026-11-02: step 2 broke off, was redone, then step 3 broke off.
        Touch($"step1-{Lecture}-all-offset.tex");
        Touch($"step2-{Lecture}-offset-merged-truncated.tex");
        var p = LectureProgress.Scan(_folder);
        Assert.Equal(LectureStage.Merge, p.NextStage);
        Assert.Equal($"step1-{Lecture}-all-offset.tex", Path.GetFileName(p.NextInput));

        Touch($"step2-{Lecture}-offset-merged.tex");
        Touch($"step3-{Lecture}-offset-speech_refined-truncated.tex");
        Touch($"step4-{Lecture}-offset-final-truncated.tex");
        p = LectureProgress.Scan(_folder);

        Assert.Equal(LectureStage.Speech, p.NextStage);
        Assert.Equal($"step2-{Lecture}-offset-merged.tex", Path.GetFileName(p.NextInput));
        Assert.Equal(
            ["refine", "run", "--tex", p.NextInput!, "--step", "2", "--through-end"],
            FinishCommand.PlanActions([p], _ => null, []).Single().Arguments!);
    }

    [Fact]
    public void The_lectures_pdf_means_done() {
        Touch($"step4-{Lecture}-all-offset-final.tex");
        Touch($"{Lecture}.pdf");

        Assert.Equal(LectureStage.Done, LectureProgress.Scan(_folder).NextStage);
    }

    [Fact]
    public void Finish_continues_each_stage_with_its_own_command() {
        LectureProgress At(string name, int parts = 4, string? combined = null, string? merged = null, string? speech = null, string? final = null, string? pdf = null) =>
            new(Path.Combine(_root, name), parts, combined, merged, speech, final, pdf, 0);

        var actions = FinishCommand.PlanActions([
            At("c-polish", speech: "s.tex"),
            At("a-transcribe", parts: 2),
            At("b-merge", combined: "c.tex"),
            At("d-done", final: "f.tex", pdf: "x.pdf"),
            At("e-empty", parts: 0),
            At("f-speech", merged: "m.tex"),
            At("g-pdf", final: "f.tex"),
        ], lecture => $"V:/{lecture}.mp4", ["--profile", "2"]);

        Assert.Equal(
            new[] {
                $"lecx run --video V:/a-transcribe.mp4 --resume --out {_root} --profile 2",
                "lecx refine run --tex c.tex --profile 2",
                "lecx refine run --tex s.tex --step 3 --through-end --profile 2",
                "lecx refine run --tex m.tex --step 2 --through-end --profile 2",
                "lecx pdf compile --tex f.tex --profile 2",
            },
            actions.Select(a => FinishCommand.CommandLine(a.Arguments!)));
    }

    [Fact]
    public void A_lecture_without_its_video_is_listed_with_the_reason_instead_of_a_command() {
        var action = FinishCommand.PlanActions([new LectureProgress(_folder, 2, null, null, null, null, null, 0)], _ => null, []).Single();

        Assert.Null(action.Arguments);
        Assert.Contains("--source", action.Problem);
    }

    [Theory]
    [InlineData("02-16-2026-monday-week1-Analysis_II-_mehrere_Variablen-speed-1-compressed.mp4", "2026-02-16 Mo W1")]
    [InlineData("04-28-25-tuesday", "2025-04-28 Di")]
    [InlineData("old", "old")]
    public void The_short_label_is_date_weekday_and_week(string name, string expected) {
        Assert.Equal(expected, LectureLabel.Short(name));
    }

    [Fact]
    public void Same_stage_lectures_are_ordered_chronologically_by_date_not_month_string() {
        var actions = FinishCommand.PlanActions([
            new LectureProgress(Path.Combine(_root, "02-18-2026-wednesday"), 3, "c2026.tex", null, null, null, null, 0),
            new LectureProgress(Path.Combine(_root, "12-15-2025-monday"), 3, "c2025.tex", null, null, null, null, 0),
        ], _ => null, []);

        Assert.Equal(["12-15-2025-monday", "02-18-2026-wednesday"], actions.Select(a => a.Lecture));
    }
}
