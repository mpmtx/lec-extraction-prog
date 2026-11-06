# Driving this pipeline as an agent

Written for an AI agent (or any unattended script) operating `lecx`. If you read one
file, read this one.

The binary is `bin/Debug/net10.0/lec-extraction-prog.exe`. `lecx.cmd` in the repository
root is a shim for the same thing. **Running it with no arguments starts an interactive
menu that will block forever** — always pass a command.

---

## The five rules

### 1. `plan` before you spend

`lecx plan` issues no API call and costs nothing. It tells you how many videos matched,
how many requests are still pending, which model and key profile would be used, and
whether that key actually resolves.

```bash
lecx plan --folder "D:/lecture-videos/analysis2" --json
```

If `pendingRequests` is not the number you expected, your `--folder` or `--from` is
wrong. Find that out here, not four videos into a paid batch.

### 2. Never pass `--save-config`

The app rewrites its own `*Config.json` during a normal run. In CLI mode that writeback
is **off by default**, which is what keeps an unattended run from changing the settings
the human comes back to. `--save-config` turns it back on. You almost never want it.

If you need different settings for one run, pass them as flags (`--model`, `--profile`,
`--parts`). They apply to that run only.

### 3. Read the exit code, especially `6`

| Code | Meaning | What to do |
|---|---|---|
| 0 | Everything requested completed | — |
| 1 | Unhandled exception | Read stderr; this is a bug |
| 2 | Bad arguments | Fix the command line |
| 3 | A prompt had no answer | You hit an interactive question; supply the value as a flag |
| 4 | Config or credentials | An API key env var is unset — `lecx config profiles` |
| 5 | API gave up after retries | Rate limit or outage; retry later |
| **6** | **Partial success** | **Some videos produced output and others did not.** Do not treat as success |

`6` is the one that matters. A batch that transcribed four videos and failed the fifth
exits `6`, and the JSON payload names which.

### 4. Resume is time-limited — this is where money leaks

A finished `.tex` part is reused instead of re-requested **only if it is younger than two
hours**. Retry a failed batch three hours later and you silently pay for every part
again. A part that was requested again is saved as `-partN-copy-1.tex`; the newest of the
part's files is the one whose age counts and the one reused.

```bash
lecx plan --folder ... --resume-window 24 --json   # what would be reused with a 24h window
lecx run  --folder ... --resume-window 24          # actually use it
```

`--force` does the opposite: ignore everything on disk and re-request. Only use it when
you know the existing output is wrong.

### 5. Under `--json`, stdout is the payload and stderr is the log

```bash
lecx plan --folder ... --json 2>/dev/null | jq .pendingRequests
```

Without `--json`, human-readable logging goes to stdout and there is nothing to parse.

### For now: always pass `--thoughts file`

**Current standing instruction from the maintainer (2026-10-15):** every paid command (`run`,
`extract run`, `refine run`, `pdf compile`, `finish`, `batch`) gets `--thoughts file`, so each
stage leaves an `<output>-thoughts.md` beside its output. Not every stage has thought summaries
on in its config (step 2, speech, does not), and without the file a continue or an odd answer
can only be explained from token counts afterwards, not from the model's reasoning. It does
not change the request beyond asking for the summaries, and it is not saved to config.

```bash
lecx finish --folder "<lecture folder>" --thoughts file
lecx thoughts show --folder "<lecture folder>" --stage speech
```

This holds until the maintainer says otherwise.

---

## A safe working loop

```bash
# 1. Is the environment sane? (exits 4 if the active key is missing)
lecx config profiles --json

# 2. What would happen? (no cost)
lecx plan --folder "D:/lecture-videos/analysis2" --json > plan.json

# 3. Check the warnings before committing to it
jq -r '.warnings[]' plan.json

# 4. Rehearse the exact command (no cost)
lecx run --video "<one video>" --dry-run --json

# 5. Run it for real
lecx run --video "<one video>" --json
echo "exit=$?"
```

