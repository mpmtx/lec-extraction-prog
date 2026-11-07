using System.Collections.Generic;
using System.Reflection;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.Refinement;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins <see cref="RefinementOptions"/> against the four telescoping constructors it replaced.
///
/// <para>Each factory must leave <see cref="LatexRefinementSession"/> believing exactly what the
/// corresponding old constructor left it believing - which of the pipeline's three input modes is
/// active is decided purely by which of these fields are null, so a single misplaced null silently
/// switches the session to a different mode instead of failing. The assertions therefore read the
/// session's own fields rather than the options object: what matters is what the session ends up
/// believing, not what was handed to it.</para>
/// </summary>
public class RefinementOptionsTests {
    private static readonly string[] TwoParts = ["a.tex", "b.tex"];

    /// <summary>The session's state after construction, as the old constructors would have set it.</summary>
    private static (string? Single, string[]? Multiple, IAutoExtractionConfig? Extraction, string? Audio, List<Part>? PreUploaded) StateOf(RefinementOptions options) {
        // The client is never touched by the constructor, so a null keeps the test off the network.
        var session = new LatexRefinementSession(null!, options);

        T Field<T>(string name) => (T)typeof(LatexRefinementSession)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(session)!;

        return (Field<string?>("_singleFilePathToProcess"),
                Field<string[]?>("_multipleFilesToProcess"),
                Field<IAutoExtractionConfig?>("_extractionConfig"),
                Field<string?>("_audioFilePath"),
                Field<List<Part>?>("_preUploadedAudioAttachments"));
    }

    [Fact]
    public void ForConfiguredFolder_matches_the_two_argument_constructor() {
        var config = new LatexRefinementSessionConfig();

        var state = StateOf(RefinementOptions.ForConfiguredFolder(config));

        Assert.Null(state.Single);
        Assert.Null(state.Multiple);
        Assert.Null(state.Extraction);
        Assert.Null(state.Audio);
        Assert.Null(state.PreUploaded);
    }

    [Fact]
    public void LatexRefinementSessionConfig_Clone_produces_independent_deep_copy() {
        var original = new LatexRefinementSessionConfig {
            UseVertex = false,
            Step1MergeAndTimestamp = new RefinementStepConfig { Enabled = true }
        };

        var clone = original.Clone();
        clone.UseVertex = true;
        clone.Step1MergeAndTimestamp.Enabled = false;

        Assert.False(original.UseVertex);
        Assert.True(original.Step1MergeAndTimestamp.Enabled);
        Assert.True(clone.UseVertex);
        Assert.False(clone.Step1MergeAndTimestamp.Enabled);
    }

    [Fact]
    public void ForFile_without_extraction_config_matches_the_three_argument_constructor() {
        var state = StateOf(RefinementOptions.ForFile(new LatexRefinementSessionConfig(), "lecture.tex"));

        Assert.Equal("lecture.tex", state.Single);
        Assert.Null(state.Multiple);
        Assert.Null(state.Extraction);
        Assert.Null(state.Audio);
        Assert.Null(state.PreUploaded);
    }

    [Fact]
    public void ForFile_with_everything_matches_the_full_single_file_constructor() {
        var extraction = new AiStudioAutoExtractionConfig();
        var parts = new List<Part> { new() { Text = "audio" } };

        var state = StateOf(RefinementOptions.ForFile(
            new LatexRefinementSessionConfig(), "lecture.tex", extraction, "lecture.aac", parts));

        Assert.Equal("lecture.tex", state.Single);
        Assert.Null(state.Multiple);
        Assert.Same(extraction, state.Extraction);
        Assert.Equal("lecture.aac", state.Audio);
        Assert.Same(parts, state.PreUploaded);
    }

    [Fact]
    public void ForFiles_matches_the_multiple_file_constructor() {
        var extraction = new AiStudioAutoExtractionConfig();

        var state = StateOf(RefinementOptions.ForFiles(
            new LatexRefinementSessionConfig(), TwoParts, extraction, "lecture.aac"));

        Assert.Null(state.Single);
        Assert.Equal(TwoParts, state.Multiple);
        Assert.Same(extraction, state.Extraction);
        Assert.Equal("lecture.aac", state.Audio);
        Assert.Null(state.PreUploaded);
    }

    /// <summary>
    /// The modes are mutually exclusive by construction: <c>ExecutePipelineAsync</c> checks
    /// MultipleFilePaths first, so an options object carrying both would silently ignore the single
    /// file. No factory can produce that, and this test is what keeps it that way.
    /// </summary>
    [Fact]
    public void No_factory_produces_both_a_single_file_and_a_file_list() {
        var config = new LatexRefinementSessionConfig();

        foreach (var options in new[] {
            RefinementOptions.ForConfiguredFolder(config),
            RefinementOptions.ForFile(config, "lecture.tex"),
            RefinementOptions.ForFiles(config, TwoParts)
        }) {
            Assert.True(options.SingleFilePath == null || options.MultipleFilePaths == null);
        }
    }
}
