using System;
using System.Linq;
using LectureExtraction.Refinement;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers <see cref="NonSpeechGuard"/>, which makes refinement step 2 unable to change anything outside
/// its speech blocks: on 2026-11-07 the model retyped "(R1) --" as "(V1) --" in a tikzpicture of a
/// content block while re-emitting the whole document.
/// </summary>
public class NonSpeechGuardTests {
    private static string N(string text) => text.Replace("\r\n", "\n");

    private static readonly string Input = N("""
        \section{Homomorphisms}

        \begin{speech}[00:00:00 - 00:01:00]
        So, um, we we draw the diagram.
        \end{speech}

        \begin{content}[Diagram]
        \begin{tikzpicture}
            \draw[->, thick, figBlue] (V1) -- node[above] {$f$} (V2);
            \draw[->, thick, figBlue] (R1) -- node[below left=-1pt] {$q$} (RQ);
        \end{tikzpicture}
        \end{content}

        \begin{speech}[00:01:00 - 00:02:00]
        And, uh, this is the quotient \inlinemetanote{ein Student antwortet: \qt{The kernel}} yes, the kernel.
        \end{speech}

        \begin{theorem}[First isomorphism theorem]
        $G_1/\ker f \cong \operatorname{Im} f$.
        \end{theorem}
        """);

    private const string Speech1 = "So, um, we we draw the diagram.";
    private const string Speech1Refined = "So we draw the diagram.";
    private const string SlipInput = @"(R1) -- node[below left=-1pt] {$q$} (RQ)";
    private const string SlipModel = @"(V1) -- node[below left=-1pt] {$q$} (RQ)";
    private const string Flag = "% [REFINER-FLAG: label q sits on the wrong arrow]";

    private static int LineOf(string text, string fragment) =>
        text.Split('\n').Select((line, index) => (line, index)).First(pair => pair.line.Contains(fragment)).index + 1;

    // ---- The case from 2026-11-07 ---------------------------------------------------

    [Fact]
    public void ChangedTikzLine_IsRestored_AndAnAddedFlagLineIsKept() {
        string answer = Input
            .Replace(Speech1, Speech1Refined)
            .Replace(SlipInput, SlipModel)
            .Replace("\\begin{content}[Diagram]", Flag + "\n\\begin{content}[Diagram]");
        string expected = Input
            .Replace(Speech1, Speech1Refined)
            .Replace("\\begin{content}[Diagram]", Flag + "\n\\begin{content}[Diagram]");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.True(result.Applied);
        Assert.Equal(expected, result.Body);
        var restored = Assert.Single(result.Restored);
        Assert.Equal(1, restored.Segment);
        Assert.Equal(LineOf(Input, SlipInput), restored.InputLine);
        Assert.Contains(SlipInput, restored.InputText);
        Assert.Contains(SlipModel, restored.ModelText);
        Assert.Equal(1, result.KeptCommentLines);
        Assert.Equal(3, result.Segments);
    }

    [Fact]
    public void MismatchedSpeechBlockCount_LeavesTheAnswerAsItIs() {
        // The model merged the two speech blocks (forbidden) and made the slip: nothing can be matched safely.
        string answer = Input
            .Replace(SlipInput, SlipModel)
            .Replace("\\end{speech}\n\n\\begin{content}", "\\begin{content}")
            .Replace("\\end{content}\n\n\\begin{speech}[00:01:00 - 00:02:00]", "\\end{content}");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.False(result.Applied);
        Assert.Equal(NonSpeechGuard.SkipReason.FewerSpeechBlocks, result.Skip);
        Assert.Same(answer, result.Body);
        Assert.Equal(2, result.InputSpeechBlocks);
        Assert.Equal(1, result.AnswerSpeechBlocks);
        Assert.Contains("not applied (2 speech blocks in the input, 1 in the answer)", NonSpeechGuard.HeaderNote(result));
    }

    // ---- What the model may change ---------------------------------------------------

