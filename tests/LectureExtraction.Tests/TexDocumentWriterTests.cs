using System.Collections.Generic;
using System.Linq;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction;
using LectureExtraction.Extraction.Model;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the .tex headers: the stack of per-step layers every output carries, the per-part blocks
/// under the "% --- TEIL n" separators, and <see cref="TexDocumentWriter.CountParts"/>, which
/// refinement step 1 trusts over the configured NumberOfParts.
/// </summary>
public class TexDocumentWriterTests {
    private static readonly GenerationParameters Generation = new() { Temperature = 0.36f, TopP = 0.95f, TopK = 40, ThinkingBudget = 32765, ThinkingLevel = "HIGH" };

    private static string PartHeader(int part, int total = 2) => TexDocumentWriter.BuildPartHeader(
        part, total, $"lecture-part{part}.mp4", (part - 1) * 1302.75, new TokenUsage(330_036, 14_680, 324_665, 12_000, 2), "gemini-3.8-flash", Generation);

    // ---- CountParts ---------------------------------------------------------

    [Fact]
    public void CountParts_CountsFreshlyTranscribedParts() {
        string tex = "% header\n\n"
            + "% --- TEIL 1 (Tokens: Input Gesamt 311’231, Gecacht 0, Frisch/Video 311’231, Output 12’607) ---\nA\n"
            + "% --- TEIL 2 (Tokens: Input Gesamt 324’542, Gecacht 232’898, Frisch/Video 91’644, Output 12’741) ---\nB\n"
            + "% --- TEIL 3 ---\nC\n"
            + "% --- TEIL 4 ---\nD\n";

        Assert.Equal(4, TexDocumentWriter.CountParts(tex));
    }

    [Fact]
    public void CountParts_CountsPartsResumedFromCache() {
        string tex = "% --- TEIL 1 (Aus Cache geladen) ---\nA\n% --- TEIL 2 (Aus Cache geladen) ---\nB\n";

        Assert.Equal(2, TexDocumentWriter.CountParts(tex));
    }

    [Fact]
    public void CountParts_CountsYouTubeAndVertexFragmentSeparators() {
        string tex = "% --- TEIL 1: 00:00-20:00 (Intro) ---\nA\n% --- TEIL 2: 20:00-40:00 (Proof) ---\nB\n% --- TEIL 3: 40:00-60:00 (End) ---\nC\n";

        Assert.Equal(3, TexDocumentWriter.CountParts(tex));
    }

    [Fact]
    public void CountParts_CountsCrlfDocuments() {
        string tex = "% --- TEIL 1 (Aus Cache geladen) ---\r\nA\r\n% --- TEIL 2 (Aus Cache geladen) ---\r\nB\r\n";

        Assert.Equal(2, TexDocumentWriter.CountParts(tex));
    }

    [Fact]
    public void CountParts_ReturnsZeroForADocumentWithoutSeparators() {
        // A merged step-2 document may have lost its separators; 0 lets the caller fall back.
        Assert.Equal(0, TexDocumentWriter.CountParts("\\section{Merged}\nNo separators here.\n"));
    }

    [Fact]
    public void CountParts_IgnoresSeparatorTextThatIsNotAtALineStart() {
        Assert.Equal(0, TexDocumentWriter.CountParts("text % --- TEIL 7 (Aus Cache geladen) ---\n"));
    }

    [Fact]
    public void CountParts_CountsTheSeparatorsTheCombinedDocumentNowWrites() {
        string tex = TexDocumentWriter.BuildPartSeparator(1, PartHeader(1), fromCache: false) + "A"
            + TexDocumentWriter.BuildPartSeparator(2, PartHeader(2), fromCache: true) + "B";

        Assert.Equal(2, TexDocumentWriter.CountParts(tex));
    }

    // ---- Layers -------------------------------------------------------------

    [Fact]
    public void EveryLayer_NamesItsStepOnItsFirstLine() {
        string refinement = TexDocumentWriter.BuildRefinementHeader(
            TexDocumentWriter.MergeStep, "step2-x-offset-merged.tex", "step1-x-all-offset.tex", default, "gemini-3.8-flash", Generation);
        string combined = TexDocumentWriter.BuildCombinedHeader(
            "step1-x-all-offset.tex", "x.mp4", 2, default, "gemini-3.8-flash", Generation, ["a", "b"]);

        Assert.Equal("% Step 2/4 - Merge & timestamp alignment (refinement step 1)", refinement.Split('\n')[1]);
        Assert.Equal("% Step 1/4 - Transcription, 2 part(s) combined", combined.Split('\n')[1]);
        Assert.Equal("% Step 1/4 - Transcription, part 2 of 2", PartHeader(2).Split('\n')[1]);
    }

