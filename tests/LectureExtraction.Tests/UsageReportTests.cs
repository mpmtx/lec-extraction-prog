using Google.GenAI.Types;
using LectureExtraction.GoogleAi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins review finding F9: a token line that never printed was indistinguishable from a request
/// that cost nothing.
///
/// <para>Usage metadata arrives on the final chunk of a streamed response, so "never arrived" is a
/// normal outcome, not an error - and the old <c>if (inputTokens > 0)</c> guard rendered it as
/// silence. These tests keep "not reported" and "zero" textually distinct.</para>
/// </summary>
public class UsageReportTests {
    [Fact]
    public void A_stream_that_never_reported_usage_says_so_instead_of_printing_nothing() {
        var report = new UsageReport();

        report.Absorb(null);
        report.Absorb(null);

        Assert.False(report.WasReported);
        Assert.Contains("Keine Nutzungsdaten", report.Describe("ignoriert"));
        Assert.Contains("kostenpflichtig", report.Describe("ignoriert"));
    }

    [Fact]
    public void A_genuinely_zero_response_still_prints_the_callers_line() {
        var report = new UsageReport();
        report.Absorb(new GenerateContentResponseUsageMetadata { PromptTokenCount = 0, CandidatesTokenCount = 0 });

        string line = report.Describe("Total Prompt: 0");

        Assert.True(report.WasReported);
        Assert.DoesNotContain("Keine Nutzungsdaten", line);
        Assert.Contains("Total Prompt: 0", line);
    }

    /// <summary>
    /// The direct answer to "the warm-up produced only 2 output tokens": reasoning tokens are billed
    /// but are not part of CandidatesTokenCount, and nothing in this app read them before.
    /// </summary>
    [Fact]
    public void Thinking_tokens_are_surfaced_because_they_are_not_part_of_the_output_count() {
        var report = new UsageReport();
        report.Absorb(new GenerateContentResponseUsageMetadata {
            PromptTokenCount = 1200,
            CandidatesTokenCount = 2,
            ThoughtsTokenCount = 847,
            TotalTokenCount = 2049
        });

        string line = report.Describe("Output: 2");

        // Formatted with "N0", so the thousands separator follows the running culture - assert
        // against the same formatting rather than pinning a German separator the CI host may not use.
        Assert.Contains($"Denk-Tokens: {847:N0}", line);
        Assert.Contains($"Gesamt: {2049:N0}", line);
    }

    [Fact]
    public void Thinking_and_total_are_omitted_when_absent_rather_than_printed_as_zero() {
        var report = new UsageReport();
        report.Absorb(new GenerateContentResponseUsageMetadata { PromptTokenCount = 10, CandidatesTokenCount = 5 });

        string line = report.Describe("Output: 5");

        Assert.DoesNotContain("Denk-Tokens", line);
        Assert.DoesNotContain("Gesamt", line);
    }

    /// <summary>
    /// Usage is cumulative-to-date per chunk, not incremental. Summing it would multiply the
    /// reported cost of every streamed request by the number of chunks that carried metadata.
    /// </summary>
    [Fact]
    public void Later_chunks_replace_earlier_usage_rather_than_accumulating() {
        var report = new UsageReport();

        report.Absorb(new GenerateContentResponseUsageMetadata { PromptTokenCount = 100, CandidatesTokenCount = 10 });
        report.Absorb(new GenerateContentResponseUsageMetadata { PromptTokenCount = 100, CandidatesTokenCount = 40 });

        Assert.Equal(100, report.PromptTokens);
        Assert.Equal(40, report.CandidateTokens);
    }

    [Fact]
    public void A_null_chunk_after_a_reported_one_does_not_erase_what_was_reported() {
        var report = new UsageReport();

        report.Absorb(new GenerateContentResponseUsageMetadata { PromptTokenCount = 100, ThoughtsTokenCount = 5 });
        report.Absorb(null);

        Assert.True(report.WasReported);
        Assert.Equal(100, report.PromptTokens);
        Assert.Equal(5, report.ThoughtTokens);
    }

    /// <summary>
    /// The 2026-10-10 merge of the 09-22 lecture: 42,156 output + 23,375 thinking hit the 65,535
    /// cap. The budget line has to make that sum - and thinking's share of it - visible.
    /// </summary>
    [Fact]
    public void Output_budget_adds_thinking_to_output_and_names_the_output_limit() {
        var report = new UsageReport();
        report.Absorb(new GenerateContentResponseUsageMetadata { CandidatesTokenCount = 42156, ThoughtsTokenCount = 23375 });
        report.RecordFinishReason("MAX_TOKENS");

        string line = report.DescribeOutputBudget(65535);

        Assert.Contains("= 65’531", line.Replace(",", "’").Replace(".", "’").Replace("'", "’"));
        Assert.Contains("MAX_TOKENS", line);
        Assert.Contains("Output-Limit abgeschnitten", line);
    }

    [Fact]
    public void Output_budget_does_not_blame_the_limit_for_a_normal_stop() {
        var report = new UsageReport();
        report.Absorb(new GenerateContentResponseUsageMetadata { CandidatesTokenCount = 7731, ThoughtsTokenCount = 8948 });
        report.RecordFinishReason("STOP");

        string line = report.DescribeOutputBudget(65535);

        Assert.Contains("FinishReason: STOP", line);
        Assert.DoesNotContain("abgeschnitten", line);
    }

    [Fact]
    public void A_later_empty_finish_reason_does_not_erase_the_reported_one() {
        var report = new UsageReport();
        report.RecordFinishReason("MAX_TOKENS");
        report.RecordFinishReason(null);
        report.RecordFinishReason("");

        Assert.Equal("MAX_TOKENS", report.FinishReason);
    }

    [Fact]
    public void Output_budget_identifies_long_decode_continuation_separately_from_max_tokens() {
        var report = new UsageReport();
        report.Absorb(new GenerateContentResponseUsageMetadata { CandidatesTokenCount = 3, ThoughtsTokenCount = 65533 });
        report.RecordFinishReason("CONTINUATION");

        string line = report.DescribeOutputBudget(100000);

        Assert.Contains("FinishReason: CONTINUATION", line);
        Assert.Contains("Long Decode", line);
        Assert.DoesNotContain("abgeschnitten", line);
    }
}
