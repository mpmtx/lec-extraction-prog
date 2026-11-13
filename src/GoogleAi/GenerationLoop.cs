using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] What one stage tells <see cref="GenerationLoop"/>: the completion marker, the
/// continue-prompt wording, the per-request logging, the delays and the console labels. Everything
/// else - retries, usage, thought summaries, empty answers, the continue budget, the history - is the
/// loop's, so a fix to it lands in every stage at once (it used to be copied into four loops).
/// [Human] Was eine Stufe der gemeinsamen Streaming-Schleife vorgibt: Abschluss-Marker,
/// Continue-Prompt, Logging, Wartezeiten und Konsolen-Texte.
/// </summary>
public sealed class GenerationLoopOptions {
    /// <summary>Hard cap on requests, the first one included (see <see cref="ContinueBudget"/>).</summary>
    public required int MaxContinueRequests { get; init; }

    /// <summary>Shown before each request; gets the request number and the cap.</summary>
    public required Action<int, int> AnnounceRequest { get; init; }

    /// <summary>True when one request's answer text carries the stage's completion marker.</summary>
    public required Func<string, bool> IsComplete { get; init; }

    /// <summary>
    /// Builds the continue prompt from the answer that was cut short, and prints the stage's own
    /// lines about it. Called only when another request is allowed.
    /// </summary>
    public required Func<string, string> BuildContinuePrompt { get; init; }

    /// <summary>The wait between a continue prompt and its request; false (cancelled) ends the loop.</summary>
    public required Func<Task<bool>> WaitBeforeContinueAsync { get; init; }

    /// <summary>Appended to the budget's stop reason, e.g. "Breche dieses Refinement ab."</summary>
    public required string BudgetStopSuffix { get; init; }

    /// <summary>Retry context for <see cref="ApiRetryPolicy"/>'s messages.</summary>
    public required string RetryContext { get; init; }

    /// <summary>Printed before the exception when the retries gave up.</summary>
    public string UnrecoverableErrorPrefix { get; init; } = "Der Fehler konnte nicht durch einen automatischen Retry behoben werden: ";

    /// <summary>Whether a cancelled or failed stream is reported as a warning (else as info).</summary>
    public bool CancelledAsWarning { get; init; }

    /// <summary>The retry policy's first backoff; null keeps its default.</summary>
    public int? InitialBackoff { get; init; }

    /// <summary>The retry policy's wait when the model is in high demand; null keeps its default.</summary>
    public int? HighDemandDelay { get; init; }

    /// <summary>Runs before every request (a rate-limit wait); false ends the loop.</summary>
    public Func<Task<bool>>? BeforeRequestAsync { get; init; }

    /// <summary>Sees every chunk with its answer text, before the text is printed.</summary>
    public Action<GenerateContentResponse, string>? OnChunk { get; init; }

    /// <summary>Print a request's Google Search sources once its stream has ended.</summary>
    public bool ShowGroundingSources { get; init; }

    /// <summary>A completed request's token lines; called before its thoughts are written.</summary>
    public Action<GenerationRequest>? ReportRequest { get; init; }

    /// <summary>Called after a completed request's thoughts are written (Stage 1's chat log).</summary>
    public Func<GenerationRequest, Task>? AfterRequestAsync { get; init; }

    /// <summary>How often a blank answer is sent again before the loop gives up; 0 treats it like any answer.</summary>
    public int EmptyAnswerRetries { get; init; }

