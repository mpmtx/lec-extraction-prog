using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Refinement;

/// <summary>
/// [AI Context] Makes refinement step 2 (speech refinement) unable to change anything outside its
/// <c>speech</c> blocks. The model re-emits the whole document (~165 KB) and makes copy slips the
/// prompt cannot prevent: on 2026-11-07 it retyped <c>(R1) -- node[below left=-1pt] {$q$} (RQ)</c>
/// as <c>(V1) -- ...</c> inside a tikzpicture of the Algebra week-8 lecture, with no intent in its
/// thoughts. So after the step every <i>segment</i> (the text between two speech blocks, plus the
/// text before the first and after the last) is taken from the step's input, and only the speech
/// blocks come from the model.
/// <para>The prompt allows three edits outside <c>speech</c>, and they survive: new
/// <c>% [REFINER-FLAG: ...]</c> and <c>% [TIMESTAMP-SUSPECT: ...]</c> comment lines, and new
/// <c>student</c> blocks split out of a student quote in a speech block. A quote in the middle of a
/// block splits it in two and adds a segment holding only the student block (and comment and blank
/// lines); <see cref="Align"/> matches the input's segments to the answer's around such additions.</para>
/// <para>When the structure cannot be matched - unbalanced <c>\begin/\end{speech}</c>, fewer speech
/// blocks than the input, an added segment holding anything else, or too many differing segments
/// once a split made the matching a choice - the guard changes nothing and says why: the model's
/// answer is used as it was before the guard existed.</para>
/// [Human] Schützt in Schritt 2 alles außerhalb der speech-Blöcke: Es wird aus der Eingabe
/// übernommen; vom Modell kommen nur die speech-Blöcke, neue Kommentarzeilen (REFINER-FLAG,
/// TIMESTAMP-SUSPECT) und neue student-Blöcke aus einer Zitat-Aufteilung.
/// </summary>
public static partial class NonSpeechGuard {
    /// <summary>Why the guard left the answer as it was.</summary>
    public enum SkipReason {
        None,
        NoSpeechBlocks,
        UnbalancedInput,
        UnbalancedAnswer,
        FewerSpeechBlocks,
        AddedSegmentWithContent,
        TooManyDifferences
    }

    /// <summary>
    /// One segment in which the model had changed something it may not, restored from the input.
    /// </summary>
    /// <param name="Segment">Index of the segment in the answer: segment <c>i</c> follows speech block <c>i</c> (0 = before the first).</param>
    /// <param name="InputLine">1-based line, in the input text given to <see cref="Apply"/>, of the first differing line.</param>
    /// <param name="InputText">That input line, or empty where the model only added lines.</param>
    /// <param name="ModelText">The model's line it replaced, or empty where the model only dropped lines.</param>
    public sealed record RestoredSegment(int Segment, int InputLine, string InputText, string ModelText);

    /// <param name="Body">The guarded text (the model's answer unchanged when <see cref="Applied"/> is false).</param>
    /// <param name="Segments">Segments in the answer.</param>
    /// <param name="Restored">Segments with a change other than whitespace, in order.</param>
    /// <param name="WhitespaceOnlySegments">Segments that differed only in blank lines or spaces (also restored).</param>
    /// <param name="KeptCommentLines">New REFINER-FLAG / TIMESTAMP-SUSPECT lines taken from the answer.</param>
    /// <param name="KeptStudentBlocks">New student blocks taken from the answer.</param>
    /// <param name="AddedSegments">Segments the answer added between the pieces of a split speech block, kept as they are.</param>
    /// <param name="UncheckedSegments">Segments too large to compare line by line; the model's text stands there.</param>
    /// <param name="SkipLine">For an unbalanced text, the line of the offending tag.</param>
    public sealed record Result(
        string Body,
        SkipReason Skip,
        int InputSpeechBlocks,
        int AnswerSpeechBlocks,
        int Segments,
        IReadOnlyList<RestoredSegment> Restored,
        int WhitespaceOnlySegments,
        int KeptCommentLines,
        int KeptStudentBlocks,
        int AddedSegments,
        int UncheckedSegments,
        int SkipLine = 0) {
        public bool Applied => Skip == SkipReason.None;
    }

