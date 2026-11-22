using System.Linq;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The seams the session merge created: what one shared <see cref="AutoExtractionSession"/> does
/// differently on AI Studio and Vertex, and how Vertex's config answers the shared interface.
/// Vertex has not had a real run since the merge, so these are the checks it has.
/// </summary>
public class ExtractionBackendTests {
    private static readonly Part[] History = [
        new() { Text = "history.tex" },
        new() { FileData = new FileData { FileUri = "gs://bucket/slides.pdf", MimeType = "application/pdf" } },
    ];

    [Fact]
    public void A_system_instruction_that_takes_files_gets_the_whole_history() {
        var parts = AutoExtractionSession.BuildSystemInstructionParts("rules", History, loadHistoryIntoSystemInstruction: true, acceptsFiles: true);

        Assert.Equal(3, parts.Count);
        Assert.Equal("rules", parts[0].Text);
        Assert.NotNull(parts[2].FileData);
    }

    [Fact]
    public void A_text_only_system_instruction_keeps_the_history_files_out() {
        var parts = AutoExtractionSession.BuildSystemInstructionParts("rules", History, loadHistoryIntoSystemInstruction: true, acceptsFiles: false);

        Assert.Equal(new[] { "rules", "history.tex" }, parts.Select(p => p.Text));
    }

    [Fact]
    public void Text_only_partition_keeps_image_wrapper_tags_paired_with_their_blob() {
        Part[] history = [
            new() { Text = "ex1 explanation" },
            new() { Text = "\n<image path=\"./training-history/ex1.png\">\n" },
            new() { InlineData = new Blob { Data = [1, 2, 3], MimeType = "image/png" } },
            new() { Text = "\n</image>\n" },
            new() { Text = "ex2 explanation" },
        ];

        var (textOnly, nonSystem) = SystemInstructionTextBuilder.PartitionForTextOnlySystemInstruction(history);

        Assert.Equal(new[] { "ex1 explanation", "ex2 explanation" }, textOnly.Select(p => p.Text));
        Assert.Equal(3, nonSystem.Count);
        Assert.Contains("<image path=\"./training-history/ex1.png\">", nonSystem[0].Text);
        Assert.NotNull(nonSystem[1].InlineData);
        Assert.Equal("\n</image>\n", nonSystem[2].Text);
    }

    [Fact]
    public void History_outside_the_system_instruction_is_not_added_to_it() {
        var parts = AutoExtractionSession.BuildSystemInstructionParts("rules", History, loadHistoryIntoSystemInstruction: false, acceptsFiles: true);

        Assert.Equal("rules", parts.Single().Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_chosen_model_reaches_the_parameter_set_the_refinement_reads(bool useVertex) {
        var refinement = new LatexRefinementSessionConfig();
        string untouchedModel = (useVertex ? refinement.Step2SpeechRefinement.AiStudio : refinement.Step2SpeechRefinement.Vertex).CurrentModel;
        var extraction = new AiStudioAutoExtractionConfig { CurrentModel = "gemini-test-model", Temperature = 0.11f, ThinkingLevel = "LOW" };

        AutoExtractionSession.ApplyChosenModelToRefinement(refinement, extraction, useVertex);

        foreach (var step in new[] { refinement.Step1MergeAndTimestamp, refinement.Step2SpeechRefinement, refinement.Step3LastRefinement }) {
            var read = useVertex ? step.Vertex : step.AiStudio;
            var other = useVertex ? step.AiStudio : step.Vertex;
            Assert.Equal(("gemini-test-model", 0.11f, "LOW"), (read.CurrentModel, read.Temperature, read.ThinkingLevel));
            Assert.Equal(untouchedModel, other.CurrentModel);
        }
    }

    [Fact]
    public void Vertex_keeps_its_own_key_for_the_wait_between_parts() {
        IAutoExtractionConfig config = new VertexAutoExtractionConfig { RateLimitDelaySeconds = 77 };

        Assert.Equal(77, config.VideoPartDelaySeconds);
    }

    [Fact]
    public void Vertex_starts_the_settings_it_gained_from_AI_Studios_defaults() {
        IAutoExtractionConfig vertex = new VertexAutoExtractionConfig();
        IAutoExtractionConfig aiStudio = new AiStudioAutoExtractionConfig();

        Assert.Equal(
            (aiStudio.StripCommentsFromReferenceContext, aiStudio.HistoryBatchCount, aiStudio.SystemInstructionDelaySeconds, aiStudio.HistoryRateLimitDelaySeconds,
                aiStudio.MergeSystemInstructionAndFirstHistoryBatch, aiStudio.MergeAllConsecutiveHistoryBatches, aiStudio.SendDummyFileWithEachWarmUpRound, aiStudio.DebugHelloRoundtrip),
            (vertex.StripCommentsFromReferenceContext, vertex.HistoryBatchCount, vertex.SystemInstructionDelaySeconds, vertex.HistoryRateLimitDelaySeconds,
                vertex.MergeSystemInstructionAndFirstHistoryBatch, vertex.MergeAllConsecutiveHistoryBatches, vertex.SendDummyFileWithEachWarmUpRound, vertex.DebugHelloRoundtrip));
    }
}
