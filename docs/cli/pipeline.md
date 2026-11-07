# The pipeline, stage by stage

Four stages. Each writes to disk, and the next reads what the previous one wrote — which
is why every stage is separately runnable and why a run can resume partway through.

```
   video.mp4
       │
   ┌───▼──────────────────────────────────────┐
   │ 1. Media (FFmpeg)          local, free   │  lecx media segment
   │    compress → 1 FPS, mono                │  lecx media audio
   │    slice into overlapping segments       │
   └───┬──────────────────────────────────────┘
       │  <target>/<name>/tmp/<name>-partN.mp4
   ┌───▼──────────────────────────────────────┐
   │ 2. Extraction (Gemini)            PAID   │  lecx extract run
   │    one request per segment               │
   └───┬──────────────────────────────────────┘
       │  <target>/<name>/step1-<name>-partN.tex
   ┌───▼──────────────────────────────────────┐
   │ 3. Refinement, steps 1-3          PAID   │  lecx refine run
   │    merge + timestamps → speech → polish  │
   └───┬──────────────────────────────────────┘
       │  <target>/<name>/*.tex
   ┌───▼──────────────────────────────────────┐
   │ 4. PDF (LaTeX + optional AI repair)      │  lecx pdf compile
   └───┬──────────────────────────────────────┘
       │
     <name>.pdf   (one file, named after the lecture folder)
```

`lecx run` performs all four, because the pipeline chains internally: a successful
extraction constructs the refinement session and awaits it, and that session compiles the
PDF as its step 4. `--stop-after extract` turns the chaining off rather than
reimplementing the sequence.

---

## Naming: the folder and the file stem differ

This trips people up, so state it plainly.

| Thing | Derived how | Example |
|---|---|---|
| Output folder | filename minus `-speed-N-compressed` / `-compressed` | `02-19-2026-thursday-week1-Analysis_II` |
| `.tex` stem | the same, plus a `step1-` prefix | `step1-02-19-2026-thursday-week1-Analysis_II` |

So a part file lands at:

```
<target>/<folder-name>/step1-<folder-name>-part2.tex
```

Both come from `ExtractionHelpers.ComputeOutputFolderName` and `ComputeTexBaseName`, which
the planner and the pipeline share — so a plan cannot predict a path the run will not
write.

**Consequence:** `lecture.mp4` and `lecture-speed-1-compressed.mp4` produce the *same*
folder and the *same* part names, which is why the program treats them as one lecture and
uses the compressed file (see "A raw video and its `-speed-N-compressed` sibling" in
[agents.md](agents.md)). Only a script that passes both paths itself can make them collide;
`lecx plan` reports that in `warnings`.

## The hand-off record

Stage 1 → stage 2 is a `PreparedVideo`, which already exists as the payload the FFmpeg
producer sends to the Gemini consumer over a bounded channel. `media segment --json`
emits exactly that record, so nothing about the boundary is CLI-specific:

```json
{
  "sourceVideoPath": "...",
  "outputFolder": "...",
  "tempFolder": "...",
  "segments": [ { "filePath": "...", "startTimeSeconds": 0 },
                { "filePath": "...", "startTimeSeconds": 2398 } ],
  "cameFromCache": false,
  "sourceDurationSeconds": 5036
}
```

## Segment geometry

With `N` parts and `V` seconds of video, each segment is
`(V + (N-1) × overlap) / N` seconds long and part *i* starts at
`i × (segmentLength − overlap)`. The overlap exists so a sentence spanning a cut is not
lost; it is why part 2 of an 84-minute lecture starts at 2398 s rather than 2638 s.

`lecx media probe` reports the resulting geometry before anything is cut.

`N` is `NumberOfParts`, or with `--parts auto` the video length divided by 22.5 minutes,
rounded: a 45-minute lecture gets 2 parts, a 90-minute double lecture 4, so every segment
stays around 25 minutes whatever the lecture's length.

## Caching and resume — two different windows

| Layer | Window | Where |
|---|---|---|
| FFmpeg segments (`.mp4`) | **48 hours** | `VideoSegmentProducer` |
| Transcribed parts (`.tex`) | **2 hours** | the extraction session |

The 2-hour one is the expensive one: past it, every part is re-requested and re-billed.
It was a hardcoded constant with no way to reach it; `--resume-window` and `--force` now
expose it.

**Which file of a part counts.** A part requested again while its `.tex` still exists is
written next to it as `<name>-partN-copy-1.tex` (then `-copy-2`, …). The window is measured on
the **newest** of `<name>-partN.tex` and its copies, and that file is the one reused. Until
2026-10-26 only `<name>-partN.tex` was checked, so once it was older than the window every
later run paid for the part again and wrote one more copy. The run, its pre-upload of the next
part and `lecx plan` share this rule (`PartReuse`), so the plan's `resumableSegments` is what
the run reuses.

The FFmpeg cache is additionally validated — an incomplete set, a part smaller
than 1 KB, or a count that disagrees with `--parts` is discarded and re-cut.

**The compressed intermediate is not validated.** For a raw video the producer first writes
`tmp/<name>-speed-1-compressed.mp4` and then cuts the segments from it. On the next run that
file is reused as soon as it is **larger than 1 KB and younger than 48 hours**; its length is
never checked. A compression that was killed (Ctrl+C, Task Manager, a crashed run) leaves a
truncated file that passes, and the run then transcribes only the first part of the lecture,
without any error. After killing FFmpeg, delete that file or replace it with a complete copy.
A copy needs a fresh modification time (`cp`, not `cp -p`), or it counts as older than 48
hours and is recompressed anyway.

## The header stack at the top of every `.tex`