    // A segment's line diff is quadratic; past this many cells (2000 x 2000 lines left after the
    // common head and tail are cut off) the segment is left as the model wrote it.
    private const long MaxDiffCells = 4_000_000;

    /// <summary>
    /// [AI Context] Rebuilds <paramref name="answer"/> from the segments of <paramref name="input"/>
    /// and the speech blocks of <paramref name="answer"/>. Both texts are bodies without header layers;
    /// line endings are normalised to <c>\n</c>.
    /// [Human] Setzt die Antwort aus den Abschnitten der Eingabe und den speech-Blöcken des Modells zusammen.
    /// </summary>
    public static Result Apply(string input, string answer) {
        input = input.Replace("\r\n", "\n");
        string normalizedAnswer = answer.Replace("\r\n", "\n");

        var inputStructure = Parse(input, out int inputErrorLine);
        if (inputStructure == null) return Skipped(answer, SkipReason.UnbalancedInput, 0, 0, inputErrorLine);
        var answerStructure = Parse(normalizedAnswer, out int answerErrorLine);
        if (answerStructure == null) return Skipped(answer, SkipReason.UnbalancedAnswer, inputStructure.Blocks.Count, 0, answerErrorLine);

        int inputBlocks = inputStructure.Blocks.Count, answerBlocks = answerStructure.Blocks.Count;
        if (inputBlocks == 0) return Skipped(answer, SkipReason.NoSpeechBlocks, 0, answerBlocks);
        if (answerBlocks < inputBlocks) return Skipped(answer, SkipReason.FewerSpeechBlocks, inputBlocks, answerBlocks);

        var alignment = Align(inputStructure.Segments, answerStructure.Segments, out int differingPairs);
        if (alignment == null) return Skipped(answer, SkipReason.AddedSegmentWithContent, inputBlocks, answerBlocks);
        // Without a split the pairing is fixed (segment i to segment i). After one, a pairing that
        // needs many restorations more likely pairs the wrong segments than finds many slips.
        if (answerBlocks > inputBlocks && differingPairs > Math.Max(5, inputStructure.Segments.Count / 4)) {
            return Skipped(answer, SkipReason.TooManyDifferences, inputBlocks, answerBlocks);
        }

        var body = new StringBuilder(normalizedAnswer.Length);
        var restored = new List<RestoredSegment>();
        int whitespaceOnly = 0, keptComments = 0, keptStudents = 0, added = 0, uncheckedSegments = 0;

        for (int segment = 0; segment < answerStructure.Segments.Count; segment++) {
            string answerSegment = answerStructure.Segments[segment];
            int source = alignment[segment];
            if (source < 0) {
                body.Append(answerSegment);
                added++;
                keptComments += answerSegment.Split('\n').Count(IsPermittedComment);
                keptStudents += StudentBlockIds(answerSegment.Split('\n')).Where(id => id >= 0).Distinct().Count();
            }
            else {
                var merge = Merge(inputStructure.Segments[source], answerSegment);
                body.Append(merge.Text);
                keptComments += merge.KeptComments;
                keptStudents += merge.KeptStudents;
                if (merge.TooLarge) uncheckedSegments++;
                if (merge.FirstDifference is var (lineInSegment, inputText, modelText)) {
                    int inputLine = LineOf(input, inputStructure.SegmentStarts[source]) + lineInSegment;
                    restored.Add(new RestoredSegment(segment, inputLine, inputText, modelText));
                }
                else if (merge.WhitespaceDiffers) {
                    whitespaceOnly++;
                }
            }
            if (segment < answerStructure.Blocks.Count) {
                body.Append(answerStructure.Blocks[segment]);
            }
        }

        return new Result(body.ToString(), SkipReason.None, inputBlocks, answerBlocks, answerStructure.Segments.Count,
            restored, whitespaceOnly, keptComments, keptStudents, added, uncheckedSegments);
    }

