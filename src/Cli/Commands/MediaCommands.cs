using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.Extraction.Model;
using LectureExtraction.Media;

namespace LectureExtraction.Cli.Commands;

/// <summary>
/// The local FFmpeg stages, standalone. Nothing here calls an API or costs anything, which is why
/// the JSON shapes and exit-code behaviour are settled on these commands first.
///
/// <para><c>segment</c> emits a <see cref="PreparedVideo"/> - the record the extraction pipeline
/// already passes from its FFmpeg producer to its Gemini consumer. Reusing it rather than
/// inventing a manifest format means the hand-off from this command into <c>run --prepared</c> is
/// the same hand-off the pipeline makes internally.</para>
/// </summary>
public static class MediaCommands {
    private static readonly Option<string> Input = new("--input", "-i") {
        Description = "The source video file.",
        Required = true
    };

    public static Command Build() {
        return new Command("media", "Local FFmpeg stages. No API calls, no cost.") {
            BuildProbe(),
            BuildSegment(),
            BuildAudio()
        };
    }

    private static Command BuildProbe() {
        var command = new Command("probe", "Report a video's duration, size and parsed lecture date.") { Input, ExtractionOptions.Parts, ExtractionOptions.Overlap, ExtractionOptions.Model };

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            string input = parseResult.GetValue(Input)!;

            if (!File.Exists(input)) {
                return MissingInput(input);
            }

            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);

            double duration = await FfmpegToolkit.GetVideoDurationAsync(input);
            var lecture = VideoDateParser.Parse(input);
            var info = new FileInfo(input);

            int resolvedParts = config.NumberOfParts.Resolve(duration, config.CurrentModel);

            // The same geometry the splitter uses, so a caller can see the segment count and the
            // request count that follows from it before committing to a run.
            double segmentLength = duration > 0
                ? (duration + ((resolvedParts - 1) * config.OverlapSeconds)) / resolvedParts
                : 0;

            var payload = new {
                file = Path.GetFullPath(input),
                durationSeconds = duration,
                sizeBytes = info.Length,
                lecture = new {
                    isValid = lecture.IsValid,
                    date = lecture.Date == DateTime.MinValue ? null : lecture.Date.ToString("yyyy-MM-dd"),
                    weekNumber = lecture.WeekNumber,
                    weekday = lecture.WeekdayEnglish
                },
                segments = new {
                    count = resolvedParts,
                    overlapSeconds = config.OverlapSeconds,
                    estimatedLengthSeconds = Math.Round(segmentLength, 2)
                }
            };

            CliOutput.Payload(context, payload, () => {
                Ui.Step(Path.GetFileName(input));
                Ui.Detail($"Dauer:     {TimeSpan.FromSeconds(Math.Max(0, duration)):hh\\:mm\\:ss} ({duration:F1}s)");
                Ui.Detail($"Größe:     {info.Length / 1024.0 / 1024.0:F1} MB");
                Ui.Detail($"Vorlesung: {(lecture.IsValid ? lecture.GetFormattedContext() : "kein erkanntes Datums-/Wochen-Schema")}");
                Ui.Detail($"Segmente:  {resolvedParts} x ~{segmentLength:F0}s ({config.OverlapSeconds}s Overlap)");
            });