Every file a stage writes opens with one comment block per step that produced it, newest
on top — the configuration, date and token usage of the whole chain, like the rings of an
onion. The first line of each block names its step, numbered like the file prefixes:

```
% ==========================================
% Step 3/4 - Speech refinement (refinement step 2)
% Output: step3-<name>-offset-speech_refined.tex
% Input: step2-<name>-offset-merged.tex
% Model: ...  Temperature: ...  Processed on: ...
% ------------------------------------------
% Token Usage (1 Request(s)):
%   ... Output Tokens / Thinking Tokens listed separately ...
% ==========================================
% ==========================================
% Step 2/4 - Merge & timestamp alignment (refinement step 1)
% ...
% ==========================================
% ==========================================
% Step 1/4 - Transcription, 4 part(s) combined
% ...
%   - Part 1: <part>.mp4 | start 00:00:00 | model ... | processed ... | prompt ... | output ... | thinking ...
% ==========================================
```

The program stacks the blocks, not the model: a step reads the blocks of its input, puts its
own on top, and drops any blocks the model echoed back. Inside a combined document each
`% --- TEIL n` separator is followed by that part's own block, and every refinement step
restores those blocks under each separator the model kept. The final refinement usually
drops the separators; the one-line-per-part summary in the Step 1 block keeps the per-part
facts after that.

Token counts: *Output* is the visible answer only, *Thinking* is billed on top of it, and a
step that needed continue requests reports the sum over all of them.

## Refinement step 2 changes only the `speech` blocks

Step 2 (speech refinement) re-emits the whole document, ~165 KB, and makes copy slips outside
`speech` that no prompt wording prevents. On 2026-11-07 it turned
`(R1) -- node[below left=-1pt] {$q$} (RQ)` into `(V1) -- ...` inside a tikzpicture. Run over
the step-2/step-3 pairs on disk, the same check found a dropped `$` in a `content` block, `\&`
turned into `&` in an `aiNote` title, `&\le` turned into `&=` in an `align`, moved TikZ
coordinates, a dropped `content` block and dropped `% --- TEIL n` separators.

So the program, not the model, owns everything outside `speech` (`NonSpeechGuard`). After the
answer arrives, the step splits its input and the answer into **segments**: the text between
two speech blocks, plus the text before the first and after the last. Each segment of the
output is taken from the input; only the speech blocks come from the model. What the prompt
allows outside `speech` is kept from the answer:

- new `% [REFINER-FLAG: ...]` and `% [TIMESTAMP-SUSPECT: ...]` comment lines, in the place the
  model put them;
- new `student` blocks split out of a student quote. A quote in the middle of a block splits it
  in two and adds a segment that holds only the student block; the guard matches the input's
  segments to the answer's around such additions. A quote at a block's start or end adds no
  speech block, and the new student block is kept where it lands.

Everything else the model changed outside `speech` is undone, fixes included: a `$` the model
added to a broken formula in a `content` block is removed again. The prompt leaves those fixes to
the final step, and the compile before step 3 hands it the errors. An existing `student` block
the model edited (for example by removing an "uh") is also restored.

**Where to see what it did.** The step's header layer gets one line, for example

```
% Non-speech guard: restored 1 of 75 segments outside speech from the input (input line 2012)
```

and the console names each restored segment with its first differing line (`[Step 2]
Nicht-speech-Schutz: ...`, then `Eingabe-Zeile 2012: Eingabe "..." / Modell "..."`). The line
numbers refer to the step's input file (`step2-...-merged.tex`). Segments that differed only in
blank lines or spaces are restored too and only counted.

**When it does nothing.** If the structure cannot be matched, the guard leaves the answer as it
is, warns (`Nicht-speech-Schutz nicht angewendet: ...`), and the header line says
`not applied (...)`. That happens when `\begin{speech}`/`\end{speech}` do not alternate in the
input or the answer, when the answer has fewer speech blocks than the input (the model merged or
dropped some), when an added segment holds more than a student block, and when, after a split,
more than five segments, and more than a quarter of them, would need restoring (that many
restorations more likely mean a wrong matching than that many slips). Then the output is
exactly what the step wrote before the guard existed. Steps 1 and 3 change content blocks on
purpose and are not guarded.

## Continue requests ("please continue")

A response that stops before the model's completion marker (cut off at `MaxOutputTokens`,
thinking tokens included) is followed by an automatic **continue request**, which re-sends
the whole history. Each one is a full extra paid request.

| Stage | Continue prompt | Limit per part/step |
|---|---|---|
| Extraction (per part) | `Continue` if the model reached its segment-limit marker, otherwise `[IMPORTANT] Your response was cut short … Please "continue" exactly where you left off` | `MaxContinueRequests` (15) |
| Refinement steps 1-3 | always the `[IMPORTANT] Your response was cut short … Please "continue"` form | per-step `MaxContinueRequests` (10) |

Both stop early after 3 responses in a row that are empty or repeat the previous tail
(`ContinueBudget`).

**How to count them after a run.** For each part or step, `continues = Requests − 1`, where
`Requests` is the number in its header, `Token Usage (N Request(s))`. The Step 1 block
lists every part separately; steps 2-4 carry their own header. Extraction continues are
also logged in `C:\Users\miche\gemini-logs\folder-*\chat_log.md` as
`[Continue Prompt für Part N]`. Add the PDF-repair rounds if any ran
(`step4-compile-log.txt`, `compile-log-step5-last_tryN.txt`). `lecx run --json` reports the
total as `generationRequests` (all paid requests of that process; subtract the planned
`requestsPlanned` for the continues).

## Backends

Everything above describes **AI Studio**, which is what the CLI drives. Vertex AI exists
in the codebase behind `AppConfig.IsVertexAiEnabled` and is not exposed by the CLI.