    [Fact]
    public void TokenLines_ListOutputAndThinkingSeparately() {
        string header = PartHeader(1);

        Assert.Contains("% Token Usage (2 Request(s)):", header);
        Assert.Contains("Output Tokens       : 14", header);
        Assert.Contains("Thinking Tokens     : 12", header);
        Assert.DoesNotContain("+ Thinking", header); // the old label claimed output included thinking
    }

    [Fact]
    public void SplitHeaderLayers_SeparatesStackedLayersFromTheBody() {
        string refinement = TexDocumentWriter.BuildRefinementHeader(TexDocumentWriter.MergeStep, "out.tex", "in.tex", default, "m", Generation);
        string combined = TexDocumentWriter.BuildCombinedHeader("in.tex", "x.mp4", 1, default, "m", Generation, []);
        string body = "% Primary Language: English\n\\section{A}\n";

        var (layers, rest) = TexDocumentWriter.SplitHeaderLayers(refinement + combined + "\n" + body);

        Assert.Equal(refinement + combined, layers);
        Assert.Equal(body, rest);
    }

    [Fact]
    public void SplitHeaderLayers_StopsAtThePartSeparator() {
        // A part's own block sits under its separator in the body and must not be taken for a layer.
        string tex = TexDocumentWriter.BuildPartSeparator(1, PartHeader(1), fromCache: false).TrimStart('\n') + "\\section{A}\n";

        var (layers, body) = TexDocumentWriter.SplitHeaderLayers(tex);

        Assert.Equal("", layers);
        Assert.StartsWith("% --- TEIL 1 ---", body);
    }

    [Fact]
    public void SplitHeaderLayers_ReadsTheOldSingleBlockHeaderAsALayer() {
        string old = "% ==========================================\n% LatexRefinement Step Output: x.tex\n% Model: gemini-3.7-flash\n% ==========================================\n\n% Primary Language: English\nBody\n";

        var (layers, body) = TexDocumentWriter.SplitHeaderLayers(old);

        Assert.Contains("LatexRefinement Step Output", layers);
        Assert.Equal("% Primary Language: English\nBody\n", body);
    }

    [Fact]
    public void SplitHeaderLayers_TreatsAnUnterminatedBlockAsBody() {
        string tex = "% ==========================================\n% Model: x\n\\section{A}\n";

        var (layers, body) = TexDocumentWriter.SplitHeaderLayers(tex);

        Assert.Equal("", layers);
        Assert.Equal(tex, body);
    }

    [Fact]
    public void Layers_StackNewestFirstAcrossSteps() {
        string step1 = TexDocumentWriter.BuildCombinedHeader("step1-x-all-offset.tex", "x.mp4", 2, default, "m", Generation, []);
        string step2 = TexDocumentWriter.BuildRefinementHeader(TexDocumentWriter.MergeStep, "step2.tex", "step1.tex", default, "m", Generation);
        string step3 = TexDocumentWriter.BuildRefinementHeader(TexDocumentWriter.SpeechStep, "step3.tex", "step2.tex", default, "m", Generation);

        string afterStep2 = TexDocumentWriter.StackLayers(step2, TexDocumentWriter.SplitHeaderLayers(step1 + "\nBody").Layers, "Body");
        // The model echoed step 2's layers at the top of its answer; they must not appear twice.
        string modelAnswer = afterStep2;
        var (inherited, _) = TexDocumentWriter.SplitHeaderLayers(afterStep2);
        var (_, answerBody) = TexDocumentWriter.SplitHeaderLayers(modelAnswer);
        string afterStep3 = TexDocumentWriter.StackLayers(step3, inherited, answerBody);

        var stepLines = afterStep3.Split('\n').Where(line => line.StartsWith("% Step ")).ToList();
        Assert.Equal(["% Step 3/4 - Speech refinement (refinement step 2)", "% Step 2/4 - Merge & timestamp alignment (refinement step 1)", "% Step 1/4 - Transcription, 2 part(s) combined"], stepLines);
        Assert.EndsWith("\nBody", afterStep3);
    }

    // ---- Part headers ---------------------------------------------------------

    [Fact]
    public void SummarizePartHeader_ReadsTheFactsBackOutOfTheHeader() {
        string summary = TexDocumentWriter.SummarizePartHeader(PartHeader(2));

        Assert.StartsWith("lecture-part2.mp4 | start 00:21:42 | model gemini-3.8-flash | processed ", summary);
        Assert.Contains("| prompt 330", summary);
        Assert.Contains("| output 14", summary);
        Assert.Contains("| thinking 12", summary);
    }