            // A duration of -1 means ffprobe could not read the file at all - a caller scripting
            // around this needs that to be a failure, not a report full of zeroes.
            return duration > 0 ? ExitCodes.Success : ExitCodes.Unexpected;
        });

        return command;
    }

    private static Command BuildSegment() {
        var command = new Command("segment", "Compress and slice a video into overlapping segments.") {
            Input, ExtractionOptions.Out, ExtractionOptions.Parts,
            ExtractionOptions.Overlap, ExtractionOptions.Speed, ExtractionOptions.Preset, ExtractionOptions.Model
        };

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            string input = parseResult.GetValue(Input)!;

            if (!File.Exists(input)) {
                return MissingInput(input);
            }

            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);
            EnsureTargetFolder(config, input);

            if (context.DryRun) {
                double duration = await FfmpegToolkit.GetVideoDurationAsync(input);
                int parts = config.NumberOfParts.Resolve(duration, config.CurrentModel);
                CliOutput.Payload(context, new {
                    file = Path.GetFullPath(input),
                    targetFolder = config.TargetFolder,
                    parts = parts,
                    overlapSeconds = config.OverlapSeconds,
                    speed = config.SpeedMultiplier,
                    preset = config.FfmpegPreset,
                    wouldRun = true
                }, () => {
                    Ui.Step("Dry run");
                    Ui.Detail($"Würde {Path.GetFileName(input)} in {parts} Teile schneiden ({config.OverlapSeconds}s Overlap, {config.SpeedMultiplier}x).");
                    Ui.Detail($"Ziel: {config.TargetFolder}");
                });
                return ExitCodes.Success;
            }

            var prepared = await ProduceAsync([input], config);
            if (prepared.Count == 0) {
                Ui.Error($"FFmpeg produced no segments for '{Path.GetFileName(input)}'.", "media segment");
                return ExitCodes.Unexpected;
            }

            CliOutput.Payload(context, prepared[0], () => RenderPrepared(prepared[0]));
            return ExitCodes.Success;
        });

        return command;
    }

    private static Command BuildAudio() {
        var command = new Command("audio", "Extract the mono AAC audio track used for timestamp correction.") { Input, ExtractionOptions.Out };

        command.SetAction(async (parseResult, _) => {
            var context = CliOptions.ReadContext(parseResult);
            string input = parseResult.GetValue(Input)!;

            if (!File.Exists(input)) {
                return MissingInput(input);
            }

            // The lecture's own output folder, the same one `segment` and the extraction write to:
            // that is where the extraction looks for an existing track before cutting its own, and
            // where `refine run` finds it beside the .tex. --out names the target root, as it does
            // for every other stage.
            var config = (AiStudioAutoExtractionConfig)ConfigSectionRegistry.Load(typeof(AiStudioAutoExtractionConfig));
            ExtractionOptions.Apply(config, parseResult);
            EnsureTargetFolder(config, input);
            string target = Path.Combine(config.TargetFolder, ExtractionHelpers.ComputeOutputFolderName(input));
            string audioPath = Path.Combine(target, $"{Path.GetFileNameWithoutExtension(input)}_audio.aac");

            // Same reuse rule as AudioTrackExtractor, so running this stage before `extract run` does
            // not make the extraction cut the track a second time, and a re-run does not add a copy.
            bool cached = File.Exists(audioPath) && new FileInfo(audioPath).Length >= 1024;

            if (context.DryRun) {
                CliOutput.Payload(context, new { file = Path.GetFullPath(input), audioPath, cameFromCache = cached, wouldRun = !cached },
                    () => Ui.Info(cached
                        ? $"Audio liegt bereits vor: {audioPath}"
                        : $"Würde Audio aus {Path.GetFileName(input)} nach {audioPath} extrahieren."));
                return ExitCodes.Success;
            }

            bool ok = true;
            if (cached) {
                Ui.Info($"Vorhandene Audio-Datei gefunden: {Path.GetFileName(audioPath)}. Überspringe Audio-Extraktion.", "Cache");
            }
            else {
                Directory.CreateDirectory(target);
                ok = await FfmpegToolkit.ExtractAudioAsAacAsync(input, target);
            }

            CliOutput.Payload(context, new { file = Path.GetFullPath(input), audioPath, cameFromCache = cached, extracted = ok },
                () => { /* ExtractAudioAsAacAsync already reports the destination it chose. */ });

            return ok ? ExitCodes.Success : ExitCodes.Unexpected;
        });

        return command;
    }

    /// <summary>
    /// Drains <see cref="VideoSegmentProducer"/>'s channel. The producer is written for the
    /// pipeline's producer/consumer pairing, so a standalone caller becomes the consumer rather
    /// than the producer growing a second entry point.
    /// </summary>
    private static async Task<List<PreparedVideo>> ProduceAsync(string[] files, IAutoExtractionConfig config) {
        var channel = Channel.CreateBounded<PreparedVideo>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        var producer = Task.Run(() => VideoSegmentProducer.RunAsync(files, channel.Writer, config));

        List<PreparedVideo> prepared = [];
        await foreach (var video in channel.Reader.ReadAllAsync()) {
            prepared.Add(video);
        }

        await producer;
        return prepared;
    }

    /// <summary>
    /// The producer writes into <c>config.TargetFolder</c>, which is empty by default - the
    /// extraction session fills it in from the source folder. A standalone media command has no
    /// session, so it applies the same rule here rather than writing to the process's cwd.
    /// </summary>
    private static void EnsureTargetFolder(AiStudioAutoExtractionConfig config, string input) {
        if (string.IsNullOrWhiteSpace(config.TargetFolder)) {
            string sourceFolder = Path.GetDirectoryName(Path.GetFullPath(input))!;
            config.TargetFolder = Path.Combine(sourceFolder, "extracted_output");
        }

        Directory.CreateDirectory(config.TargetFolder);
    }

    private static void RenderPrepared(PreparedVideo video) {
        Ui.Step(Path.GetFileName(video.SourceVideoPath));
        Ui.Detail($"Ausgabe: {video.OutputFolder}");
        Ui.Detail($"Segmente: {video.Segments.Count}{(video.CameFromCache ? " (aus Cache)" : "")}");
        foreach (var segment in video.Segments) {
            Ui.Detail($"  {Path.GetFileName(segment.FilePath)} @ {segment.StartTimeSeconds:F1}s");
        }
    }

    private static int MissingInput(string input) {
        Ui.Error($"Input file not found: '{input}'", "media");
        return ExitCodes.Usage;
    }
}
