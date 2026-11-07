using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using LectureExtraction.Configuration;
using LectureExtraction.Extraction.Model;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Builds and reads the metadata headers written into every .tex file the pipeline
/// produces. Shared verbatim between the AI Studio and Vertex extraction sessions and the refinement.
///
/// <para>A header is a stack of <i>layers</i>, newest on top: each pipeline step puts one
/// rule-delimited comment block (<see cref="LayerRule"/>) above the layers of the file it read, so the
/// final document carries the configuration, date and token usage of every step that produced it -
/// like the rings of an onion. The first line of every layer names its step, numbered after the file
/// prefixes (a step2-*.tex file is written by "Step 2/4"). The C# stacks the layers, not the model:
/// whatever the model echoes back at the top of its answer is discarded.</para>
///
/// <para>Inside a combined document every part keeps its own layer directly under its
/// "% --- TEIL n" separator, and the refinement restores those blocks under every separator that
/// survives a step, so each part's configuration stays visible where that part's text is.</para>
/// [Human] Baut und liest die Kommentar-Header der .tex-Dateien: pro Pipeline-Schritt ein Block,
/// der neueste oben, darunter die Blöcke aller vorherigen Schritte.
/// </summary>
public static partial class TexDocumentWriter {
    /// <summary>Opens and closes every header layer.</summary>
    public const string LayerRule = "% ==========================================";

    private const string Divider = "% ------------------------------------------";

    // The step names that open each layer. Numbered after the file prefixes, so the label and the
    // file name agree: step2-<name>-offset-merged.tex is written by "Step 2/4".
    public const string TranscriptionStep = "Step 1/4 - Transcription";
    public const string MergeStep = "Step 2/4 - Merge & timestamp alignment (refinement step 1)";
    public const string SpeechStep = "Step 3/4 - Speech refinement (refinement step 2)";
    public const string FinalStep = "Step 4/4 - Final refinement (refinement step 3)";
    public const string PdfRepairStep = "PDF repair after step 4/4";