    /// <summary>
    /// [AI Context] One comment line for the step's header layer, so the .tex itself records what the
    /// guard did. <paramref name="lineOffset"/> turns the body's line numbers into the input file's.
    /// [Human] Eine Zeile für den Header des Schritts: was der Schutz getan hat.
    /// </summary>
    public static string HeaderNote(Result result, int lineOffset = 0) {
        const string prefix = "Non-speech guard: ";
        if (!result.Applied) {
            return prefix + "not applied (" + result.Skip switch {
                SkipReason.NoSpeechBlocks => "the input has no speech blocks",
                // No literal speech tags here: this line goes into the .tex header, where a tag counter could see it.
                SkipReason.UnbalancedInput => $"speech begin/end tags do not alternate in the input, line {result.SkipLine + lineOffset}",
                SkipReason.UnbalancedAnswer => $"speech begin/end tags do not alternate in the answer, line {result.SkipLine}",
                SkipReason.FewerSpeechBlocks => $"{result.InputSpeechBlocks} speech blocks in the input, {result.AnswerSpeechBlocks} in the answer",
                SkipReason.AddedSegmentWithContent => $"{result.AnswerSpeechBlocks - result.InputSpeechBlocks} added speech block(s) and an added segment holding more than a student block",
                SkipReason.TooManyDifferences => "too many segments differ to trust the matching after a split",
                _ => result.Skip.ToString()
            } + "); the answer is used unchecked outside speech";
        }

        var parts = new List<string>();
        if (result.Restored.Count == 0) {
            parts.Add($"all {result.Segments} segments outside speech match the input");
        }
        else {
            var lines = result.Restored.Take(5).Select(r => (r.InputLine + lineOffset).ToString());
            string more = result.Restored.Count > 5 ? ", ..." : "";
            parts.Add($"restored {result.Restored.Count} of {result.Segments} segments outside speech from the input (input line {string.Join(", ", lines)}{more})");
        }
        if (result.WhitespaceOnlySegments > 0) parts.Add($"{result.WhitespaceOnlySegments} more differed only in whitespace");
        if (result.KeptCommentLines > 0) parts.Add($"kept {result.KeptCommentLines} flag line(s)");
        if (result.KeptStudentBlocks > 0) parts.Add($"kept {result.KeptStudentBlocks} new student block(s)");
        if (result.UncheckedSegments > 0) parts.Add($"{result.UncheckedSegments} segment(s) too large to check");
        return prefix + string.Join("; ", parts);
    }

    /// <summary>
    /// [AI Context] Logs what the guard did: a warning for every segment it restored (with the first
    /// differing line of each) and for a skipped guard, a detail line otherwise.
    /// [Human] Meldet in der Konsole, was der Schutz wiederhergestellt oder warum er nichts getan hat.
    /// </summary>
    public static void Report(Result result, int lineOffset, string scope) {
        if (!result.Applied) {
            string reason = result.Skip switch {
                SkipReason.NoSpeechBlocks => "die Eingabe hat keine speech-Blöcke",
                SkipReason.UnbalancedInput => $"\\begin/\\end{{speech}} der Eingabe sind nicht ausgeglichen (Zeile {result.SkipLine + lineOffset})",
                SkipReason.UnbalancedAnswer => $"\\begin/\\end{{speech}} der Antwort sind nicht ausgeglichen (Zeile {result.SkipLine} des Inhalts)",
                SkipReason.FewerSpeechBlocks => $"die Antwort hat weniger speech-Blöcke als die Eingabe ({result.AnswerSpeechBlocks} statt {result.InputSpeechBlocks})",
                SkipReason.AddedSegmentWithContent => $"die Antwort hat {result.AnswerSpeechBlocks - result.InputSpeechBlocks} speech-Block/Blöcke mehr, und ein zusätzlicher Abschnitt enthält mehr als einen student-Block",
                SkipReason.TooManyDifferences => "nach einer Aufteilung weichen zu viele Abschnitte ab, um sie sicher zuzuordnen",
                _ => result.Skip.ToString()
            };
            Ui.Warn($"Nicht-speech-Schutz nicht angewendet: {reason}. Außerhalb von speech gilt die Antwort des Modells ungeprüft.", scope);
            return;
        }

        if (result.Restored.Count == 0) {
            Ui.Detail($"Nicht-speech-Schutz: alle {result.Segments} Abschnitte außerhalb von speech stimmen mit der Eingabe überein.", scope);
        }
        else {
            Ui.Warn($"Nicht-speech-Schutz: {result.Restored.Count} von {result.Segments} Abschnitten außerhalb von speech hatte das Modell verändert; sie sind aus der Eingabe wiederhergestellt.", scope);
            foreach (var segment in result.Restored) {
                Ui.Detail($"Eingabe-Zeile {segment.InputLine + lineOffset}: Eingabe \"{Shorten(segment.InputText)}\" / Modell \"{Shorten(segment.ModelText)}\"", scope);
            }
        }
        if (result.WhitespaceOnlySegments > 0) {
            Ui.Detail($"{result.WhitespaceOnlySegments} weitere(r) Abschnitt(e) unterschied(en) sich nur in Leerzeilen oder Leerzeichen; aus der Eingabe übernommen.", scope);
        }
        if (result.KeptCommentLines > 0 || result.KeptStudentBlocks > 0) {
            string split = result.AddedSegments > 0 ? $", {result.AddedSegments} zusätzliche(r) Abschnitt(e) aus Zitat-Aufteilungen" : "";
            Ui.Detail($"Vom Modell übernommen: {result.KeptCommentLines} Kommentarzeile(n) (REFINER-FLAG/TIMESTAMP-SUSPECT), {result.KeptStudentBlocks} neue(r) student-Block/Blöcke{split}.", scope);
        }
        if (result.UncheckedSegments > 0) {
            Ui.Warn($"{result.UncheckedSegments} Abschnitt(e) zu groß für den Zeilenvergleich; dort gilt die Antwort des Modells.", scope);
        }
    }