## Stopping between stages

Every stage is separately runnable, and each reads what the previous one wrote:

```bash
lecx media segment --input video.mp4 --out ./work --json   # local, free
lecx extract run   --video video.mp4 --out ./work --json   # .tex only, no refinement
lecx refine run    --tex ./work/<name>/step1-<name>.tex --json
lecx pdf compile   --tex <refined>.tex --fix-loop 0 --json # --fix-loop 0 keeps it free
```

`lecx run` does all of this in one go, because the pipeline already chains internally.
Use the separate commands when you want to inspect or intervene between stages.

## Finishing what is started

`lecx status` names each lecture's next stage; `lecx finish` runs it. Use `finish`, not
`run`, for any lecture that already has output — it picks the right step file and command
per folder (see [commands.md](commands.md#lecx-finish--paid)):

```bash
lecx status --folder "D:/lecture-videos/analysis2/extracted_output"
lecx finish --folder "D:/lecture-videos/analysis2/extracted_output" --dry-run   # free: table + exact commands
lecx finish --folder "D:/lecture-videos/analysis2/extracted_output" --limit 2
```

---

## Things that will surprise you

**A raw video and its `-speed-N-compressed` sibling are one lecture, and the compressed
one wins.** A folder often holds both `lecture.mp4` and `lecture-speed-1-compressed.mp4`.
The compression suffix is stripped when deriving the output folder, so both map to the
same folder and part filenames. You do **not** have to pick a variant, because the program
already does:

- `--folder` / `--from` / `--limit` (and so `plan`, `run`, `batch`) keep only the
  compressed file of each pair (`ExtractionHelpers.DeduplicatePreferCompressed`).
- `--video <raw.mp4>` is swapped for the compressed sibling in the same directory, with an
  info line `Verwende vorhandene komprimierte Datei`.
- FFmpeg then cuts the segments from that compressed file (`VideoSegmentProducer`).

So a pair can only collide if you hand both paths to a script yourself. `plan` still
reports `warnings` for that case. To see which file a run will really use, read the
`videos[].fileName` of `lecx plan --json` — not the filename you passed.

**A 1 FPS video is split as-is, whatever its name.** Before splitting, the producer
compresses a video unless it is already compressed (`ExtractionHelpers.IsAlreadyCompressedAsync`).
A `-compressed` / `-speed-N-compressed` name settles that without opening the file. Any other
name is probed with ffprobe, and an average frame rate of at most 2 FPS
(`MaxCompressedFrameRate`) counts as compressed: raw ETH recordings run at 25 FPS, every
compression keeps 1, whichever preset, CRF or tool made it. Only the name counts for
choosing between a raw file and its sibling above, so a 1 FPS file without the marker is
not preferred over a raw file of the same lecture; it just isn't compressed again. The log
says `bereits komprimiert (niedrige Framerate)` when the frame rate decided it.

**Run `lecx` from the repository root — otherwise `run` loses its PDF and corrupts step 3.**
Two files in the repository root are looked up relative to the *current directory*:

- The preambles (`PdfCompilation.PreamblePath*`, relative): `pdf-preamble-de.tex` for a German
  lecture, `pdf-preamble-en.tex` for an English one, `pdf-preamble.tex` when the language is
  unknown. The transcript's `% Primary Language:` line decides (else its words); the log names the
  choice as `Preamble: … (Sprache: …)`. The two language files are generated from the Grundstrukturen
  and Algebra 1 book preambles: after either changes, run `py tools/build-language-preambles.py`.
  From anywhere else the log says
  `Preamble-Datei (pdf-preamble.tex) nicht gefunden`. The probe compile before step 3 then
  fails, and step 3 is handed that failure as if it were a LaTeX syntax error to fix. Step 4
  skips the PDF, and the video exits `6` although all text steps finished. Redo step 3 and
  the PDF from the repository root with `refine run --step 3 --through-end`.
- `dummy-part0.tex` (also searched next to the `.exe`). Missing, it only logs `dummy-part0.tex
  nicht gefunden`: it is a Lorem-Ipsum placeholder for the implicit prefix cache, so the
  transcription is unaffected and only a cache hit becomes less likely.

A script that drives `lecx` must `cd` into the repository root first, and pass `--video` and
`--out` as absolute paths.

**"High demand" (HTTP 503) gives up after 8 attempts and loses only that part.** The video then
ends with exit `6`. The parts finished before it are on disk, so running the same video again
within `--resume-window` requests only the missing part and the refinement. Whether the 503
attempts count against the free-tier daily quota is **not settled**. In the maintainer's
experience they usually do not. But on 2026-10-15 one key was refused with
`GenerateRequestsPerDay…` on `gemini-3.8-flash` after about 9 requests that were not 503
(4 warm-ups, 1 complete answer, 2 stream breaks, 1 continue) and about 20 that were. So either part of
the 503s counted (perhaps those that failed after Google had started generating), or the key
was used outside this pipeline that day. The per-project usage page in AI Studio or the Cloud
console is the place to check. On 2026-10-19 that page showed 7 requests for a run that sent 6 (2 warm-ups, 4 parts):
the 7th was part 4's first attempt, which ended in a 503. The log shows no streamed text before
it (`Die bisherige Teilantwort wird verworfen` is printed on every retry, it is no evidence of
output). So at least some 503s count against the daily quota; which ones is still open.

**A key low on 3.8 quota may not gain anything from `--model gemini-3.7-flash`.** Google answers 3.7 requests with
3.8. It used to count the daily 20 per requested model name, but on 2026-11-06 a 3.7 request was
refused under `model: gemini-3.8-flash`, so the second budget is no longer certain
(`parallel.md` § Free-tier quota). Resuming a video on 3.7 after its 3.8 parts is fine: each part's
`.tex` header records the name it was requested under.

**A lost internet connection does not use up attempts.** When the Google host cannot be resolved
or has no route (`Der angegebene Host ist unbekannt`, `[Netzwerk-Fehler]` in the log), the app
probes it with a bare TCP connect to port 443 every `NetworkPollIntervalSeconds` (15) for up to
`NetworkMaxWaitMinutes` (30) and resends at once when it answers. The probe sends no request to
Google (no TLS, no HTTP, no quota); a DNS lookup alone would not do, since it answers from the cache
while the route is down. The failed request never reached Google, so it counts neither against the
retry limit nor in the request table. If the connection stays down for the full wait, the part or
step fails as after any exhausted retry. Other broken connections (reset, `Error while copying
content to a stream`) are a counted retry after `ConnectionErrorDelaySeconds` (AI Studio and Vertex
config, default 60; a fixed 300 s before 2026-10-23).

**The per-minute limit counts inline text characters, not tokens (measured 2026-10-23).** The free
tier caps input at 250,000 per minute per model and key
(`GenerateContentInputTokensPerModelPerMinute-FreeTier`; seen on 3.5 Flash Lite and 3.6, 3.7, 3.8
Flash). Before a request, Google estimates it as the request's **text characters ÷ 4** (system
instruction, history, inlined parts, static prompt) plus the real tokens already charged that minute;
after acceptance, the real count, video and images included, is charged. Measured with
`C:\Users\miche\programming\gemini-cache-probe` (README "Findings"; the full report with every measurement is [docs/gemini-free-tier-limit-report.md](../gemini-free-tier-limit-report.md)):

- One request passes while its inline text stays under ~1M characters: ~300k tokens of this prompt
  (3.30 characters per token), ~380k of plain nonsense. The prompt alone is ~863k characters (87 %).
- Video (File API), inline images and files uploaded as text cost nothing at that check; only the
  1,048,576-token context window limits them (868k with video and 997k with images were accepted).
- The implicit cache does not raise the limit; it only shortens the wait for the first reply. A
  request too big cold is too big warm, so the warm-up never lets an oversized request through.
- Two large requests in the same minute collide, because the first is charged at its real count. The
  64 s waits after each handshake and between parts are what keep them apart.
- Google's "Gecacht" figure is consistent as a cache figure (a hit on 0 of 79 new prefixes, on 84 of
  103 repeats within 10 minutes) but says nothing about why a request was accepted or refused.

