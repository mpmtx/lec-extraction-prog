using LectureExtraction.Configuration;
using LectureExtraction.Extraction;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the per-fragment prompt construction that moved out of
/// <c>AutoExtractionSession</c>'s YouTube mode when
/// <see cref="YouTubeTaskRunner"/> was extracted (Phase 11).
///
/// <para>This text goes straight into a paid request and shapes what the model transcribes, but it
/// was previously buried mid-method in a 90-line loop and unreachable from a test. Pulling the
/// runner out made it a pure function; these tests pin it.</para>
/// </summary>
public class YouTubeTaskRunnerTests {
    private static YouTubeTimestampFragment Fragment(string start = "00:00", string end = "10:00", string title = "Intro") =>
        new() { StartTime = start, EndTime = end, PartTitle = title };

    [Fact]
    public void FragmentPrompt_CarriesTheFragmentBoundsAndTitle() {
        string prompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment("05:00", "15:30", "Beweis"), partNum: 2);

        Assert.Contains("05:00", prompt);
        Assert.Contains("15:30", prompt);
        Assert.Contains("Beweis", prompt);
        // The model must transcribe only the requested window, not the whole video.
        Assert.Contains("ONLY", prompt);
    }

    [Fact]
    public void FirstPart_DatesTheChapterOnlyFromTheVideo() {
        string prompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 1);

        // A YouTube task carries no date. The old text called the date important without giving one,
        // which invites a guess; Stage 1 omits [Date] from \youtubechapter when it is unknown.
        Assert.Contains("never guess", prompt);
        Assert.DoesNotContain("date of the transcription is important", prompt);
    }

    [Fact]
    public void LaterParts_RepeatNeitherTheChapterNorTheDate() {
        string prompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 3);

        // A Part 2 or later opens no chapter in Stage 1, so there is no place for the date; the old
        // "(but tell it anyway)" asked for one anyway.
        Assert.Contains("do not repeat the `\\youtubechapter` heading or the date", prompt);
        Assert.DoesNotContain("tell it anyway", prompt);
    }

    [Fact]
    public void PartNumber_IsStatedSoTheModelKnowsWhereItIs() {
        Assert.Contains("This is part 4", YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 4));
    }

    [Fact]
    public void SinglePart_IsToldToOmitTrailingBoardStateCheckpoint() {
        string singlePrompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 1, totalParts: 1);
        string multiPrompt = YouTubeTaskRunner.BuildFragmentPrompt(Fragment(), partNum: 1, totalParts: 2);

        Assert.Contains("complete single-part video (Part 1 of 1)", singlePrompt);
        Assert.DoesNotContain("complete single-part video (Part 1 of 1)", multiPrompt);
    }
}