    private static string Shorten(string line) {
        string trimmed = line.Trim();
        if (trimmed.Length == 0) return "(nichts)";
        return trimmed.Length <= 160 ? trimmed : trimmed[..157] + "...";
    }

    private static Result Skipped(string answer, SkipReason reason, int inputBlocks, int answerBlocks, int line = 0) =>
        new(answer, reason, inputBlocks, answerBlocks, 0, [], 0, 0, 0, 0, 0, line);

    // ---- Structure ---------------------------------------------------------------

    /// <summary>The segments and speech blocks of a text, alternating: segment 0, block 0, segment 1, ..., segment n.</summary>
    private sealed record Structure(List<string> Segments, List<int> SegmentStarts, List<string> Blocks);

    // `spoken-clean` is the environment's name before the rename; older documents still use it.
    [GeneratedRegex(@"\\(begin|end)\{(speech|spoken-clean)\}")]
    private static partial Regex SpeechTagRegex();

    [GeneratedRegex(@"\\begin\{student\}")]
    private static partial Regex StudentBeginRegex();

    [GeneratedRegex(@"\\end\{student\}")]
    private static partial Regex StudentEndRegex();

    /// <summary>Null when the speech tags do not alternate begin/end; <paramref name="errorLine"/> then names the tag.</summary>
    private static Structure? Parse(string text, out int errorLine) {
        var segments = new List<string>();
        var starts = new List<int>();
        var blocks = new List<string>();
        int segmentStart = 0, blockStart = 0;
        string? open = null;

        foreach (Match match in SpeechTagRegex().Matches(text)) {
            if (IsInComment(text, match.Index)) continue; // e.g. a flag line that mentions \end{speech}
            string name = match.Groups[2].Value;
            if (match.Groups[1].Value == "begin") {
                if (open != null) { errorLine = LineOf(text, match.Index); return null; }
                segments.Add(text[segmentStart..match.Index]);
                starts.Add(segmentStart);
                blockStart = match.Index;
                open = name;
            }
            else {
                if (open != name) { errorLine = LineOf(text, match.Index); return null; }
                blocks.Add(text[blockStart..(match.Index + match.Length)]);
                segmentStart = match.Index + match.Length;
                open = null;
            }
        }
        if (open != null) { errorLine = LineOf(text, blockStart); return null; }

        segments.Add(text[segmentStart..]);
        starts.Add(segmentStart);
        errorLine = 0;
        return new Structure(segments, starts, blocks);
    }

    /// <summary>True when an unescaped % precedes <paramref name="index"/> on its line.</summary>
    private static bool IsInComment(string text, int index) {
        int lineStart = index == 0 ? 0 : text.LastIndexOf('\n', index - 1) + 1;
        for (int i = lineStart; i < index; i++) {
            if (text[i] == '\\') i++;
            else if (text[i] == '%') return true;
        }
        return false;
    }

    private static int LineOf(string text, int index) {
        int line = 1;
        for (int i = 0; i < index && i < text.Length; i++) {
            if (text[i] == '\n') line++;
        }
        return line;
    }

