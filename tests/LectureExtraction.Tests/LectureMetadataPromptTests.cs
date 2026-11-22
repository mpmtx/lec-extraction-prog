using System;
using LectureExtraction.Extraction;
using LectureExtraction.Media;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The lecture_metadata prompt parameter. A file name without a weekday used to reach the model as
/// "day of the week (Unknown)" with no reason given, which invites a guess.
/// </summary>
public class LectureMetadataPromptTests {
    // Built directly rather than through VideoDateParser.Parse, which warns on the console for a
    // name without a date - and this class is not in the console collection.
    private static VideoDateParser.VideoDateInfo Info(DateTime date, string? weekday) =>
        new() { Date = date, Weekday = weekday, WeekdayEnglish = weekday, WeekInfo = "Week 1" };

    [Fact]
    public void A_stated_weekday_keeps_the_text_unchanged() {
        string text = AutoExtractionSession.LectureMetadataText(Info(new DateTime(2026, 2, 16), "Monday"), "Monday, 02/16/2026, Week 1", 1);

        Assert.Equal("The lecture being transcribed is from Monday, 02/16/2026, Week 1. Please note that the exact date, day of the week (Monday), and week number (Week 1) are important metadata since this is part 1 of the lecture.", text);
    }

    [Fact]
    public void A_missing_weekday_is_derived_from_the_date_and_the_model_is_told_so() {
        string text = AutoExtractionSession.LectureMetadataText(Info(new DateTime(2026, 2, 18), null), "02/18/2026, Week 1", 2);

        Assert.Equal("The lecture took place on 02/18/2026, Week 1 (Day of the week: Wednesday). The file name does not state the day of the week; Wednesday is derived from the date.", text);
    }

    [Fact]
    public void Without_weekday_and_date_the_model_is_told_not_to_guess() {
        string text = AutoExtractionSession.LectureMetadataText(Info(DateTime.MinValue, null), "lecture", 1);

        Assert.Contains("day of the week (Unknown)", text);
        Assert.Contains("Do not guess it", text);
    }

    [Fact]
    public void Single_part_metadata_and_segment_prompt_state_the_lecture_has_only_one_part() {
        string meta = Stage1SegmentPrompt.LectureMetadataText(Info(new DateTime(2026, 10, 28), "Wednesday"), "Wednesday, 10/28/2026, Week 7", 1, totalParts: 1);
        Assert.Contains("ONLY ONE part", meta);
        Assert.Contains("no later merge step", meta);

        string singlePrompt = Stage1SegmentPrompt.BuildParameters(
            @"D:\tmp\10-28-2026-wednesday-week7-Algebra_I.mp4",
            partNumber: 1,
            totalParts: 1,
            originalFileName: "10-28-2026-wednesday-week7-Algebra_I.mp4",
            segmentDurationSeconds: 2634,
            fullOriginalVideoDurationSeconds: 2634);

        Assert.Contains("`10-28-2026-wednesday-week7-Algebra_I.mp4`", singlePrompt);
        Assert.Contains("SINGLE-PART LECTURE (only 1 part total", singlePrompt);
        Assert.Contains("there is no Part 2 and no merging phase afterwards", singlePrompt);
        Assert.Contains("do NOT emit a trailing", singlePrompt);
        Assert.DoesNotContain("even if it cuts off mid-sentence", singlePrompt);

        string multiPrompt = Stage1SegmentPrompt.BuildParameters(
            @"D:\tmp\10-27-2026-tuesday-week7-Algebra_I-part1.mp4",
            partNumber: 1,
            totalParts: 2,
            originalFileName: "10-27-2026-tuesday-week7-Algebra_I.mp4",
            segmentDurationSeconds: 2769,
            fullOriginalVideoDurationSeconds: 5298);

        Assert.Contains("Part 1 of 2", multiPrompt);
        Assert.Contains("even if it cuts off mid-sentence", multiPrompt);
        Assert.DoesNotContain("SINGLE-PART LECTURE", multiPrompt);
    }
}
