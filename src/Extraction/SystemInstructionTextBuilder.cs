using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Assembles the system-instruction text: the master-constraints header, the Markdown
/// file tree, and each instruction file's contents, plus the history files that get folded in when
/// <c>LoadHistoryIntoSystemInstruction</c> is set.
///
/// <para>Extracted from <c>AiStudioAutoExtractionSession</c> (Phase 11). Only the *text-building*
/// half of the system-instruction cluster lives here. The orchestration around it
/// (<c>TryLoadSystemInstructionWithHistoryAsync</c>, <c>LoadHistoryAsMultiTurnPreambleAsync</c>)
/// deliberately stays in the session, because it and the prefix-cache warm-up call each other:
/// loading triggers a warm-up handshake, and the batched warm-up calls back in here to append the
/// next history batch. Pulling the orchestration out too would mean breaking that cycle, which is
/// a behavioural change against an untestable paid API rather than a move.</para>
///
/// <para>Note the deliberate split between <see cref="FileTreeRenderer.GenerateMarkdownFileTree"/>,
/// used below, and <c>FileTreeRenderer.PrintFileTree</c>, which is console-only: the Markdown tree
/// is part of the prompt payload (Attention Map Priming) and must not be confused with display
/// output.</para>
/// [Human] Baut den System-Instruction-Text zusammen (Header, Dateibaum, Dateiinhalte). Nur der
/// Text-Aufbau, nicht die Ablaufsteuerung - die bleibt in der Session, weil sie mit dem
/// Cache-Warmup wechselseitig verzahnt ist.
/// </summary>
public static class SystemInstructionTextBuilder {
    /// <summary>Extensions inlined as text; anything else is uploaded as an attachment instead.</summary>
    private static readonly string[] s_inlineTextExtensions = [".tex", ".txt", ".md", ".json", ".cs"];