    /// <summary>
    /// [AI Context] The layer of one transcribed part: model parameters, the part's start offset in
    /// the lecture, and its token usage.
    /// [Human] Header-Block einer einzelnen .tex-Teildatei.
    /// </summary>
    /// <param name="thoughtsFileName">The part's thoughts file, when thought summaries were written; a
    /// reader of the .tex finds them from here.</param>
    public static string BuildPartHeader(int partNumber, int totalParts, string sourcePartFileName, double partStartTimeSeconds,
        TokenUsage usage, string model, GenerationParameters generation, string? thoughtsFileName = null) {
        var lines = new List<string> {
            $"{TranscriptionStep}, part {partNumber} of {totalParts}",
            $"Source part: {sourcePartFileName}"
        };
        lines.AddRange(ModelLines(model, generation));
        lines.Add($"Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.Add($"PART_START_SECONDS: {partStartTimeSeconds.ToString("F2", CultureInfo.InvariantCulture)}");
        AddThoughtsLine(lines, thoughtsFileName);
        return Layer(lines, TokenLines(usage, "Token Usage"));
    }

    private const string ThoughtsLinePrefix = "Thought summaries: ";

    private static void AddThoughtsLine(List<string> lines, string? thoughtsFileName) {
        if (!string.IsNullOrEmpty(thoughtsFileName)) {
            lines.Add(ThoughtsLinePrefix + thoughtsFileName);
        }
    }

    /// <summary>
    /// [AI Context] The thoughts file named in the top header layer of <paramref name="tex"/>, or null.
    /// Only the top layer is this file's own; the layers below were inherited from its inputs and
    /// name their stages' thoughts files, not this one's.
    /// [Human] Name der Gedanken-Datei aus dem obersten Header-Block.
    /// </summary>
    public static string? ThoughtsFileNameOf(string tex) {
        string[] lines = SplitHeaderLayers(tex).Layers.Split('\n');
        for (int index = 1; index < lines.Length && lines[index].TrimEnd() != LayerRule; index++) {
            string line = lines[index].StartsWith("% ", StringComparison.Ordinal) ? lines[index][2..] : lines[index];
            if (line.StartsWith(ThoughtsLinePrefix, StringComparison.Ordinal)) {
                return line[ThoughtsLinePrefix.Length..].Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// [AI Context] The layer of the combined (-all) document: model parameters, one summary line per
    /// part, and the token usage of this run. The part lines are what keeps each part's facts in the
    /// stack once a later step has merged the parts and dropped their separators.
    /// [Human] Header-Block der zusammengesetzten (-all) .tex-Datei.
    /// </summary>
    /// <param name="partSummaries">One line per part, from <see cref="SummarizePartHeader"/>.</param>
    public static string BuildCombinedHeader(string outputFileName, string sourceFileName, int totalParts, TokenUsage runUsage,
        string model, GenerationParameters generation, IReadOnlyList<string> partSummaries) {
        var lines = new List<string> {
            $"{TranscriptionStep}, {totalParts} part(s) combined",
            $"Output: {outputFileName}",
            $"Source video: {sourceFileName}"
        };
        lines.AddRange(ModelLines(model, generation));
        lines.Add($"Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var sections = new List<IReadOnlyList<string>>();
        if (partSummaries.Count > 0) {
            sections.Add(["Parts:", .. partSummaries.Select((summary, index) => $"  - Part {index + 1}: {summary}")]);
        }
        // Parts reused from an earlier run cost nothing now; their own usage is in their line above.
        sections.Add(TokenLines(runUsage, "Token Usage of this run"));
        return Layer(lines, [.. sections]);
    }

    /// <summary>
    /// [AI Context] The layer of one refinement step's output.
    /// [Human] Header-Block eines Refinement-Schritts.
    /// </summary>
    /// <param name="thoughtsFileName">The step's thoughts file, when thought summaries were written.</param>
    /// <param name="notes">Further lines about how the output was made (step 2: what the non-speech guard did).</param>
    public static string BuildRefinementHeader(string stepLabel, string outputFileName, string? inputFileName,
        TokenUsage usage, string model, GenerationParameters generation, string? thoughtsFileName = null,
        IReadOnlyList<string>? notes = null) {
        var lines = new List<string> { stepLabel, $"Output: {outputFileName}" };
        if (!string.IsNullOrEmpty(inputFileName)) {
            lines.Add($"Input: {inputFileName}");
        }
        lines.AddRange(ModelLines(model, generation));
        lines.Add($"Processed on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        AddThoughtsLine(lines, thoughtsFileName);
        if (notes != null) lines.AddRange(notes);
        return Layer(lines, TokenLines(usage, "Token Usage"));
    }

    /// <summary>
    /// [AI Context] The separator that opens one part inside a combined document, followed by that
    /// part's own header layer.
    /// [Human] Trennkommentar eines Teils im Gesamtdokument, mit dessen eigenem Header-Block.
    /// </summary>
    public static string BuildPartSeparator(int partNumber, string partHeader, bool fromCache) =>
        $"\n\n% --- TEIL {partNumber}{(fromCache ? " (Aus Cache geladen)" : "")} ---\n{partHeader}";

    /// <summary>
    /// [AI Context] One line describing a part, read back out of its header layer - which works the
    /// same for a part transcribed just now and for one reused from disk, whose numbers exist only
    /// in its file. Missing fields (older header formats) are left out.
    /// [Human] Eine Zusammenfassungszeile pro Teil, aus dessen Header gelesen.
    /// </summary>
    public static string SummarizePartHeader(string partHeader) {
        string? Value(params string[] keys) {
            foreach (string line in partHeader.Split('\n')) {
                // "%   - Total Prompt Tokens : 330'036 (note)" -> key, padded colon, value, note.
                string text = line.TrimEnd('\r').TrimStart('%', ' ', '-');
                foreach (string key in keys) {
                    if (!text.StartsWith(key, StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = text[key.Length..].TrimStart();
                    if (!rest.StartsWith(':')) continue;
                    string value = rest[1..].Trim();
                    int note = value.IndexOf(" (", StringComparison.Ordinal);
                    return note > 0 ? value[..note] : value;
                }
            }
            return null;
        }

        string? start = Value("PART_START_SECONDS") is string seconds
            && double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out double startSeconds)
            ? TimeSpan.FromSeconds(startSeconds).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)
            : null;

        var fields = new List<string?> {
            Value("Source part", "AutoExtraction Source Part"),
            start != null ? $"start {start}" : null,
            Value("Model") is string model ? $"model {model}" : null,
            Value("Processed on") is string processed ? $"processed {processed}" : null,
            Value("Total Prompt Tokens") is string prompt ? $"prompt {prompt}" : null,
            Value("Output Tokens", "Generated Output") is string output ? $"output {output}" : null,
            Value("Thinking Tokens") is string thinking ? $"thinking {thinking}" : null
        };
        return string.Join(" | ", fields.Where(field => field != null));
    }

    /// <summary>
    /// [AI Context] Splits the header layers at the top of a .tex file from the body below them. A
    /// layer is a comment block opened and closed by <see cref="LayerRule"/>; blank lines between
    /// layers are allowed. Anything else - a "% Primary Language" line, a "% --- TEIL" separator -
    /// ends the header, so per-part blocks inside the body are never mistaken for layers.
    /// [Human] Trennt die Header-Blöcke am Dateianfang vom eigentlichen Inhalt.
    /// </summary>
    public static (string Layers, string Body) SplitHeaderLayers(string tex) {
        string[] lines = tex.Replace("\r\n", "\n").Split('\n');
        var layers = new StringBuilder();
        int index = 0;

        while (true) {
            int start = index;
            while (start < lines.Length && string.IsNullOrWhiteSpace(lines[start])) start++;
            if (start >= lines.Length || lines[start].TrimEnd() != LayerRule) break;

            int end = start + 1;
            while (end < lines.Length && lines[end].StartsWith('%') && lines[end].TrimEnd() != LayerRule) end++;
            if (end >= lines.Length || lines[end].TrimEnd() != LayerRule) break; // unterminated: body, not a layer

            for (int line = start; line <= end; line++) {
                layers.Append(lines[line]).Append('\n');
            }
            index = end + 1;
        }

        string body = string.Join("\n", lines[index..]).TrimStart('\n', '\r', ' ', '\t');
        return (layers.ToString(), body);
    }

    /// <summary>
    /// [AI Context] Puts a new layer on top of the layers a step inherited from its input, then the body.
    /// [Human] Legt den neuen Header-Block über die geerbten Blöcke.
    /// </summary>
    public static string StackLayers(string newLayer, string inheritedLayers, string body) =>
        newLayer + inheritedLayers + "\n" + body;

    /// <summary>
    /// [AI Context] Each part's header layer, keyed by part number, as found directly under the
    /// "% --- TEIL n" separators of a document.
    /// [Human] Liest die Header-Blöcke unter den TEIL-Trennern eines Dokuments.
    /// </summary>
    public static IReadOnlyDictionary<int, string> ExtractPartHeaders(string tex) {
        string[] lines = tex.Replace("\r\n", "\n").Split('\n');
        var headers = new Dictionary<int, string>();

        for (int index = 0; index < lines.Length; index++) {
            if (PartNumber(lines[index]) is not int part || headers.ContainsKey(part)) continue;
            if (ReadLayerAt(lines, index + 1) is (string layer, _)) {
                headers[part] = layer;
            }
        }
        return headers;
    }

    /// <summary>
    /// [AI Context] Puts each part's original header layer back under every "% --- TEIL n" separator
    /// of a step's output, replacing whatever block the model left there - dropped, shortened or
    /// rewritten. The model is told to keep the separators, not the comment blocks under them.
    /// [Human] Setzt die Header-Blöcke der Teile unter die erhaltenen TEIL-Trenner zurück.
    /// </summary>
    public static string RestorePartHeaders(string body, IReadOnlyDictionary<int, string> partHeaders) {
        if (partHeaders.Count == 0) return body;

        string[] lines = body.Replace("\r\n", "\n").Split('\n');
        var result = new StringBuilder();

        for (int index = 0; index < lines.Length; index++) {
            result.Append(lines[index]).Append('\n');
            if (PartNumber(lines[index]) is not int part || !partHeaders.TryGetValue(part, out string? header)) continue;

            if (ReadLayerAt(lines, index + 1) is (_, int end)) {
                index = end; // skip the model's copy
            }
            result.Append(header);
        }

        return result.ToString(0, Math.Max(0, result.Length - 1)); // the loop added one '\n' too many
    }

    /// <summary>
    /// [AI Context] The text a refinement step sends to the model: the document without the header
    /// layers on top and without the per-part blocks under the "% --- TEIL n" separators (the
    /// separators themselves stay - the merge relies on them). The blocks describe how the file was
    /// made, not the lecture: sent along they cost input tokens, and a model that copies them spends
    /// output on them. The step restores both from the input file when it writes its result.
    /// [Human] Entfernt die Header-Blöcke vor dem Senden an das Modell; sie werden danach wieder eingesetzt.
    /// </summary>
    public static string StripHeadersForModel(string tex) {
        string[] lines = SplitHeaderLayers(tex).Body.Split('\n');
        var result = new StringBuilder();

        for (int index = 0; index < lines.Length; index++) {
            result.Append(lines[index]).Append('\n');
            if (PartNumber(lines[index]) != null && ReadLayerAt(lines, index + 1) is (_, int end)) {
                index = end;
            }
        }

        return result.ToString(0, Math.Max(0, result.Length - 1));
    }

    /// <summary>
    /// [AI Context] Counts the video parts a combined (-all) document was assembled from, by its
    /// "% --- TEIL n" separators - every extraction path writes one per part, fresh or from cache.
    /// The document is the authority on its own part count: the configured NumberOfParts may be
    /// "auto", may have changed since the run, or may be absent entirely (a standalone refinement).
    /// Returns 0 when the text carries no separators, i.e. it is not a combined extraction output.
    /// [Human] Zählt, aus wie vielen Video-Teilen ein zusammengesetztes Dokument besteht.
    /// </summary>
    public static int CountParts(string combinedTex) =>
        PartSeparatorRegex().Matches(combinedTex)
            .Select(match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0)
            .Max();

    [GeneratedRegex(@"^% --- TEIL (\d+)\b", RegexOptions.Multiline)]
    private static partial Regex PartSeparatorRegex();

    private static int? PartNumber(string line) {
        var match = PartSeparatorRegex().Match(line);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The layer starting at <paramref name="start"/> (no blank lines skipped), with the index of its closing rule.</summary>
    private static (string Layer, int End)? ReadLayerAt(string[] lines, int start) {
        if (start >= lines.Length || lines[start].TrimEnd() != LayerRule) return null;

        int end = start + 1;
        while (end < lines.Length && lines[end].StartsWith('%') && lines[end].TrimEnd() != LayerRule) end++;
        if (end >= lines.Length || lines[end].TrimEnd() != LayerRule) return null;

        return (string.Join("\n", lines[start..(end + 1)]) + "\n", end);
    }

    private static string Layer(IReadOnlyList<string> lines, params IReadOnlyList<string>[] sections) {
        var builder = new StringBuilder();
        builder.Append(LayerRule).Append('\n');
        foreach (string line in lines) {
            builder.Append("% ").Append(line).Append('\n');
        }
        foreach (var section in sections) {
            builder.Append(Divider).Append('\n');
            foreach (string line in section) {
                builder.Append("% ").Append(line).Append('\n');
            }
        }
        builder.Append(LayerRule).Append('\n');
        return builder.ToString();
    }

    private static IEnumerable<string> ModelLines(string model, GenerationParameters generation) {
        yield return $"Model: {model}";
        yield return $"Temperature: {generation.Temperature}";
        yield return $"TopP: {generation.TopP}";
        yield return $"TopK: {generation.TopK}";
        yield return $"MaxOutputTokens: {generation.MaxOutputTokens}";
        if (generation.ThinkingBudget.HasValue) yield return $"ThinkingBudget: {generation.ThinkingBudget.Value}";
        if (!string.IsNullOrEmpty(generation.ThinkingLevel)) yield return $"ThinkingLevel: {generation.ThinkingLevel}";
    }

    /// <summary>
    /// [AI Context] Output and thinking are listed apart: Gemini's candidate count is the visible
    /// answer only, the thinking tokens are reported separately and billed as output on top of it.
    /// </summary>
    private static IReadOnlyList<string> TokenLines(TokenUsage usage, string title) => [
        usage.Requests > 0 ? $"{title} ({usage.Requests} Request(s)):" : $"{title}:",
        $"  - Total Prompt Tokens : {usage.Input:N0} (gesamter Prompt, inkl. Cache)",
        $"  - Cached Context      : {usage.Cached:N0} (aus Google Context-Cache, rabattiert)",
        $"  - Fresh Input Tokens  : {usage.Fresh:N0} (neu verrechneter Input)",
        $"  - Output Tokens       : {usage.Output:N0} (generiertes LaTeX, ohne Thinking)",
        $"  - Thinking Tokens     : {usage.Thinking:N0} (Denk-Tokens, als Output verrechnet)"
    ];
}