    private static bool IsPermittedComment(string line) {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("% [REFINER-FLAG:", StringComparison.Ordinal)
            || trimmed.StartsWith("% [TIMESTAMP-SUSPECT:", StringComparison.Ordinal);
    }

    /// <summary>For each line, the number of the student block it belongs to (begin and end lines included), or -1.</summary>
    private static int[] StudentBlockIds(string[] lines) {
        var ids = new int[lines.Length];
        int current = -1, next = 0;
        for (int i = 0; i < lines.Length; i++) {
            if (current < 0 && HasUncommentedMatch(StudentBeginRegex(), lines[i])) current = next++;
            ids[i] = current;
            if (current >= 0 && HasUncommentedMatch(StudentEndRegex(), lines[i])) current = -1;
        }
        return ids;
    }

    /// <summary>A match of <paramref name="tag"/> in <paramref name="line"/> that no % before it comments out.</summary>
    private static bool HasUncommentedMatch(Regex tag, string line) {
        foreach (Match match in tag.Matches(line)) {
            if (!IsInComment(line, match.Index)) return true;
        }
        return false;
    }

    // ---- Alignment ---------------------------------------------------------------

    /// <summary>
    /// [AI Context] For each answer segment, the input segment it stands for, or -1 for a segment a
    /// student-quote split added. Every input segment is matched once and in order; an answer segment
    /// may be left unmatched only if it holds nothing but student blocks, flag lines and blank lines.
    /// Among the pairings allowed, the one with the fewest differing segments wins; ties go to the
    /// pairing whose segments are most alike, student blocks included. That decides where an added
    /// segment goes when an input segment holding only a student block has two candidates: if the
    /// model also edited that block (dropping an "uh"), the edited copy is still far more alike than
    /// the new quote next to it.
    /// Null when no pairing is allowed. <paramref name="differingPairs"/> counts the pairs whose
    /// content (student blocks and flag lines aside) differs.
    /// [Human] Ordnet jedem Abschnitt der Antwort seinen Abschnitt der Eingabe zu.
    /// </summary>
    private static int[]? Align(List<string> input, List<string> answer, out int differingPairs) {
        int k = input.Count, m = answer.Count, extra = m - k;
        differingPairs = 0;
        if (extra < 0) return null;

        string[] inputCore = [.. input.Select(Core)], answerCore = [.. answer.Select(Core)];
        string[] inputLoose = [.. input.Select(Loose)], answerLoose = [.. answer.Select(Loose)];

        // A differing core outweighs any sum of likeness costs (at most 100 per pair). Without an added
        // segment the pairing is fixed, so the likeness is not computed at all.
        const long CoreMismatch = 100_000_000;
        long PairCost(int i, int j) =>
            (inputCore[i] == answerCore[j] ? 0 : CoreMismatch) + (extra == 0 ? 0 : Dissimilarity(inputLoose[i], answerLoose[j]));

        const long Unreachable = long.MaxValue / 4;
        // cost[i, s]: input segments 0..i-1 matched, answer segments 0..i+s-1 used, s of them unmatched.
        var cost = new long[k + 1, extra + 1];
        var cameByMatch = new bool[k + 1, extra + 1];
        for (int i = 0; i <= k; i++) for (int s = 0; s <= extra; s++) cost[i, s] = Unreachable;
        cost[0, 0] = 0;

        for (int i = 0; i <= k; i++) {
            for (int s = 0; s <= extra; s++) {
                if (cost[i, s] == Unreachable) continue;
                int j = i + s;
                if (i < k && j < m) {
                    long match = cost[i, s] + PairCost(i, j);
                    if (match < cost[i + 1, s]) { cost[i + 1, s] = match; cameByMatch[i + 1, s] = true; }
                }
                if (s < extra && j < m && answerCore[j].Length == 0 && cost[i, s] < cost[i, s + 1]) {
                    cost[i, s + 1] = cost[i, s];
                    cameByMatch[i, s + 1] = false;
                }
            }
        }
        if (cost[k, extra] == Unreachable) return null;

        var alignment = new int[m];
        for (int i = k, s = extra; i > 0 || s > 0;) {
            if (cameByMatch[i, s]) {
                alignment[i - 1 + s] = i - 1;
                if (inputCore[i - 1] != answerCore[i - 1 + s]) differingPairs++;
                i--;
            }
            else {
                alignment[i + s - 1] = -1;
                s--;
            }
        }
        return alignment;
    }

