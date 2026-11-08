using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;
using Google.GenAI;
using Google.GenAI.Types;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Provides a centralized, resilient execution wrapper for Google GenAI API calls.
/// Implements linear backoff, server-suggested delay parsing, and user-cancellable waits.
/// [Human] Diese Klasse schützt das Programm vor API-Ausfällen und Ratelimits. Sie wiederholt fehlgeschlagene Google-Anfragen intelligent.
/// </summary>
public static partial class ApiRetryPolicy {
    /// <summary>
    /// [AI Context] Global default delay in seconds when the model returns a "high demand" error.
    /// Defaults to 180 seconds (3 minutes). Can be overridden globally or via the <c>highDemandDelay</c> parameter.
    /// [Human] Standard-Wartezeit in Sekunden bei hoher Modellauslastung ("high demand").
    /// </summary>
    public static int DefaultHighDemandDelaySeconds { get; set; } = 180;

    /// <summary>
    /// [AI Context] Delay in seconds before retrying after a broken connection that is not an outage (reset,
    /// "Error while copying content to a stream"); a counted attempt. Set from <c>ConnectionErrorDelaySeconds</c>.
    /// Defaults to 60 (was a fixed 300 until 2026-10-23).
    /// [Human] Wartezeit in Sekunden nach einem Verbindungsabbruch (kein Internetausfall).
    /// </summary>
    public static int ConnectionErrorDelaySeconds { get; set; } = 60;

    /// <summary>
    /// [AI Context] The wait between attempts. Defaults to the interactive spinner; tests swap in an
    /// instant fake so the retry logic can be exercised without sleeping through real backoffs.
    /// [Human] Die Wartefunktion zwischen Versuchen (in Tests ersetzbar).
    /// </summary>
    public static Func<int, string, Task<bool>> DelayAsync { get; set; } = InteractiveDelay.SmartDelayAsync;

    /// <summary>What a retry message opens with: the step's context and a colon, or nothing.</summary>
    private static string ContextPrefix(string retryContext) =>
        string.IsNullOrWhiteSpace(retryContext) ? "" : $"{retryContext}: ";

    /// <summary>The Enter hint, only where a keyboard can answer it; an unattended run has none.</summary>
    private static string EnterHint => Ui.PromptSource.IsInteractive ? " (Oder drücke Enter für sofortigen Retry)" : "";