    [Fact]
    public void SummarizePartHeader_ReadsTheOldPartHeaderFormat() {
        string old = "% ==========================================\n% AutoExtraction Source Part: lecture-part1.mp4\n% Model: gemini-3.7-flash\n"
            + "% Processed on: 2026-10-03 16:27:28\n% PART_START_SECONDS: 0.00\n%   - Total Prompt Tokens : 311’231 (Gesamtumfang)\n"
            + "%   - Generated Output    : 12’607 (Generiertes LaTeX + Thinking Tokens)\n% ==========================================\n";

        Assert.Equal("lecture-part1.mp4 | start 00:00:00 | model gemini-3.7-flash | processed 2026-10-03 16:27:28 | prompt 311’231 | output 12’607",
            TexDocumentWriter.SummarizePartHeader(old));
    }

    [Fact]
    public void RestorePartHeaders_PutsTheOriginalBlockBackUnderEverySurvivingSeparator() {
        var headers = new Dictionary<int, string> { [1] = PartHeader(1), [2] = PartHeader(2) };
        // The model kept separator 1 bare, rewrote the block under separator 2, and dropped none.
        string modelBody = "% --- TEIL 1 ---\nA\n% --- TEIL 2 ---\n% ==========================================\n% Model: something else\n% ==========================================\nB";

        string restored = TexDocumentWriter.RestorePartHeaders(modelBody, headers);

        Assert.Equal("% --- TEIL 1 ---\n" + headers[1] + "A\n% --- TEIL 2 ---\n" + headers[2] + "B", restored);
    }

    [Fact]
    public void ExtractPartHeaders_FindsEachPartsBlockUnderItsSeparator() {
        string combined = TexDocumentWriter.BuildPartSeparator(1, PartHeader(1), fromCache: false) + "A"
            + TexDocumentWriter.BuildPartSeparator(2, PartHeader(2), fromCache: true) + "B";

        var headers = TexDocumentWriter.ExtractPartHeaders(combined);

        Assert.Equal(PartHeader(1).Split('\n')[1], headers[1].Split('\n')[1]);
        Assert.Equal(PartHeader(2).Split('\n')[1], headers[2].Split('\n')[1]);
    }

    [Fact]
    public void StripHeadersForModel_KeepsTheSeparatorsAndDropsEveryHeaderBlock() {
        string layer = TexDocumentWriter.BuildCombinedHeader("in.tex", "x.mp4", 2, default, "m", Generation, []);
        string document = layer + "\n% Primary Language: English\n"
            + TexDocumentWriter.BuildPartSeparator(1, PartHeader(1), false) + "A"
            + TexDocumentWriter.BuildPartSeparator(2, PartHeader(2), true) + "B";

        string forModel = TexDocumentWriter.StripHeadersForModel(document);

        Assert.Equal("% Primary Language: English\n\n\n% --- TEIL 1 ---\nA\n\n% --- TEIL 2 (Aus Cache geladen) ---\nB", forModel);
        Assert.Equal(2, TexDocumentWriter.CountParts(forModel));
    }

    [Fact]
    public void ExtractPartHeaders_RoundTripsThroughRestore() {
        // A step that keeps every separator bare gets every part's block back, unchanged.
        string combined = (TexDocumentWriter.BuildPartSeparator(1, PartHeader(1), false) + "A"
            + TexDocumentWriter.BuildPartSeparator(2, PartHeader(2), false) + "B").TrimStart('\n');
        string bare = "% --- TEIL 1 ---\nA\n\n% --- TEIL 2 ---\nB";

        Assert.Equal(combined, TexDocumentWriter.RestorePartHeaders(bare, TexDocumentWriter.ExtractPartHeaders(combined)));
    }

    [Fact]
    public void SinglePartHeader_MarksSinglePassAndPreservesThoughtsLinkAcrossLayers() {
        string single = TexDocumentWriter.BuildSinglePartHeader(
            "step1-lec.tex", "lec.mp4", "lec-speed-1-compressed.mp4",
            new TokenUsage(100_000, 0, 100_000, 20_000, 1), "gemini-4-hydrogen", Generation, "step1-lec-thoughts.md");

        Assert.Equal($"% {TexDocumentWriter.SinglePartTranscriptionStep}", single.Split('\n')[1]);
        Assert.Equal("step1-lec-thoughts.md", TexDocumentWriter.ThoughtsFileNameOf(single));
        Assert.True(TexDocumentWriter.HasSinglePartHeader(single));
        Assert.False(TexDocumentWriter.HasMultiPartOrMergeHeader(single));

        string afterSpeech = TexDocumentWriter.StackLayers(
            TexDocumentWriter.BuildRefinementHeader(TexDocumentWriter.SpeechStep, "step3-lec-speech_refined.tex", "step1-lec.tex", default, "gemini-4-hydrogen", Generation),
            TexDocumentWriter.SplitHeaderLayers(single).Layers,
            "\\section{Body}");

        Assert.True(TexDocumentWriter.HasSinglePartHeader(afterSpeech));
        Assert.False(TexDocumentWriter.HasMultiPartOrMergeHeader(afterSpeech));
    }
}