**The usage page shows real tokens per minute, minutes late, so it can read over its own 250K.**
A 400,217-token probe request (99,600 of it video) read 400.22K; 380,491 read 380.49K. Two readings
remain unexplained: on 2026-10-23 the 3.6 Flash page stayed at 294.21K (the `[Resume Part 3]`
handshake, 294,213) after parts 3 and 4 of ~359k with video, and the 3.8 Flash page read 220.7K,
235.06K and 235.21K, matching no request. Do not read the page's peak as a request's size. The same
3.8 run made 9 attempts while the page counted 8, so at least 4 of its 5 `503`s counted against the
daily quota.

**The log shows that estimate before every request.** Since 2026-10-23 each `[Part N Prompt]` and
each warm-up step (`[Schritt 0]`, `[Schritt 1/1]`, `[Resume Part N]`) logs `Minutenlimit-Schätzung:
N Textzeichen ÷ 4 = M (P % von 250,000 …)`: system instruction + history + inlined parts, no media
and no uploaded files (earlier parts or history), only the short path lines in front of them.
It is computed locally (no request) and appears even with `SkipTokenCountingDuringWarmUp`. A request
refused with P under 100 % had a request of the same minute in front of it; P over 100 % is refused
on its own, cache or not.

**`InlinePrecedingLecTexParts: false` lifts the limit for late parts (tested 2026-10-23).** The earlier parts'
`.tex` are then uploaded in full as `text/plain` files, and uploaded files do not count at the gate. Information
Security 03-14 part 4 on `gemini-3.8-flash`, profile 1, warm-up on: the log read `875'899 Textzeichen ÷ 4 =
218'974 (87 %)` and the part (381,583 tokens) was accepted on the first attempt; no `[Resume Part 4]` handshake
is sent in this mode. Inline, the same parts add ~136k characters and put the request at ~100 % (the probe's
copy of this request was refused). The output kept its quality markers: same opening section as the inline
run, `hh:mm:ss` timestamps throughout, and the speech kept its fillers in every quarter of the part (54 / 56 /
88 / 64 per 1,000 words, crude count) where both inline runs dropped to ~25 after the first quarter. One run
only. Should an upload fail, that file falls back to being inlined unslimmed, so watch the log line.

