using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading;
using System.Threading.Tasks;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.Media;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] The FFmpeg "producer" half of the producer/consumer pipeline shared verbatim
/// between the AI Studio and Vertex extraction sessions: chronological sort, per-file cache
/// validation (including the incomplete/corrupt-cache guard), speed/FPS preprocessing, splitting,
/// and part renaming. Feeds a bounded channel so the Gemini "consumer" can process one video while
/// FFmpeg prepares the next.
/// [Human] Der FFmpeg-Produzent der Fließband-Pipeline: sortiert Dateien chronologisch, prüft den
/// Cache, verarbeitet und splittet Videos, und schreibt das Ergebnis in den gemeinsamen Kanal.
/// </summary>
public static partial class VideoSegmentProducer {
    /// <summary>
    /// [AI Context] Completes <paramref name="writer"/> on every exit, so a consumer reading the channel
    /// never waits forever on a producer that threw; the exception itself surfaces where the caller
    /// awaits this task. <paramref name="cancellationToken"/> lets a consumer that stopped early release
    /// a producer blocked on the full channel.
    /// [Human] Schließt den Kanal immer ab, auch bei einem Fehler, damit der Verbraucher nicht hängen bleibt.
    /// </summary>
    public static async Task RunAsync(string[] files, ChannelWriter<PreparedVideo> writer, IAutoExtractionConfig config, CancellationToken cancellationToken = default) {
        try {
            await ProduceAsync(files, writer, config, cancellationToken);
        }
        finally {
            writer.TryComplete(); // Signalisiert dem Fließband: "Feierabend, es kommen keine Videos mehr."
        }
    }

