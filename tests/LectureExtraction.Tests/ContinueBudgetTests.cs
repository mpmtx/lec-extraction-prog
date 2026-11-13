using LectureExtraction.GoogleAi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the two limits of <see cref="ContinueBudget"/>. Every continue re-sends the whole history
/// (for extraction: the video), so these limits are what bounds the bill of a model that never
/// writes its completion marker.
/// </summary>
public class ContinueBudgetTests {
    [Fact]
    public void Stops_at_the_total_even_when_every_response_makes_progress() {
        var budget = new ContinueBudget(maxTotal: 3);

        Assert.True(budget.TryContinue("part one", out _));
        Assert.Equal(2, budget.RequestNumber);
        Assert.True(budget.TryContinue("part two", out _));
        Assert.Equal(3, budget.RequestNumber);
        Assert.False(budget.TryContinue("part three", out string reason));
        Assert.Contains("3", reason);
    }

    [Fact]
    public void Stops_after_consecutive_empty_responses() {
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinue("", out _));
        Assert.False(budget.TryContinue("   ", out string reason));
        Assert.Contains("ohne Fortschritt", reason);
    }

    [Fact]
    public void A_repeated_tail_counts_as_no_progress() {
        // A model stuck re-emitting the same block until MAX_TOKENS.
        string loop = new('x', 500);
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinue(loop, out _));
        Assert.True(budget.TryContinue(loop, out _));
        Assert.False(budget.TryContinue(loop, out _));
    }

    [Fact]
    public void Progress_resets_the_no_progress_count() {
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinue("", out _));
        Assert.True(budget.TryContinue("text", out _));
        Assert.True(budget.TryContinue("", out _));
        Assert.Equal(1, budget.RequestsWithoutProgress);
    }

    [Fact]
    public void Echo_tail_quotes_the_last_EchoLength_characters_of_a_long_response() {
        string response = new string('a', 500) + new string('b', ContinueBudget.EchoLength);

        string echo = ContinueBudget.EchoTail(response);

        Assert.Equal("...\n" + new string('b', ContinueBudget.EchoLength), echo);
    }

    [Fact]
    public void Echo_tail_honours_a_configured_length_and_falls_back_when_it_is_not_positive() {
        string response = new('x', 3000);

        Assert.Equal("...\n" + new string('x', 2000), ContinueBudget.EchoTail(response, 2000));
        Assert.Equal("...\n" + new string('x', ContinueBudget.EchoLength), ContinueBudget.EchoTail(response, 0));
    }

    [Fact]
    public void Echo_tail_quotes_a_short_response_whole() {
        Assert.Equal("short", ContinueBudget.EchoTail("short"));
    }

    [Fact]
    public void Continuation_hop_with_thinking_only_and_new_token_counts_as_progress() {
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinueContinuation("", [1, 2], out _));
        Assert.Equal(0, budget.RequestsWithoutProgress);
        Assert.Equal(2, budget.RequestNumber);

        Assert.True(budget.TryContinueContinuation("  ", [1, 3], out _));
        Assert.Equal(0, budget.RequestsWithoutProgress);
        Assert.Equal(3, budget.RequestNumber);
    }

    [Fact]
    public void Continuation_hop_stops_when_token_and_text_both_stall() {
        var budget = new ContinueBudget(maxTotal: 10, maxWithoutProgress: 2);

        Assert.True(budget.TryContinueContinuation("", [1, 2], out _));
        Assert.Equal(0, budget.RequestsWithoutProgress);

        // Identical token with empty text does not make progress.
        Assert.True(budget.TryContinueContinuation("", [1, 2], out _));
        Assert.Equal(1, budget.RequestsWithoutProgress);

        Assert.False(budget.TryContinueContinuation("", [1, 2], out string reason));
        Assert.Contains("ohne Fortschritt", reason);
    }
}