    [Fact]
    public void SpeechOnlyChanges_ReturnTheAnswerVerbatim() {
        string answer = Input.Replace(Speech1, Speech1Refined);

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.True(result.Applied);
        Assert.Equal(answer, result.Body);
        Assert.Empty(result.Restored);
        Assert.Equal(0, result.WhitespaceOnlySegments);
        Assert.Equal("Non-speech guard: all 3 segments outside speech match the input", NonSpeechGuard.HeaderNote(result));
    }

    [Fact]
    public void FlagAboveARetypedEnvironmentLine_StaysAboveTheRestoredLine() {
        string answer = Input.Replace("\\begin{content}[Diagram]", Flag + "\n\\begin{content}[\\textbf{Diagram}]");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.Equal(Input.Replace("\\begin{content}[Diagram]", Flag + "\n\\begin{content}[Diagram]"), result.Body);
        Assert.Contains("[\\textbf{Diagram}]", Assert.Single(result.Restored).ModelText);
    }

    [Fact]
    public void StudentQuoteSplitInTheMiddle_KeepsTheAddedSegment_AndStillRestoresSlips() {
        const string suspect1 = "% [TIMESTAMP-SUSPECT: estimated from the text — original: 00:01:00 - 00:02:00]";
        string split = N("""
            % [TIMESTAMP-SUSPECT: estimated from the text — original: 00:01:00 - 00:02:00]
            \begin{speech}[00:01:00 - 00:01:30]
            And this is the quotient.
            \end{speech}

            \begin{student}[Student Answer]
            The kernel.
            \end{student}

            % [TIMESTAMP-SUSPECT: estimated from the text — original: 00:01:00 - 00:02:00]
            \begin{speech}[00:01:30 - 00:02:00]
            Yes, the kernel.
            \end{speech}
            """);
        int start = Input.IndexOf("\\begin{speech}[00:01:00", StringComparison.Ordinal);
        int end = Input.IndexOf("\\end{speech}", start, StringComparison.Ordinal) + "\\end{speech}".Length;
        string answer = (Input[..start] + split + Input[end..]).Replace(SlipInput, SlipModel);

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.True(result.Applied);
        Assert.Equal(answer.Replace(SlipModel, SlipInput), result.Body);
        Assert.Equal(1, result.AddedSegments);
        Assert.Equal(1, result.KeptStudentBlocks);
        Assert.Equal(2, result.KeptCommentLines);
        Assert.Single(result.Restored);
        Assert.Equal(2, result.Body.Split(suspect1).Length - 1);
    }

    [Fact]
    public void StudentQuoteAtTheEndOfABlock_KeepsTheNewStudentBlock() {
        // The quote closes the block, so the split adds no speech block: the student block lands at the
        // start of the next segment, and the block count alone cannot tell.
        string input = Input.Replace("\\inlinemetanote{ein Student antwortet: \\qt{The kernel}} yes, the kernel.", "\\inlinemetanote{ein Student antwortet: \\qt{The kernel}}");
        string answer = input
            .Replace(" \\inlinemetanote{ein Student antwortet: \\qt{The kernel}}", "")
            .Replace("\\end{speech}\n\n\\begin{theorem}", "\\end{speech}\n\n\\begin{student}[Student Answer]\nThe kernel.\n\\end{student}\n\n\\begin{theorem}");

        var result = NonSpeechGuard.Apply(input, answer);

        Assert.True(result.Applied);
        Assert.Equal(answer, result.Body);
        Assert.Equal(1, result.KeptStudentBlocks);
        Assert.Empty(result.Restored);
    }