    /// <summary>The pause before a blank answer is sent again.</summary>
    public TimeSpan EmptyAnswerRetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a request may stream nothing before a line says it is still waiting; zero turns it off.</summary>
    public TimeSpan SilenceNoticeInterval { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long an attempt may deliver no chunk at all before it is abandoned and sent again; zero turns
    /// it off (then only the client's 20-minute HTTP timeout applies). A started stream is never cut.
    /// </summary>
    public TimeSpan FirstChunkTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The first request's counted input tokens (a lower bound for continues). Lets the retry policy
    /// stop when a per-minute token limit is smaller than the request itself; null when not counted.
    /// </summary>
    public long? RequestInputTokens { get; init; }

    /// <summary>
    /// Enables Long Decode continuation (<c>FinishReason.Continuation</c> + <c>ContinuationToken</c>) for
    /// Gemini Hydrogen / Gemini 4 models. Older models keep this <see langword="false"/> to stay on the
    /// legacy C# text-continue loop untouched.
    /// </summary>
    public bool SupportsLongDecode { get; init; }
}

/// <summary>
/// One completed request: its answer text, its usage, the loop's total so far (this request
/// included), its grounding, and the continue prompt it answered (null for the first request).
/// </summary>
public sealed record GenerationRequest(int RequestNumber, string Text, UsageReport Usage, TokenUsage Total, GroundingMetadata? Grounding, string? ContinuePrompt);

/// <summary>Why <see cref="GenerationLoop"/> stopped.</summary>
public enum GenerationEnd {
    /// <summary>The stage's completion marker arrived.</summary>
    Completed,
    /// <summary>The model kept answering without the marker until the ContinueBudget ran out.</summary>
    BudgetExhausted,
    /// <summary>A request failed for good (the retries gave up); the text so far is incomplete.</summary>
    Failed,
    /// <summary>Blank answers until the retries ran out.</summary>
    EmptyAnswers,
    /// <summary>Cancelled by the user or a cancelled wait.</summary>
    Cancelled
}

/// <summary>
/// The joined answer text of every request, their summed usage, and why the loop ended.
/// <para>IMPORTANT: on Failed, EmptyAnswers or Cancelled the text is a fragment - it stops where the
/// failed request would have continued. On 2026-10-16 a refinement step saved such a fragment (43 of
/// 68 speech blocks) as its result and the next step polished it; callers must not do that.</para>
/// </summary>
public readonly record struct GenerationLoopResult(string Text, TokenUsage Usage, GenerationEnd End) {
    /// <summary>True when the text may be used as the stage's result: the marker arrived, or the model ran the budget out.</summary>
    public bool IsUsable => End is GenerationEnd.Completed or GenerationEnd.BudgetExhausted;
}

/// <summary>
/// [AI Context] The one streaming request loop behind Stage 1 (AI Studio and Vertex), the refinement
/// steps and the PDF repair: stream with <see cref="ApiRetryPolicy"/>, sum the usage, write the
/// thought summaries, send a blank answer again, and send continue prompts until the stage's
/// completion marker arrives or the <see cref="ContinueBudget"/> runs out. The stages differ only in
/// what <see cref="GenerationLoopOptions"/> carries.
///
/// <para>Every completed request is billed, a blank one included, so each one is counted in the
/// usage. A request the loop gave up on mid-stream (an error, a cancel) is not counted, but its
/// thoughts are written, marked ABORTED.</para>
/// [Human] Die gemeinsame Streaming-Schleife aller Stufen: Retry, Tokens, Gedanken, leere Antworten
/// und Continue-Anfragen. Die Stufen unterscheiden sich nur in den Optionen.
/// </summary>
public static class GenerationLoop {
    /// <summary>
    /// The opening every continue prompt shares: the cut-off answer's tail, quoted back as an anchor,
    /// followed by the stage's <paramref name="instruction"/>.
    /// </summary>
    public static string CutShortPrompt(string response, string instruction, int echoCharacters = ContinueBudget.EchoLength) =>
        "[IMPORTANT] Your response was cut short. Your last output ended with:\n\n" +
        $"{ContinueBudget.EchoTail(response, echoCharacters)}\n\n" +
        instruction;

    /// <summary>
    /// [AI Context] Clones <paramref name="baseConfig"/> for a single hop. Older models keep SDK
    /// <c>AutomaticContinuation = false</c> and <c>ContinuationToken = null</c> (pure legacy behavior);
    /// Hydrogen also sets <c>AutomaticContinuation = false</c> so <see cref="GenerationLoop"/> explicitly
    /// tracks <paramref name="continuationToken"/> across retries, quotas, and thoughts.
    /// [Human] Klont die Config für einen Hop (mit ContinuationToken bei Hydrogen, strikt Legacy bei älteren Modellen).
    /// </summary>
    public static GenerateContentConfig PrepareHopConfig(GenerateContentConfig baseConfig, string modelName, byte[]? continuationToken) =>
        ModelCapabilities.SupportsLongDecode(modelName)
            ? baseConfig with { AutomaticContinuation = false, ContinuationToken = continuationToken }
            : baseConfig with { AutomaticContinuation = false, ContinuationToken = null };

    /// <param name="history">The conversation; each legacy continue appends the model turn and the continue prompt to it.</param>
    /// <param name="generate">Starts one streamed request over the history as it is at that moment.</param>
    public static Task<GenerationLoopResult> RunAsync(
        List<Content> history, Func<List<Content>, IAsyncEnumerable<GenerateContentResponse>> generate, ThoughtLog thoughts, GenerationLoopOptions options) =>
        RunAsync(history, (h, _) => generate(h), thoughts, options);

