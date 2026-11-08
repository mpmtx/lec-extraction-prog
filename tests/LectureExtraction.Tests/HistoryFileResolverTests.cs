using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;
using File = System.IO.File;

namespace LectureExtraction.Tests;

/// <summary>
/// [AI Context] Tests token-optimization file filtering: montage pictures are retained while
/// individual non-montage pictures (and draft files) prefixed with "ignored-" (or a legacy
/// "ignore-", "deleted-", "delted-") are excluded
/// from system instructions and history preloads sent to Gemini.
/// [Human] Testet das Herausfiltern von mit "ignored-" markierten Nicht-Montage-Bildern
/// bei der System-Instruction- und History-Erstellung.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class HistoryFileResolverTests {
    [Theory]
    [InlineData("image.png", true)]
    [InlineData("photo.jpg", true)]
    [InlineData("picture.jpeg", true)]
    [InlineData("graphic.webp", true)]
    [InlineData("diagram.svg", true)]
    [InlineData("document.md", false)]
    [InlineData("code.cs", false)]
    [InlineData("data.json", false)]
    [InlineData("latex.tex", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsPictureFile_identifies_image_extensions(string? path, bool expected) {
        Assert.Equal(expected, HistoryFileResolver.IsPictureFile(path!));
    }

    [Theory]
    [InlineData("deleted-slide1.png", true)]
    [InlineData("DELETED-whiteboard.jpg", true)]
    [InlineData("deleted-figure.webp", true)]
    [InlineData("deleted-draft.md", true)]
    [InlineData("deleted-....png", true)]
    [InlineData("delted-slide1.png", true)]
    [InlineData("DELTED-whiteboard.jpg", true)]
    [InlineData("ignore-slide2.png", true)]
    [InlineData("IGNORE-diagram.png", true)]
    [InlineData("ignored-notes.md", true)]
    [InlineData("ignored-auswahlaxiom.jpg", true)]
    [InlineData("IGNORED-board.png", true)]
    [InlineData("not-ignored-board.png", false)]
    [InlineData("montage-slide1.png", false)]
    [InlineData("slide1.png", false)]
    [InlineData("environments.md", false)]
    [InlineData("transcription.md", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsIgnoredPromptFile_identifies_all_ignore_prefixes(string? path, bool expected) {
        Assert.Equal(expected, HistoryFileResolver.IsIgnoredPromptFile(path!));
        Assert.Equal(expected, HistoryFileResolver.IsDeletedPromptFile(path!));
    }

    [Fact]
    public void ResolveHistoryFiles_excludes_files_with_deleted_and_ignore_prefixes() {
        AnsiConsole.Console = new TestConsole();
        string tempDir = Path.Combine(Path.GetTempPath(), $"hist_test_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);

        try {
            string montageImg = Path.Combine(tempDir, "montage-board.png");
            string deletedImg1 = Path.Combine(tempDir, "deleted-board1.png");
            string deltedImg = Path.Combine(tempDir, "delted-board2.jpg");
            string ignoreImg = Path.Combine(tempDir, "ignore-board3.png");
            string notesMd = Path.Combine(tempDir, "lecture-notes.md");
            string deletedNotes = Path.Combine(tempDir, "deleted-old-notes.md");

            File.WriteAllText(montageImg, "dummy png");
            File.WriteAllText(deletedImg1, "dummy png");
            File.WriteAllText(deltedImg, "dummy jpg");
            File.WriteAllText(ignoreImg, "dummy png");
            File.WriteAllText(notesMd, "# Notes");
            File.WriteAllText(deletedNotes, "# Old Notes");

            var resolved = HistoryFileResolver.ResolveHistoryFiles(
                [montageImg, deletedImg1, deltedImg, ignoreImg, notesMd, deletedNotes]);

            Assert.Equal([montageImg, notesMd], resolved);
        }
        finally {
            Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Folders are never expanded (2026-11-08): a prompt file is loaded only when it is named on its own, so
    /// nothing reaches the model just by sitting in a folder, and the order is the list's order.
    /// </summary>
    [Fact]
    public void ResolveHistoryFiles_does_not_load_a_folder_and_keeps_the_given_order() {
        AnsiConsole.Console = new TestConsole();
        string tempDir = Path.Combine(Path.GetTempPath(), $"hist_test_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);

        try {
            string first = Path.Combine(tempDir, "z-first.md");
            string second = Path.Combine(tempDir, "a-second.md");
            File.WriteAllText(first, "# Z");
            File.WriteAllText(second, "# A");

            Assert.Empty(HistoryFileResolver.ResolveHistoryFiles([tempDir]));
            Assert.Equal([first, second], HistoryFileResolver.ResolveHistoryFiles([first, tempDir, second, first]));
        }
        finally {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task AttachmentUploader_skips_deleted_file_when_asSystemInstruction_is_true() {
        AnsiConsole.Console = new TestConsole();
        string tempFile = Path.Combine(Path.GetTempPath(), "deleted-board.png");
        File.WriteAllText(tempFile, "fake image bytes");

        try {
            var uploader = new AttachmentUploader(
                client: null!, uploadFolder: "", includePaths: [], isAiStudio: true, gcsBucketName: "");
            var parts = new List<Part>();

            bool result = await uploader.UploadAndAttachFileAsync(tempFile, parts, asSystemInstruction: true);

            Assert.True(result);
            Assert.Empty(parts);
        }
        finally {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SystemInstructionTextBuilder_BuildAsync_excludes_deleted_files() {
        AnsiConsole.Console = new TestConsole();
        string tempDir = Path.Combine(Path.GetTempPath(), $"builder_test_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);

        try {
            string validDoc = Path.Combine(tempDir, "valid.md");
            string deletedDoc = Path.Combine(tempDir, "deleted-obsolete.md");

            File.WriteAllText(validDoc, "Valid Instruction Content");
            File.WriteAllText(deletedDoc, "Should Not Appear");

            string result = await SystemInstructionTextBuilder.BuildAsync([validDoc, deletedDoc], [], tempDir);

            Assert.Contains("valid.md", result);
            Assert.Contains("Valid Instruction Content", result);
            Assert.DoesNotContain("deleted-obsolete.md", result);
            Assert.DoesNotContain("Should Not Appear", result);
        }
        finally {
            Directory.Delete(tempDir, true);
        }
    }
}
