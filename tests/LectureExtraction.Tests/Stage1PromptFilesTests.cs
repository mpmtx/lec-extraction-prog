using System;
using System.IO;
using System.Linq;
using LectureExtraction.Extraction;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// [AI Context] Guards the compiled-in Stage 1 file list. The shape tests run anywhere; the last test compares the
/// list with the prompt-engineering repository and only runs where that repository exists, so it fails when a
/// prompt file was added, renamed or deleted there without updating the list.
/// [Human] Prüft die fest eingetragene Liste der Stage-1-Prompt-Dateien.
/// </summary>
public class Stage1PromptFilesTests {
    [Fact]
    public void Lists_name_each_file_once_relative_to_the_root() {
        var all = Stage1PromptFiles.SystemInstructionFiles.Concat(Stage1PromptFiles.HistoryFiles).ToList();

        Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(all, path => Assert.False(Path.IsPathRooted(path), path));
        Assert.All(all, path => Assert.DoesNotContain('\\', path));
        Assert.All(all, path => Assert.False(HistoryFileResolver.IsIgnoredPromptFile(path), path));
    }

    [Fact]
    public void System_instruction_starts_with_the_protocol_and_its_split_files() {
        Assert.Equal(
            ["transcription.md",
             "transcription/transcription-source-media.md",
             "transcription/transcription-multi-part.md",
             "transcription/transcription-speech-writing.md",
             "transcription/transcription-notation.md",
             "environments.md"],
            Stage1PromptFiles.SystemInstructionFiles.Take(6));
        Assert.All(Stage1PromptFiles.HistoryFiles, path => Assert.StartsWith("training-history/", path));
    }

    [Fact]
    public void Absolute_paths_keep_the_order_and_sit_under_the_root() {
        string root = Path.GetFullPath(Stage1PromptFiles.Root) + Path.DirectorySeparatorChar;

        Assert.Equal(Stage1PromptFiles.SystemInstructionFiles.Count, Stage1PromptFiles.SystemInstructionPaths.Length);
        Assert.EndsWith("transcription.md", Stage1PromptFiles.SystemInstructionPaths[0]);
        Assert.All(Stage1PromptFiles.SystemInstructionPaths.Concat(Stage1PromptFiles.HistoryPaths),
            path => Assert.StartsWith(root, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void List_matches_the_prompt_repository_where_it_exists() {
        if (!Directory.Exists(Stage1PromptFiles.Root)) return;

        var missing = Stage1PromptFiles.SystemInstructionPaths.Concat(Stage1PromptFiles.HistoryPaths)
            .Where(path => !File.Exists(path)).ToList();

        Assert.True(missing.Count == 0, "Listed but missing: " + string.Join(", ", missing));
        var unlisted = Stage1PromptFiles.FindUnlistedFiles();
        Assert.True(unlisted.Count == 0, "In the prompt folders but not listed: " + string.Join(", ", unlisted));
    }
}