    /// <param name="history">The conversation; unchanged across Long Decode <c>ContinuationToken</c> hops, appended only on legacy text-continue turns.</param>
    /// <param name="generate">Starts one streamed request over the history and optional Long Decode continuation token.</param>
    public static async Task<GenerationLoopResult> RunAsync(
        List<Content> history, Func<List<Content>, byte[]?, IAsyncEnumerable<GenerateContentResponse>> generate, ThoughtLog thoughts, GenerationLoopOptions options) {
        string fullText = "";
        string textSinceHistoryTurn = "";
        TokenUsage total = default;
        var budget = new ContinueBudget(options.MaxContinueRequests);
        int emptyAnswerRetries = 0;
        string? continuePrompt = null;
        byte[]? continuationToken = null;
        var end = GenerationEnd.Cancelled;

        using var cancelScope = new ConsoleCancelScope();

        while (true) {
            if (options.BeforeRequestAsync != null && !await options.BeforeRequestAsync()) {
                end = GenerationEnd.Cancelled;
                break;
            }

            options.AnnounceRequest(budget.RequestNumber, budget.MaxTotal);

            string text = "";
            GroundingMetadata? grounding = null;
            byte[]? attemptContinuationToken = null;
            var usage = new UsageReport();
            bool callSuccess;

            var heartbeat = new StreamHeartbeat(options.SilenceNoticeInterval);
            try {
                callSuccess = await ApiRetryPolicy.ExecuteStreamWithRetryAsync(
                    streamFactory: () => WithSilenceTimeout(generate(history, continuationToken), options.FirstChunkTimeout),
                    onChunkReceived: chunk => {
                        heartbeat.Touch();
                        string answer = thoughts.Absorb(chunk);
                        options.OnChunk?.Invoke(chunk, answer);
                        Ui.Raw(answer);
                        text += answer;

                        grounding = ResponseParts.Grounding(chunk) ?? grounding;
                        attemptContinuationToken = ResponseParts.ContinuationToken(chunk) ?? attemptContinuationToken;

                        // Per request, the last chunk carrying usage wins; requests are summed below.
                        usage.Absorb(chunk.UsageMetadata);
                        usage.RecordFinishReason(ResponseParts.FinishReason(chunk));
                        return Task.CompletedTask;
                    },
                    cancellationToken: cancelScope.Token,
                    initialBackoff: options.InitialBackoff ?? 130,
                    retryContext: options.RetryContext,
                    onRetry: () => {
                        heartbeat.Restart();
                        text = "";
                        grounding = null;
                        attemptContinuationToken = null;
                        usage = new UsageReport();
                        thoughts.DiscardRequest();
                    },
                    highDemandDelay: options.HighDemandDelay,
                    resumeOnPartialProgress: true,
                    requestInputTokens: options.RequestInputTokens
                );
            }
            catch (Exception ex) {
                Ui.Error($"{options.UnrecoverableErrorPrefix}{ex.Describe()}", "Abbruch");
                end = GenerationEnd.Failed;
                break;
            }
            finally {
                heartbeat.Dispose();
            }

            if (options.ShowGroundingSources && grounding != null) {
                PrintGroundingSources(grounding);
            }

            if (!callSuccess) {
                const string cancelled = "Generierung durch Benutzer abgebrochen oder fehlgeschlagen.";
                if (options.CancelledAsWarning) Ui.Warn(cancelled);
                else Ui.Info(cancelled);
                end = GenerationEnd.Cancelled;
                break;
            }

            // Every completed request is billed, a blank answer included.
            total += new TokenUsage(usage.PromptTokens, usage.CandidateTokens, usage.CachedTokens, usage.ThoughtTokens, Requests: 1);
            var request = new GenerationRequest(budget.RequestNumber, text, usage, total, grounding, continuePrompt);
            options.ReportRequest?.Invoke(request);
            await thoughts.EndRequestAsync(budget.RequestNumber, usage);
            if (options.AfterRequestAsync != null) {
                await options.AfterRequestAsync(request);
            }

            bool isContinuationStop = options.SupportsLongDecode
                && ResponseParts.IsContinuationFinishReason(usage.FinishReason)
                && attemptContinuationToken is { Length: > 0 };

            if (isContinuationStop) {
                emptyAnswerRetries = 0;
                fullText += text;
                textSinceHistoryTurn += text;

                if (options.IsComplete(text) || options.IsComplete(textSinceHistoryTurn)) {
                    end = GenerationEnd.Completed;
                    break;
                }

                if (!budget.TryContinueContinuation(text, attemptContinuationToken!, out string continuationStopReason)) {
                    Ui.Warn($"{continuationStopReason} {options.BudgetStopSuffix}");
                    end = GenerationEnd.BudgetExhausted;
                    break;
                }

                int finishedHop = budget.RequestNumber - 1;
                continuationToken = attemptContinuationToken;
                continuePrompt = null;
                Ui.Info($"Long Decode: Hop {finishedHop} am Single-Request-Limit pausiert ({usage.CandidateTokens:N0} Output + {usage.ThoughtTokens:N0} Denk-Tokens, FinishReason: CONTINUATION). Setze nahtlos mit ContinuationToken fort (History unverändert)...", "Long Decode");

                if (!await options.WaitBeforeContinueAsync()) {
                    end = GenerationEnd.Cancelled;
                    break;
                }
                continue;
            }

            if (options.EmptyAnswerRetries > 0 && string.IsNullOrWhiteSpace(text)) {
                if (emptyAnswerRetries < options.EmptyAnswerRetries) {
                    emptyAnswerRetries++;
                    Ui.Error("Das Modell hat eine komplett leere Antwort zurückgegeben (z.B. wegen MALFORMED_RESPONSE oder Safety-Filtern).");
                    Ui.Detail($"Warte {options.EmptyAnswerRetryDelay.TotalSeconds:0} Sekunden vor Versuch {emptyAnswerRetries}/{options.EmptyAnswerRetries}...");
                    try {
                        await Task.Delay(options.EmptyAnswerRetryDelay, cancelScope.Token);
                    }
                    catch (OperationCanceledException) {
                        end = GenerationEnd.Cancelled;
                        break;
                    }
                    continue;
                }
                Ui.Error($"Das Modell hat nach {options.EmptyAnswerRetries} Versuchen weiterhin eine komplett leere Antwort zurückgegeben. Der Vorgang wird abgebrochen.");
                end = GenerationEnd.EmptyAnswers;
                break;
            }

            emptyAnswerRetries = 0;
            fullText += text;
            textSinceHistoryTurn += text;

            if (options.IsComplete(text) || (options.SupportsLongDecode && options.IsComplete(textSinceHistoryTurn))) {
                end = GenerationEnd.Completed;
                break;
            }

            if (!budget.TryContinue(text, out string stopReason)) {
                Ui.Warn($"{stopReason} {options.BudgetStopSuffix}");
                end = GenerationEnd.BudgetExhausted;
                break;
            }

            string anchorText = !string.IsNullOrWhiteSpace(text) ? text : textSinceHistoryTurn;
            continuePrompt = options.BuildContinuePrompt(anchorText);
            history.Add(new Content { Role = "model", Parts = [new Part { Text = textSinceHistoryTurn }] });
            history.Add(new Content { Role = "user", Parts = [new Part { Text = continuePrompt }] });
            continuationToken = null;
            textSinceHistoryTurn = "";

            if (!await options.WaitBeforeContinueAsync()) {
                end = GenerationEnd.Cancelled;
                break;
            }
        }

        await thoughts.EndAbortedRequestAsync(budget.RequestNumber);
        return new GenerationLoopResult(fullText, total, end);
    }

