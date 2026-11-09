using LectureExtraction.Extraction;

namespace LectureExtraction.Tests;

/// <summary>
/// Characterization tests for <see cref="FileTreeRenderer"/>.
/// </summary>
public class FileTreeRendererTests {
    // ---- CleanCopySuffix ---------------------------------------------------
    // Windows Explorer and the FFmpeg step both produce "- Kopie" / "-Copy" names.

    [Theory]
    [InlineData("lecture - Kopie", "lecture")]
    [InlineData("lecture-Copy", "lecture")]
    [InlineData("lecture - copy", "lecture")]
    [InlineData("lecture", "lecture")]
    [InlineData("", "")]
    public void CleanCopySuffix_RemovesGermanAndEnglishCopyMarkers(string input, string expected) {
        Assert.Equal(expected, FileTreeRenderer.CleanCopySuffix(input));
    }

    // ---- NormalizeRelativePath --------------------------------------------
    // Produces the "./a/b.tex" form used inside the generated markdown file trees.

    [Theory]
    [InlineData("sub\\file.tex", "./sub/file.tex")]
    [InlineData("sub/file.tex", "./sub/file.tex")]
    [InlineData("./sub/file.tex", "./sub/file.tex")]
    [InlineData("/abs/file.tex", "./abs/file.tex")]
    public void NormalizeRelativePath_ProducesForwardSlashedDotPrefixedPaths(string input, string expected) {
        Assert.Equal(expected, FileTreeRenderer.NormalizeRelativePath(input));
    }

    [Fact]
    public void NormalizeRelativePath_AlsoStripsCopySuffixes() {
        Assert.Equal("./lecture.tex", FileTreeRenderer.NormalizeRelativePath("lecture - Kopie.tex"));
    }

    // ---- FindCommonBaseDirectory ------------------------------------------

    [Fact]
    public void FindCommonBaseDirectory_OfSiblingFiles_IsTheirSharedFolder() {
        string folder = Path.Combine(Path.GetTempPath(), "lec-test", "a", "b");
        List<string> paths = [Path.Combine(folder, "1.tex"), Path.Combine(folder, "2.tex")];

        Assert.Equal(folder, FileTreeRenderer.FindCommonBaseDirectory(paths));
    }

    [Fact]
    public void FindCommonBaseDirectory_WalksUpUntilEveryPathIsCovered() {
        string root = Path.Combine(Path.GetTempPath(), "lec-test", "a");
        List<string> paths = [
            Path.Combine(root, "b", "1.tex"),
            Path.Combine(root, "c", "2.tex")
        ];

        Assert.Equal(root, FileTreeRenderer.FindCommonBaseDirectory(paths));
    }

    [Fact]
    public void FindCommonBaseDirectory_OfNothing_IsNull() {
        Assert.Null(FileTreeRenderer.FindCommonBaseDirectory([]));
    }

    // ---- GenerateMarkdownFileTree ------------------------------------------
    // The list goes into every Stage 1 request as the prompt's table of contents, so it must keep
    // the order the files are sent in, not sort them.

    [Fact]
    public void GenerateMarkdownFileTree_KeepsTheGivenOrder() {
        string root = Path.Combine(Path.GetTempPath(), "lec-test", "prompt");
        List<string> paths = [
            Path.Combine(root, "transcription.md"),
            Path.Combine(root, "environments", "environments-syntax-rules.md"),
            Path.Combine(root, "environments", "environments-content.md"),
            Path.Combine(root, "big-examples.md"),
        ];

        string list = FileTreeRenderer.GenerateMarkdownFileTree(paths, root);

        Assert.Equal(
            $"Root: {root}\n- ./transcription.md\n- ./environments/environments-syntax-rules.md\n" +
            "- ./environments/environments-content.md\n- ./big-examples.md\n",
            list.Replace("\r\n", "\n"));
    }

    [Fact]
    public void GenerateMarkdownFileTree_ListsADuplicateOnce() {
        string root = Path.Combine(Path.GetTempPath(), "lec-test", "prompt");
        string file = Path.Combine(root, "a.md");

        string list = FileTreeRenderer.GenerateMarkdownFileTree([file, file], root);

        Assert.Equal($"Root: {root}\n- ./a.md\n", list.Replace("\r\n", "\n"));
    }
}
