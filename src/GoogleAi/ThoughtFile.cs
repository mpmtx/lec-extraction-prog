using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.GoogleAi;

/// <summary>What a thoughts file says about the stage that wrote it.</summary>
/// <param name="StageKey">Stable short name: <c>extract-part1</c>, <c>merge</c>, <c>speech</c>, <c>final</c>, <c>pdf-repair-1</c>.</param>
/// <param name="Stage">The stage's long label, as in the .tex header.</param>
/// <param name="Output">File name of the output the thoughts belong to.</param>
public sealed record ThoughtFileHeader(string StageKey, string Stage, string Output, string Model, DateTimeOffset Started, int Version = ThoughtFile.FormatVersion);

/// <summary>One request's thought summary and the usage the server reported for it.</summary>
/// <param name="Attempt">1 normally; 2, 3... when an empty answer made the stage send the same request again.</param>
public sealed record ThoughtRequestRecord(int Request, DateTimeOffset Time, int ThoughtTokens, int OutputTokens, string? FinishReason, string Text, int Attempt = 1);

/// <summary>A parsed thoughts file.</summary>
public sealed record ThoughtFileContent(string Path, ThoughtFileHeader Header, IReadOnlyList<ThoughtRequestRecord> Requests);

/// <summary>
/// [AI Context] The on-disk format of <c>&lt;output stem&gt;-thoughts.md</c>: Markdown for a human, with
/// one machine-readable HTML-comment line per file and per request, so <c>lecx thoughts</c> (and any
/// other reader) can parse it without guessing at headings the model may also write:
/// <code>
/// &lt;!-- lecx-thoughts {"stageKey":"speech",...} --&gt;
/// # Thought summaries: step3-x-offset-speech_refined.tex
/// ...
/// &lt;!-- lecx-thoughts-request {"request":1,"thoughtTokens":12345,...} --&gt;
/// ## Request 1 · 12,345 thinking tokens · 30,000 output tokens · MAX_TOKENS
///
/// (summary text)
/// </code>
/// A request's text runs from its heading to the next request marker. One file holds one run.
/// [Human] Dateiformat der Gedanken-Dateien: Markdown plus je eine maschinenlesbare Kommentarzeile.
/// </summary>
public static partial class ThoughtFile {
    public const int FormatVersion = 1;
    public const string FileMarker = "lecx-thoughts";
    public const string RequestMarker = "lecx-thoughts-request";
    public const string FileSuffix = "-thoughts";

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// <c>&lt;stem&gt;-thoughts.md</c> beside <paramref name="outputPath"/>, or <c>-thoughts-2.md</c>,
    /// <c>-thoughts-3.md</c>... when an earlier run's file is already there.
    /// </summary>
    public static string ResolveNewPath(string outputPath) {
        string folder = System.IO.Path.GetDirectoryName(outputPath) ?? ".";
        string stem = System.IO.Path.GetFileNameWithoutExtension(outputPath);
        string candidate = System.IO.Path.Combine(folder, $"{stem}{FileSuffix}.md");
        for (int index = 2; File.Exists(candidate); index++) {
            candidate = System.IO.Path.Combine(folder, $"{stem}{FileSuffix}-{index}.md");
        }
        return candidate;
    }

    public static string FormatHeader(ThoughtFileHeader header) =>
        $"<!-- {FileMarker} {JsonSerializer.Serialize(header, JsonOptions)} -->\n" +
        $"# Thought summaries: {header.Output}\n\n" +
        $"Stage: {header.Stage} (`{header.StageKey}`) · Model: {header.Model} · Started: {header.Started:yyyy-MM-dd HH:mm:ss}\n\n" +
        "Gemini's condensed summary of its reasoning, one section per request. The raw thinking is not returned by the API.\n\n";

    public static string FormatRequest(ThoughtRequestRecord record) {
        // The text is the section below the marker, not part of it.
        string json = JsonSerializer.Serialize(new {
            record.Request, record.Attempt, record.Time, record.ThoughtTokens, record.OutputTokens, record.FinishReason
        }, JsonOptions);
        return $"<!-- {RequestMarker} {json} -->\n" +
               $"## {RequestHeading(record)}\n\n" +
               $"{SummaryText(record)}\n\n";
    }

    /// <summary>
    /// The block appended to a stage's existing log: the stage line plus the request, without the
    /// machine markers, since that log is not a thoughts file and nothing parses it as one.
    /// </summary>
    public static string FormatForExistingLog(ThoughtFileHeader header, ThoughtRequestRecord record) =>
        $"\n\n---\n\n### Thought summary: {header.Stage}, {RequestHeading(record)}\n\n{SummaryText(record)}\n";

