using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
/// [AI Context] Covers the InlineHistory switch in SystemInstructionTextBuilder.AppendHistoryFilesAsync
/// as far as it is reachable without a network call: the default path stays inline, a failed upload
/// falls back to inline text, and a type the File API is not sent as text/plain is never tried.
/// A successful upload needs the paid API and is not tested here.
/// [Human] Testet den History-Upload-Schalter ohne echten Upload.
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class HistoryUploadTests {
    private static AttachmentUploader CreateFailingUploader() =>
        new(client: null!, uploadFolder: "", includePaths: [], isAiStudio: true, gcsBucketName: "", uploadMaxRetries: 1) {
            ClientFactory = () => throw new InvalidOperationException("upload refused by test")
        };

    [Fact]
    public async Task Default_inlines_history_text_and_uploads_nothing() {
        AnsiConsole.Console = new TestConsole();
        string dir = CreateTempDir();
        try {
            string md = Path.Combine(dir, "example.md");
            File.WriteAllText(md, "History Content");
            var builder = new StringBuilder();

            var loaded = await SystemInstructionTextBuilder.AppendHistoryFilesAsync([md], builder, dir, CreateFailingUploader());

            Assert.Contains("Here is history reference file", builder.ToString());
            Assert.Contains("example.md", builder.ToString());
            Assert.Contains("History Content", builder.ToString());
            Assert.Empty(loaded.UploadedTextParts);
            Assert.Empty(loaded.MediaParts);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task A_history_image_whose_path_has_a_comma_is_attached() {
        // Non-text history files used to go through `attach "a", "b"`, which split this path in two.
        AnsiConsole.Console = new TestConsole();
        string dir = CreateTempDir();
        try {
            string png = Path.Combine(dir, "board, week 3.png");
            File.WriteAllBytes(png, [0x89, 0x50, 0x4E, 0x47]);
            var uploader = new AttachmentUploader(client: null!, uploadFolder: "", includePaths: [], isAiStudio: true, gcsBucketName: "", inlineImages: true);

            var loaded = await SystemInstructionTextBuilder.AppendHistoryFilesAsync([png], new StringBuilder(), dir, uploader);

            Assert.Contains(loaded.MediaParts, part => part.InlineData != null);
            Assert.Contains(loaded.MediaParts, part => part.Text?.Contains("board, week 3.png") == true);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Interleaves_markdown_and_images_in_input_order() {
        AnsiConsole.Console = new TestConsole();
        string dir = CreateTempDir();
        try {
            string firstMd = Path.Combine(dir, "ex1.md");
            string img = Path.Combine(dir, "ex1.png");
            string secondMd = Path.Combine(dir, "ex2.md");
            File.WriteAllText(firstMd, "First Example");
            File.WriteAllBytes(img, [0x89, 0x50, 0x4E, 0x47]);
            File.WriteAllText(secondMd, "Second Example");
            var uploader = new AttachmentUploader(client: null!, uploadFolder: "", includePaths: [], isAiStudio: true, gcsBucketName: "", inlineImages: true);

            var loaded = await SystemInstructionTextBuilder.AppendHistoryFilesAsync([firstMd, img, secondMd], new StringBuilder(), dir, uploader);

            Assert.NotNull(loaded.OrderedParts);
            Assert.Equal(5, loaded.OrderedParts!.Count);
            Assert.Contains("First Example", loaded.OrderedParts[0].Text);
            Assert.Contains("<image path=\"./ex1.png\">", loaded.OrderedParts[1].Text);
            Assert.NotNull(loaded.OrderedParts[2].InlineData);
            Assert.Equal("\n</image>\n", loaded.OrderedParts[3].Text);
            Assert.Contains("Second Example", loaded.OrderedParts[4].Text);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Failed_upload_is_inlined_as_before() {
        AnsiConsole.Console = new TestConsole();
        string dir = CreateTempDir();
        try {
            string md = Path.Combine(dir, "example.md");
            File.WriteAllText(md, "History Content");
            var builder = new StringBuilder();

            var loaded = await SystemInstructionTextBuilder.AppendHistoryFilesAsync([md], builder, dir, CreateFailingUploader(), uploadTextFiles: true);

            Assert.Contains("History Content", builder.ToString());
            Assert.Empty(loaded.UploadedTextParts);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Text_type_without_text_plain_mime_is_inlined_without_upload() {
        var console = new TestConsole();
        AnsiConsole.Console = console;
        string dir = CreateTempDir();
        try {
            string json = Path.Combine(dir, "data.json");
            File.WriteAllText(json, "{\"a\": 1}");
            var builder = new StringBuilder();

            var loaded = await SystemInstructionTextBuilder.AppendHistoryFilesAsync([json], builder, dir, CreateFailingUploader(), uploadTextFiles: true);

            Assert.Contains("{\"a\": 1}", builder.ToString());
            Assert.Empty(loaded.UploadedTextParts);
            Assert.DoesNotContain("fehlgeschlagen", console.Output);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Uploaded_history_counts_only_its_header_at_the_minute_gate() {
        string header = SystemInstructionTextBuilder.UploadedHistoryFileHeader("conventions/example.md");
        var contents = new List<Content> {
            new() {
                Role = "user",
                Parts = [
                    new Part { Text = header },
                    new Part { FileData = new FileData { FileUri = "https://example.invalid/f", MimeType = "text/plain" } },
                ]
            },
        };

        Assert.Equal(header.Length, MinuteGateEstimate.TextCharacters(null, contents));
    }

    private static string CreateTempDir() {
        string dir = Path.Combine(Path.GetTempPath(), $"history_upload_test_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
