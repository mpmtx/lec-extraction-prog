# Command reference

Every command accepts the [global options](#global-options) below. Anything marked
**paid** issues billable API requests; everything else is local and free.

---

## `lecx run` — paid

The whole pipeline: preprocess → segment → transcribe → refine → PDF. This is the
one-liner. It works because the pipeline already chains internally — a successful
extraction constructs the refinement session itself.

```bash
lecx run --video "D:/lecture-videos/analysis2/02-19-2026-thursday-week1-....mp4"
lecx run --folder "D:/lecture-videos/analysis2" --from 03-30-2026
```

| Option | Meaning |
|---|---|
| `--video <file>` | A single video file to work on |
| `--folder <dir>` | Every `.mp4` in this folder. Defaults to the configured source folder |
| `--from <text>` | Skip videos before the first whose filename contains this, in chronological order |
| `--limit <n>` | At most n videos, in chronological order |
| `--stop-after extract` | Transcribe only; skip refinement and PDF |
| `--resume-window <hours>` | How long a finished `.tex` part stays reusable, measured on the newest of `-partN.tex` and its `-copy-N` files. Default **2** |
| `--resume` | Reuse finished `.tex` parts whatever their age (an unlimited `--resume-window`) |
| `--force` | Ignore existing `.tex` parts and re-request every segment |
| `--clean-tmp` | Delete the lecture's `tmp/` after a successful run |
| `--out <dir>` | Target folder. Defaults to `<source>/extracted_output` |
| `--parts <n\|auto>` · `--overlap <s>` · `--speed <x>` · `--preset <p>` | Segment geometry and FFmpeg preset. `auto` = one part per 22.5 min of video: a 45-min lecture gets 2, a 90-min double lecture 4 |
| `--model <id>` · `--profile <n>` | Model and API-key profile for this run only |
| `--fps <rate>` | How many frames per second Gemini samples from each uploaded part (`GoogleVideoFps`, AI Studio), e.g. `0.25`, `0.3333333`, `1`. Aliases `--google-video-fps`, `--video-fps`. `plan` reports it as `googleVideoFps` |
| `--skip-token-counting` | Skip the pre-flight token count, which spends free-tier quota |
| `--file-activation-delay <s>` | Seconds to wait after an uploaded file becomes active. Default 0 |
| `--max-output-tokens <n>` | Output-token budget per request, this run only, for Stage 1 and refinement steps 1-3 (step 3's value also holds for the PDF repair rounds). Only Hydrogen (`gemini-4-*`) takes more than 65,536; an older model is held at 65,536 |
| `--thinking-level <level>` | Thinking level for the same stages, this run only: `MINIMAL`, `LOW`, `MEDIUM`, `HIGH` or `XHIGH`. An older model gets `HIGH` for `XHIGH`; Hydrogen takes `LOW` to `XHIGH` (it rejects `MINIMAL`) |
| `--thoughts <mode>` | Thought summaries for every stage, see [`lecx thoughts`](#lecx-thoughts--free) |

`batch` accepts the same selection and segment flags (`--from`, `--limit`, `--parts`, `--fps`,
`--stop-after`, `--thoughts`, `--force`, `--max-output-tokens`, `--thinking-level`) and passes them to every worker.

Exits `0` on full success, `6` if some videos succeeded and others failed, `4` if the
key does not resolve (checked **before** any request).

## `lecx extract run` — paid

Identical to `run` minus `--stop-after`, and it never chains into refinement. Produces
the per-part `.tex` files and stops.

## `lecx youtube` — paid

Menu entry 3 (YouTube) without the menu. The model reads the video straight from YouTube
(`Part.FromUri`), one request per timestamp fragment, then the usual refinement and PDF. Nothing
is downloaded, cut or uploaded.

```bash
lecx youtube --url "https://www.youtube.com/watch?v=IPfi49IbHNU" --duration 1:35:00 --name my-lecture --profile 3 --dry-run
```

| Option | Meaning |
|---|---|
| `--url <link>` | Required. `watch?v=`, `youtu.be/`, `shorts/`, `live/` or a bare id. A playlist's `list=`/`index=` are dropped, so only that one video is sent |
| `--duration <d>` | Required. Minutes (`90`) or `HH:MM:SS` / `MM:SS`. The app cannot look it up, and the fragments are cut from it |
| `--name <name>` | Output name. The folder is `<out>/step1-<name>/`, the parts `step1-<name>-partN.tex`. Default `youtube-<video id>` |
| `--parts <n>` | Number of fragments. Default one per started 45 minutes; they overlap by `--overlap` (config `OverlapSeconds`) |
| `--out` · `--overlap` · `--model` · `--profile` · `--max-output-tokens` · `--thinking-level` · `--stop-after extract` · `--thoughts` | As for `run` |

Differences from `run`: **no resume** (a rerun requests every fragment again), no `plan` support
(use `--dry-run`, which prints the fragments and the key), and `--fps` has no effect. A fragment
without an answer is left out of the combined file, the refinement still runs, and the command
exits `6`.

## `lecx plan` — free

Reports what a run would do and calls nothing. Same selection options as `run`.

```bash
lecx plan --folder "D:/lecture-videos/analysis2" --json
```

Payload includes `videoCount`, `pendingRequests`, `resumableSegments`,
`apiKeyResolves`, `resumeWindowHours`, a per-video breakdown, and `warnings`.
Exits `4` when the active profile's key is unset.

`numberOfParts` is the setting (`"4"` or `"auto"`). Under `auto` the plan probes each
video's length with ffprobe and `segmentsPerVideo` is absent — each video's own
`segmentCount` is the number to read.

## `lecx media` — free

| Command | Does |
|---|---|
| `media probe --input <mp4>` | Duration, size, parsed lecture date, resulting segment geometry |
| `media segment --input <mp4>` | Compresses and slices into overlapping segments |
| `media audio --input <mp4>` | Extracts the mono AAC track used for timestamp correction into `<target>/<lecture>/`, where `extract run` and `refine run` look for it. Reuses a track already there |

`media segment --json` emits a `PreparedVideo` — the same record the pipeline passes
internally from its FFmpeg producer to its Gemini consumer:

```json
{
  "sourceVideoPath": "...", "outputFolder": "...", "tempFolder": "...",
  "segments": [ { "filePath": "...", "startTimeSeconds": 0 } ],
  "cameFromCache": true, "sourceDurationSeconds": 5036
}
```

`probe` exits `1` if ffprobe cannot read the file, rather than reporting zeroes.

## `lecx refine run` — paid

```bash
lecx refine run --tex out/step1-lecture.tex            # all three steps
lecx refine run --tex out/step1-lecture.tex --step 2   # only step 2
lecx refine run --tex out/step1-lecture.tex --step 1 --through-end
```

| Option | Meaning |
|---|---|
| `--tex <file>` | **Required.** The file to work on |
| `--step 1\|2\|3` | One step only: merge/timestamps, speech, final polish. Omit for all three |
| `--through-end` | With `--step`, continue through the remaining steps and compile the PDF |
| `--audio <file>` | Audio for timestamp correction. Defaults to `*_audio.aac` beside the `.tex` |
| `--overlap <s>` | Overlap the segments were cut with, for step 1. Defaults to the extraction config's `OverlapSeconds` |
| `--profile <n>` | API-key profile for this run only: the number from `lecx config profiles` (or a key name such as `latex-refinement`). That key is then looked up by name in the refinement config's own key list, whose order may differ (before 2026-10-30 the number was an index into that list, so `--profile 4` ran on profile 0's key). Exits `2` if the key is not in that list, `4` if it is unset. Switch it when a key hits its rate limit |
| `--model <id>` | Model for every step of this run only (e.g. `gemini-3.7-flash` when a key's daily quota on the configured model is used up: the free-tier quota counts per model) |
| `--max-output-tokens <n>` · `--thinking-level <level>` | As for `run`, for every step of this run and the PDF repair that follows it (which uses step 3's settings) |

Step 1 reads the part count from the document itself (its `% --- TEIL n` separators), so
it is right for `--parts auto` and for a config changed since the extraction.

The input of each step is the previous step's output:

| Step | Reads | Writes |
|---|---|---|
| 1 | `step1-<name>-all-offset.tex` | `step2-<name>-offset-merged.tex` |
| 2 | `step2-<name>-offset-merged.tex` | `step3-<name>-offset-speech_refined.tex` |
| 3 | `step3-<name>-offset-speech_refined.tex` | `step4-<name>-offset-final.tex` |

Exits `6` when a step failed — the log on stderr names which.

## `lecx pdf compile` — paid unless `--fix-loop 0`

Runs step 4 alone. **Not free by default**: the repair path sends the failed document and
its compile log to the model.

```bash
lecx pdf compile --tex final.tex --fix-loop 0   # local only
```

`--profile <n>` selects the refinement key the repair loop uses, as for `refine run`.

## `lecx batch` — paid

One child process per worker, each with its own API-key profile and therefore its own
rate-limit budget. See [parallel.md](parallel.md) for why this is processes and not
threads.

```bash
lecx batch --folder "D:/lecture-videos/analysis2" \
           --workers "profile=1:model=gemini-3.5-flash,profile=2"
```

| Option | Meaning |
|---|---|
| `--workers <spec>` | **Required.** Comma-separated `profile=N:model=X` entries |
| `--log-folder <dir>` | Per-worker logs. Defaults to `<target>/batch-logs` |

Plus the selection and geometry options from `run`. Two workers on the same profile are
**refused** — they would share one rate-limit budget and finish slower than a single
worker.

## `lecx config` — free

| Command | Does |
|---|---|
| `config list [--section <name>]` | The effective configuration |
| `config get <Section.Path>` | One value. `AiStudioAutoExtractionConfig.CurrentModel` or `...Paths.SourceFolder` |
| `config set <Section.Path> <value>` | Writes one scalar value |
| `config models` | Selectable models per backend |
| `config profiles` | Key profiles and whether each resolves. **Exits 4** if the active one does not |
| `config folders` | Configured source and target folders |

`config get`/`set` accept both the nested path and its flat alias — `Paths.SourceFolder`
and `SourceFolder` reach the same value. `set` handles scalars only; arrays have no
unambiguous command-line spelling.

## `lecx chat`

Starts an interactive chat REPL directly from the command line with Google AI Studio or Vertex AI.

```bash
lecx chat
lecx chat --model gemini-3.6-flash --profile 1 --skip-setup
lecx chat --backend vertex
```

| Option | Meaning |
|---|---|
| `--backend <aistudio\|vertex>` | Backend to use. Default **aistudio** |
| `--model <id>` | Model override for this session |
| `--profile <n>` | API-key profile index for AI Studio |
| `--skip-setup` | Skip interactive wizard prompts and enter chat directly |
| `--system-prompt <file>` | Path to custom system instruction file |
| `--no-system-prompt` | Do not load any system instruction file |
| `--no-history` | Do not preload history attachments |

## `lecx status` — free

Progress per lecture folder: how far it got, the next stage, PDF size, `tmp/` size. One row
per lecture, labelled `2026-02-16 Mo W1` (date, weekday from the name, week).

```bash
lecx status --folder "D:/lecture-videos/analysis2/extracted_output" --json
```

`--folder <dir>` (a lecture folder or a folder of them; defaults to the configured target
folder) · `--video <name>` (one lecture). The JSON keeps the per-stage booleans and adds
`nextStep` (`Transcribe`, `Merge`, `Speech`, `Polish`, `Pdf`, `Done`, `NotStarted`) and
`nextInput` (the file that stage reads).

## `lecx finish` — paid

Brings every **started** lecture to a PDF from wherever it stopped. For each folder it runs
the one command that continues it, as if typed:

| Folder holds | Runs |
|---|---|
| some parts, no combined `step1-*-all*.tex` | `run --video <video> --resume --out <root>` (reuses every part, however old). With `EnableImplicitPrefixCacheWarmup` on, this sends the warm-up handshakes even when every part is already there (2026-10-24: two requests on profile 3 just to combine); turn the warm-up off and it combines without any request |
| the combined file | `refine run --tex <newest step1-*-all-offset*.tex>` |
| `step2-*merged*.tex` | `refine run --tex <it> --step 2 --through-end` |
| `step3-*speech_refined*.tex` | `refine run --tex <it> --step 3 --through-end` |
| `step4-*final*.tex`, no PDF | `pdf compile --tex <it>` |

The newest file of a stage wins, `-copy-N` re-runs included; the `-main.tex` copies a compile
leaves behind are skipped. So are a PDF repair's leftovers ([../latex-auto-fix.md](../latex-auto-fix.md)):
the backup `-orig.tex` / `-origN.tex` with its `.pdf`, and the Antigravity candidates `-tryN.tex`,
`-tryN-rejected.tex`, `-tryN.pdf`. The next command reads the step's own file (`pdf compile`
starts again from `step4-*-final.tex`), and none of their PDFs counts as the lecture's PDF. Before
2026-11-02 the backup, written seconds after the step's file with the same text, was the newest and
was read instead (grundstrukturen 04-21-25, and 05-05-25 / 05-12-25 at step 3). Folders without any
`.tex` (not started) are left to `run`.

A `-truncated.tex` file is never a stage's output. A refinement step whose answer broke off (eight
`503`s on a continue, say) saves the fragment as `<its output>-truncated.tex` and fails, so `finish`
(and `status`, `plan`, which read the same scan) treats that step as not done and redoes it from its
own input. A folder with `step2-*-merged.tex` and only `step3-*-speech_refined-truncated.tex` gets
`refine run --tex <the merged file> --step 2 --through-end`; a failed merge is redone from the
combined file. Until 2026-11-02 the fragment counted as the finished step and the next step was
built on it, losing the end of the lecture. The fragment stays on disk for inspection; a successful
redo writes the step's normal name beside it.

```bash
lecx finish --folder "D:/lecture-videos/analysis2/extracted_output" --dry-run   # table + exact commands, free
lecx finish --folder "<one lecture folder>"
```

| Option | Meaning |
|---|---|
| `--folder` · `--video` | Which lectures, as for `status` |
| `--source <dir>` | Where the videos are, for unfinished transcriptions. Defaults to the configured source folder, then two levels up from the lecture folder |
| `--limit <n>` | At most n lectures, chronologically |
| `--clean-tmp` | Delete a lecture's `tmp/` (FFmpeg segments, ~0.5 GB) once it has its PDF |
| `--profile` · `--model` · `--max-output-tokens` · `--thinking-level` · `--thoughts` | Passed to every command it runs |

Exits `0` when every lecture reached a PDF, `6` when some did, otherwise the first failing
command's code. A `4` (key) or `3` (prompt) stops it, since every later lecture would fail
the same way.

## `lecx clean` — free, deletes files

| Option | Meaning |
|---|---|
| `--tmp` | Delete the `tmp/` folders (FFmpeg segments and cache) |
| `--copies` | Keep the newest of duplicate `-copy-N.tex` files, archive the older ones |
| `--pdfs` | Keep one finished PDF per lecture, `<lecture>.pdf` (created from the newest final copy if missing), and archive byte-identical copies. Runs before 2026-10-15 wrote each PDF twice. Combine with `--dry-run` to preview |
| `--folder <dir>` · `--video <name>` | Which lectures, as for `status` |
| `--force` | Also touch files changed in the last 10 minutes |

## `lecx quota` — free

API-key profiles, which env variable each maps to, and the daily Free-Tier quota use.

## `lecx thoughts` — free

Reads the `<output>-thoughts.md` files the stages write when `ThoughtSummaries.Enabled`
and `ToSeparateFile` are on (config, or `--thoughts` on the run). Gemini's condensed
summary of its reasoning, one section per request (first request and each continue),
with thinking tokens, output tokens and finish reason.

```bash
lecx thoughts list --folder "<output-folder>" --json
lecx thoughts show --folder "<lecture-folder>" --stage speech --request 1
lecx thoughts show "<lecture-folder>/step3-<name>-offset-speech_refined.tex" --json
```

| Option | Meaning |
|---|---|
| `[path]` (`show`) | A thoughts file, the `.tex` output it belongs to (needs not exist), or a folder |
| `--folder <dir>` · `--video <name>` | Where to look. Defaults to the configured target folder |
| `--stage <key>` | `extract-partN`, `merge`, `speech`, `final`, `pdf-repair-N`; a prefix (`extract`, `pdf`) matches all. A number matches only itself: `extract-part1` is not parts 10-19 |
| `--request <n>` (`show`) | Only that request, every attempt of it |

`show` prints the newest match; exits `2` when nothing matches. Given a `.tex` with a
header, it opens the thoughts file that header names (`Thought summaries: ...`), so a
re-run step's `-copyN` output finds its own file; a header without that line means the
output has none. Without a header it takes the newest `<stem>-thoughts*.md` beside it.

A request sent again after an empty answer appears as `Request N · attempt 2`; a request
the stage gave up on mid-stream has finish reason `ABORTED` and 0 tokens (not reported).

**`--thoughts on|off|all|console,log,file`** on `run`, `extract run`, `refine run`,
`pdf compile` and `batch` turns the summaries on or off for every stage of that run
(not saved). `log` = the stage's existing log, `file` = the `-thoughts.md` file.

## `lecx ask`

**Not implemented.** Registered so the intended shape is visible; refuses with exit `2`.

---

## Global options

| Option | Meaning |
|---|---|
| `--json` | Payload to **stdout**, all logging to **stderr** |
| `--dry-run` | Resolve everything and report; issue no paid request |
| `--yes` / `-y` | Accept defaults for questions that have one. Does **not** answer menus |
| `--save-config` | Allow the run to persist config changes. **Off by default** |
| `--config-dir <dir>` | Where the `*Config.json` live. Also suppresses the working-copy mirror write |
| `--quiet` / `-q` | Errors only |

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Unhandled exception |
| 2 | Usage / bad arguments |
| 3 | An unattended prompt had no answer |
| 4 | Configuration or credentials |
| 5 | API exhausted its retries |
| 6 | Partial success — some items succeeded, others failed |
