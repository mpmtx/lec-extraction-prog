using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Stage 1's prompt, file by file, in the order it is sent: first the system instruction files,
/// then the training history. Nothing is loaded by folder: a file in the prompt-engineering repository is in
/// the prompt only if it is listed here, and its place in the list is its place in the prompt. Both Stage 1
/// configs read these lists (their SystemInstructionPaths and HistoryPreloadPaths are computed from them);
/// no JSON file can change them.
/// Moving a prompt file means editing this list and rebuilding. <see cref="FindUnlistedFiles"/> reports a file
/// that sits in the prompt folders but is missing here (lecx plan and the session start warn about it), and
/// ExtractionPlanner reports a listed file that does not exist.
/// The order is the one the folder entries produced before 2026-11-08 (each folder alphabetical, the history
/// breadth-first), plus the four files split out of transcription.md, which follow it in their old order.
/// [Human] Die Stage-1-Prompt-Dateien, einzeln und in fester Reihenfolge. Ordner werden nicht geladen: eine neue
/// Prompt-Datei muss hier eingetragen werden (dann neu bauen).
/// </summary>
public static class Stage1PromptFiles {
    /// <summary>The prompt-engineering repository's transcription folder; every entry is relative to it.</summary>
    public const string Root = @"C:\Users\miche\latex\prompt-engineering\transcription";

    /// <summary>The training-history folder. The history cache warm-up batches its files by subfolder.</summary>
    public static string HistoryRoot => Path.Combine(Root, "training-history");

    /// <summary>The system instruction files, in the order they are sent.</summary>
    public static readonly IReadOnlyList<string> SystemInstructionFiles = [
        "transcription.md",
        "transcription/transcription-source-media.md",
        "transcription/transcription-multi-part.md",
        "transcription/transcription-speech-writing.md",
        "transcription/transcription-notation.md",
        "environments.md",
        "environments/environments-content.md",
        "environments/environments-document-structure.md",
        "environments/environments-inlinemetanote.md",
        "environments/environments-invisible-layer.md",
        "environments/environments-pedagogical-boxes.md",
        "environments/environments-proof-wrappers.md",
        "environments/environments-speech.md",
        "environments/environments-syntax-rules.md",
        "environments/environments-theorem-family.md",
        "environments/environments-tikz.md",
        "big-examples.md",
        "big-examples/big-examples-anti-patterns.md",
        "big-examples/big-examples-audio-fidelity.md",
        "big-examples/big-examples-compilation-errors.md",
        "big-examples/big-examples-document-conventions.md",
        "big-examples/big-examples-document-hierarchy.md",
        "big-examples/big-examples-environment-usage.md",
        "big-examples/big-examples-example-scenarios.md",
        "big-examples/big-examples-fallback-strategy.md",
        "big-examples/big-examples-formula-formatting.md",
        "big-examples/big-examples-list-enumeration.md",
        "big-examples/big-examples-proof-structuring.md",
        "big-examples/big-examples-speech-structuring.md",
        "big-examples/big-examples-typography-notation.md",
    ];