**`InlineHistory: false` does the same for the training history (added 2026-10-25, not yet run).** Optional
key in `AiStudioAutoExtractionConfig.json`; absent or `true` changes nothing. With `false`, each history text
file (`.md`, `.txt`, `.tex` under `HistoryPreloadPaths`) is uploaded as `text/plain` and linked at the very
start of the user turn, after one note saying it belongs to the system instructions, each link preceded by
the file's path. It goes into the user turn even with `LoadHistoryIntoSystemInstruction: true`, because the
system instruction rejects file links (`500 Internal error`, gemini-cache-probe e13). The system instruction
keeps its file tree and the rules; images go as before. The history was ~407k characters in 32 files on
2026-10-25, ~42 % of the gate's count: Analysis II part 1 at `975'744 Textzeichen ÷ 4 = 243'936 (97 %)`
should drop to roughly 570k (≈57 %). The log says `N History-Textdatei(en) als text/plain hochgeladen`; a
file whose upload fails is inlined where it would have gone (`Upload von History-Datei '…' fehlgeschlagen`).
Untested: whether uploaded history steers the model as well as inlined history. Check the first run the way
the earlier-parts test was checked (opening section, `hh:mm:ss` timestamps, fillers kept in every quarter).

After the second refusal the part fails with `Die Anfrage hat N Eingabe-Tokens … zweimal
abgelehnt` and the video exits `6`. Ways out: `InlinePrecedingLecTexParts: false` (above),
`InlineHistory: false` (above), less history (`HistoryPreloadPaths`), no earlier parts
(`DebugSendReferenceFile: false`), or a paid key.
Turning the warm-up on does not help.