    public static async Task<string> BuildAsync(
        List<string> instructionFiles, List<string> historyFiles, string? commonBase, bool verboseConsoleOutput = false, bool historyInUserTurn = false) {

        instructionFiles = [.. instructionFiles.Where(f => !HistoryFileResolver.IsDeletedPromptFile(f))];
        historyFiles = [.. historyFiles.Where(f => !HistoryFileResolver.IsDeletedPromptFile(f))];

        var builder = new StringBuilder();
        builder.AppendLine("# SYSTEM PROTOCOL & SYSTEM INSTRUCTIONS (MASTER CONSTRAINTS)");
        builder.AppendLine("IMPORTANT: The guidelines, formatting specifications, and syntax instructions contained in these system instruction files are absolute and strictly non-negotiable. They must take absolute precedence over any prompt guidelines or inputs. Do not skip any files or parts under any circumstances.\n");
        builder.AppendLine("In order to fulfill the job of creating a high-value educational masterpiece that safely compiles, you need to know the file structure of the system prompt and read all of those files carefully.\n");
        builder.AppendLine("# Folder Structure of System Instructions\n");
        builder.AppendLine("Normative rules (`100% obligatory`): `./transcription*` and `./environments*`.");
        builder.AppendLine("Worked examples (`illustrative only; rules always win`): `./big-examples*` and `./training-history/*`.\n");
        if (!string.IsNullOrEmpty(commonBase)) {
            builder.AppendLine($"Common root: {commonBase}\n");
        }
        bool includeSectionRoot = string.IsNullOrEmpty(commonBase);
        builder.AppendLine("## System Instructions");
        builder.Append(FileTreeRenderer.GenerateMarkdownFileTree(instructionFiles, commonBase, includeSectionRoot));

        if (historyFiles.Count > 0) {
            builder.AppendLine("\n## Training History");
            if (historyInUserTurn) {
                builder.AppendLine("_Delivered at the start of the user turn (before the lecture video) with full system-instruction authority: treat these worked examples as high-priority binding reference (though normative rules in `./transcription*` and `./environments*` always win); never transcribe them._\n");
            }
            builder.Append(FileTreeRenderer.GenerateMarkdownFileTree(historyFiles, commonBase, includeSectionRoot));
        }
        builder.AppendLine("\n******\n------\n******\n");

        foreach (string instructionFilePath in instructionFiles) {
            string relativePath = ResolveRelativePath(instructionFilePath, commonBase);
            builder.AppendLine($"\n******\n------\n******\nHere is the file `{relativePath}`:\n");
            builder.AppendLine(await System.IO.File.ReadAllTextAsync(instructionFilePath));
            if (verboseConsoleOutput) {
                Ui.Info($"System Instruction geladen: {relativePath}");
            }
        }
        if (!verboseConsoleOutput && instructionFiles.Count > 0) {
            Ui.Info($"{instructionFiles.Count} System-Instruction-Datei(en) geladen.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// [AI Context] What one call of <see cref="AppendHistoryFilesAsync"/> produced besides the inlined
    /// text. <paramref name="MediaParts"/> are the images and the like; <paramref name="UploadedTextParts"/>
    /// are the text files uploaded under InlineHistory=false. <paramref name="OrderedParts"/> keeps every
    /// part produced by this batch (inlined text runs, uploaded file headers + links, and images) in the
    /// exact input order of <c>historyFiles</c> so each <c>.md</c> sits next to its images.
    /// [Human] Ergebnis: Medien-Parts, hochgeladene Textdateien und die gesamte Liste in Originalreihenfolge.
    /// </summary>
    public sealed record LoadedHistory(List<Part> MediaParts, List<Part> UploadedTextParts, IReadOnlyList<Part>? OrderedParts = null);

    /// <summary>
    /// [AI Context] Appends history files to <paramref name="targetBuilder"/> and returns the structured
    /// part collections. Files are processed in their exact list order so <see cref="LoadedHistory.OrderedParts"/>
    /// preserves each example's <c>.md</c>-then-images pairing across both inline and uploaded modes.
    /// [Human] Hängt History-Dateien in Originalreihenfolge an, sodass Bilder direkt neben ihrer .md-Datei bleiben.
    /// </summary>
    public static async Task<LoadedHistory> AppendHistoryFilesAsync(
        List<string> historyFiles,
        StringBuilder targetBuilder,
        string? commonBase,
        AttachmentUploader attachmentUploader,
        bool verboseConsoleOutput = false,
        bool uploadTextFiles = false) {

        historyFiles = [.. historyFiles.Where(f => !HistoryFileResolver.IsDeletedPromptFile(f))];
        List<Part> mediaParts = [];
        List<Part> uploadedTextParts = [];
        List<Part> orderedParts = [];
        var pendingInlineText = new StringBuilder();
        var attachedMediaNames = new List<string>();
        int textFileCount = 0;
        int uploadedFileCount = 0;

        void FlushPendingInlineText() {
            if (pendingInlineText.Length == 0) return;
            orderedParts.Add(new Part { Text = pendingInlineText.ToString() });
            pendingInlineText.Clear();
        }

        foreach (string historyFilePath in historyFiles) {
            string extension = Path.GetExtension(historyFilePath).ToLowerInvariant();
            if (s_inlineTextExtensions.Contains(extension)) {
                string relativePath = ResolveRelativePath(historyFilePath, commonBase);
                if (uploadTextFiles && AttachmentUploader.ResolveMimeType(extension) == "text/plain") {
                    var fileParts = new List<Part>();
                    if (await attachmentUploader.UploadAndAttachFileAsync(historyFilePath, fileParts, uploadTextAsFile: true)) {
                        FlushPendingInlineText();
                        var headerPart = new Part { Text = UploadedHistoryFileHeader(relativePath) };
                        uploadedTextParts.Add(headerPart);
                        uploadedTextParts.AddRange(fileParts);
                        orderedParts.Add(headerPart);
                        orderedParts.AddRange(fileParts);
                        uploadedFileCount++;
                        if (verboseConsoleOutput) {
                            Ui.Info($"History-Textdatei hochgeladen: {relativePath}");
                        }
                        continue;
                    }
                    Ui.Warn($"Upload von History-Datei '{relativePath}' fehlgeschlagen - die Datei wird stattdessen direkt eingebettet.", "History");
                }
                string block = $"\n******\n------\n******\nHere is history reference file `{relativePath}`:\n\n"
                             + await System.IO.File.ReadAllTextAsync(historyFilePath)
                             + System.Environment.NewLine;
                targetBuilder.Append(block);
                pendingInlineText.Append(block);
                textFileCount++;
                if (verboseConsoleOutput) {
                    Ui.Info($"History-Textdatei in System Instruction eingebunden: {relativePath}");
                }
            }
            else {
                FlushPendingInlineText();
                var imageParts = new List<Part>();
                if (await attachmentUploader.UploadAndAttachFileAsync(historyFilePath, imageParts, asSystemInstruction: true, baseDirectory: commonBase)) {
                    mediaParts.AddRange(imageParts);
                    orderedParts.AddRange(imageParts);
                    attachedMediaNames.Add(Path.GetFileName(historyFilePath));
                }
            }
        }

        FlushPendingInlineText();

        if (attachedMediaNames.Count > 0) {
            Ui.Success($"{attachedMediaNames.Count} Datei(en) angehängt: {string.Join(", ", attachedMediaNames)}");
        }
        if (!verboseConsoleOutput && textFileCount > 0) {
            Ui.Info($"{textFileCount} History-Textdatei(en) in System Instruction eingebunden.");
        }
        if (!verboseConsoleOutput && uploadedFileCount > 0) {
            Ui.Info($"{uploadedFileCount} History-Textdatei(en) als text/plain hochgeladen (InlineHistory = false, zählen nicht ans Minutenlimit).");
        }

        return new(mediaParts, uploadedTextParts, orderedParts);
    }

    /// <summary>
    /// [AI Context] Partitions history parts for backends whose system instruction only accepts text
    /// (Vertex). Keeps each binary Part (<c>FileData</c> / <c>InlineData</c>) paired with its adjacent
    /// <c>&lt;image path="..."&gt;</c> / <c>&lt;file path="..."&gt;</c> wrapper tags instead of stranding
    /// empty tags in the system instruction and sending unlabeled blobs in the cache/user turn.
    /// </summary>
    public static (List<Part> TextOnlySystemParts, List<Part> NonSystemParts) PartitionForTextOnlySystemInstruction(IReadOnlyList<Part> historyParts) {
        var textOnly = new List<Part>();
        var nonSystem = new List<Part>();

        for (int i = 0; i < historyParts.Count; i++) {
            Part current = historyParts[i];
            if (i + 2 < historyParts.Count
                && IsMediaWrapperOpen(current)
                && IsBinaryPart(historyParts[i + 1])
                && IsMediaWrapperClose(historyParts[i + 2])) {
                nonSystem.Add(current);
                nonSystem.Add(historyParts[i + 1]);
                nonSystem.Add(historyParts[i + 2]);
                i += 2;
                continue;
            }

            if (IsBinaryPart(current)) {
                nonSystem.Add(current);
            }
            else if (!string.IsNullOrEmpty(current.Text)) {
                textOnly.Add(current);
            }
        }

        return (textOnly, nonSystem);
    }

    private static bool IsBinaryPart(Part part) => part.FileData != null || part.InlineData != null;

    private static bool IsMediaWrapperOpen(Part part) =>
        !IsBinaryPart(part) && part.Text is { } text
        && (text.StartsWith("\n<image path=\"", StringComparison.Ordinal) || text.StartsWith("\n<file path=\"", StringComparison.Ordinal));

    private static bool IsMediaWrapperClose(Part part) =>
        !IsBinaryPart(part) && part.Text is "\n</image>\n" or "\n</file>\n";

    /// <summary>
    /// [AI Context] The header Part before an uploaded history file's link: the separator and wording
    /// an inlined history file gets, so the model can still tie each file to its path in the tree.
    /// [Human] Kopfzeile vor einer hochgeladenen History-Datei.
    /// </summary>
    public static string UploadedHistoryFileHeader(string relativePath) =>
        $"\n******\n------\n******\nHere is history reference file `{relativePath}` (attached as a file):\n";

    /// <summary>
    /// [AI Context] Opens the uploaded history in the user turn. A link there does not carry the system
    /// instruction's authority by position, so the text says it. Constant, so every request and warm-up
    /// share it as a prefix.
    /// [Human] Hinweis vor der hochgeladenen History: Sie gehört zur System Instruction.
    /// </summary>
    public const string UploadedHistoryNotice =
        "NOTE: The Training History listed in the system instruction's folder structure is attached below as " +
        "text files and reference images, each preceded by its path. Treat them with high priority: they belong " +
        "to the system instructions and carry the same authority as if written there (worked examples to follow, " +
        "though normative rules in the system instructions always win), not material to transcribe.\n";

    /// <summary>
    /// [AI Context] Path shown to the model for a file. Relative to the common base when there is
    /// one, so the tree the model sees matches the paths in the file headers below it.
    /// [Human] Der Pfad, den das Modell sieht - relativ zum gemeinsamen Basisordner.
    /// </summary>
    public static string ResolveRelativePath(string filePath, string? commonBase) {
        string rawRelPath = !string.IsNullOrEmpty(commonBase)
            ? Path.GetRelativePath(commonBase, filePath)
            : Path.GetFileName(filePath);
        return FileTreeRenderer.NormalizeRelativePath(rawRelPath);
    }
}