    /// <summary>
    /// [AI Context] Says so when a request has streamed nothing for a while; in a log that silence
    /// looked exactly like a hang. It does not guess why: on 2026-10-15 one silence was a long thinking
    /// phase (5+ minutes, then the answer), another a stalled request that delivered nothing for 20
    /// minutes although thought summaries were on (its retry then needed only 3,600 thinking tokens).
    /// Quiet during the retry policy's own waits, which print their own line.
    /// [Human] Meldet sich, wenn eine Anfrage lange nichts liefert, damit Stille nicht wie ein Hänger aussieht.
    /// </summary>
    private sealed class StreamHeartbeat : IDisposable {
        private readonly TimeSpan _interval;
        private DateTime _startedUtc = DateTime.UtcNow;
        private readonly Timer? _timer;
        private long _lastActivityTicks = DateTime.UtcNow.Ticks;   // last chunk or last notice
        private long _lastChunkTicks;                               // 0 until this attempt's first chunk

        public StreamHeartbeat(TimeSpan interval) {
            _interval = interval;
            if (interval > TimeSpan.Zero) {
                _timer = new Timer(_ => Check(), null, interval, interval);
            }
        }

        /// <summary>A chunk arrived.</summary>
        public void Touch() {
            long now = DateTime.UtcNow.Ticks;
            Interlocked.Exchange(ref _lastChunkTicks, now);
            Interlocked.Exchange(ref _lastActivityTicks, now);
        }

