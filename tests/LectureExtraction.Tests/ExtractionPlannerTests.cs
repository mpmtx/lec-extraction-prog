using System;
using System.IO;
using System.Linq;
using LectureExtraction.Cli;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the pre-flight report. Its whole value is being trustworthy about cost, so the tests
/// concentrate on the two numbers a caller decides on - how many requests are still pending, and
/// how many parts can be reused - plus the warnings that catch an expensive mistake before it is
/// made.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class ExtractionPlannerTests : IDisposable {
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lecx-plan-" + Guid.NewGuid().ToString("N"));

    public ExtractionPlannerTests() => Directory.CreateDirectory(_root);

    public void Dispose() {
        try {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) {
            Console.WriteLine($"[Test cleanup] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private AiStudioAutoExtractionConfig Config(int parts = 2) {
        var config = new AiStudioAutoExtractionConfig { NumberOfParts = parts };
        config.SourceFolder = _root;
        config.TargetFolder = Path.Combine(_root, "out");
        return config;
    }

    private string Video(string name) {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "not a real video");
        return path;
    }

    /// <summary>Writes a finished part .tex where the pipeline would, so the planner can find it.</summary>
    private void WriteFinishedPart(AiStudioAutoExtractionConfig config, string videoPath, int part, TimeSpan age, string suffix = "") {
        string folder = Path.Combine(config.TargetFolder, ExtractionHelpers.ComputeOutputFolderName(videoPath));
        Directory.CreateDirectory(folder);

        string texPath = Path.Combine(folder, $"{ExtractionHelpers.ComputeTexBaseName(videoPath)}-part{part}{suffix}.tex");
        File.WriteAllText(texPath, "\\section{done}");
        File.SetLastWriteTime(texPath, DateTime.Now - age);
    }

    [Fact]
    public void PendingRequests_IsSegmentsTimesVideos_WhenNothingExistsYet() {
        var config = Config(parts: 3);
        string[] videos = [Video("02-16-2026-monday-week1-Analysis.mp4"), Video("02-18-2026-wednesday-week1-Analysis.mp4")];

        var plan = ExtractionPlanner.Build(config, videos, ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal(2, plan.VideoCount);
        Assert.Equal(6, plan.PendingRequests);
        Assert.Equal(0, plan.ResumableSegments);
    }

    [Fact]
    public void AFreshPart_CountsAsResumable_AndIsNotBilledAgain() {
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromMinutes(30));

        var plan = ExtractionPlanner.Build(config, [video], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal(1, plan.ResumableSegments);
        Assert.Equal(1, plan.PendingRequests);
    }

    [Fact]
    public void AStalePart_FallsOutsideTheWindow_AndIsBilledAgain() {
        // The behaviour that costs money silently: the same file, three hours later, is re-requested.
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromHours(3));

        var plan = ExtractionPlanner.Build(config, [video], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal(0, plan.ResumableSegments);
        Assert.Equal(2, plan.PendingRequests);
    }

    [Fact]
    public void AWiderResumeWindow_RescuesTheSameStalePart() {
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromHours(3));

        var plan = ExtractionPlanner.Build(config, [video], resumeWindowHours: 24, force: false);

        Assert.Equal(1, plan.ResumableSegments);
    }

    [Fact]
    public void AFreshCopyOfAStalePart_IsReused_AsTheRunReusesIt() {
        // A part transcribed again is written as -copy-1 next to the stale original; the run reuses
        // the copy, so the plan must not count the part as pending.
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromHours(5));
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromMinutes(20), suffix: "-copy-1");

        var plan = ExtractionPlanner.Build(config, [video], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal(1, plan.ResumableSegments);
        Assert.Equal(1, plan.PendingRequests);
    }

    [Fact]
    public void Force_IgnoresEveryExistingPart() {
        var config = Config(parts: 2);
        string video = Video("02-16-2026-monday-week1-Analysis.mp4");
        WriteFinishedPart(config, video, part: 1, age: TimeSpan.FromMinutes(5));

        var plan = ExtractionPlanner.Build(config, [video], ExtractionPlanner.DefaultResumeWindowHours, force: true);

        Assert.Equal(0, plan.ResumableSegments);
        Assert.Equal(2, plan.PendingRequests);
    }

    [Fact]
    public void BothVariantsOfOneLecture_AreReportedAsAnOutputFolderCollision() {
        // The real hazard: a source folder holding lecture.mp4 and lecture-speed-1-compressed.mp4
        // resolves both to one output folder, so each reads the other's parts as its own cache.
        var config = Config();
        string[] videos = [
            Video("02-16-2026-monday-week1-Analysis.mp4"),
            Video("02-16-2026-monday-week1-Analysis-speed-1-compressed.mp4")
        ];

        var plan = ExtractionPlanner.Build(config, videos, ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Single(plan.Videos.Select(v => v.OutputFolder).Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(plan.Warnings, warning => warning.Contains("share the output folder"));
    }

    [Fact]
    public void AVideoOutsideTheNamingScheme_IsWarnedAbout() {
        var config = Config();

        var plan = ExtractionPlanner.Build(config, [Video("random-recording.mp4")], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Contains(plan.Warnings, warning => warning.Contains("naming scheme"));
    }

    [Fact]
    public void Videos_AreOrderedChronologically() {
        var config = Config();
        string[] videos = [
            Video("03-02-2026-monday-week3-Analysis.mp4"),
            Video("02-16-2026-monday-week1-Analysis.mp4")
        ];

        var plan = ExtractionPlanner.Build(config, videos, ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.Equal("02-16-2026-monday-week1-Analysis.mp4", plan.Videos[0].FileName);
    }

    [Fact]
    public void TargetFolder_DefaultsUnderTheSourceFolder_AsTheSessionDoes() {
        var config = Config();
        config.TargetFolder = "";

        var plan = ExtractionPlanner.Build(config, [Video("02-16-2026-monday-week1-Analysis.mp4")], 2, force: false);

        Assert.Equal(Path.Combine(_root, "extracted_output"), plan.TargetFolder);
    }

    [Fact]
    public void Warnings_IncludesMissingPromptPaths_WhenConfiguredPathDoesNotExist() {
        var config = Config(parts: 1);
        string missingPrompt = Path.Combine(_root, "non_existent_prompt.txt");
        config.SystemInstructionPaths = [missingPrompt];

        var plan = ExtractionPlanner.Build(config, [Video("02-16-2026-monday-week1-Analysis.mp4")], ExtractionPlanner.DefaultResumeWindowHours, force: false);

        Assert.NotNull(plan.MissingPromptPaths);
        Assert.Contains(missingPrompt, plan.MissingPromptPaths);
        Assert.Contains(plan.Warnings, w => w.Contains(missingPrompt));
    }
}

/// <summary>The base-name derivation the planner and the pipeline must agree on.</summary>
public class ExtractionHelpersNamingTests {
    [Theory]
    [InlineData("lecture-speed-1-compressed.mp4", "lecture")]
    [InlineData("lecture-speed-1.25-compressed.mp4", "lecture")]
    [InlineData("lecture-compressed.mp4", "lecture")]
    [InlineData("lecture.mp4", "lecture")]
    [InlineData("lecture.mp4.mp4", "lecture")]
    [InlineData("lecture-compressed.webm.mp4", "lecture")]
    public void ComputeOutputFolderName_StripsCompressionSuffixes(string fileName, string expected) {
        Assert.Equal(expected, ExtractionHelpers.ComputeOutputFolderName(fileName));
    }

    [Theory]
    [InlineData("vorlesung.MOV", "vorlesung")]
    [InlineData("vorlesung.mkv.avi", "vorlesung")]
    [InlineData("vorlesung.v2", "vorlesung.v2")]
    public void StripVideoExtensions_RemovesOnlyVideoExtensions(string name, string expected) {
        Assert.Equal(expected, ExtractionHelpers.StripVideoExtensions(name));
    }

    [Fact]
    public void ComputeTexBaseName_AddsTheStageOnePrefix() {
        Assert.Equal("step1-lecture", ExtractionHelpers.ComputeTexBaseName("lecture-speed-1-compressed.mp4"));
    }

    [Fact]
    public void ComputeTexBaseName_DoesNotDoubleThePrefix() {
        Assert.Equal("step1-lecture", ExtractionHelpers.ComputeTexBaseName("step1-lecture.mp4"));
    }

    [Fact]
    public void OutputFolderName_AndTexBaseName_DifferByThePrefix() {
        // Stated as a test because predicting .tex paths from the folder name alone is a natural
        // mistake that would look for files the pipeline never writes.
        Assert.Equal("step1-" + ExtractionHelpers.ComputeOutputFolderName("lecture.mp4"),
                     ExtractionHelpers.ComputeTexBaseName("lecture.mp4"));
    }

    [Theory]
    [InlineData("lecture-speed-1-compressed.mp4", true)]
    [InlineData("lecture-speed-1.5-compressed.mp4", true)]
    [InlineData("lecture-compressed.mp4", true)]
    [InlineData("lecture-speed-1-compressed", true)]
    [InlineData("lecture-compressed", true)]
    [InlineData("lecture.mp4", false)]
    [InlineData("lecture-speed-1.mp4", false)]
    public void IsPreCompressed_DetectsCompressedMarkers(string fileName, bool expected) {
        Assert.Equal(expected, ExtractionHelpers.IsPreCompressed(fileName));
    }

    [Fact]
    public void DeduplicatePreferCompressed_PicksCompressedOverRawVariant() {
        string[] inputs = [
            "02-16-2026-monday-week1-Analysis_II.mp4",
            "02-16-2026-monday-week1-Analysis_II-speed-1-compressed.mp4",
            "02-18-2026-wednesday-week1-Analysis_II.mp4"
        ];

        var result = ExtractionHelpers.DeduplicatePreferCompressed(inputs);

        Assert.Equal(2, result.Count);
        Assert.Contains("02-16-2026-monday-week1-Analysis_II-speed-1-compressed.mp4", result);
        Assert.Contains("02-18-2026-wednesday-week1-Analysis_II.mp4", result);
    }

    [Fact]
    public void ResolveCompressedVariantIfExists_ResolvesToSiblingWhenPresent() {
        string tempDir = Path.Combine(Path.GetTempPath(), "lec_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try {
            string rawFile = Path.Combine(tempDir, "02-16-2026-monday-week1-Analysis_II.mp4");
            string compressedFile = Path.Combine(tempDir, "02-16-2026-monday-week1-Analysis_II-speed-1-compressed.mp4");
            File.WriteAllText(rawFile, "fake raw");
            File.WriteAllText(compressedFile, "fake compressed");

            string resolved = ExtractionHelpers.ResolveCompressedVariantIfExists(rawFile);
            Assert.Equal(compressedFile, resolved);

            // If already compressed, it returns itself
            Assert.Equal(compressedFile, ExtractionHelpers.ResolveCompressedVariantIfExists(compressedFile));
        }
        finally {
            Directory.Delete(tempDir, true);
        }
    }
}

/// <summary>The durations the per-part prompt announces; the hour used to be dropped.</summary>
public class ExtractionHelpersDurationTests {
    [Theory]
    [InlineData(1979, "32 minutes and 59 seconds (00:32:59)")]               // algebra-1 week 2, part 1
    [InlineData(5450.88, "1 hour, 30 minutes and 50 seconds (01:30:50)")]    // the whole lecture, was "30 minutes and 50 seconds"
    [InlineData(7261, "2 hours, 1 minute and 1 second (02:01:01)")]
    [InlineData(59, "0 minutes and 59 seconds (00:00:59)")]
    public void FormatDurationForPrompt_KeepsTheHours(double seconds, string expected) {
        Assert.Equal(expected, ExtractionHelpers.FormatDurationForPrompt(seconds));
    }
}
