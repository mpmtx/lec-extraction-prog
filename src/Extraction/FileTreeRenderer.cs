using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Renders lists of file paths as hierarchical trees (to the console or as markdown),
/// and provides the underlying path-normalization helpers those renders depend on.
/// </summary>
public static partial class FileTreeRenderer {
    /// <summary>
    /// [AI Context] Prints a list of file paths in a structured, hierarchical tree format showing files first, then folders.
    /// </summary>
    public static void PrintFileTree(List<string> filePaths, bool verbose = false) {
        if (filePaths == null || filePaths.Count == 0) return;

        string? baseDir = FindCommonBaseDirectory(filePaths);
        if (!verbose) {
            string dirInfo = !string.IsNullOrEmpty(baseDir) ? $" in {baseDir}" : "";
            Ui.Info($"{filePaths.Count} Datei(en){dirInfo} geladen.");
            return;
        }

        var root = BuildVirtualTree(filePaths, baseDir);
        if (!string.IsNullOrEmpty(baseDir)) {
            Ui.Info($"📁 {baseDir}");
        }
        RenderVirtualTreeNode(root, "      ", showRelativePath: false, line => Ui.Info(line));
    }

    public static string? FindCommonBaseDirectory(List<string> allPaths) {
        if (allPaths == null || allPaths.Count == 0) return null;
        try {
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(allPaths[0])) ?? "";
            foreach (var path in allPaths) {
                string fullPath = Path.GetFullPath(path);
                while (!fullPath.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(baseDir)) {
                    baseDir = Path.GetDirectoryName(baseDir) ?? "";
                }
            }
            return string.IsNullOrEmpty(baseDir) ? null : baseDir;
        }
        catch (Exception ex) {
            Ui.Error($"[Exception gefangen] {ex.Describe()}");
            return null;
        }
    }

    public static string GenerateMarkdownFileTree(List<string> filePaths, string? baseDir) {
        if (filePaths == null || filePaths.Count == 0) return "";

        if (string.IsNullOrEmpty(baseDir)) {
            baseDir = FindCommonBaseDirectory(filePaths);
        }

        // [AI Context] This text goes into every Gemini request, so it is a flat list of relative
        // paths: the box-drawing tree, the emoji icons and the repeated file names of the console
        // view cost ~2k tokens per request and tell the model nothing the paths do not.
        // The paths keep the order of filePaths, which is the order the files are sent in
        // (Stage1PromptFiles), so the list doubles as a table of contents. Until 2026-11-09 it was
        // sorted (files before folders, by name) and matched neither that order nor the hubs' indexes.
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrEmpty(baseDir)) {
            sb.AppendLine($"Root: {baseDir}");
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in filePaths) {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string relPath = !string.IsNullOrEmpty(baseDir)
                ? Path.GetRelativePath(baseDir, path).Replace('\\', '/')
                : path.Replace('\\', '/');
            if (relPath == "." || relPath == string.Empty) continue;
            string normalized = NormalizeRelativePath(CleanCopySuffix(relPath));
            if (seen.Add(normalized)) {
                sb.AppendLine($"- {normalized}");
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// [AI Context] Represents a node in an in-memory virtual directory tree constructed from a list of file paths.
    /// Used to generate sorted, hierarchical visual layouts for console output and system prompts.
    /// </summary>
    private sealed class VirtualTreeNode {
        public string Name { get; set; } = "";
        public string? RelativePath { get; set; }
        public bool IsDirectory { get; set; }
        public Dictionary<string, VirtualTreeNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// [AI Context] Builds a hierarchical virtual tree structure from a flat list of file paths,
    /// splitting by directory separators relative to a given base directory.
    /// </summary>
    private static VirtualTreeNode BuildVirtualTree(List<string> filePaths, string? baseDir) {
        var root = new VirtualTreeNode { IsDirectory = true };

        foreach (var path in filePaths) {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string relPath = !string.IsNullOrEmpty(baseDir)
                ? Path.GetRelativePath(baseDir, path).Replace('\\', '/')
                : path.Replace('\\', '/');

            if (relPath == "." || relPath == string.Empty) continue;

            string cleanedRelPath = CleanCopySuffix(relPath);
            string normalizedRelPath = NormalizeRelativePath(cleanedRelPath);

            string[] parts = cleanedRelPath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            var current = root;
            for (int i = 0; i < parts.Length; i++) {
                string part = parts[i];
                bool isFile = (i == parts.Length - 1);

                if (!current.Children.TryGetValue(part, out VirtualTreeNode? child)) {
                    child = new() {
                        Name = part,
                        IsDirectory = !isFile,
                        RelativePath = isFile ? normalizedRelPath : null
                    };
                    current.Children[part] = child;
                }
                current = child;
            }
        }

        return root;
    }

    /// <summary>
    /// [AI Context] Normalizes a relative path to follow Unix conventions:
    /// uses forward slashes, prefixes with "./", and removes common copy suffixes like " - Kopie" or " - Copy".
    /// </summary>
    public static string NormalizeRelativePath(string relPath) {
        if (string.IsNullOrEmpty(relPath)) return relPath;

        string normalized = relPath.Replace('\\', '/');
        normalized = CleanCopySuffix(normalized);

        if (normalized.StartsWith("./", StringComparison.Ordinal)) {
            normalized = normalized[2..];
        }

        if (normalized.StartsWith('/')) {
            return "." + normalized;
        }
        return "./" + normalized;
    }

    /// <summary>
    /// [AI Context] Removes common copy suffixes (such as " - Kopie" or "-Copy") case-insensitively using a robust Regex.
    /// </summary>
    public static string CleanCopySuffix(string input) {
        if (string.IsNullOrEmpty(input)) return input;
        return CopySuffixRegex().Replace(input, "");
    }

    /// <summary>
    /// [AI Context] Recursively renders virtual tree nodes with visual branching characters (├──, └──).
    /// Files are displayed first, followed by directories, with optional inclusion of full relative paths.
    /// </summary>
    private static void RenderVirtualTreeNode(VirtualTreeNode node, string indent, bool showRelativePath, Action<string> writeLine) {
        var children = node.Children.Values
            .OrderBy(c => c.IsDirectory ? 1 : 0)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        for (int i = 0; i < children.Count; i++) {
            var child = children[i];
            bool isLast = (i == children.Count - 1);
            string branch = isLast ? "└── " : "├── ";

            if (child.IsDirectory) {
                writeLine($"{indent}{branch}📁 {child.Name}/");
                string childIndent = indent + (isLast ? "    " : "│   ");
                RenderVirtualTreeNode(child, childIndent, showRelativePath, writeLine);
            }
            else {
                string ext = Path.GetExtension(child.Name).ToLowerInvariant();
                string icon = DirectoryTreeRenderer.GetFileIcon(ext);
                string label = (showRelativePath && !string.IsNullOrEmpty(child.RelativePath) && !string.Equals(child.RelativePath, child.Name, StringComparison.OrdinalIgnoreCase))
                    ? $"{child.Name} ({child.RelativePath})"
                    : child.Name;
                writeLine($"{indent}{branch}{icon} {label}");
            }
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\s*[\-‐-―]\s*(?:Kopie|Copy)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CopySuffixRegex();
}