    /// <summary>
    /// [AI Context] Executes a streaming API call with a robust retry mechanism.
    /// On each retry, the optional <paramref name="onRetry"/> callback is invoked BEFORE the new attempt
    /// so callers can reset their accumulation buffers (e.g. <c>chunkResp = ""</c>) to prevent the
    /// partial-stream leak that occurs when a transient 503 mid-stream causes duplicate/corrupt output.
    ///
    /// <para>Two bounds: <paramref name="maxRetries"/> failures in a row without a single chunk, and
    /// <paramref name="maxTotalAttempts"/> attempts overall. The first resets whenever an attempt
    /// streamed something; the second never does, so a stream that keeps dying after its first chunk
    /// still ends.</para>
    /// [Human] Führt eine Google API Streaming-Anfrage mit automatischen Wiederholungen durch. Ein neuer
    /// Versuch beginnt von vorn (der Puffer wird geleert). Nur mit <paramref name="resumeOnPartialProgress"/>
    /// wird bei einem Abbruch nach genug Text dieser behalten und der Aufrufer setzt per 'Continue' fort.
    /// </summary>
    /// <param name="streamFactory">A function that creates the IAsyncEnumerable stream from the API.</param>
    /// <param name="onChunkReceived">An async action to process each received chunk from the stream.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <param name="maxRetries">Maximum number of consecutive attempts that fail without receiving any chunk.</param>
    /// <param name="initialBackoff">Initial delay in seconds for the first retry.</param>
    /// <param name="retryContext">Human-readable label printed in retry log messages.</param>
    /// <param name="onRetry">Optional callback invoked before every retry (attempt > 1). Use it to clear accumulation buffers.</param>
    /// <param name="highDemandDelay">Optional delay in seconds when the model is in high demand; falls back to DefaultHighDemandDelaySeconds if unspecified.</param>
    /// <param name="resumeOnPartialProgress">When a transient error ends a stream that already delivered
    /// <paramref name="minCharactersToResume"/> characters, return true and keep the text, so the caller
    /// sends a continue prompt instead of paying for the whole response again.</param>
    /// <param name="maxTotalAttempts">Hard cap on attempts, progress or not. Defaults to twice <paramref name="maxRetries"/>.</param>
    /// <returns>True if the stream completed (or was resumable), false if it was cancelled. Throws on unrecoverable errors.</returns>
    public static async Task<bool> ExecuteStreamWithRetryAsync(
        Func<IAsyncEnumerable<GenerateContentResponse>> streamFactory,
        Func<GenerateContentResponse, Task> onChunkReceived,
        CancellationToken cancellationToken,
        int maxRetries = 8,
        int initialBackoff = 130,
        string retryContext = "",
        Action? onRetry = null,
        int? highDemandDelay = null,
        bool resumeOnPartialProgress = false,
        int minCharactersToResume = 20,
        int? maxTotalAttempts = null,
        long? requestInputTokens = null) {
        int totalAttemptLimit = Math.Max(1, maxTotalAttempts ?? maxRetries * 2);
        string contextMsg = ContextPrefix(retryContext);
        int backoff = initialBackoff;
        int consecutiveFailuresWithoutProgress = 0;
        int perMinuteTokenRefusals = 0;
        int uncountedNetworkWaits = 0;
        bool resendAfterOutage = false;
        int attempt = 0;

        while (true) {
            attempt++;
            int chunksReceivedThisAttempt = 0;
            long charactersReceivedThisAttempt = 0;

            try {
                if (attempt > 1) {
                    // After an outage the wait already said it resends, and nothing was received to discard.
                    if (!resendAfterOutage) {
                        Ui.Warn($"{contextMsg}Sende Anfrage neu (Versuch {attempt}). Die bisherige Teilantwort wird verworfen...", "API Retry");
                    }
                    onRetry?.Invoke();
                }
                resendAfterOutage = false;

                SessionCostLedger.RecordRequest(isGeneration: true, attempt);
                var responseStream = streamFactory();
                await foreach (var chunk in responseStream.WithCancellation(cancellationToken)) {
                    if (cancellationToken.IsCancellationRequested) break;
                    chunksReceivedThisAttempt++;
                    // Answer text only: with IncludeThoughts on, chunk.Text also carries the thought
                    // summary, and a stream cut during thinking would look like partial output and be
                    // "continued" from an empty answer instead of retried.
                    charactersReceivedThisAttempt += ResponseParts.Split(chunk).Answer.Length;
                    await onChunkReceived(chunk);
                }

                return !cancellationToken.IsCancellationRequested;
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && (ex is OperationCanceledException || ex.InnerException is OperationCanceledException)) {
                return false; // User cancelled
            }
            catch (Exception rawEx) {
                var ex = (rawEx is OperationCanceledException || rawEx.InnerException is OperationCanceledException)
                    ? new TimeoutException("The API request timed out before returning any data.", rawEx)
                    : rawEx;
                // A per-minute input-token limit smaller than the request itself. Such a request can pass:
                // gemini-3.5-flash-lite (limit 250,000) accepted 274k and 310k warm-up handshakes on 2026-10-15/16,
                // most likely because their prefix was cached. IMPORTANT: that inference rests on Google accepting
                // them, not on the reported "Gecacht" figure, which is unreliable either way (the 310k one reported
                // 271,834 cached once and 0 the next day, accepted both times; Documentation.md 4.4). The first
                // refusal gets its wait; after a second one the retries stop - on 2026-10-15 a 392,711-token part
                // was refused for 19 minutes.
                long? perMinuteLimit = PerMinuteInputTokenLimit(ex);
                if (perMinuteLimit != null && requestInputTokens is long tokens && tokens > perMinuteLimit) {
                    perMinuteTokenRefusals++;
                    if (perMinuteTokenRefusals >= 2) {
                        Ui.Error($"{ex.Describe()}", "API");
                        Ui.Error(DescribeRequestOverPerMinuteLimit(tokens, perMinuteLimit.Value), "Quota");
                        throw;
                    }
                }

                if (!IsTransientError(ex)) {
                    Ui.Error($"{ex.Describe()}", "API");
                    if (IsDailyQuotaExhausted(ex)) {
                        Ui.Error("Das Tageskontingent dieses API-Keys ist aufgebraucht - ein Retry hilft erst nach dem Reset. Anderes API-Key-Profil wählen (CLI: --profile).", "Quota");
                        ApiKeyQuotaTracker.RecordQuotaExhaustedFromException(ex);
                    }
                    Ui.Error($"Unrecoverable error after {attempt} attempt(s).", "API Failure");
                    throw; // Re-throw for the caller to handle
                }

                // No DNS / no route: the request never left this machine, so it is neither counted against
                // the retry limit nor spent at Google. Wait for the connection (TCP connect probes only), resend at once.
                var outage = await WaitOutNetworkOutageAsync(ex, contextMsg, uncountedNetworkWaits, cancellationToken);
                if (outage == NetworkOutage.Cancelled) {
                    return false; // User cancelled the wait
                }
                if (outage == NetworkOutage.GaveUp) {
                    Ui.Error($"Unrecoverable error after {attempt} attempt(s).", "API Failure");
                    throw; // Same path as any other exhausted retry
                }
                if (outage == NetworkOutage.Recovered) {
                    SessionCostLedger.ForgetUnsentRequest(isGeneration: true, attempt);
                    uncountedNetworkWaits++;
                    resendAfterOutage = true;
                    attempt--;
                    continue;
                }

                if (resumeOnPartialProgress && charactersReceivedThisAttempt >= minCharactersToResume) {
                    Ui.Warn($"{contextMsg}Der Datenstream wurde vom Server vorzeitig unterbrochen ({ex.Describe()}).", "Stream Unterbrochen");
                    Ui.Info($"Da bereits {charactersReceivedThisAttempt:N0} Zeichen empfangen wurden, wird der Text behalten und per 'Continue' fortgesetzt.", "Auto-Resume");
                    return true;
                }

                Ui.Warn($"{ex.Describe()}", "API");

                bool madeProgress = chunksReceivedThisAttempt > 0;
                if (madeProgress) {
                    Ui.Info("Fortschritt während des Streams erkannt: Zähler für Fehlversuche in Folge wird zurückgesetzt.", "API Retry");
                    consecutiveFailuresWithoutProgress = 0;
                    backoff = initialBackoff;
                }
                else {
                    consecutiveFailuresWithoutProgress++;
                }

                if (consecutiveFailuresWithoutProgress >= maxRetries || attempt >= totalAttemptLimit) {
                    Ui.Error($"Unrecoverable error after {attempt} attempt(s).", "API Failure");
                    throw; // Re-throw for the caller to handle
                }

                // The last attempt still possible if no further attempt makes progress.
                int lastPossibleAttempt = Math.Min(totalAttemptLimit, attempt + maxRetries - consecutiveFailuresWithoutProgress);
                var (WaitSuccess, NewBackoff) = await HandleBackoffAsync(
                    ex,
                    isFirstFailure: consecutiveFailuresWithoutProgress <= 1,
                    nextAttempt: attempt + 1,
                    maxAttempts: lastPossibleAttempt,
                    backoff,
                    retryContext,
                    highDemandDelay
                );
                backoff = NewBackoff;
                if (!WaitSuccess) {
                    return false; // User cancelled the wait
                }
            }
        }
    }