    /// <summary>The training history, in the order it is sent (files with the ignored- prefix are not listed: the fidelity montage's sources and the six montages retired on 2026-11-08).</summary>
    public static readonly IReadOnlyList<string> HistoryFiles = [
        "training-history/board-transcription-examples/auswahlaxiom/auswahlaxiom.jpg",
        "training-history/board-transcription-examples/auswahlaxiom/auswahlaxiom.md",
        "training-history/board-transcription-examples/beschleunigte_bewegung/beschl_bewegung.md",
        "training-history/board-transcription-examples/beschleunigte_bewegung/beschl_wandtafel1.jpg",
        "training-history/board-transcription-examples/beschleunigte_bewegung/beschl_wandtafel2.jpg",
        "training-history/board-transcription-examples/kartesisches_produkt/kart_board.jpg",
        "training-history/board-transcription-examples/kartesisches_produkt/kart_explaination.md",
        "training-history/board-transcription-examples/small-detail/screenshot1.jpg",
        "training-history/board-transcription-examples/small-detail/screenshot1.md",
        "training-history/board-transcription-examples/tabular/tabular1.md",
        "training-history/board-transcription-examples/variablenbelegung/BAD-example-variablenbelegung.md",
        "training-history/board-transcription-examples/variablenbelegung/board-variablenbelegung.png",
        "training-history/board-transcription-examples/variablenbelegung/GOOD-example-variablenbelegung.md",
        "training-history/board-transcription-examples/writes-note-fidelity/tidied-notes.png",
        "training-history/board-transcription-examples/writes-note-fidelity/writes-note-fidelity-montage.jpg",
        "training-history/conventions/proofs/proof_vs_short_proof.md",
        "training-history/didactic_examples/2.5-minute-rule/BAD-GOOD-example-2.5-minute-rule.md",
        "training-history/didactic_examples/anti-duplication/BAD-GOOD-example-anti-duplication-and-semantic-hygiene.md",
        "training-history/didactic_examples/graph-and-tables/BAD-GOOD-example-graph-schlichtheit-tabular-vs-align.md",
        "training-history/didactic_examples/remark-environment/BAD-GOOD-example-remark-environment.md",
        "training-history/didactic_examples/remark-environment/BAD-GOOD-example-tutor-hat-phantom.md",
        "training-history/tikz-examples/blackboard-example/blackboard-example-1-BAD-result.jpg",
        "training-history/tikz-examples/blackboard-example/blackboard-example-1.jpg",
        "training-history/tikz-examples/blackboard-example/blackboard-example-good.png",
        "training-history/tikz-examples/blackboard-example/blackboard-example.md",
        "training-history/tikz-examples/block-diagrams/1-timeline.png",
        "training-history/tikz-examples/block-diagrams/2-security-game.png",
        "training-history/tikz-examples/block-diagrams/3-xor-labels.png",
        "training-history/tikz-examples/block-diagrams/4-cbc-wires.png",
        "training-history/tikz-examples/block-diagrams/block-diagrams.md",
        "training-history/tikz-examples/cramped-figures/1-unit-square-bad.png",
        "training-history/tikz-examples/cramped-figures/1-unit-square-good.png",
        "training-history/tikz-examples/cramped-figures/2-included-skin-bad.png",
        "training-history/tikz-examples/cramped-figures/2-included-skin-good.png",
        "training-history/tikz-examples/cramped-figures/3-three-panels-bad.png",
        "training-history/tikz-examples/cramped-figures/3-three-panels-good.png",
        "training-history/tikz-examples/cramped-figures/4-nested-intervals-bad.png",
        "training-history/tikz-examples/cramped-figures/4-nested-intervals-good.png",
        "training-history/tikz-examples/cramped-figures/5-two-balls-bad.png",
        "training-history/tikz-examples/cramped-figures/5-two-balls-good.png",
        "training-history/tikz-examples/cramped-figures/6-continuity-image-bad.png",
        "training-history/tikz-examples/cramped-figures/6-continuity-image-good.png",
        "training-history/tikz-examples/cramped-figures/7-continuity-preimage-bad.png",
        "training-history/tikz-examples/cramped-figures/7-continuity-preimage-good.png",
        "training-history/tikz-examples/cramped-figures/cramped-figures.md",
        "training-history/tikz-examples/dedekind-cuts/dedekind-cuts-didactic-example.md",
        "training-history/tikz-examples/intervals/characterization-of-intervals.md",
        "training-history/tikz-examples/label-collisions/label-collisions.jpg",
        "training-history/tikz-examples/label-collisions/label-collisions.md",
        "training-history/tikz-examples/max-distance-label/max-distance-label-bad.png",
        "training-history/tikz-examples/max-distance-label/max-distance-label-good.png",
        "training-history/tikz-examples/max-distance-label/max-distance-label.md",
        "training-history/tikz-examples/regular-tree/regular-tree-board.jpg",
        "training-history/tikz-examples/regular-tree/regular-tree.md",
        "training-history/tikz-examples/slide-build-up/slide-build-up.jpg",
        "training-history/tikz-examples/slide-build-up/slide-build-up.md",
        "training-history/tikz-examples/slide-callout-table/slide-callout-table-good.png",
        "training-history/tikz-examples/slide-callout-table/slide-callout-table-slide.jpg",
        "training-history/tikz-examples/slide-callout-table/slide-callout-table.md",
        "training-history/tikz-examples/slide-color-fidelity/1-slide.png",
        "training-history/tikz-examples/slide-color-fidelity/2-bad-good.png",
        "training-history/tikz-examples/slide-color-fidelity/slide-color-fidelity.md",
        "training-history/tikz-examples/slide-copy-styling/slide-copy-styling-good.png",
        "training-history/tikz-examples/slide-copy-styling/slide-copy-styling-slide.png",
        "training-history/tikz-examples/slide-copy-styling/slide-copy-styling.md",
        "training-history/tikz-examples/slide-framed-definition/slide-framed-definition-good.png",
        "training-history/tikz-examples/slide-framed-definition/slide-framed-definition-slide.jpg",
        "training-history/tikz-examples/slide-framed-definition/slide-framed-definition.md",
        "training-history/tikz-examples/slide-shape-meaning/slide-shape-meaning.jpg",
        "training-history/tikz-examples/slide-shape-meaning/slide-shape-meaning.md",
        "training-history/tikz-examples/using_colors/using_colors_for_didactic_enhancement_cantor_bernstein_schroeder_thm.md",
        "training-history/board-transcription-examples/section_headers/example-1/sectioning_example1-continued.jpg",
        "training-history/board-transcription-examples/section_headers/example-1/sectioning_example1.jpg",
        "training-history/board-transcription-examples/section_headers/example-1/sectioning_example1.md",
        "training-history/board-transcription-examples/section_headers/example-2/section_headers_example_2.md",
        "training-history/board-transcription-examples/section_headers/example-2/section_headers_example_2_part1.jpg",
        "training-history/board-transcription-examples/section_headers/example-2/section_headers_example_2_part2.jpg",
        "training-history/board-transcription-examples/section_headers/example-2/section_headers_example_2_part3.jpg",
        "training-history/board-transcription-examples/section_headers/example-2/section_headers_example_2_part4.jpg",
        "training-history/conventions/workflow/transcription_endings_1/transcription_endings.jpg",
        "training-history/conventions/workflow/transcription_endings_1/transcription_endings.md",
        "training-history/conventions/workflow/transcription_ending_2_speech_length/beginning_of_part_2.jpg",
        "training-history/conventions/workflow/transcription_ending_2_speech_length/end_of_part_1.jpg",
        "training-history/conventions/workflow/transcription_ending_2_speech_length/workflow2.md",
        "training-history/didactic_examples/analysis2/cramped_math/2-sphere.md",
        "training-history/didactic_examples/analysis2/gauss–green–stokes-divergence-theorem/gauss-green-stokes-divergence-theorem1.md",
    ];