**The warm-up is a config switch, with no CLI flag.** `EnableImplicitPrefixCacheWarmup` in
`AiStudioAutoExtractionConfig.json`; when off, the log says `Lade History-Dateien direkt in den
Session-Zustand (Warmup deaktiviert)`. When on, a session sends one handshake per history batch
before its first part (`Schritt 0`, `Schritt 1/1`, …). A session that starts at part 2 or later
(`finish` / `run --resume` with earlier parts on disk, inlined) sends one more, `[Resume Part N]`:
system instruction + history + the inlined earlier parts, everything but the video
(`PrimePrefixCacheForResumedPartAsync`). If it fails, the part is sent anyway. With
`InlinePrecedingLecTexParts: false` no `[Resume Part N]` is sent. The warm-up buys only a faster first
reply; it costs 2–3 large requests (daily quota) and 2 × 64 s per session.

**Part 4 is where inline text runs out (2026-10-18).** Information Security, three keys: every part
1–3 went through (352k–387k, warm-up off), every part 4 (382k–396k) was refused twice, in the first run
and on resume; `Schritt 1/1` (284k) was accepted but `[Resume Part 4]` (309k) and part 4 refused.
Part 4 carries the most inlined text (the prompt was then ~920k characters before the 2026-10-23
trim), so it crossed ~1M characters on its own; the probe's 1:1 copy of the request was refused at
exactly that edge.

The inlined earlier parts can also be shrunk: `ReferenceSlimming` in
`AiStudioAutoExtractionConfig.json` says per kind of part what goes (`OlderParts`, `PrecedingPart`;
details in [Documentation.md §4.4](../../Documentation.md)). Since 2026-10-18 the older parts lose
speech, didactic boxes, `aiNote`s and TikZ (~2–3k tokens each instead of ~5k); the part directly before
stays complete. That is margin; uploading them (`InlinePrecedingLecTexParts: false`) takes them out of
the count altogether. With slimming, 02-23, 02-29 and 03-01 part 4 (359k–364k) passed inline.

**A request that sends nothing for 10 minutes is sent again.** The heartbeat prints
`Noch keine Daten vom Server (N min …)` every two silent minutes; at `FirstChunkTimeoutMinutes`
(appsettings.json, default 10, `0` = off) the attempt is abandoned and retried after 30 s. On
2026-10-15 one request stalled for 20 minutes and its retry then answered normally.

**A stream that stops mid-answer is continued after the same 10 minutes.** The limit is on silence,
not length, so a long answer is never cut. Once data has arrived the heartbeat says `Stream liefert
seit N min nichts mehr`; at the limit the text received so far is kept and a continue request
picks up from it. Before 2026-10-18 only the first chunk was timed: three refinement streams on three
keys went silent mid-answer around midnight and waited over an hour, and the heartbeat kept saying
"Noch keine Daten" although the answer had begun. A process stuck like that (built before the fix)
never ends on its own: stop it and continue with `lecx finish`.

**`lecx config profiles` can show room on a key Google has already refused.** On 2026-10-17 it
showed profile 2 at `2/20` for `gemini-3.8-flash` while Google answered `limit: 20` exceeded. The
counter is the app's own bookkeeping, not Google's; trust the refusal.

**After a refinement failure, continue with `finish`, not `run`.** `run` reuses the finished
parts, but it combines them again and starts the refinement at step 1. Because the first
combined file is still there, the second one is written as `step1-<name>-all-offset-copy-1.tex`,
and every later file carries that stem (`step2-<name>-all-offset-copy-1-offset-merged.tex`, …).
The merge is paid for twice and the names no longer match the table in
[pipeline.md](pipeline.md). `lecx finish --folder <lecture>` continues from the last finished
step; by hand that is `refine run --tex <that step's input> --step N --through-end`. `plan`
flags videos that are already in refinement.