    /// <summary>What a segment says apart from student blocks, flag lines and whitespace.</summary>
    private static string Core(string segment) {
        string[] lines = segment.Split('\n');
        int[] students = StudentBlockIds(lines);
        return string.Join("\n", lines.Where((line, i) => students[i] < 0 && !IsPermittedComment(line))
            .Select(line => line.Trim()).Where(line => line.Length > 0));
    }

    /// <summary>A segment without flag lines and whitespace, student blocks included.</summary>
    private static string Loose(string segment) =>
        string.Join("\n", segment.Split('\n').Where(line => !IsPermittedComment(line))
            .Select(line => line.Trim()).Where(line => line.Length > 0));

    /// <summary>
    /// 0 for equal texts, otherwise 1 (nearly equal) to 100 (nothing in common): one minus the Dice
    /// coefficient of their character pairs.
    /// </summary>
    private static int Dissimilarity(string x, string y) {
        if (x == y) return 0;
        if (x.Length < 2 || y.Length < 2) return 100;
        var pairs = new Dictionary<int, int>();
        for (int i = 0; i + 1 < x.Length; i++) {
            int key = (x[i] << 16) | x[i + 1];
            pairs[key] = pairs.GetValueOrDefault(key) + 1;
        }
        int shared = 0;
        for (int i = 0; i + 1 < y.Length; i++) {
            int key = (y[i] << 16) | y[i + 1];
            if (pairs.TryGetValue(key, out int count) && count > 0) {
                pairs[key] = count - 1;
                shared++;
            }
        }
        double dice = 2.0 * shared / (x.Length - 1 + y.Length - 1);
        return 1 + (int)Math.Round((1 - dice) * 99);
    }

    // ---- Merging one segment -----------------------------------------------------

    private sealed record SegmentMerge(
        string Text, int KeptComments, int KeptStudents, bool WhitespaceDiffers, bool TooLarge,
        (int LineInSegment, string InputText, string ModelText)? FirstDifference);

