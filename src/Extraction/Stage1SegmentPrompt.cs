using System;
using System.IO;
using LectureExtraction.Media;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Shared per-segment Stage 1 prompt parameter builder used by both AI Studio and Vertex
/// extraction sessions. Single-pass uploads (<paramref name="totalParts"/> == 1) explicitly tell Gemini
/// that the lecture consists of only one part (complete lecture, no Part 2, no merge step, and no
/// trailing Final Video Cut-off &amp; Board State Checkpoint), while multi-part runs preserve the existing
/// segment/overlap instructions.
/// [Human] Gemeinsamer Builder für den Stage-1-Prompt (inkl. ausdrücklichem Hinweis bei einteiligen Vorlesungen).
/// </summary>
public static class Stage1SegmentPrompt {
    public static string BuildParameters(
        string segmentFilePath,
        int partNumber,
        int totalParts,
        string originalFileName,
        double segmentDurationSeconds,
        double fullOriginalVideoDurationSeconds) {
        var dateInfo = VideoDateParser.Parse(originalFileName);
        string dateContext = dateInfo.GetFormattedContext();
        string durationString = ExtractionHelpers.FormatDurationForPrompt(segmentDurationSeconds);
        string fullDurationString = ExtractionHelpers.FormatDurationForPrompt(fullOriginalVideoDurationSeconds);
        string segmentName = Path.GetFileName(segmentFilePath);
        string dateMetadata = LectureMetadataText(dateInfo, dateContext, partNumber, totalParts);

        if (totalParts == 1) {
            return
                $"<parameter name=\"lecture_metadata\">{dateMetadata}</parameter>\n" +
                $"<parameter name=\"source_video\">You must transcribe the complete single-part lecture video attachment named `{segmentName}` verbatim according to the system instructions. Ensure you transcribe every single spoken word up to the very last second of the video.</parameter>\n" +
                $"<parameter name=\"segment_info\">SINGLE-PART LECTURE (only 1 part total — complete lecture): Ignore any earlier generic phrasing about video fragments or other parts; this single video IS the entire {durationString} lecture from start to finish, and there is no Part 2 and no merging phase afterwards. Transcribe the whole lecture cleanly and completely in one pass (including any closing remarks or wrap-up at the end). This video is exactly {durationString} long; that is the value for `% End of the video`. Since this lecture consists of only one part and no following video segment exists, do NOT emit a trailing `\\begin{{aiNote}}[Final Video Cut-off \\& Board State Checkpoint]` at the end of the document.</parameter>\n" +
                $"<parameter name=\"duration_and_timestamps\">The lecture starts at 00:00:00 and runs for {durationString}. Ensure the timestamps in every 'speech' block reflect the actual time in the video and the final timestamp in your very last 'speech' block matches the video length ({durationString}).</parameter>\n" +
                "</context_and_parameters>";
        }

        return
            $"<parameter name=\"lecture_metadata\">{dateMetadata}</parameter>\n" +
            $"<parameter name=\"source_video\">You must transcribe the video attachment named `{segmentName}` verbatim according to the system instructions. Ensure you transcribe every single spoken word up to the very last second of the video, even if it cuts off mid-sentence.</parameter>\n" +
            $"<parameter name=\"segment_info\">You are currently transcribing Part {partNumber} of {totalParts} from this lecture. This specific video segment is exactly {durationString} long; that is the value for `% End of the video`. For context only, the whole lecture, of which this segment is one part, runs {fullDurationString}; never use that number for a timestamp.</parameter>\n" +
            $"<parameter name=\"duration_and_timestamps\">Do NOT calculate any time offset for the 'speech' environment. Start at 00:00:00 and ensure the final timestamp in your very last 'speech' block perfectly matches the segment length ({durationString}).</parameter>\n" +
            "</context_and_parameters>";
    }

    /// <summary>
    /// [AI Context] Formats the <c>lecture_metadata</c> parameter (date, weekday, week number).
    /// Passing <paramref name="totalParts"/> == 1 notifies the model that the upload is a complete
    /// single-part lecture; default <c>0</c> keeps the multi-part Part 1 / Part N phrasing unchanged.
    /// [Human] Datums- und Teil-Metadaten für den Prompt; kennzeichnet einteilige Vorlesungen ausdrücklich.
    /// </summary>
    public static string LectureMetadataText(VideoDateParser.VideoDateInfo dateInfo, string dateContext, int partNumber, int totalParts = 0) {
        string? weekday = dateInfo.WeekdayEnglish ?? dateInfo.Weekday;
        string weekdayNote = "";
        if (weekday == null) {
            if (dateInfo.Date != DateTime.MinValue) {
                weekday = dateInfo.Date.DayOfWeek.ToString();
                weekdayNote = $" The file name does not state the day of the week; {weekday} is derived from the date.";
            }
            else {
                weekday = "Unknown";
                weekdayNote = " The file name states neither the day of the week nor a date to derive it from, so the day of the week is unknown. Do not guess it: where the system instructions ask for it, say that it is unknown.";
            }
        }

        string weekInfo = dateInfo.WeekInfo ?? "N/A";
        if (totalParts == 1) {
            return $"The lecture being transcribed is from {dateContext}. This lecture consists of ONLY ONE part (a complete single-part upload covering the whole lecture; there are no other parts and no later merge step). Please note that the exact date, day of the week ({weekday}), and week number ({weekInfo}) are important metadata for the lecture header.{weekdayNote}";
        }

        return partNumber == 1
            ? $"The lecture being transcribed is from {dateContext}. Please note that the exact date, day of the week ({weekday}), and week number ({weekInfo}) are important metadata since this is part 1 of the lecture.{weekdayNote}"
            : $"The lecture took place on {dateContext} (Day of the week: {weekday}).{weekdayNote}";
    }
}
