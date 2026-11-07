using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.GoogleAi;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// Pins the bounds of <see cref="ApiRetryPolicy.ExecuteStreamWithRetryAsync"/> and which errors it
/// retries at all.
///
/// <para>Every attempt is a billed request (for extraction it re-sends the video), so "retries until
/// it works" is a cost bug, not resilience. The case that motivated these tests: resetting the
/// failure counter whenever a chunk arrived made a stream that keeps dying after its first chunk
/// retry forever. And treating every 4xx as transient meant a wrong model name waited out minutes
/// of backoff eight times before failing.</para>
///
/// <para>The waits are replaced by an instant fake and the console by a <see cref="TestConsole"/>,
/// so the class joins the console collection.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class ApiRetryPolicyTests : IDisposable {
    private readonly IAnsiConsole _previousConsole = AnsiConsole.Console;
    private readonly Func<int, string, Task<bool>> _previousDelay = ApiRetryPolicy.DelayAsync;
    private readonly List<int> _waits = [];

    public ApiRetryPolicyTests() {
        AnsiConsole.Console = new TestConsole();
        ApiRetryPolicy.DelayAsync = (seconds, _) => {
            _waits.Add(seconds);
            return Task.FromResult(true);
        };
    }

    public void Dispose() {
        AnsiConsole.Console = _previousConsole;
        ApiRetryPolicy.DelayAsync = _previousDelay;
    }

    [Fact]
    public async Task A_stream_that_keeps_dying_after_one_chunk_stops_at_the_total_cap() {
        int attempts = 0;

        await Assert.ThrowsAsync<ServerError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream(["x"], new ServerError("unavailable", 503, "UNAVAILABLE")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 3));

        // Each attempt made progress, so the consecutive-failure counter never reached 3;
        // the total cap (twice maxRetries) is what ended it.
        Assert.Equal(6, attempts);
    }

    [Fact]
    public async Task Failures_without_any_chunk_stop_at_maxRetries() {
        int attempts = 0;

        await Assert.ThrowsAsync<ClientError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ClientError("rate limited", 429, "RESOURCE_EXHAUSTED")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 3));

        Assert.Equal(3, attempts);
        Assert.Equal(2, _waits.Count);
    }

    [Theory]
    [InlineData(400, "FAILED_PRECONDITION")]
    [InlineData(401, "UNAUTHENTICATED")]
    [InlineData(403, "PERMISSION_DENIED")]
    [InlineData(404, "NOT_FOUND")]
    public async Task A_client_error_other_than_408_or_429_fails_on_the_first_attempt(int statusCode, string status) {
        int attempts = 0;

        await Assert.ThrowsAsync<ClientError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ClientError($"models/gemini-x is {status}", statusCode, status)); },
            _ => Task.CompletedTask,
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task Resume_keeps_the_partial_text_instead_of_retrying() {
        int attempts = 0;
        int retries = 0;
        string received = "";

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream(["\\section{Gruppen}\n", "Eine Gruppe ist"], new ServerError("unavailable", 503, "UNAVAILABLE")); },
            chunk => { received += chunk.Candidates![0].Content!.Parts![0].Text; return Task.CompletedTask; },
            CancellationToken.None,
            onRetry: () => retries++,
            resumeOnPartialProgress: true);

        Assert.True(result);
        Assert.Equal(1, attempts);
        Assert.Equal(0, retries);
        Assert.Equal("\\section{Gruppen}\nEine Gruppe ist", received);
    }

    [Fact]
    public async Task A_stream_cut_while_still_thinking_is_retried_not_resumed() {
        // With IncludeThoughts on, the thought summary arrives as text parts before the answer.
        // Counting it as progress turned a cut during thinking into a "continue" of an empty
        // answer (seen 2026-10-15, 02-16 part 4: 8,864 thought characters, 0 output tokens).
        int attempts = 0;
        int retries = 0;

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => {
                attempts++;
                return attempts == 1
                    ? Thoughts(["**Inspecting the Instructions**\n", "I'm now diving deep into the board."], new ServerError("unavailable", 503, "UNAVAILABLE"))
                    : Stream(["\\section{Gruppen}"], null);
            },
            _ => Task.CompletedTask,
            CancellationToken.None,
            onRetry: () => retries++,
            resumeOnPartialProgress: true);

        Assert.True(result);
        Assert.Equal(2, attempts);
        Assert.Equal(1, retries);
    }

    [Fact]
    public async Task OnRetry_runs_once_before_each_new_attempt() {
        int attempts = 0;
        int retries = 0;

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => {
                attempts++;
                return attempts < 3
                    ? Stream([], new ServerError("internal", 500, "INTERNAL"))
                    : Stream(["done"], null);
            },
            _ => Task.CompletedTask,
            CancellationToken.None,
            onRetry: () => retries++);

        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, retries);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    public void IsTransientError_classifies_client_errors_by_status(int statusCode, bool expected) {
        Assert.Equal(expected, ApiRetryPolicy.IsTransientError(new ClientError("error", statusCode, "STATUS")));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void IsTransientError_retries_server_errors(int statusCode) {
        Assert.True(ApiRetryPolicy.IsTransientError(new ServerError("error", statusCode, "STATUS")));
    }

    [Fact]
    public void An_api_error_is_not_a_lost_connection() {
        // ClientError derives from HttpRequestException; it must not trigger the 5-minute network pause.
        Assert.False(ApiRetryPolicy.IsNetworkConnectionError(new ClientError("not found", 404, "NOT_FOUND")));
        Assert.True(ApiRetryPolicy.IsNetworkConnectionError(new HttpRequestException("No such host is known.")));
    }

    [Fact]
    public void A_number_that_merely_contains_500_is_not_a_server_error() {
        Assert.False(ApiRetryPolicy.IsTransientError(new InvalidDataException("The prompt used 1500 tokens.")));
        Assert.True(ApiRetryPolicy.IsTransientError(new InvalidDataException("Response status code 503 (Service Unavailable).")));
    }

    // The message the API returned when the free-tier daily request quota ran out, details included:
    // its retry hint pointed 15 hours ahead, which the backoff used to sleep through.
    private const string DailyQuotaMessage =
        "You exceeded your current quota, please check your plan and billing details.\n" +
        "* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 20, model: gemini-3.8-flash\n" +
        "Please retry in 15h7.728714795s.\n" +
        "Details: {\"@type\":\"type.googleapis.com/google.rpc.QuotaFailure\",\"violations\":[{\"quotaId\":\"GenerateRequestsPerDayPerProjectPerModel-FreeTier\",\"quotaValue\":\"20\"}]}";

    [Fact]
    public void An_exhausted_daily_quota_is_not_retried() {
        var dailyQuota = new ClientError(DailyQuotaMessage, 429, "RESOURCE_EXHAUSTED");

        Assert.True(ApiRetryPolicy.IsDailyQuotaExhausted(dailyQuota));
        Assert.False(ApiRetryPolicy.IsTransientError(dailyQuota));
    }

    [Fact]
    public void A_per_minute_rate_limit_is_still_retried() {
        var perMinute = new ClientError(
            "Quota exceeded. Details: {\"violations\":[{\"quotaId\":\"GenerateRequestsPerMinutePerProjectPerModel-FreeTier\"}]}",
            429, "RESOURCE_EXHAUSTED");

        Assert.False(ApiRetryPolicy.IsDailyQuotaExhausted(perMinute));
        Assert.True(ApiRetryPolicy.IsTransientError(perMinute));
    }

    [Fact]
    public async Task An_exhausted_daily_quota_fails_on_the_first_attempt() {
        int attempts = 0;

        await Assert.ThrowsAsync<ClientError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ClientError(DailyQuotaMessage, 429, "RESOURCE_EXHAUSTED")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            initialBackoff: 0));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task An_unrequested_cancellation_or_timeout_is_retried() {
        int attempts = 0;
        int retries = 0;

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => {
                attempts++;
                return attempts < 3
                    ? Stream([], new TaskCanceledException("The operation was canceled (HttpClient timeout)."))
                    : Stream(["success"], null);
            },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 3,
            onRetry: () => retries++);

        Assert.True(result);
        Assert.Equal(3, attempts);
        Assert.Equal(2, retries);
        Assert.Equal(2, _waits.Count);
        Assert.All(_waits, w => Assert.Equal(30, w));
    }

    [Fact]
    public async Task A_user_requested_cancellation_returns_false_without_retrying() {
        int attempts = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        bool result = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new OperationCanceledException(cts.Token)); },
            _ => Task.CompletedTask,
            cts.Token,
            maxRetries: 3);

        Assert.False(result);
        Assert.Equal(1, attempts);
        Assert.Empty(_waits);
    }

    [Fact]
    public async Task ExecuteWithRetryAsync_returns_null_on_user_cancellation_without_retrying() {
        int attempts = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        string? result = await ApiRetryPolicy.ExecuteWithRetryAsync<string>(
            () => { attempts++; throw new OperationCanceledException(cts.Token); },
            maxRetries: 3,
            cancellationToken: cts.Token);

        Assert.Null(result);
        Assert.Equal(1, attempts);
        Assert.Empty(_waits);
    }

    // The 429 a 392,711-token part got on 2026-10-15, as the SDK reported it.
    private const string PerMinuteTokenMessage =
        "You exceeded your current quota, please check your plan and billing details. Quota exceeded for metric: " +
        "generativelanguage.googleapis.com/generate_content_free_tier_input_token_count, limit: 250000, model: gemini-3.8-flash " +
        "Please retry in 31.322840295s [GenerateContentInputTokensPerModelPerMinute-FreeTier (limit: 250000)]";

    [Fact]
    public void The_per_minute_token_limit_is_read_from_the_error_and_not_from_a_daily_quota() {
        Assert.Equal(250000, ApiRetryPolicy.PerMinuteInputTokenLimit(new ClientError(PerMinuteTokenMessage, 429, "RESOURCE_EXHAUSTED")));
        Assert.Null(ApiRetryPolicy.PerMinuteInputTokenLimit(new ClientError(DailyQuotaMessage, 429, "RESOURCE_EXHAUSTED")));
    }

    [Fact]
    public async Task A_request_larger_than_the_per_minute_token_limit_stops_after_the_second_refusal() {
        int attempts = 0;

        await Assert.ThrowsAsync<ClientError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ClientError(PerMinuteTokenMessage, 429, "RESOURCE_EXHAUSTED")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            requestInputTokens: 392_711));

        // One wait (the window may have been full from earlier requests), then no more.
        Assert.Equal(2, attempts);
        Assert.Single(_waits);
    }

    [Fact]
    public async Task A_request_within_the_per_minute_token_limit_keeps_waiting_it_out() {
        int attempts = 0;

        await Assert.ThrowsAsync<ClientError>(() => ApiRetryPolicy.ExecuteStreamWithRetryAsync(
            () => { attempts++; return Stream([], new ClientError(PerMinuteTokenMessage, 429, "RESOURCE_EXHAUSTED")); },
            _ => Task.CompletedTask,
            CancellationToken.None,
            maxRetries: 4,
            requestInputTokens: 120_000));

        Assert.Equal(4, attempts);
    }

    [Fact]
    public void ApiStatusCode_reads_a_wrapped_error() {
        var wrapped = new InvalidOperationException("outer", new ClientError("inner", 429, "RESOURCE_EXHAUSTED"));

        Assert.Equal(429, ApiRetryPolicy.ApiStatusCode(wrapped));
        Assert.Null(ApiRetryPolicy.ApiStatusCode(new IOException("disk")));
    }

    private static async IAsyncEnumerable<GenerateContentResponse> Stream(string[] texts, Exception? thenThrow, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        foreach (string text in texts) {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new GenerateContentResponse {
                Candidates = [new Candidate { Content = new Content { Role = "model", Parts = [new Part { Text = text }] } }]
            };
        }
        if (thenThrow != null) {
            throw thenThrow;
        }
    }

    /// <summary>Like <see cref="Stream"/>, but every part is a thought-summary part.</summary>
    private static async IAsyncEnumerable<GenerateContentResponse> Thoughts(string[] texts, Exception? thenThrow, [EnumeratorCancellation] CancellationToken cancellationToken = default) {
        foreach (string text in texts) {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new GenerateContentResponse {
                Candidates = [new Candidate { Content = new Content { Role = "model", Parts = [new Part { Text = text, Thought = true }] } }]
            };
        }
        if (thenThrow != null) {
            throw thenThrow;
        }
    }
}