**Output to a file or pipe is not wrapped.** Log lines and model text go out as single lines;
tables and rules are laid out for 160 columns. In a console window nothing changed.

**The output folder and the `.tex` stem differ.** The folder is
`<target>/<name-without-compression-suffix>/`, but the parts inside are
`step1-<name>-partN.tex`. Predicting `.tex` paths from the folder name alone finds
nothing.

**`pdf compile` is not free by default.** Its repair path sends the failed document and
the compile log to the model. `--fix-loop 0` makes it purely local.

**A PDF that will not compile is usually a known error class.** Before any AI repair, the
highly experimental auto-fixer (`PdfCompilation.ExperimentalLatexAutoFix`) applies our own
rule-based fixes, and the AI repair gets structural findings with line numbers. To repair
by hand: read the first `ERROR` in `step4-compile-log.txt`, fix every occurrence of that
pattern (not just the reported line), and compile with `--fix-loop 0`. The playbook and the
known error classes are in [../latex-auto-fix.md](../latex-auto-fix.md).

**Parallelism means processes, not threads.** The pipeline keeps its rate-limit pacing in
one process-wide clock, so two runs inside one process would corrupt each other's timing.
Use `lecx batch`, which spawns one child process per worker. See
[parallel.md](parallel.md).

**An answer that breaks off is never kept as a result.** When a request fails for good
mid-answer (say a continue that hits eight `503`s), is cancelled, or keeps coming back blank,
the text received so far is a fragment. The loop reports that (`GenerationEnd` /
`GenerationLoopResult.IsUsable`), and every stage treats it as a failure instead of passing it on:

- Stage 1: the part is not written, so the next run (or `finish`) requests it again instead
  of resuming from a half part.
- Refinement step: error `Antwort unvollständig`, the fragment goes to the step's
  truncated-response file for inspection, and the pipeline stops before the next step.
  Before 2026-10-16 such a fragment (43 of 68 speech blocks) passed the 60 % integrity check
  and step 3 polished it.
- PDF fix: that fix attempt is discarded.

A run that stops on the request budget (`MaxContinueRequests`) is *not* treated this way:
the text is complete up to the budget and is kept as before.

**A menu can never be auto-answered.** If a flow reaches an interactive choice, the run
fails with exit `3` naming the question rather than picking the first entry. That is
deliberate: guessing a menu entry is how an unattended run buys the wrong model. Supply
the value as a flag instead. `--yes` accepts defaults only for questions that *have* a
considered default — it does not answer menus.

---

## Reading the model's reasoning

Run with `--thoughts file` (or set `ThoughtSummaries.Enabled` per stage) and each stage
writes `<output>-thoughts.md` beside its output. Then:

```bash
lecx thoughts list --folder "<output-folder>" --json        # stage, requests, thinking tokens
lecx thoughts show --folder "<lecture>" --stage speech --json # the summaries themselves
```

The files are plain Markdown with one `<!-- lecx-thoughts... {json} -->` line per file and
per request, so `cat` works too. Thinking tokens share `MaxOutputTokens` with the answer;
a request ending in `MAX_TOKENS` with a large thinking count is where a continue came from.

---

## Not implemented

`lecx ask` is registered but refuses with exit `2`. Everything else in `--help` works.

**YouTube: `lecx youtube`, not the menu.** `lecx youtube --url <link> --duration <min|HH:MM:SS>`
runs menu entry 3 without its questions (see [commands.md](commands.md#lecx-youtube--paid)). The
model reads the video from its URL, so nothing is downloaded, but there is also no resume: a rerun
asks for every fragment again. `YouTubeTasks` in the config is still read only by the menu.