    private static string SummaryText(ThoughtRequestRecord record) =>
        string.IsNullOrWhiteSpace(record.Text) ? NoSummary : record.Text.Trim();

    private const string NoSummary = "(no summary returned)";

    /// <summary>The human heading of a request, e.g. <c>Request 1 · attempt 2 · 12,345 thinking tokens · ...</c>.</summary>
    public static string RequestHeading(ThoughtRequestRecord record) {
        var heading = new StringBuilder($"Request {record.Request}");
        if (record.Attempt > 1) {
            heading.Append($" · attempt {record.Attempt}");
        }
        heading.Append($" · {record.ThoughtTokens.ToString("N0", CultureInfo.InvariantCulture)} thinking tokens");
        heading.Append($" · {record.OutputTokens.ToString("N0", CultureInfo.InvariantCulture)} output tokens");
        if (!string.IsNullOrWhiteSpace(record.FinishReason)) {
            heading.Append($" · {record.FinishReason}");
        }
        return heading.ToString();
    }

    /// <summary>True when the file starts with the thoughts marker - a cheap check for directory scans.</summary>
    public static bool IsThoughtFile(string path) {
        try {
            using var reader = new StreamReader(path);
            string? firstLine = reader.ReadLine();
            return firstLine != null && firstLine.StartsWith($"<!-- {FileMarker} ", StringComparison.Ordinal);
        }
        catch (IOException) {
            return false;
        }
        catch (UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Reads a thoughts file; null when it is not one or its header cannot be parsed.</summary>
    public static ThoughtFileContent? Read(string path) {
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        return Parse(path, text);
    }

    /// <summary>
    /// <see cref="Read"/> for directory scans: null instead of an exception when the file is not a
    /// thoughts file or cannot be read right now (a running stage appending to it, no access).
    /// </summary>
    public static ThoughtFileContent? TryRead(string path) {
        if (!IsThoughtFile(path)) {
            return null;
        }
        try {
            return Read(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Ui.Detail($"Gedankendatei kann nicht gelesen werden ({path}): {ex.Describe()}");
            return null;
        }
    }

    public static ThoughtFileContent? Parse(string path, string text) {
        var headerMatch = HeaderRegex().Match(text);
        if (!headerMatch.Success) {
            return null;
        }

        ThoughtFileHeader? header;
        try {
            header = JsonSerializer.Deserialize<ThoughtFileHeader>(headerMatch.Groups["json"].Value, JsonOptions);
        }
        catch (JsonException ex) {
            Ui.Detail($"Gedankendatei-Header enthält ungültiges JSON ({path}): {ex.Describe()}");
            return null;
        }
        if (header == null) {
            return null;
        }

        var requests = new List<ThoughtRequestRecord>();
        var matches = RequestRegex().Matches(text);
        for (int i = 0; i < matches.Count; i++) {
            var match = matches[i];
            int bodyStart = match.Index + match.Length;
            int bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            string body = text[bodyStart..bodyEnd].TrimStart('\n');

            // The first line of the body is the human heading this writer put under the marker.
            if (body.StartsWith("## Request ", StringComparison.Ordinal)) {
                int lineEnd = body.IndexOf('\n');
                body = lineEnd >= 0 ? body[(lineEnd + 1)..] : "";
            }
            body = body.Trim();
            if (body == NoSummary) {
                body = "";
            }

            ThoughtRequestRecord? metadata;
            try {
                metadata = JsonSerializer.Deserialize<ThoughtRequestRecord>(match.Groups["json"].Value, JsonOptions);
            }
            catch (JsonException ex) {
                Ui.Warn($"Defekter Request-Eintrag in '{Path.GetFileName(path)}' übersprungen: {ex.Describe()}", "Thoughts");
                continue;
            }
            if (metadata != null) {
                requests.Add(metadata with { Text = body });
            }
        }

        return new ThoughtFileContent(path, header, requests);
    }

    [GeneratedRegex(@"^<!-- lecx-thoughts (?<json>\{.*\}) -->$", RegexOptions.Multiline)]
    private static partial Regex HeaderRegex();

    [GeneratedRegex(@"^<!-- lecx-thoughts-request (?<json>\{.*\}) -->$", RegexOptions.Multiline)]
    private static partial Regex RequestRegex();
}