    /// <summary>[AI Context] Absolute paths of <see cref="SystemInstructionFiles"/>.</summary>
    public static string[] SystemInstructionPaths => ToAbsolute(SystemInstructionFiles);

    /// <summary>[AI Context] Absolute paths of <see cref="HistoryFiles"/>.</summary>
    public static string[] HistoryPaths => ToAbsolute(HistoryFiles);

    private static string[] ToAbsolute(IEnumerable<string> relative) =>
        [.. relative.Select(path => Path.GetFullPath(Path.Combine(Root, path)))];

    /// <summary>
    /// [AI Context] Files under <see cref="Root"/> that the lists do not name, so they are not sent: usually a
    /// prompt file added to the repository but not here. Skips archive/ (kept out on purpose) and the files with
    /// an ignored- prefix (the fidelity montage's sources, retired montages). This walks the folders only to compare; it loads nothing.
    /// Empty when <see cref="Root"/> does not exist on this machine.
    /// [Human] Dateien im Prompt-Ordner, die nicht in der Liste stehen und darum nicht geladen werden.
    /// </summary>
    public static List<string> FindUnlistedFiles() {
        if (!Directory.Exists(Root)) return [];
        var listed = new HashSet<string>(SystemInstructionPaths.Concat(HistoryPaths), StringComparer.OrdinalIgnoreCase);
        string archive = Path.Combine(Root, "archive") + Path.DirectorySeparatorChar;
        return [.. Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Where(file => !file.StartsWith(archive, StringComparison.OrdinalIgnoreCase))
            .Where(file => !HistoryFileResolver.IsIgnoredPromptFile(file))
            .Where(file => !listed.Contains(file))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// [AI Context] Warns about every file <see cref="FindUnlistedFiles"/> finds, at the start of a Stage 1 session.
    /// [Human] Warnt vor Prompt-Dateien, die nicht in der Liste stehen.
    /// </summary>
    public static void WarnAboutUnlistedFiles() {
        var unlisted = FindUnlistedFiles();
        if (unlisted.Count == 0) return;
        Ui.Warn($"{unlisted.Count} Datei(en) im Prompt-Ordner stehen nicht in Stage1PromptFiles und werden nicht geladen:", "Stage1PromptFiles");
        foreach (var file in unlisted) {
            Ui.Detail($"- {Path.GetRelativePath(Root, file)}");
        }
    }
}