    private static async Task ProduceAsync(string[] files, ChannelWriter<PreparedVideo> writer, IAutoExtractionConfig config, CancellationToken cancellationToken) {
        double speed = config.SpeedMultiplier;
        // Chronologisch aufsteigend sortieren anhand des Dateinamens und der Woche
        files = [.. files.OrderChronologically()];

        foreach (var rawFile in files) {
            cancellationToken.ThrowIfCancellationRequested();
            string file = ExtractionHelpers.ResolveCompressedVariantIfExists(rawFile);
            string baseName = ExtractionHelpers.ComputeOutputFolderName(file);
            // Create a file-specific output folder within the main target folder
            string targetRoot = ExtractionHelpers.OutputRoot(config);
            string fileSpecificOutputFolder = Path.Combine(targetRoot, baseName);
            if (!Directory.Exists(fileSpecificOutputFolder)) {
                Directory.CreateDirectory(fileSpecificOutputFolder);
            }
            // Create a file-specific temporary folder inside the file-specific output folder
            string tmpFolderForFile = Path.Combine(fileSpecificOutputFolder, "tmp");
            if (!Directory.Exists(tmpFolderForFile)) {
                Directory.CreateDirectory(tmpFolderForFile);
            }

            string expectedProcessedVideoPath = Path.Combine(tmpFolderForFile, $"{baseName}-speed-{speed.ToString(CultureInfo.InvariantCulture)}-compressed.mp4");
            // Decided once per file: the cache branch below must agree with how the cached parts were cut
            bool isPreCompressed = await ExtractionHelpers.IsAlreadyCompressedAsync(file);

            // Audio extraction was moved to the Consumer loop to run in parallel with API calls

            // Removed dateStr from filename pattern for caching to work across days for 2-hour window
            var cachedParts = Directory.GetFiles(tmpFolderForFile, $"{baseName}-part*.mp4").ToList();

            double fullOriginalVideoDuration = await FfmpegToolkit.GetVideoDurationAsync(file); // Get original video duration
            int expectedParts = config.NumberOfParts.Resolve(fullOriginalVideoDuration);
            TimeSpan cacheDuration = TimeSpan.FromHours(48); // Set cache duration to 48 hours (2 days)
            bool useCache = false;

            if (cachedParts.Count > 0) {
                var fileInfo = new FileInfo(cachedParts[0]);
                if ((DateTime.Now - fileInfo.LastWriteTime) <= cacheDuration) {
                    // [AI Context] Defend against incomplete caches from interrupted FFmpeg runs, and against
                    // stale caches left over from a run with a different NumberOfParts (split geometry only
                    // matches the exact part count it was produced with). We also check if the files are
                    // actually valid (not 0 bytes).
                    // [Human] Wenn ein alter Lauf abgebrochen ist, liegen vielleicht nur 1-2 Teile im Cache, oder sie sind 0 Bytes groß. Das wird hier verhindert!
                    bool allFilesValid = true;
                    foreach (var cachedPartFile in cachedParts) {
                        if (new FileInfo(cachedPartFile).Length < 1024) { // less than 1KB is definitely invalid for a video
                            allFilesValid = false;
                            break;
                        }
                    }

                    if (cachedParts.Count == expectedParts && allFilesValid) {
                        useCache = true;
                    }
                    else {
                        Ui.Warn($"Ignoriere unvollständigen oder defekten Cache für '{Path.GetFileName(file)}' ({cachedParts.Count} Teil(e), valid: {allFilesValid}). FFmpeg wird neu gestartet...", "Cache");
                        foreach (var stalePartFile in cachedParts) {
                            try {
                                System.IO.File.Delete(stalePartFile);
                            }
                            catch (Exception ex) {
                                Ui.Detail($"[Cache Bereinigung] Veraltete Datei '{Path.GetFileName(stalePartFile)}' konnte nicht gelöscht werden: {ex.Describe()}");
                            }
                        }
                    }
                }
            }

            if (useCache) {
                Ui.Blank();
                Ui.Detail($"FFmpeg übersprungen für '{file}'. Verwende folgende gecachte Dateien (jünger als 48h):", "Cache");
                cachedParts.Sort();

                // Determine the duration of the video that was actually split (either pre-compressed input or processed output)
                double speedVideoDuration;
                if (isPreCompressed) {
                    // If the input file was pre-compressed, its duration is what was effectively "processed" and split.
                    speedVideoDuration = await FfmpegToolkit.GetVideoDurationAsync(file);
                }
                else {
                    // Otherwise, it was the output of ProcessGeneralVideoAsync that was cached.
                    speedVideoDuration = await FfmpegToolkit.GetVideoDurationAsync(expectedProcessedVideoPath);
                }
                double segmentLengthForCached = (speedVideoDuration > 0) ? (speedVideoDuration + (expectedParts - 1) * config.OverlapSeconds) / expectedParts : 0;
                var cachedPartsWithTimes = new List<VideoSegment>();
                for (int i = 0; i < cachedParts.Count; i++) {
                    double startTime = (segmentLengthForCached > 0 && i > 0) ? i * (segmentLengthForCached - config.OverlapSeconds) : 0;
                    Ui.Detail($"- {cachedParts[i]} (Est. Start: {startTime.ToString("F2", CultureInfo.InvariantCulture)}s)");
                    cachedPartsWithTimes.Add(new VideoSegment(cachedParts[i], startTime));
                }

                await writer.WriteAsync(new PreparedVideo(file, fileSpecificOutputFolder, tmpFolderForFile, cachedPartsWithTimes, true, fullOriginalVideoDuration), cancellationToken);
                continue;
            }

            // Determine if the file is already processed in tmp
            bool isProcessedVideoCached = System.IO.File.Exists(expectedProcessedVideoPath) &&
                new FileInfo(expectedProcessedVideoPath).Length > 1024 &&
                (DateTime.Now - new FileInfo(expectedProcessedVideoPath).LastWriteTime) <= cacheDuration;

            string? videoToSplit;
            if (isPreCompressed) {
                Ui.Blank();
                string why = ExtractionHelpers.IsPreCompressed(file) ? "als komprimiert markiert" : "bereits komprimiert (niedrige Framerate)";
                Ui.Detail($"{Path.GetFileName(file)} ist {why}. Überspringe Vorverarbeitung, starte direkt Splitting...", "FFmpeg Producer");
                videoToSplit = file; // Use the original file directly for splitting
            }
            else if (isProcessedVideoCached) {
                Ui.Blank();
                Ui.Detail($"Bereits vorverarbeitetes Video in tmp gefunden ({Path.GetFileName(expectedProcessedVideoPath)}). Überspringe Vorverarbeitung, starte direkt Splitting...", "FFmpeg Producer");
                videoToSplit = expectedProcessedVideoPath;
            }
            else {
                Ui.Blank();
                Ui.Detail($"Starte Vorverarbeitung für {Path.GetFileName(file)} ({speed}x Speed, 1 FPS, Mono)...", "FFmpeg Producer");
                videoToSplit = await FfmpegToolkit.ProcessGeneralVideoAsync(file, tmpFolderForFile, speedMultiplier: speed, fps: 1, downmixToMono: true, scaleTo720p: false, overwrite: true, preset: config.FfmpegPreset);
                if (videoToSplit == null) {
                    Ui.Error($"Vorverarbeitung für {Path.GetFileName(file)} fehlgeschlagen. Überspringe Datei.", "FFmpeg Producer");
                    continue;
                }
            }

            Ui.Blank();
            Ui.Detail($"Starte Splitting für {Path.GetFileName(videoToSplit)} in {expectedParts} Teile ({config.OverlapSeconds}s Overlap)...", "FFmpeg Producer");
            var rawPartsWithTimes = await FfmpegToolkit.ProcessSplitVideoAsync(videoToSplit, tmpFolderForFile, parts: expectedParts, overlapSeconds: config.OverlapSeconds, downmixToMono: false, streamCopy: true, overwrite: true, preset: config.FfmpegPreset);

            if (rawPartsWithTimes.Count > 0) {
                List<VideoSegment> safePartsWithTimes = [];
                for (int i = 0; i < rawPartsWithTimes.Count; i++) {
                    string safePartPath = Path.Combine(tmpFolderForFile, $"{baseName}-part{i + 1}.mp4");

                    if (!string.Equals(rawPartsWithTimes[i].FilePath, safePartPath, StringComparison.OrdinalIgnoreCase)) {
                        if (System.IO.File.Exists(safePartPath)) System.IO.File.Delete(safePartPath);
                        System.IO.File.Move(rawPartsWithTimes[i].FilePath, safePartPath);
                    }

                    safePartsWithTimes.Add(new VideoSegment(safePartPath, rawPartsWithTimes[i].StartTimeSeconds));
                }
                await writer.WriteAsync(new PreparedVideo(file, fileSpecificOutputFolder, tmpFolderForFile, safePartsWithTimes, false, fullOriginalVideoDuration), cancellationToken);
            }
        }
    }
}