    /// <summary>
    /// [AI Context] The input segment, plus the lines of the answer segment the prompt allows to be
    /// new: flag comment lines, and student blocks whose \begin and \end lines are both new (with the
    /// blank lines around them) - unless the same stretch also lost lines of an input student block, which makes
    /// it a rewritten block rather than a new one. Each new line keeps its place relative to the
    /// input lines around it; where the model also replaced input lines, the restored lines go where
    /// the model's first replaced line was, so a flag stays above the environment it flags.
    /// [Human] Abschnitt aus der Eingabe, ergänzt um die erlaubten neuen Zeilen der Antwort.
    /// </summary>
    private static SegmentMerge Merge(string input, string answer) {
        if (input == answer) return new SegmentMerge(input, 0, 0, false, false, null);

        string[] a = input.Split('\n'), b = answer.Split('\n');
        int head = 0;
        while (head < a.Length && head < b.Length && a[head] == b[head]) head++;
        int tail = 0;
        while (tail < a.Length - head && tail < b.Length - head && a[a.Length - 1 - tail] == b[b.Length - 1 - tail]) tail++;
        int n = a.Length - head - tail, m = b.Length - head - tail;
        if ((long)(n + 1) * (m + 1) > MaxDiffCells) return new SegmentMerge(answer, 0, 0, false, true, null);

        int[] studentsA = StudentBlockIds(a), studentsB = StudentBlockIds(b);

        // Longest common subsequence of the middle lines. A blank line inside a student block of the
        // answer never pairs with an input line: paired with some blank line of the input, it would cut
        // a new multi-paragraph quote in two and put restored input lines inside it.
        bool Same(int i, int j) =>
            a[head + i] == b[head + j] && !(studentsB[head + j] >= 0 && string.IsNullOrWhiteSpace(b[head + j]));
        var lcs = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--) {
            for (int j = m - 1; j >= 0; j--) {
                lcs[i, j] = Same(i, j) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        // Walk it into matched lines and hunks (runs of unmatched lines on either side).
        var matchedB = new bool[b.Length];
        for (int j = 0; j < head; j++) matchedB[j] = true;
        for (int j = b.Length - tail; j < b.Length; j++) matchedB[j] = true;
        var steps = new List<(int A, int B)>(); // (a, b) matched; (a, -1) input only; (-1, b) answer only
        for (int i = 0, j = 0; i < n || j < m;) {
            if (i < n && j < m && Same(i, j)) {
                steps.Add((head + i, head + j));
                matchedB[head + j] = true;
                i++; j++;
            }
            else if (j < m && (i == n || lcs[i, j + 1] >= lcs[i + 1, j])) {
                steps.Add((-1, head + j));
                j++;
            }
            else {
                steps.Add((head + i, -1));
                i++;
            }
        }

        // A student block of the answer is new when neither its \begin nor its \end line pairs with an
        // input line. Lines in between may: a short quote line ("Ja.") can equal some input line.
        var firstLine = new Dictionary<int, int>();
        var lastLine = new Dictionary<int, int>();
        for (int j = 0; j < b.Length; j++) {
            if (studentsB[j] < 0) continue;
            firstLine.TryAdd(studentsB[j], j);
            lastLine[studentsB[j]] = j;
        }
        var newStudentBlocks = new HashSet<int>(firstLine.Keys.Where(id => !matchedB[firstLine[id]] && !matchedB[lastLine[id]]));

        var result = new List<string>(a.Length + 4);
        result.AddRange(a[..head]);
        int keptComments = 0;
        var keptStudents = new HashSet<int>();
        bool whitespaceDiffers = false;
        (int, string, string)? firstDifference = null;

        for (int index = 0; index < steps.Count;) {
            if (steps[index].A >= 0 && steps[index].B >= 0) {
                result.Add(a[steps[index].A]);
                index++;
                continue;
            }

            var inputOnly = new List<int>();
            var answerOnly = new List<int>();
            for (; index < steps.Count && (steps[index].A < 0 || steps[index].B < 0); index++) {
                if (steps[index].A >= 0) inputOnly.Add(steps[index].A);
                else answerOnly.Add(steps[index].B);
            }
            int nextInputLine = index < steps.Count ? steps[index].A : a.Length - tail;

            bool rewritesInputStudent = inputOnly.Any(i => studentsA[i] >= 0);
            var keep = answerOnly.ToDictionary(j => j, j =>
                IsPermittedComment(b[j]) || (!rewritesInputStudent && studentsB[j] >= 0 && newStudentBlocks.Contains(studentsB[j])));
            if (answerOnly.Any(j => keep[j] && studentsB[j] >= 0)) {
                foreach (int j in answerOnly.Where(j => string.IsNullOrWhiteSpace(b[j]))) keep[j] = true;
            }

            bool inputPlaced = false;
            foreach (int j in answerOnly) {
                if (keep[j]) {
                    result.Add(b[j]);
                    if (IsPermittedComment(b[j])) keptComments++;
                    if (studentsB[j] >= 0) keptStudents.Add(studentsB[j]);
                }
                else if (!inputPlaced) {
                    result.AddRange(inputOnly.Select(i => a[i]));
                    inputPlaced = true;
                }
            }
            if (!inputPlaced) result.AddRange(inputOnly.Select(i => a[i]));

            var dropped = answerOnly.Where(j => !keep[j]).ToList();
            bool substantive = inputOnly.Any(i => !string.IsNullOrWhiteSpace(a[i])) || dropped.Any(j => !string.IsNullOrWhiteSpace(b[j]));
            if (substantive && firstDifference == null) {
                int firstInput = inputOnly.FirstOrDefault(i => !string.IsNullOrWhiteSpace(a[i]), -1);
                int firstDropped = dropped.FirstOrDefault(j => !string.IsNullOrWhiteSpace(b[j]), -1);
                int line = firstInput >= 0 ? firstInput : nextInputLine;
                firstDifference = (line, firstInput >= 0 ? a[firstInput] : "", firstDropped >= 0 ? b[firstDropped] : "");
            }
            else if (!substantive && (inputOnly.Count > 0 || dropped.Count > 0)) {
                whitespaceDiffers = true;
            }
        }
        result.AddRange(a[(a.Length - tail)..]);

        return new SegmentMerge(string.Join("\n", result), keptComments, keptStudents.Count, whitespaceDiffers, false, firstDifference);
    }
}