    [Fact]
    public void NewMultiParagraphStudentBlock_SurvivesADroppedBlankLineBeforeIt() {
        // The quote opened the next speech block, so its student block ends segment 1, and the block's
        // estimated start gets a TIMESTAMP-SUSPECT line. The model also dropped the blank line above the
        // student block; that input blank line must not pair with the blank line inside the new block,
        // or the block would no longer count as new and be lost.
        const string student = "\\begin{student}[Student Question]\nWhy $q$?\n\nAnd why not $f$?\n\\end{student}";
        const string suspect = "% [TIMESTAMP-SUSPECT: estimated from the text — original: 00:01:00 - 00:02:00]";
        string answer = Input
            .Replace(SlipInput, SlipModel)
            .Replace("\\end{content}\n\n\\begin{speech}[00:01:00 - 00:02:00]",
                "\\end{content}\n" + student + "\n\n" + suspect + "\n\\begin{speech}[00:01:00 - 00:02:00]");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.True(result.Applied);
        Assert.Equal(answer.Replace(SlipModel, SlipInput), result.Body);
        Assert.Equal(1, result.KeptStudentBlocks);
        Assert.Equal(1, result.KeptCommentLines);
        Assert.Contains(SlipInput, Assert.Single(result.Restored).InputText);
    }

    private static readonly string StudentInput = N("""
        \begin{speech}[00:00:00 - 00:01:00]
        First part. \inlinemetanote{ein Student fragt: \qt{Is it normal?}} Yes, it is.
        \end{speech}

        \begin{student}[Student Question]
        Can you, uh, repeat the kernel?
        \end{student}

        \begin{speech}[00:01:00 - 00:02:00]
        Second part. \inlinemetanote{ein Student antwortet: \qt{The image}} Right.
        \end{speech}

        \begin{theorem}[T]
        $x$.
        \end{theorem}
        """);

    private static string SplitBlock(string text, string start, string firstText, string quote, string secondText) {
        int from = text.IndexOf("\\begin{speech}[" + start, StringComparison.Ordinal);
        int to = text.IndexOf("\\end{speech}", from, StringComparison.Ordinal) + "\\end{speech}".Length;
        string split = $"\\begin{{speech}}[{start} - 00:00:30]\n{firstText}\n\\end{{speech}}\n\n"
            + $"\\begin{{student}}[Student Answer]\n{quote}\n\\end{{student}}\n\n"
            + $"\\begin{{speech}}[00:00:30 - 00:01:00]\n{secondText}\n\\end{{speech}}";
        return text[..from] + split + text[to..];
    }

    [Fact]
    public void SplitAfterAnEditedStudentBlock_PairsTheEditedBlockWithItsOriginal() {
        // Two answer segments hold only a student block: the edited original and the new quote. Both are
        // "empty" apart from student blocks, so only their likeness tells which one is the input's.
        string answer = SplitBlock(StudentInput, "00:01:00", "Second part.", "The image.", "Right.")
            .Replace("Can you, uh, repeat the kernel?", "Can you repeat the kernel?");

        var result = NonSpeechGuard.Apply(StudentInput, answer);

        Assert.True(result.Applied);
        Assert.Equal(answer.Replace("Can you repeat the kernel?", "Can you, uh, repeat the kernel?"), result.Body);
        Assert.Equal(1, result.AddedSegments);
        Assert.Equal(1, result.KeptStudentBlocks);
        Assert.Single(result.Restored);
    }

    [Fact]
    public void SplitBeforeAnEditedStudentBlock_PairsTheEditedBlockWithItsOriginal() {
        string answer = SplitBlock(StudentInput, "00:00:00", "First part.", "Is it normal?", "Yes, it is.")
            .Replace("Can you, uh, repeat the kernel?", "Can you repeat the kernel?");

        var result = NonSpeechGuard.Apply(StudentInput, answer);

        Assert.True(result.Applied);
        Assert.Equal(answer.Replace("Can you repeat the kernel?", "Can you, uh, repeat the kernel?"), result.Body);
        Assert.Equal(1, result.AddedSegments);
        Assert.Equal(1, result.KeptStudentBlocks);
        Assert.Single(result.Restored);
    }

    // ---- What the model may not change -----------------------------------------------

