using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Media;

/// <summary>
/// [AI Context] Helper class to parse date, weekday, and week number information from video filenames.
/// Supports flexible formats like "MM-DD-YYYY-monday-week1...", "week1-MM-DD-YYYY-montag...", and "YYYY-MM-DD-...".
/// </summary>
public static partial class VideoDateParser {
    public class VideoDateInfo {
        public DateTime Date { get; init; }
        public string? Weekday { get; init; }
        public string? WeekdayEnglish { get; init; }
        public string? WeekInfo { get; init; }
        public int? WeekNumber { get; init; }
        public string DateString { get; init; } = "";

        public bool IsValid => Date != DateTime.MinValue || !string.IsNullOrEmpty(Weekday) || !string.IsNullOrEmpty(WeekInfo);

        public string GetFormattedContext() {
            List<string> parts = [];
            if (!string.IsNullOrEmpty(Weekday)) {
                if (!string.IsNullOrEmpty(WeekdayEnglish) && !string.Equals(Weekday, WeekdayEnglish, StringComparison.OrdinalIgnoreCase)) {
                    parts.Add($"{WeekdayEnglish} ({Weekday})");
                }
                else {
                    parts.Add(Weekday);
                }
            }
            if (Date != DateTime.MinValue && !string.IsNullOrEmpty(DateString)) {
                parts.Add(DateString);
            }
            if (!string.IsNullOrEmpty(WeekInfo)) {
                parts.Add(WeekInfo);
            }
            if (parts.Count == 0) {
                return DateString;
            }
            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// [AI Context] Sorts video files chronologically by parsed date, then week number, then filename.
    /// Parses each filename once up front instead of repeatedly in OrderBy/ThenBy comparisons.
    /// [Human] Sortiert Videodateien chronologisch nach Datum, Woche und Dateiname.
    /// </summary>
    public static IEnumerable<string> OrderChronologically(this IEnumerable<string> filePaths) =>
        filePaths
            .Select(f => (Path: f, Info: Parse(f)))
            .OrderBy(x => x.Info.Date)
            .ThenBy(x => x.Info.WeekNumber ?? int.MaxValue)
            .ThenBy(x => x.Path)
            .Select(x => x.Path);

    // Matches YYYY-MM-DD anywhere with delimiters
    [GeneratedRegex(@"(?:^|[-_.\s])(\d{4})-(\d{2})-(\d{2})(?:[-_.\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex YyyyMmDdRegex();

    // Matches MM-DD-YYYY or MM-DD-YY anywhere with delimiters
    [GeneratedRegex(@"(?:^|[-_.\s])(\d{2})-(\d{2})-(\d{4}|\d{2})(?:[-_.\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MmDdYyyyRegex();

    // Matches MM-DD only anywhere with delimiters
    [GeneratedRegex(@"(?:^|[-_.\s])(\d{2})-(\d{2})(?:[-_.\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex MmDdOnlyRegex();

    // Matches weekday anywhere with delimiters
    [GeneratedRegex(@"(?:^|[-_.\s])(monday|tuesday|wednesday|thursday|friday|saturday|sunday|montag|dienstag|mittwoch|donnerstag|freitag|samstag|sonntag)(?:[-_.\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayRegex();

    // Matches week/woche followed by digits anywhere with delimiters
    [GeneratedRegex(@"(?:^|[-_.\s])(week|woche)[-_.\s]*(\d+)(?:[-_.\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WeekRegex();

    /// <summary>Whether <paramref name="name"/> contains a date Parse can read - without Parse's warning when it does not.</summary>
    public static bool HasDate(string name) =>
        YyyyMmDdRegex().IsMatch(name) || MmDdYyyyRegex().IsMatch(name) || MmDdOnlyRegex().IsMatch(name);

    /// <summary>
    /// The date to sort lectures by, or DateTime.MaxValue (sorted last) when the name has none. Checks
    /// with HasDate first, so sorting a folder list never prints Parse's "date format mismatch" warning.
    /// </summary>
    public static DateTime SortDate(string name) {
        if (!HasDate(name)) return DateTime.MaxValue;
        var date = Parse(name).Date;
        return date != DateTime.MinValue ? date : DateTime.MaxValue;
    }

    public static VideoDateInfo Parse(string filePath) {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(filePath);

        int year = 0, month = 0, day = 0;
        bool dateFound = false;

        Match matchYyyy = YyyyMmDdRegex().Match(fileNameWithoutExtension);
        if (matchYyyy.Success &&
            int.TryParse(matchYyyy.Groups[1].Value, out year) &&
            int.TryParse(matchYyyy.Groups[2].Value, out month) &&
            int.TryParse(matchYyyy.Groups[3].Value, out day)) {
            dateFound = true;
        }
        else {
            Match matchMmDdYyyy = MmDdYyyyRegex().Match(fileNameWithoutExtension);
            if (matchMmDdYyyy.Success &&
                int.TryParse(matchMmDdYyyy.Groups[1].Value, out month) &&
                int.TryParse(matchMmDdYyyy.Groups[2].Value, out day) &&
                int.TryParse(matchMmDdYyyy.Groups[3].Value, out int rawYear)) {
                year = rawYear < 100 ? 2000 + rawYear : rawYear;
                dateFound = true;
            }
            else {
                Match matchMmDd = MmDdOnlyRegex().Match(fileNameWithoutExtension);
                if (matchMmDd.Success &&
                    int.TryParse(matchMmDd.Groups[1].Value, out month) &&
                    int.TryParse(matchMmDd.Groups[2].Value, out day)) {
                    year = DateTime.Now.Year;
                    dateFound = true;
                }
            }
        }

        Match matchWeekday = WeekdayRegex().Match(fileNameWithoutExtension);
        string? rawWeekday = matchWeekday.Success ? matchWeekday.Groups[1].Value : null;
        TryParseWeekday(rawWeekday, out string? weekday, out string? weekdayEnglish);

        Match matchWeek = WeekRegex().Match(fileNameWithoutExtension);
        string? weekInfo = null;
        int? weekNumber = null;
        if (matchWeek.Success && int.TryParse(matchWeek.Groups[2].Value, out int parsedWeekNum)) {
            weekNumber = parsedWeekNum;
            string rawWord = matchWeek.Groups[1].Value.ToLowerInvariant();
            if (rawWord == "woche") {
                weekInfo = $"Week {parsedWeekNum} (Woche {parsedWeekNum})";
            }
            else {
                weekInfo = $"Week {parsedWeekNum}";
            }
        }

        if (!dateFound) {
            Ui.Warn($"Date format mismatch or missing date in file '{fileNameWithoutExtension}'.");
            return new() {
                Date = DateTime.MinValue,
                Weekday = weekday,
                WeekdayEnglish = weekdayEnglish,
                WeekInfo = weekInfo,
                WeekNumber = weekNumber,
                DateString = fileNameWithoutExtension
            };
        }

        DateTime parsedDate;
        try {
            parsedDate = new DateTime(year, month, day);
        }
        catch (ArgumentOutOfRangeException ex) {
            Ui.Warn($"Invalid date values ({year:D4}-{month:D2}-{day:D2}) in filename '{fileNameWithoutExtension}'. {ex.Describe()}");
            return new() {
                Date = DateTime.MinValue,
                Weekday = weekday,
                WeekdayEnglish = weekdayEnglish,
                WeekInfo = weekInfo,
                WeekNumber = weekNumber,
                DateString = fileNameWithoutExtension
            };
        }

        string parsedDateStringForInfo = $"{year:D4}-{month:D2}-{day:D2}";

        return new() {
            Date = parsedDate,
            Weekday = weekday,
            WeekdayEnglish = weekdayEnglish,
            WeekInfo = weekInfo,
            WeekNumber = weekNumber,
            DateString = parsedDateStringForInfo
        };
    }

    private static bool TryParseWeekday(string? token, out string? weekday, out string? weekdayEnglish) {
        if (string.IsNullOrWhiteSpace(token)) {
            weekday = null;
            weekdayEnglish = null;
            return false;
        }

        string lower = token.ToLowerInvariant();
        switch (lower) {
            case "monday":
                weekday = "Monday";
                weekdayEnglish = "Monday";
                return true;
            case "montag":
                weekday = "Montag";
                weekdayEnglish = "Monday";
                return true;
            case "tuesday":
                weekday = "Tuesday";
                weekdayEnglish = "Tuesday";
                return true;
            case "dienstag":
                weekday = "Dienstag";
                weekdayEnglish = "Tuesday";
                return true;
            case "wednesday":
                weekday = "Wednesday";
                weekdayEnglish = "Wednesday";
                return true;
            case "mittwoch":
                weekday = "Mittwoch";
                weekdayEnglish = "Wednesday";
                return true;
            case "thursday":
                weekday = "Thursday";
                weekdayEnglish = "Thursday";
                return true;
            case "donnerstag":
                weekday = "Donnerstag";
                weekdayEnglish = "Thursday";
                return true;
            case "friday":
                weekday = "Friday";
                weekdayEnglish = "Friday";
                return true;
            case "freitag":
                weekday = "Freitag";
                weekdayEnglish = "Friday";
                return true;
            case "saturday":
                weekday = "Saturday";
                weekdayEnglish = "Saturday";
                return true;
            case "samstag":
                weekday = "Samstag";
                weekdayEnglish = "Saturday";
                return true;
            case "sunday":
                weekday = "Sunday";
                weekdayEnglish = "Sunday";
                return true;
            case "sonntag":
                weekday = "Sonntag";
                weekdayEnglish = "Sunday";
                return true;
            default:
                weekday = null;
                weekdayEnglish = null;
                return false;
        }
    }
}