    /// <summary>
    /// [AI Context] Executes a non-streaming, single-response API call with a robust retry mechanism.
    /// [Human] Führt eine einmalige API-Anfrage (für strukturierte Daten) mit automatischen Wiederholungen aus.
    /// </summary>
    public static async Task<T?> ExecuteWithRetryAsync<T>(
        Func<Task<T>> apiCall,
        int maxRetries = 8,
        int initialBackoff = 45,
        string retryContext = "",
        int? highDemandDelay = null,
        CancellationToken cancellationToken = default) where T : class {
        int backoff = initialBackoff;
        int uncountedNetworkWaits = 0;

        for (int attempt = 1; attempt <= maxRetries; attempt++) {
            try {
                if (attempt > 1) {
                    string contextMsg = ContextPrefix(retryContext);
                    Ui.Detail($"{contextMsg}Sending request (Attempt {attempt}/{maxRetries})...", "API Retry");
                }
                SessionCostLedger.RecordRequest(isGeneration: false, attempt);
                return await apiCall();
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested && (ex is OperationCanceledException || ex.InnerException is OperationCanceledException)) {
                return null; // User cancelled
            }
            catch (Exception rawEx) {
                var ex = (rawEx is OperationCanceledException || rawEx.InnerException is OperationCanceledException)
                    ? new TimeoutException("The API request timed out.", rawEx)
                    : rawEx;
                Ui.Error($"{ex.Describe()}", "API");

                // No DNS / no route: not counted, see ExecuteStreamWithRetryAsync. Even on the last attempt
                // an outage is waited out instead of failing the call.
                if (IsTransientError(ex)) {
                    var outage = await WaitOutNetworkOutageAsync(ex, ContextPrefix(retryContext), uncountedNetworkWaits, cancellationToken);
                    if (outage == NetworkOutage.Cancelled) {
                        return null; // User cancelled the wait
                    }
                    if (outage == NetworkOutage.GaveUp) {
                        Ui.Error($"Unrecoverable error after {attempt} attempts.", "API Failure");
                        return null;
                    }
                    if (outage == NetworkOutage.Recovered) {
                        SessionCostLedger.ForgetUnsentRequest(isGeneration: false, attempt);
                        uncountedNetworkWaits++;
                        attempt--;
                        continue;
                    }
                }

                if (IsTransientError(ex) && attempt < maxRetries) {
                    var (WaitSuccess, NewBackoff) = await HandleBackoffAsync(ex, isFirstFailure: attempt == 1, nextAttempt: attempt + 1, maxAttempts: maxRetries, backoff, retryContext, highDemandDelay);
                    backoff = NewBackoff;
                    if (!WaitSuccess) {
                        return null; // User cancelled the wait
                    }
                }
                else {
                    Ui.Error($"Unrecoverable error after {attempt} attempts.", "API Failure");
                    return null;
                }
            }
        }
        return null; // All retries failed
    }

