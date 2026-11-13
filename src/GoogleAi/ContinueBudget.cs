namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Bounds the "continue" requests of one generation loop (extraction part, refinement
/// step, PDF fix). Each continue re-sends the whole history - for extraction that includes the
/// video - so an unbounded loop is an unbounded bill.
///
/// <para>Two limits. A hard total, so even a model that keeps producing text stops eventually. And a
/// limit on requests in a row that made no progress: an empty response, or one whose tail repeats
/// the previous response's tail (a model stuck re-emitting the same block until MAX_TOKENS). A
/// plain "reset the counter whenever there was text" has neither, which is the bug this replaces.</para>
/// [Human] Begrenzt die Continue-Anfragen einer Generierung: harte Obergrenze plus Abbruch, wenn
/// mehrere Antworten hintereinander leer sind oder sich wiederholen.
/// </summary>
public sealed class ContinueBudget {
    private const int TailLength = 300;

    /// <summary>
    /// [AI Context] How much of the cut-off response a continue prompt quotes back. The quote carries no
    /// new information: the WHOLE response is already in the history as the model turn before it. It is
    /// only an anchor that says "resume exactly here" (useful when the cut falls inside a formula or an
    /// environment), so its length is a matter of taste, not cost - 500 characters are ~150 tokens
    /// against a prompt of hundreds of thousands. Separate from TailLength, which only detects a model
    /// repeating itself.
    /// [Human] Wie viel vom abgeschnittenen Text der Continue-Prompt als Anker wiederholt.
    /// </summary>
    public const int EchoLength = 500;

    /// <summary>
    /// The end of <paramref name="response"/> for a continue prompt: its last
    /// <paramref name="length"/> characters (EchoLength when not positive).
    /// </summary>
    public static string EchoTail(string response, int length = EchoLength) {
        if (length <= 0) length = EchoLength;
        return response.Length > length ? "...\n" + response[^length..] : response;
    }

    private readonly int _maxWithoutProgress;
    private string _previousTail = "";
    private byte[]? _previousContinuationToken;

    public ContinueBudget(int maxTotal, int maxWithoutProgress = 3) {
        MaxTotal = Math.Max(1, maxTotal);
        _maxWithoutProgress = Math.Max(1, maxWithoutProgress);
    }

    /// <summary>The 1-based number of the request currently being sent.</summary>
    public int RequestNumber { get; private set; } = 1;

    public int MaxTotal { get; }

    /// <summary>Responses in a row that were empty or repeated the previous tail.</summary>
    public int RequestsWithoutProgress { get; private set; }

    /// <summary>
    /// Records the response of the request just sent. Returns true if another continue request may
    /// follow, otherwise false with a German, user-facing <paramref name="stopReason"/>.
    /// </summary>
    public bool TryContinue(string response, out string stopReason) {
        string tail = response.Length > TailLength ? response[^TailLength..] : response;
        bool madeProgress = !string.IsNullOrWhiteSpace(response) && tail != _previousTail;
        _previousTail = tail;
        _previousContinuationToken = null;
        return TryAdvance(madeProgress, out stopReason);
    }

    /// <summary>
    /// [AI Context] Records a Long Decode hop that stopped with <c>FinishReason.Continuation</c>.
    /// When the hop spent all 65,536 tokens on reasoning (empty visible text, fresh token),
    /// a changed <paramref name="continuationToken"/> still counts as forward progress.
    /// [Human] Zählt einen Long-Decode-Hop auch bei reinen Denk-Tokens als Fortschritt, sofern ein neuer Token kam.
    /// </summary>
    public bool TryContinueContinuation(string response, byte[] continuationToken, out string stopReason) {
        string tail = response.Length > TailLength ? response[^TailLength..] : response;
        bool textAdvanced = !string.IsNullOrWhiteSpace(response) && tail != _previousTail;
        bool tokenAdvanced = continuationToken.Length > 0 &&
                             (_previousContinuationToken == null || !continuationToken.AsSpan().SequenceEqual(_previousContinuationToken));
        _previousTail = tail;
        _previousContinuationToken = continuationToken;
        return TryAdvance(textAdvanced || tokenAdvanced, out stopReason);
    }

    private bool TryAdvance(bool madeProgress, out string stopReason) {
        RequestsWithoutProgress = madeProgress ? 0 : RequestsWithoutProgress + 1;

        if (RequestNumber >= MaxTotal) {
            stopReason = $"Maximale Anzahl an Requests ({MaxTotal}) erreicht.";
            return false;
        }
        if (RequestsWithoutProgress >= _maxWithoutProgress) {
            stopReason = $"{RequestsWithoutProgress} Antworten in Folge ohne Fortschritt (leer oder wiederholt).";
            return false;
        }

        RequestNumber++;
        stopReason = "";
        return true;
    }
}
