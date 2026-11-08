using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Resolves history/system-instruction file lists into absolute paths, and groups them into
/// batches for chunked loading. Folders are never expanded: every prompt file is named on its own.
/// </summary>
public static class HistoryFileResolver {
    private static readonly HashSet<string> s_imageExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp", ".tiff", ".heic", ".heif", ".svg"
    };

    // "ignored-" is the prefix the prompt-engineering repo uses; the others are legacy spellings
    // (its montage sources were "deleted-" until 2026-10-16) and stay accepted.
    private static readonly string[] s_ignoredPrefixes = ["ignored-", "ignore-", "deleted-", "delted-"];

    /// <summary>
    /// [AI Context] Determines whether a file path points to an image/picture based on its extension.
    /// [Human] Prüft anhand der Dateiendung, ob es sich um eine Bilddatei handelt.
    /// </summary>
    public static bool IsPictureFile(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        return s_imageExtensions.Contains(Path.GetExtension(filePath));
    }

    /// <summary>
    /// [AI Context] Determines whether a file is marked as ignored/non-montage (prefixed with
    /// "ignored-", or the legacy "ignore-", "deleted-", "delted-").
    /// In token-optimized prompt directories, montage pictures are included while individual
    /// non-montage pictures are marked with an ignore/delete prefix to exclude them
    /// from system instructions sent to Gemini.
    /// [Human] Prüft, ob eine Datei als ignoriert oder gelöscht markiert ist (Präfix "ignored-", oder alt "ignore-", "deleted-", "delted-").
    /// Nicht-Montage-Bilder mit diesem Präfix werden nicht in die System Instruction übernommen, um Tokens zu sparen.
    /// </summary>
    public static bool IsIgnoredPromptFile(string filePath) {
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        string fileName = Path.GetFileName(filePath);
        foreach (var prefix in s_ignoredPrefixes) {
            if (fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Backward-compatible alias for <see cref="IsIgnoredPromptFile"/>.
    /// </summary>
    public static bool IsDeletedPromptFile(string filePath) => IsIgnoredPromptFile(filePath);

    /// <summary>
    /// [AI Context] Resolves a list of file paths into a distinct list of absolute paths, in the given order,
    /// skipping files marked with "ignored-" (or a legacy prefix). A folder is not loaded, only warned about:
    /// since 2026-11-08 every prompt file is named on its own and in a fixed order (Stage 1: Stage1PromptFiles),
    /// so nothing enters a prompt just by sitting in a folder.
    /// [Human] Löst eine Liste von Dateipfaden auf. Ordner werden nicht geladen, nur einzeln genannte Dateien.
    /// </summary>
    public static List<string> ResolveHistoryFiles(string[] paths, string label = "Konfigurierter Pfad") {
        List<string> allHistoryFiles = [];
        if (paths == null) return allHistoryFiles;

        foreach (var path in paths.Where(p => !string.IsNullOrWhiteSpace(p))) {
            if (File.Exists(path)) {
                if (!IsDeletedPromptFile(path))
                    allHistoryFiles.Add(Path.GetFullPath(path));
            }
            else if (Directory.Exists(path))
                Ui.Warn($"{label} ist ein Ordner und wird nicht geladen (nur einzeln genannte Dateien werden geladen): {path}");
            else
                Ui.Warn($"{label} nicht gefunden: {path}");
        }
        return [.. allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// [AI Context] Groups a flat list of resolved history files into at most <paramref name="batchCount"/> batches.
    /// Step 1: Files are grouped by their subfolder relative to one of <paramref name="historyPreloadPaths"/>, the
    ///         root folders (Stage 1 passes Stage1PromptFiles.HistoryRoot).
    /// Step 2: Those subfolder-groups are distributed evenly into batchCount buckets (chunked).
    ///         If batchCount &lt;= 1, all files are returned as a single batch.
    ///         If batchCount &gt;= subfolder count, each subfolder is its own batch (one-per-subfolder mode).
    /// Each result entry has a human-readable label listing the subfolders it contains.
    /// [Human] Verteilt die History-Subfolders gleichmäßig auf batchCount Batches.
    /// </summary>
    public static List<(string GroupLabel, List<string> Files)> GroupHistoryFilesByTopLevelSubfolder(
        List<string> files, string[] historyPreloadPaths, int batchCount) {

        if (files == null || files.Count == 0) return [];

        // --- Step 1: group files by their relative directory path ---
        var folderGroups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var folderOrder = new List<string>();

        foreach (var file in files) {
            string groupKey = "";
            foreach (var rootPath in historyPreloadPaths.Where(p => !string.IsNullOrWhiteSpace(p))) {
                if (!Directory.Exists(rootPath)) continue;
                string root = Path.GetFullPath(rootPath);
                string fullFile = Path.GetFullPath(file);
                if (fullFile.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || fullFile.Equals(root, StringComparison.OrdinalIgnoreCase)) {
                    string relative = Path.GetRelativePath(root, fullFile);
                    string? dirName = Path.GetDirectoryName(relative);
                    groupKey = !string.IsNullOrEmpty(dirName) ? dirName : "";
                    break;
                }
            }
            if (!folderGroups.TryGetValue(groupKey, out var groupList)) {
                groupList = [];
                folderGroups[groupKey] = groupList;
                folderOrder.Add(groupKey);
            }
            groupList.Add(file);
        }

        // Build ordered list of (folderKey, files) - root files first
        var ordered = new List<(string Key, List<string> Files)>();
        if (folderGroups.TryGetValue("", out var rootFiles)) ordered.Add(("(root)", rootFiles));
        foreach (var key in folderOrder.Where(k => k != "")) ordered.Add((key, folderGroups[key]));

        // --- Step 2: if batchCount <= 1, return single batch ---
        if (batchCount <= 1 || ordered.Count == 0) {
            var allFiles = ordered.SelectMany(g => g.Files).ToList();
            string label = ordered.Count > 0 ? string.Join(", ", ordered.Select(g => g.Key)) : "(alle)";
            return [(label, allFiles)];
        }

        // --- Step 3: distribute folder groups deterministically across batchCount buckets ---
        int effectiveBatches = Math.Min(batchCount, ordered.Count);
        var result = new List<(string, List<string>)>();

        for (int b = 0; b < effectiveBatches; b++) {
            int start = b * ordered.Count / effectiveBatches;
            int end = (b + 1) * ordered.Count / effectiveBatches;
            var bucket = ordered.GetRange(start, end - start);
            if (bucket.Count > 0) {
                var displayNames = bucket.Select(g => Path.GetFileName(g.Key)).Where(name => !string.IsNullOrEmpty(name)).Distinct().ToList();
                string bucketLabel = displayNames.Count > 0 ? string.Join(" + ", displayNames) : "(root)";
                var bucketFiles = bucket.SelectMany(g => g.Files).ToList();
                result.Add((bucketLabel, bucketFiles));
            }
        }
        return result;
    }
}