        /// <summary>A new attempt: the minutes count from its start, not the first attempt's.</summary>
        public void Restart() {
            _startedUtc = DateTime.UtcNow;
            Interlocked.Exchange(ref _lastChunkTicks, 0);
            Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        }

        private void Check() {
            long now = DateTime.UtcNow.Ticks;
            if (InteractiveDelay.IsInSmartDelay) {
                Interlocked.Exchange(ref _lastActivityTicks, now);
                return;
            }
            var silent = TimeSpan.FromTicks(now - Interlocked.Read(ref _lastActivityTicks));
            if (silent < _interval) return;
            Interlocked.Exchange(ref _lastActivityTicks, now);
            int minutes = (int)Math.Round((DateTime.UtcNow - _startedUtc).TotalMinutes);
            long lastChunk = Interlocked.Read(ref _lastChunkTicks);
            if (lastChunk != 0) {
                // Data did arrive: this is a stalled stream, not a long thinking phase.
                int stalled = (int)Math.Round(TimeSpan.FromTicks(now - lastChunk).TotalMinutes);
                Ui.Detail($"Stream liefert seit {stalled} min nichts mehr ({minutes} min seit Anfrage-Start). Strg+C bricht ab.", "API");
                return;
            }
            Ui.Detail($"Noch keine Daten vom Server ({minutes} min seit Anfrage-Start): lange Denkphase oder Warteschlange beim Server. Strg+C bricht ab.", "API");
        }

        public void Dispose() => _timer?.Dispose();
    }

    /// <summary>
    /// [AI Context] Ends an attempt with a <see cref="TimeoutException"/> when no chunk arrives within
    /// <paramref name="timeout"/>: before the first chunk (the retry policy waits briefly and sends the
    /// request again; on 2026-10-15 a request stalled for 20 minutes and its retry then streamed
    /// normally) and between any two later chunks (the retry policy keeps the text received so far and the
    /// loop continues with a continue prompt). Before 2026-10-18 only the first chunk was timed: three
    /// refinement streams on three keys went silent mid-answer and waited over an hour. The limit is on
    /// silence, not on length, so a long answer is never cut; thought chunks count as arrivals. The
    /// abandoned request is not cancelled at the server (the SDK call takes no token here).
    /// [Human] Bricht einen Versuch ab, der zu lange nichts liefert (vor dem ersten oder zwischen zwei Teilen).
    /// </summary>
    internal static async IAsyncEnumerable<GenerateContentResponse> WithSilenceTimeout(
        IAsyncEnumerable<GenerateContentResponse> source, TimeSpan timeout,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) {
        var enumerator = source.GetAsyncEnumerator(cancellationToken);
        bool abandoned = false;
        bool receivedAny = false;
        try {
            while (true) {
                var next = enumerator.MoveNextAsync().AsTask();
                if (timeout > TimeSpan.Zero) {
                    using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var finished = await Task.WhenAny(next, Task.Delay(timeout, timer.Token));
                    if (finished != next) {
                        abandoned = true;
                        _ = next.ContinueWith(t => _ = t.Exception, TaskScheduler.Default); // observe a late fault
                        throw new TimeoutException(receivedAny
                            ? $"Der Stream liefert seit {timeout.TotalMinutes:0.#} min nichts mehr (FirstChunkTimeoutMinutes)."
                            : $"Keine Daten vom Server innerhalb von {timeout.TotalMinutes:0.#} min (FirstChunkTimeoutMinutes).");
                    }
                    timer.Cancel();
                }
                if (!await next) yield break;
                receivedAny = true;
                yield return enumerator.Current;
            }
        }
        finally {
            // Disposing while the abandoned MoveNext is still pending would throw; let it finish on its own.
            if (!abandoned) {
                await enumerator.DisposeAsync();
            }
        }
    }

    private static void PrintGroundingSources(GroundingMetadata grounding) {
        Ui.Info("Quellen (Google Search Grounding):");
        if (grounding.WebSearchQueries != null && grounding.WebSearchQueries.Count > 0) {
            Ui.Detail($"Suchanfragen: {string.Join(", ", grounding.WebSearchQueries.Select(q => $"\"{q}\""))}");
        }
        if (grounding.GroundingChunks != null) {
            int refIdx = 1;
            foreach (var chunkRef in grounding.GroundingChunks) {
                if (chunkRef.Web != null) {
                    Ui.Detail($"[{refIdx}] {chunkRef.Web.Title} - {chunkRef.Web.Uri}");
                    refIdx++;
                }
            }
        }
    }
}