    [Fact]
    public void AddedLineWithACommentedStudentTag_IsNotAStudentBlock() {
        string answer = Input.Replace("\\end{tikzpicture}", "    \\node at (0, 0) {};  % \\begin{student} would go here\n\\end{tikzpicture}");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.Equal(Input, result.Body);
        Assert.Equal(0, result.KeptStudentBlocks);
        Assert.Contains("% \\begin{student}", Assert.Single(result.Restored).ModelText);
    }

    [Fact]
    public void RewrittenExistingStudentBlock_IsRestored_NotDuplicated() {
        string input = Input.Replace("\\begin{theorem}", "\\begin{student}[Student Question]\nIs the kernel normal?\n\\end{student}\n\n\\begin{theorem}");
        string answer = input.Replace("Is the kernel normal?", "Is the kernel always normal?");

        var result = NonSpeechGuard.Apply(input, answer);

        Assert.Equal(input, result.Body);
        Assert.Single(result.Restored);
        Assert.Equal(0, result.KeptStudentBlocks);
    }

    [Fact]
    public void DroppedLineOutsideSpeech_IsRestored() {
        string answer = Input.Replace("    \\draw[->, thick, figBlue] (V1) -- node[above] {$f$} (V2);\n", "");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.Equal(Input, result.Body);
        var restored = Assert.Single(result.Restored);
        Assert.Contains("(V1) -- node[above]", restored.InputText);
        Assert.Equal("", restored.ModelText);
    }

    [Fact]
    public void WhitespaceOnlyChange_IsRestored_AndCountedApart() {
        string answer = Input.Replace("\\end{speech}\n\n\\begin{content}", "\\end{speech}\n\\begin{content}");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.Equal(Input, result.Body);
        Assert.Empty(result.Restored);
        Assert.Equal(1, result.WhitespaceOnlySegments);
    }

    [Fact]
    public void SpeechTagInAFlagComment_DoesNotCountAsABlock() {
        const string flag = @"% [REFINER-FLAG: the theorem below should not start with \end{speech}]";
        string answer = Input.Replace("\\begin{theorem}", flag + "\n\\begin{theorem}");

        var result = NonSpeechGuard.Apply(Input, answer);

        Assert.True(result.Applied);
        Assert.Equal(answer, result.Body);
        Assert.Equal(1, result.KeptCommentLines);
    }

    [Fact]
    public void UnbalancedInput_LeavesTheAnswerAsItIs() {
        string input = Input.Replace("yes, the kernel.\n\\end{speech}", "yes, the kernel.\n\\end{speach}");
        string answer = Input.Replace(SlipInput, SlipModel);

        var result = NonSpeechGuard.Apply(input, answer);

        Assert.Equal(NonSpeechGuard.SkipReason.UnbalancedInput, result.Skip);
        Assert.Same(answer, result.Body);
        // The note lands in the .tex header: no literal speech tag a counter could pick up.
        string note = NonSpeechGuard.HeaderNote(result);
        Assert.StartsWith("Non-speech guard: not applied (speech begin/end tags do not alternate in the input, line ", note);
        Assert.DoesNotContain("{speech}", note);
    }

    [Fact]
    public void CrlfInput_IsComparedLikeLf() {
        string answer = Input.Replace(Speech1, Speech1Refined);

        var result = NonSpeechGuard.Apply(Input.Replace("\n", "\r\n"), answer);

        Assert.Equal(answer, result.Body);
        Assert.Empty(result.Restored);
        Assert.Equal(0, result.WhitespaceOnlySegments);
    }

    [Fact]
    public void HeaderNote_NamesTheRestoredLinesInTheInputFile() {
        var result = NonSpeechGuard.Apply(Input, Input.Replace(SlipInput, SlipModel));

        Assert.Equal($"Non-speech guard: restored 1 of 3 segments outside speech from the input (input line {LineOf(Input, SlipInput) + 40})",
            NonSpeechGuard.HeaderNote(result, lineOffset: 40));
    }
}