    /// <summary>How a wait for the internet connection ended.</summary>
    private enum NetworkOutage {
        /// <summary>Not a host-not-found / no-route error (or too many outages in one call): the normal retry logic decides.</summary>
        NotApplicable,
        /// <summary>The host is reachable again: resend now, without using up an attempt.</summary>
        Recovered,
        /// <summary>The connection did not come back within <see cref="NetworkAvailability.MaxWaitMinutes"/>.</summary>
        GaveUp,
        /// <summary>The user cancelled (Ctrl+C) while waiting.</summary>
        Cancelled
    }

    /// <summary>Outages one call waits out without counting an attempt; a request that keeps failing right after the probe got through is not an outage.</summary>
    private const int MaxUncountedNetworkWaits = 10;

    /// <summary>
    /// [AI Context] For a host-not-found / no-route error: probes the host with a bare TCP connect (no
    /// request to Google, no quota) until it answers or <see cref="NetworkAvailability.MaxWaitMinutes"/>
    /// pass. A DNS lookup alone would answer from the cache during a no-route outage. Ctrl+C ends the
    /// wait like any other.
    /// [Human] Wartet bei fehlender Internetverbindung, bis Google wieder erreichbar ist, statt einen Versuch zu verbrauchen.
    /// </summary>
    private static async Task<NetworkOutage> WaitOutNetworkOutageAsync(Exception ex, string contextMsg, int uncountedWaitsSoFar, CancellationToken cancellationToken) {
        if (uncountedWaitsSoFar >= MaxUncountedNetworkWaits || !NetworkAvailability.IsDnsOrNoRouteError(ex)) {
            return NetworkOutage.NotApplicable;
        }

        Ui.Warn($"{contextMsg}Keine Internetverbindung zum Google-Server ({ex.Describe()}). Der Versuch zählt nicht; ich warte, bis der Server wieder erreichbar ist (Ctrl+C bricht ab).", "Netzwerk-Fehler");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        void cancelHandler(object? sender, ConsoleCancelEventArgs e) { e.Cancel = true; cts.Cancel(); }
        Console.CancelKeyPress += cancelHandler;
        try {
            bool back = await NetworkAvailability.WaitUntilHostReachableAsync(
                NetworkAvailability.GoogleApiHost,
                TimeSpan.FromSeconds(Math.Max(1, NetworkAvailability.PollIntervalSeconds)),
                TimeSpan.FromMinutes(Math.Max(0, NetworkAvailability.MaxWaitMinutes)),
                cts.Token,
                NetworkAvailability.Probe,
                NetworkAvailability.Delay);
            if (back) {
                Ui.Info($"{contextMsg}Die Verbindung ist zurück: Anfrage wird sofort neu gesendet, der fehlgeschlagene Versuch zählt nicht.", "API Retry");
                return NetworkOutage.Recovered;
            }
            return cts.IsCancellationRequested ? NetworkOutage.Cancelled : NetworkOutage.GaveUp;
        }
        finally {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>
    /// [AI Context] Identifies network connectivity drops (e.g. Wi-Fi disconnection, mobile hotspot drop).
    /// [Human] Erkennt, ob die Internetverbindung unterbrochen wurde (z.B. Hotspot ausgefallen).
    /// </summary>
    public static bool IsNetworkConnectionError(Exception ex) {
        string msg = ex.Message;
        string exStr = ex.ToString();

        // The SDK's API errors derive from HttpRequestException, but they are answers from the server,
        // not a lost connection - a 404 must not trigger the 5-minute "fix your hotspot" pause.
        if (IsApiError(ex)) {
            return false;
        }

        // Explicit rate limit, schema, or server error HTTP status codes should be handled by regular backoff or fail fast, not network pause.
        if (RetryableStatusRegex().IsMatch(msg) ||
            msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Only text is supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Thinking level is not supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        return ex is System.Net.Http.HttpRequestException ||
               ex is System.Net.Sockets.SocketException ||
               ex is System.IO.IOException ||
               ex is TimeoutException ||
               ex.InnerException is System.Net.Sockets.SocketException ||
               ex.InnerException is System.Net.Http.HttpRequestException ||
               ex.InnerException is TimeoutException ||
               msg.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Host ist unbekannt", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Error while copying content to a stream", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("A connection attempt failed", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Unable to read data from the transport connection", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("An error occurred while sending the request", StringComparison.OrdinalIgnoreCase) ||
               exStr.Contains("SocketException");
    }

    /// <summary>
    /// [AI Context] Determines if an exception is likely recoverable via retry.
    /// [Human] Erkennt, ob ein Fehler nur vorübergehend ist (z.B. Netzwerk-Wackler, Server überlastet) oder ob wir wirklich abbrechen müssen.
    /// </summary>
    public static bool IsTransientError(Exception ex) {
        string msg = ex.Message;
        string exStr = ex.ToString();

        // Non-recoverable API schema errors must fail fast and not trigger retries
        if (msg.Contains("Only text is supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("Thinking level is not supported", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("INVALID_ARGUMENT", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        // A 429 is normally worth waiting out, but not when a per-day quota is what ran out: the
        // server then suggests a retry many hours away (15h observed), which an unattended run would
        // sleep through. Failing fast lets the caller switch to another key instead.
        if (IsDailyQuotaExhausted(ex)) {
            return false;
        }

        // An API error with a status code is classified by that code alone: rate limits, timeouts and
        // server errors are worth waiting for; any other 4xx (bad key, wrong model name, missing file)
        // fails the same way on every retry, and each retry costs minutes of backoff.
        int? status = ApiStatusCode(ex);
        if (status != null) {
            return status is 408 or 429 || status >= 500;
        }

        if (IsNetworkConnectionError(ex)) return true;

        return RetryableStatusRegex().IsMatch(msg) ||
               ex is ServerError || ex.InnerException is ServerError ||
               msg.Contains("quota", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("QuotaFailure", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("high demand", StringComparison.OrdinalIgnoreCase) ||
               IsInterruptedStream(ex);
    }

    /// <summary>
    /// [AI Context] The HTTP status of a Google.GenAI <see cref="ClientError"/> / <see cref="ServerError"/>
    /// (also when wrapped), or null for other exceptions and for API errors built without a status.
    /// [Human] HTTP-Statuscode eines Google-API-Fehlers, sonst null.
    /// </summary>
    public static int? ApiStatusCode(Exception ex) {
        int code = ex switch {
            ClientError clientError => clientError.StatusCode,
            ServerError serverError => serverError.StatusCode,
            _ => 0
        };
        if (code <= 0 && ex.InnerException != null) {
            return ApiStatusCode(ex.InnerException);
        }
        return code > 0 ? code : null;
    }

    private static bool IsApiError(Exception ex) =>
        ex is ClientError or ServerError || ex.InnerException is ClientError or ServerError;

    /// <summary>
    /// [AI Context] True when a quota error names a per-day quota (e.g. the quota id
    /// "GenerateRequestsPerDayPerProjectPerModel-FreeTier" in the QuotaFailure details), as opposed to
    /// the per-minute limits a backoff does recover from.
    /// [Human] Erkennt, ob das Tageskontingent des API-Keys aufgebraucht ist.
    /// </summary>
    public static bool IsDailyQuotaExhausted(Exception ex) {
        string text = ex.ToString();
        bool isQuotaError = ApiStatusCode(ex) == 429
            || text.Contains("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase)
            || text.Contains("quota", StringComparison.OrdinalIgnoreCase);
        return isQuotaError && text.Contains("PerDay", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [AI Context] The limit of a per-minute input-token quota named in a 429 (e.g. "limit: 250000"
    /// with the quota id "GenerateContentInputTokensPerModelPerMinute-FreeTier"), or null for any
    /// other error, request-count quotas included.
    /// [Human] Das Token-pro-Minute-Limit aus einer 429-Fehlermeldung, sonst null.
    /// </summary>
    public static long? PerMinuteInputTokenLimit(Exception ex) {
        string text = ex.ToString();
        bool perMinuteTokens = text.Contains("InputTokens", StringComparison.OrdinalIgnoreCase)
            && text.Contains("PerMinute", StringComparison.OrdinalIgnoreCase);
        if (!perMinuteTokens) return null;
        var match = QuotaLimitRegex().Match(text);
        return match.Success && long.TryParse(match.Groups[1].Value, out long limit) ? limit : null;
    }

    /// <summary>The German message when a request over its per-minute token limit was refused twice (no cache hit).</summary>
    public static string DescribeRequestOverPerMinuteLimit(long requestTokens, long limit) =>
        $"Die Anfrage hat {requestTokens:N0} Eingabe-Tokens, das Limit dieses Keys und Modells ist {limit:N0} pro Minute, " +
        "und sie wurde zweimal abgelehnt. Solche Anfragen sind schon durchgegangen, vermutlich dank Prefix-Cache; diesmal nicht (Googles 'Gecacht'-Angabe ist dafür kein Beleg). " +
        "Auswege: Prefix-Cache-Warm-up einschalten (EnableImplicitPrefixCacheWarmup; der Cache greift aber nicht immer), " +
        "weniger History (die History-Liste in Stage1PromptFiles), keine vorherigen Teile mitschicken (DebugSendReferenceFile = false) oder ein bezahlter Key.";

    [GeneratedRegex(@"limit:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex QuotaLimitRegex();

    /// <summary>The server closed the stream mid-response (truncated JSON in the streamed body).</summary>
    private static bool IsInterruptedStream(Exception ex) {
        string msg = ex.Message;
        string exStr = ex.ToString();
        return msg.Contains("Incomplete JSON", StringComparison.OrdinalIgnoreCase) ||
               exStr.Contains("Incomplete JSON", StringComparison.OrdinalIgnoreCase) ||
               msg.Contains("ended prematurely", StringComparison.OrdinalIgnoreCase) ||
               exStr.Contains("ended prematurely", StringComparison.OrdinalIgnoreCase) ||
               ex is System.Text.Json.JsonException ||
               ex.InnerException is System.Text.Json.JsonException;
    }

    /// <summary>
    /// [AI Context] Implements a specific linear backoff strategy.
    /// On the first failure, reads server-suggested wait time and adds a 20s buffer. 
    /// On subsequent failures, increases wait time linearly by 30 seconds.
    /// [Human] Wenn die API überlastet ist oder das Netzwerk abgreißt, berechnet diese Methode, wie lange wir warten müssen.
    /// </summary>
    private static async Task<(bool WaitSuccess, int NewBackoff)> HandleBackoffAsync(
        Exception ex,
        bool isFirstFailure,
        int nextAttempt,
        int maxAttempts,
        int currentBackoff,
        string retryContext,
        int? highDemandDelay = null) {
        int waitTime;
        int nextBackoff;

        string contextMsg = ContextPrefix(retryContext);
        string delayMessage = "Warte auf Server-Bestätigung / Verarbeitung...";

        if (ex is TimeoutException || ex.InnerException is TimeoutException) {
            waitTime = 30;
            Ui.Warn($"{contextMsg}API-Anfrage hat das Zeitlimit überschritten ({ex.Describe()}). Warte {waitTime}s vor erneutem Versuch... (Versuch {nextAttempt}/{maxAttempts}){EnterHint}", "Timeout");
            nextBackoff = currentBackoff + 15;
            delayMessage = "Warte auf erneuten Versuch nach Timeout...";
        }
        else if (IsNetworkConnectionError(ex)) {
            waitTime = ConnectionErrorDelaySeconds > 0 ? ConnectionErrorDelaySeconds : 60;
            Ui.Warn($"{contextMsg}Verbindung zum Google-Server unterbrochen ({ex.Describe()}).", "Netzwerk-Fehler");
            Ui.Detail($"Keine Panik! Du hast jetzt {waitTime} Sekunden Zeit, um deinen Hotspot oder deine Internetverbindung zu reparieren...");
            Ui.Detail($"--> Sobald die Verbindung wieder steht, drücke ENTER, um sofort weiterzumachen! (Versuch {nextAttempt}/{maxAttempts})");
            delayMessage = "Warte auf Wiederherstellung der Internetverbindung / Hotspot...";
            nextBackoff = currentBackoff;
        }
        else if (ex.Message.Contains("high demand", StringComparison.OrdinalIgnoreCase)) {
            int highDemandWait = highDemandDelay.HasValue && highDemandDelay.Value > 0
                ? highDemandDelay.Value
                : DefaultHighDemandDelaySeconds;
            if (highDemandWait <= 0) highDemandWait = 180;
            waitTime = highDemandWait;
            string timeDesc = highDemandWait % 60 == 0 && highDemandWait > 0
                ? (highDemandWait == 60 ? "1 Minute" : $"{highDemandWait / 60} Minuten")
                : $"{highDemandWait} Sekunden";
            Ui.Warn($"{contextMsg}Das Modell ist stark nachgefragt. Warte {timeDesc}... (Versuch {nextAttempt}/{maxAttempts}){EnterHint}", "Hohe Auslastung");
            nextBackoff = waitTime;
        }
        else if (IsInterruptedStream(ex)) {
            waitTime = 20;
            Ui.Warn($"{contextMsg}Der Datenstream wurde vom Server vorzeitig unterbrochen ({ex.Describe()}). Warte {waitTime}s vor erneutem Versuch... (Versuch {nextAttempt}/{maxAttempts}){EnterHint}", "Stream Unterbrochen");
            nextBackoff = 30;
        }
        else {
            // On the very first failure, check for a server-suggested delay.
            if (isFirstFailure) {
                var retryMatch = MyRegex().Match(ex.Message);
                if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out int serverSuggestedDelay)) {
                    waitTime = serverSuggestedDelay + 20;
                    Ui.Warn($"{contextMsg}API schlägt Wartezeit von {serverSuggestedDelay}s vor. Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {nextAttempt}/{maxAttempts}){EnterHint}", "Rate Limit");
                }
                else {
                    waitTime = currentBackoff; // Use the initial backoff from the caller
                    Ui.Warn($"{contextMsg}Initiale Wartezeit: {waitTime} Sekunden... (Nächster Versuch: {nextAttempt}/{maxAttempts}){EnterHint}", "Rate Limit / Überlastung");
                }
                nextBackoff = waitTime;
            }
            else {
                waitTime = currentBackoff + 30;
                Ui.Warn($"{contextMsg}Inkrementiere Wartezeit. Warte {waitTime} Sekunden... (Nächster Versuch: {nextAttempt}/{maxAttempts}){EnterHint}", "Rate Limit");
                nextBackoff = waitTime;
            }
        }

        bool waitSuccess = await DelayAsync(waitTime, delayMessage);
        return (waitSuccess, nextBackoff);
    }

    [GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+)s""")]
    private static partial Regex MyRegex();

    // A status code as a whole token, so "1500 tokens" is not read as an HTTP 500.
    [GeneratedRegex(@"\b(?:408|429|500|502|503|504)\b")]
    private static partial Regex RetryableStatusRegex();
}