using System;
using System.IO;
using LectureExtraction.Extraction;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the reuse rule shared by the run, its pre-upload and the plan. The case that cost money:
/// a part transcribed again lands in "-partN-copy-1.tex", and the old check kept finding the stale
/// "-partN.tex", so every later run paid for the part once more and wrote another copy.
/// </summary>
public class PartReuseTests : IDisposable {
    private const string BaseName = "step1-02-16-2026-monday-week1-Analysis";
    private static readonly TimeSpan Window = TimeSpan.FromHours(2);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "lecx-reuse-" + Guid.NewGuid().ToString("N"));

    public PartReuseTests() => Directory.CreateDirectory(_folder);

    public void Dispose() {
        try {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) {
            Console.WriteLine($"[Test cleanup] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string WritePart(string fileName, TimeSpan age) {
        string path = Path.Combine(_folder, fileName);
        File.WriteAllText(path, "\\section{done}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }

    [Fact]
    public void A_missing_folder_has_nothing_to_reuse() {
        Assert.Null(PartReuse.FindReusablePart(Path.Combine(_folder, "absent"), BaseName, 1, Window));
    }

    [Fact]
    public void A_fresh_part_is_reused_and_a_stale_one_is_not() {
        string fresh = WritePart($"{BaseName}-part1.tex", TimeSpan.FromMinutes(30));
        WritePart($"{BaseName}-part2.tex", TimeSpan.FromHours(3));

        Assert.Equal(fresh, PartReuse.FindReusablePart(_folder, BaseName, 1, Window));
        Assert.Null(PartReuse.FindReusablePart(_folder, BaseName, 2, Window));
    }

    [Fact]
    public void A_fresh_copy_next_to_a_stale_original_is_reused() {
        WritePart($"{BaseName}-part1.tex", TimeSpan.FromHours(5));
        string copy = WritePart($"{BaseName}-part1-copy-1.tex", TimeSpan.FromMinutes(10));

        Assert.Equal(copy, PartReuse.FindReusablePart(_folder, BaseName, 1, Window));
    }

    [Fact]
    public void The_newest_copy_wins_and_its_age_decides() {
        WritePart($"{BaseName}-part1.tex", TimeSpan.FromHours(9));
        WritePart($"{BaseName}-part1-copy-1.tex", TimeSpan.FromHours(7));
        string newest = WritePart($"{BaseName}-part1-copy-2.tex", TimeSpan.FromHours(4));

        Assert.Equal(newest, PartReuse.FindNewestPartFile(_folder, BaseName, 1));
        Assert.Null(PartReuse.FindReusablePart(_folder, BaseName, 1, Window));
        Assert.Equal(newest, PartReuse.FindReusablePart(_folder, BaseName, 1, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void An_original_newer_than_its_copies_wins() {
        // What lecx clean --copies leaves behind: the newest text promoted to the original name.
        WritePart($"{BaseName}-part1-copy-1.tex", TimeSpan.FromHours(4));
        string original = WritePart($"{BaseName}-part1.tex", TimeSpan.FromMinutes(1));

        Assert.Equal(original, PartReuse.FindReusablePart(_folder, BaseName, 1, Window));
    }

    [Fact]
    public void On_equal_times_the_original_wins() {
        string original = WritePart($"{BaseName}-part1.tex", TimeSpan.FromMinutes(5));
        string copy = WritePart($"{BaseName}-part1-copy-1.tex", TimeSpan.Zero);
        File.SetLastWriteTimeUtc(copy, File.GetLastWriteTimeUtc(original));

        Assert.Equal(original, PartReuse.FindNewestPartFile(_folder, BaseName, 1));
    }

    [Theory]
    [InlineData("-part11.tex")]
    [InlineData("-part1-offset.tex")]
    [InlineData("-part1-offset-copy-1.tex")]
    [InlineData("-part1-copy-x.tex")]
    [InlineData("-part1.tex.bak")]
    public void Other_files_starting_like_the_part_do_not_count(string suffix) {
        WritePart(BaseName + suffix, TimeSpan.FromMinutes(1));

        Assert.Null(PartReuse.FindNewestPartFile(_folder, BaseName, 1));
    }

    [Fact]
    public void A_zero_window_reuses_nothing() {
        // --force builds the session with a zero window.
        WritePart($"{BaseName}-part1.tex", TimeSpan.FromMinutes(1));

        Assert.Null(PartReuse.FindReusablePart(_folder, BaseName, 1, TimeSpan.Zero));
    }

    [Fact]
    public void Single_part_mode_uses_bare_tex_stem_and_ignores_multipart_siblings() {
        WritePart($"{BaseName}-part1.tex", TimeSpan.FromMinutes(1));
        WritePart($"{BaseName}-all.tex", TimeSpan.FromMinutes(1));
        WritePart($"{BaseName}-all-offset.tex", TimeSpan.FromMinutes(1));

        Assert.Null(PartReuse.FindNewestPartFile(_folder, BaseName, 1, totalParts: 1));

        WritePart($"{BaseName}.tex", TimeSpan.FromHours(5));
        string copy = WritePart($"{BaseName}-copy-1.tex", TimeSpan.FromMinutes(10));

        Assert.Equal(copy, PartReuse.FindReusablePart(_folder, BaseName, 1, Window, totalParts: 1));
    }

    [Fact]
    public void Video_segment_filename_omits_part1_for_single_pass_uploads() {
        Assert.Equal(Path.Combine(_folder, "lec.mp4"), VideoSegmentProducer.SegmentFilePath(_folder, "lec", 1, 1));
        Assert.Equal(Path.Combine(_folder, "lec-part1.mp4"), VideoSegmentProducer.SegmentFilePath(_folder, "lec", 1, 2));
        Assert.Equal(Path.Combine(_folder, "lec-part2.mp4"), VideoSegmentProducer.SegmentFilePath(_folder, "lec", 2, 2));
    }
}
