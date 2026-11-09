# 📚 Detailed System Documentation / Detail-Dokumentation

*(See below for the German version / Deutsche Version unten)*

## 🇬🇧 English

This document provides a deep dive into the architecture, configuration quirks, and API constraints of the AI Lecture Extraction & Processing Pipeline. It is intended for developers and advanced users who want to understand the inner workings of the system.

---

## Codebase Philosophy: Dual-Commenting Paradigm

The entire codebase strictly follows a dual-commenting paradigm to ensure seamless human-AI collaboration:
- **`[AI Context]` (English):** Targeted at LLMs. Explains the *why* - including architectural decisions, prompt constraints, API mechanics, and token-saving strategies.
- **`[Human]` (German):** Targeted at human developers. Explains the *how* - focusing on business logic, basic flow, and UI instructions.
When modifying the code, developers and AI agents must maintain this separation of concerns.

---

## 1. Configuration Hierarchy & Lists

`ConfigLoader<T>` loads each session's settings in layers, and the later layer wins for every key it sets:
1. The C# property defaults of the config class (e.g. `AiStudioAutoExtractionConfig.cs`)
2. `appsettings.json`, section `AppConfig:<ClassName>` (optional global overrides)
3. The session's own file (e.g. `VertexAutoExtractionConfig.json`)

`appsettings.json` also holds the global settings in `AppConfig` (`UploadFolder`, `LogFolder`, `IsVertexAiEnabled`, ...); the chat sessions and the session logger start from those paths.

### Lists replace, they do not merge
On its own, the .NET configuration binder handles lists (like `HistoryPreloadPaths` or `SystemInstructionPaths`) badly in two ways: it **appends** a JSON array to the list already in the object (default `[a, b]` + JSON `[c]` = `[a, b, c]`), and an **empty** JSON array `[]` binds nothing, so it cannot clear a default.

`ConfigLayerBinder` therefore empties every list a layer names before binding it. The result:
- A list in the JSON **replaces** the default (or the earlier layer's list) as a whole. `[]` gives an empty list.
- A list the JSON **leaves out keeps** its value: the C# default, or what an earlier layer set.

(Until 2026-10-26 the loader emptied *every* list before binding, so a list default only applied if the JSON repeated it. `ConfigLoaderDefaultsTests` now checks that loading without any JSON gives exactly the class defaults.)

### What the defaults contain
The C# defaults hold only neutral values that work on any machine: tuning values (delays, retries, generation parameters), feature switches, the API-key variable names, and the shared model list (`ModelCatalog.Default`). Paths, prompt files, the Vertex project id and bucket names belong to one machine or one Google Cloud account; they default to empty and are set in the JSON files.

---

## 2. Gemini API: Models & Tokenization

### Tokenization of PDFs and Images
When attaching a PDF (e.g., an academic script) to the context, Gemini treats **each page as an image**. Token costs can vary drastically depending on the model version:
- **gemini-3.5-flash / gemini-1.5-flash:** Highly optimized for efficiency. A single PDF page might cost only a base token amount (e.g., ~258 tokens).
- **gemini-2.5-pro:** Geared towards deep reasoning and high accuracy (especially for mathematical formulas). It often processes images and PDF pages at a much higher resolution, breaking a single page into multiple tiles (e.g., 2 to 4 tiles). Consequently, a single page can cost 516 to 1000+ tokens. 
*Note: This is why uploading a 400-page PDF might cost ~114k tokens on Flash, but ~228k tokens on Pro.*

---

## 3. Advanced Reasoning: Thinking Budgets & Levels

Google's Gemini models support advanced internal reasoning ("Thinking") before emitting an output. This is strictly managed via the `ThinkingConfig` object in the API payload.

### Thinking Budget
The `ThinkingBudget` determines the maximum number of output tokens the model is allowed to spend "thinking" internally before generating the final response.
- **Vertex AI Constraints:** As of mid-2026, the Vertex AI endpoint enforces a strict integer limit for the thinking budget. Supported values range from **128 to 32768 tokens**. 
- **Code implementation:** The application intercepts any budget configured above 32768 and strictly clamps it to `32768` right before dispatching the request to Vertex AI, preventing `ClientError: thinking_budget is out of range` exceptions.

### Thinking Level
The `ThinkingLevel` is an `enum` (`MINIMAL`, `LOW`, `MEDIUM`, `HIGH`) that dictates the depth of reasoning.
- **Model Compatibility:** `ThinkingLevel` is fully supported by the newer **Gemini 3.x** frontier models. However, models like **Gemini 2.5 Pro** do not support the `thinkingLevel` parameter and will reject the request if it is included.
- **Code implementation:** `ThinkingConfigFactory.Build` (one place for every stage). On a `gemini-3` model with a `ThinkingLevel` set, **only the level is sent: `ThinkingBudget` is ignored and does not cap the request** (Step 2's `ThinkingBudget: 4096` has no effect on `MEDIUM`). The budget, clamped to 32768, is sent only to `gemini-2.5` models or when no level is set. Thinking tokens count against `MaxOutputTokens` together with the answer.

### Thought Summaries (`ThoughtSummaries`)
Every generating stage has its own block: `AiStudioAutoExtractionConfig` / `VertexAutoExtractionConfig` (Stage 1, one file per part), `Step1MergeAndTimestamp`, `Step2SpeechRefinement`, `Step3LastRefinement` and `PdfCompilation` (AI repair rounds) in `LatexRefinementSessionConfig`.

```json
"ThoughtSummaries": { "Enabled": false, "ToConsole": true, "ToExistingLog": false, "ToSeparateFile": true }
```

- `Enabled` sends `ThinkingConfig.IncludeThoughts = true`: Gemini returns a condensed summary of its reasoning as parts marked `Thought`. Billing is unchanged (thinking tokens are billed either way). `ResponseParts.Split` keeps the summary out of the `.tex` output and out of the continue history.
- `ToConsole`: printed dimmed while it streams. `ToExistingLog`: appended to the stage's own log: Stage 1 `chat_log.md` (needs `CreateLogFiles`), a step's `<output>-prompt-log.md`, the PDF repair's `compile-log-step5-last_tryN.txt`. `ToSeparateFile`: `<output stem>-thoughts.md` beside the output, one section per request with thinking/output tokens and finish reason; a second run writes `-thoughts-2.md`. The output's `.tex` header names the file (`Thought summaries: ...`). An empty answer sent again keeps its request number and is written as `attempt 2`, `3`...; a request the stage gave up on mid-stream (error, Ctrl+C) still gets its section, finish reason `ABORTED`, tokens unknown (0). The PDF repair's compile log gets the thoughts after compiling; a round that never compiles (empty or rejected answer) still writes that file, holding only them.
- CLI: `--thoughts on|off|all|console,log,file` on `run`, `extract run`, `refine run`, `pdf compile`, `batch` (this run only). Read with `lecx thoughts list` / `lecx thoughts show [path] --stage speech --request 1 --json` (see `docs/cli/commands.md`).
- Both warm-up handshakes (AI Studio and Vertex) send Part 1's thinking config, `IncludeThoughts` included (identical config for the prefix cache), but drop their thoughts; the Vertex history acknowledgement never requests them. The chat sessions do not support thought summaries yet.

---

## 4. Google AI Studio vs. Google Cloud Vertex AI

The pipeline supports both environments, but they handle payloads very differently under the hood:

### Google AI Studio (Developer Tier)
- Uses the **Google File API**.
- When processing video files or PDFs, files are uploaded directly to the generative AI file storage.
- Rate limits (RPM/TPM) are usually stricter for free-tier users, but it is excellent for rapid prototyping.

### Google Cloud Vertex AI (Enterprise Tier)
- Uses **Google Cloud Storage (GCS) Buckets**.
- The application automatically uploads local files into your specified `GcsBucketName` and attaches the `gs://...` URI to the Gemini prompt.
- **Storage Cost Management:** Because processing hundreds of overlapping video chunks can rack up storage costs, the application utilizes a `GcsWorkspace.PurgeAsync()` routine. After a chunk is successfully processed (or if an exception occurs), the application aggressively deletes the temporary files from the GCS bucket. 
*Note: Un-deleted files in a bucket do not consume prompt tokens in future requests, as the API only processes the exact `gs://` URIs sent in the specific request payload.*

---

## 4.2 Modular System Prompt & Attention Map Priming

### Modular File Injection
Instead of relying on monolithic markdown blobs, the pipeline sends multiple distinct instruction files (`transcription.md`, its `transcription/` files, `environments.md`, etc.). For Stage 1 they are compiled in: `src/Extraction/Stage1PromptFiles.cs` names every system instruction and history file, in the order it is sent, and no folder is loaded (since 2026-11-08; before, the configs' `Sources` lists could name whole folders, which loaded every file in them in file-system order). The Stage 1 configs no longer have prompt lists; `lecx plan` warns about a listed file that is missing and about a file in the prompt folders that the list does not name. Before injecting each file into the prompt payload, the application wraps it with clear delimiter headers (`******\n------\n******\nHere is the file 'filename.md':\n`). This explicit separation prevents LLM attention bleeding between distinct instructional domains.

### Attention Map Priming
The system instruction opens with a list of every prompt file ("Folder Structure of System Instructions"): one `- ./relative/path` line per file, system instructions first, then the training history. Since 2026-10-03 it is a flat path list rather than an emoji box-drawing tree (half the tokens, ~1.2k per request), and since 2026-11-09 it keeps the order the files are sent in (`Stage1PromptFiles`: each hub file followed by its folder in the hub's own index order, the history folder by folder), so it doubles as a table of contents. `FileTreeRenderer.GenerateMarkdownFileTree` writes it; the path is also what ties an attached history image to the `.md` file that explains it.

### Strict Multipart Separation (Vertex AI)
Vertex AI enforces strict schema purity on the `SystemInstruction` field: it exclusively accepts pure `text` parts. If binary data (such as image attachments from `Training History`) is passed inside `SystemInstruction`, Vertex AI rejects the request with a `ClientError`. The pipeline intercepts binary history attachments and dynamically routes them to the user prompt `contents` payload, guaranteeing cross-tier compatibility.

### Prompt Assembly & Dump Logging
To verify exact prompt construction and ensure `Training History` metadata is captured, `ExtractionHelpers.LogSystemInstructionDumpAsync` executes strictly after all text instructions and binary attachments have been assembled. It writes the complete compiled prompt into a timestamped `system_instruction_dump.md` file inside the active log directory.

```
+---------------------------------------------------------------------------------+
|                         PROMPT PAYLOAD ASSEMBLY MATRIX                          |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  [SystemInstruction Payload (Strict Pure Text)]                                 |
|  ├── 1. File list (flat paths, in the order the files follow)                   |
|  ├── 2. transcription.md (Framed with delimiter headers)                        |
|  ├── 3. hard-specs.md (Framed with delimiter headers)                           |
|  └── 4. environments.md (Framed with delimiter headers)                         |
|                                                                                 |
|  [User Content Payload (Multimodal)]                                            |
|  ├── Binary Attachments (e.g., Training History Images / Handwriting Samples)   |
|  └── Video Segment Chunk (gs://... or File API URI)                             |
+---------------------------------------------------------------------------------+
```

---


## 4.3 Intelligent Context Caching & MD5 Auto-Reload

To optimize processing times and eliminate redundant prompt upload costs when transcribing sequential batches of lecture videos, `ContextCacheStateManager` implements automated remote caching across both AI Studio and Vertex AI.

### MD5 Checksum Invalidation
Whenever an auto-extraction session boots, the application computes a unified MD5 checksum across the combined text of all configured system instruction files and preloaded history attachments.
- **Cache Match:** If a valid remote cache exists (`vertex_cache_state.json` / `aistudio_cache_state.json`), hasn't expired on Google's servers, and its stored MD5 checksum matches the newly computed checksum, the application reuses the remote cache instantly (`cachedContent/...`).
- **Cache Mismatch (Auto-Reload):** If a developer edits even a single word inside any markdown instruction file (e.g., tweaking `hard-specs.md`), the newly computed MD5 checksum diverges from the saved state. The application detects this mismatch, issues a remote `Delete` command for the stale Google Cloud Cache, and generates a fresh remote cache for the subsequent video batch automatically.

```
+---------------------------------------------------------------------------------+
|                         REMOTE CONTEXT CACHE LIFECYCLE                          |
+---------------------------------------------------------------------------------+
|  Start Auto-Extraction Session                                                  |
|         │                                                                       |
|         ▼                                                                       |
|  Compute Local MD5 Hash (All System Instructions + Preloaded History)           |
|         │                                                                       |
|         ├─────────────────────────────────────────┐                             |
|         ▼                                         ▼                             |
|  [Cache File Exists & Active?]             [No Cache / Expired / Hash Mismatch] |
|         │                                         │                             |
|  (MD5 Matches Exactly)                            ▼                             |
|         │                                  Purge Stale Remote Cache (if any)    |
|         ▼                                         │                             |
|  ✅ Reuse cachedContent/URI                       ▼                             |
|     (0 Prompt Upload Cost)                 Upload New Context & Save MD5        |
+---------------------------------------------------------------------------------+
```

---


## 4.4 Implicit Prefix Cache Warm-Up (`PrimePrefixCacheAsync`)

Google AI Studio uses a **server-side implicit prefix cache**: if consecutive API requests share an identical prefix (System Instruction + early user-turn text), Google reuses the tokenized representation instead of re-processing it. This can cut prompt-processing cost and latency significantly for each video part in a multi-part lecture.

### How the Warm-Up Works
Before the first video is uploaded, `AutoExtractionSession.WarmUpWithBatchedHistoryAsync` sends one or more lightweight "handshake" requests that contain the System Instruction and the history batches. Depending on `LoadHistoryIntoSystemInstruction`, the history is either appended directly to the System Instruction or placed at the very beginning of the User Message. This ensures Google tokenizes the exact prefix sequence and places it into the implicit cache while FFmpeg is still cutting the video in the background.

### Batch Handshake Strategy
When history is split across multiple batches (`HistoryBatchCount > 1`), the warm-up sends one handshake per batch, each time extending the System Instruction with the next batch:
- **Intermediate batches** have an incomplete System Instruction → they can never produce a cache hit for Part 1 (different prefix). These handshakes still pre-exercise Google's tokenization pipeline for those partial prefixes.
- **Last batch** has the complete System Instruction, identical to what Part 1 will use → only this handshake can produce an actual cache hit.

### Dummy File (`dummy-part0.tex`) & `PrefixCacheAnchor`
Part 1 always includes a `<reference_context file="part0.tex">` block (a synthetic LaTeX stub) in the user turn. To make the warm-up prefix bit-identical to Part 1, the last handshake also includes this block. `PrefixCacheAnchor.LoadPrefixCacheAnchorText()` loads `dummy-part0.tex` from disk once and caches it in memory for all subsequent calls.

**`SendDummyFileWithEachWarmUpRound`** (config flag): when set to `true`, the dummy block is sent with *every* handshake (not just the last). With `InlinePrecedingLecTexParts: false` the user-turn text is otherwise identical across all parts, so this setting maximizes the chance that even intermediate batches match a future request. Default: `false` (token-saving mode — only the last batch gets the dummy).

### Slimming the Inlined Earlier Parts (`ReferenceSlimming`)
With `InlinePrecedingLecTexParts`, every earlier part goes into the user turn as text, which pushes late parts past the free tier's 250k per minute (checked as inline text characters ÷ 4, see [docs/cli/agents.md](docs/cli/agents.md)). Set to `false`, the parts are uploaded in full as files instead, which that check does not count (03-14 part 4 passed at 87 % on 2026-10-23). `StripCommentsFromReferenceContext` first removes comments and `hidden` blocks from every inlined file (preamble rule 4). `ReferenceContextSlimmer` then applies one profile per file from `ReferenceSlimming`: `PrecedingPart` for the part directly before the current one, `OlderParts` for all others (only while `SlimOlderReferenceParts` is on). A profile has `StripCommentsAndHidden`, `KeepCommentPrefixes` (comment lines that survive every stripping of that file, e.g. `% Lecturer(s):`, `% Final words:`; rule 4 then says "except possibly a few header comment lines"), `RemoveEnvironments` (removed begin to end), `EmptyEnvironments` (body replaced by `% tokenOpt`), `RemoveCommands` (e.g. `inlinemetanote`, with its argument) and `MarkRemovedEnvironments` (leave `% tokenOpt (name)` behind). `\label`s inside removed or emptied blocks are kept; sectioning, `content` and theorem-like environments are touched only if listed. An unclosed environment is left as it is rather than swallowing the text after it. The preamble's rule 5 is generated from the profiles, so the model is told exactly what is missing and not to imitate it; the transcription prompt (`transcription.md`, "For Part 2 or later") says the same in general terms.

Shipped `AiStudioAutoExtractionConfig.json` (2026-10-18): `OlderParts` removes `speech`, `overview`, `insight`, `steps`, `note`, `student`, `pause`, `aiNote`, `tikzpicture` and `\inlinemetanote`; `PrecedingPart` only strips comments/`hidden` (keeping the `% Lecturer(s):`, `% Speaker(s):`, `% Primary Language:`, `% Final words:` and `% Final board:` lines), since the current part continues it (240 s overlap). Without those lines the 2026-10-18 part 4 of 02-23 wrote `% Lecturer(s): Unknown`. On Information Security that takes an older part from ~5k tokens (old slimming) to ~2–3k. The class defaults reproduce the 2026-10-16 behaviour: older parts lose speech bodies and TikZ, the preceding part stays complete.

### Uploading the History (`InlineHistory`)
`InlineHistory` (optional, default `true`) does for the training history what `InlinePrecedingLecTexParts: false` does for the earlier parts. With `false`, `SystemInstructionTextBuilder.AppendHistoryFilesAsync` uploads each history text file whose extension maps to `text/plain` (`.md`, `.txt`, `.tex`) instead of inlining it, and returns the uploads (a header Part with the file's path, then the file link) apart from the media parts (`LoadedHistory`). The session (`AddLoadedHistory`, `HistoryUserTurnParts`) puts them at the start of every user turn, after `UploadedHistoryNotice`, in the part requests and in every warm-up handshake alike, so the prefixes still match. They go there even with `LoadHistoryIntoSystemInstruction: true`: the system instruction rejects file links (gemini-cache-probe e13, `500 Internal error`). The system instruction keeps its file tree, so the paths still resolve; images are unaffected. A failed upload is inlined where the file would have gone, with a warning. Uploaded files do not count at the free tier's per-minute gate, so the `Minutenlimit-Schätzung` drops by the history's ~407k characters (2026-10-25). Whether uploaded history steers the model as well as inlined history is open (§9).

### ThinkingConfig Consistency
The `GenerateContentConfig` sent during warm-up uses the **same `ThinkingConfig`** as the actual Part-1 request (same `ThinkingBudget` / `ThinkingLevel` / model-specific routing). A mismatch would create a different cache bucket key, causing a cache miss. Use `DisableThinkingDuringWarmUp: true` to force `ThinkingBudget = 0` for the handshake only (saves thinking-tokens during warm-up at the cost of a guaranteed cache miss for that round).

### Caveat: The Cache Figure Says Nothing About Acceptance
The cached-token count Google reports (`CachedContentTokenCount`, shown as "Gecacht" in the console, the chat log and the part headers) is consistent as a cache figure, and the implicit cache is best-effort: measured with `gemini-cache-probe` on 2026-10-23, it reported a hit on 0 of 79 requests with a new prefix and on 84 of 103 repeated within 10 minutes. A hit expires: on 2026-10-10 the identical history handshake (265k tokens) reported 263k cached at 14:40 and 0 about an hour later. Parts 2 and 3 report about the same figure because only the system part is a shared prefix.

Consequences:
- The cache does not decide whether a request passes the free tier's 250k per minute. That limit is checked against the request's inline text characters ÷ 4 (see docs/cli/agents.md); a request too big cold is too big warm. "0 cached" on an accepted request is normal.
- The warm-up therefore only shortens the wait for the first reply; it never lets an oversized request through.
- Do not judge a prompt-layout or warm-up change by the cache figure of a single run.

---


## 4.5 Authentication Setup

Depending on which environment you are targeting, the application requires different authentication setups.

### Google AI Studio (API Keys)
The application dynamically resolves API keys from Windows Environment Variables. To set your keys permanently, run the following commands in PowerShell or Command Prompt:

```powershell
# Set your primary API key for the general sessions
setx API_KEY-ai-studio-test-project-1 "YOUR_GEMINI_API_KEY"

# Set a dedicated API key specifically for the LatexRefinementSession
setx API_KEY_LatexRefinement "YOUR_GEMINI_API_KEY"
```
*(Note: You must restart your terminal and any open IDEs for `setx` changes to take effect.)*

### Google Cloud Vertex AI (IAM Authentication)
Vertex AI uses Google Cloud IAM (Identity and Access Management) instead of static API keys. You must have the Google Cloud SDK (`gcloud` CLI) installed.

1. **Login and set your default application credentials:**
   ```powershell
   gcloud auth application-default login
   ```
2. **Set your active project** (Must match the `VertexProjectId` in your config):
   ```powershell
   gcloud config set project your-vertex-project-id
   ```

## 4.6 Running the Program (Local Console & Parallel Execution)

You can run the program directly from your own Windows Command Prompt (cmd) or PowerShell terminal while you are still working on it in Antigravity or Visual Studio Code.

### How to Navigate and Start the Program
1. **Open your Terminal:** Open Windows Command Prompt (`cmd`) or PowerShell.
2. **Navigate to the Project Directory:**
   Run the following change directory command:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
3. **Compile and Run the Program:**
   To build and start the program from the current directory, run:
   ```powershell
   dotnet run
   ```
   *Note: If the application is already running or the executable is locked, the build might fail to overwrite the executable. See below.*

### Running Multiple Instances Concurrently
You can execute multiple instances of the program in parallel to process different videos at the same time:
1. **Build the binary once:**
   ```powershell
   dotnet build
   ```
2. **Launch multiple instances via direct executable:**
   Open separate terminal windows, navigate to the project root directory:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
   And run the executable directly using its relative path:
   ```powershell
   .\bin\Debug\net10.0\lec-extraction-prog.exe
   ```
   Using the direct `.exe` avoids concurrent build compilation conflicts.
3. **Isolate Configurations (Optional but Recommended):**
   To let each instance have its own permanent configuration:
   - Copy the `bin/Debug/net10.0/` folder to separate directories (e.g., `bin/Debug/instance1/` and `bin/Debug/instance2/`).
   - In your terminal windows, navigate directly into those folders:
     - Window 1:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance1"
       .\lec-extraction-prog.exe
       ```
     - Window 2:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance2"
       .\lec-extraction-prog.exe
       ```
     Each instance will load and save its settings independently within its own folder.

---

## 5. Pipeline Mechanics: FFmpeg & Overlapping Segments

To transcribe a 90-minute lecture, sending the entire video at once often leads to context-window exhaustion or skipped details. The pipeline solves this via:

1. **FFmpeg Slicing:** The video is chopped into strictly defined chunks (e.g., 3 minutes each) with an overlap (e.g., 180 seconds, meaning the last X seconds of chunk 1 are the first X seconds of chunk 2).
2. **Token Optimization:** The video framerate is decimated to `1 FPS`. Since blackboards and slides rarely change sub-second, this preserves visual information while drastically reducing the token payload.
3. **Audio Downmixing:** Audio is compressed and downmixed to mono (`-ac 1`).
4. **Latex Refinement:** Because of the overlaps, the extracted LaTeX chunks will contain duplicated sentences or formulas at the boundaries. The `LatexRefinementSession` passes these chunks to a deterministic AI (Temperature 0.35) with instructions to seamlessly merge them into a single, continuous LaTeX document.

```
+---------------------------------------------------------------------------------+
|                       SLIDING WINDOW CHUNK SCRIPT MERGER                        |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  Lecture Timeline:  0m ---------- 3m ---------- 6m ---------- 9m                |
|                                                                                 |
|  Chunk 1 [0m - 3m]:   [======= Transcribed Text A =======]                      |
|                                    ▲                                            |
|                               Overlapping Boundary (e.g. 180s overlap)          |
|                                    ▼                                            |
|  Chunk 2 [1.5m - 4.5m]:            [======= Transcribed Text B =======]         |
|                                                                                 |
|  LatexRefinementSession (Temp = 0.0):                                           |
|  Fuses boundary duplicate formulas -> Outputs clean unified LaTeX stream        |
+---------------------------------------------------------------------------------+
```

### 🎬 FFmpeg Interactive Manager Dashboard
In addition to automated extraction pipeline preprocessing, option **3** in the main menu starts the **FFmpeg Interactive Preprocessor Dashboard**. It provides full interactive control over manual preprocessing:
- **Interactive Folder Browser:** Browse subfolders, parent folders (`..`), and switch Windows drives (e.g. `d:`, `c:`) to find files.
- **Conversion Dashboard:** Displays selected files and active settings in one visual overview.
- **Flexible Settings Customization:**
  - **Speed Multiplier:** Set video speed between `0.1x` and `10.0x`.
  - **Video Framerate (FPS):** Set custom target framerates.
  - **Audio Formats:** Toggle between downmixing to Mono (optimal for AI transcribing) or copying original Stereo.
  - **Video Scaling:** Toggle between scaling to standard `720p` resolution (recommended) or original resolution.
  - **Compression (Preset):** Choose FFmpeg x264 presets (`ultrafast` to `veryslow`) to trade off compilation time against file sizes.
  - **Time Range Cutting:** Crop a custom subsection of the video (e.g., setting a start timestamp and duration) instead of converting the entire file.
  - **Chunk Splitting:** Set chunk counts and custom overlapping boundary seconds.
- **Custom Mode:** Directly input custom raw FFmpeg args.

---


## 6. Architecture Breakdown: Namespaces & Core Classes

The project lives under `src/`, one folder per bounded context, namespaced `LectureExtraction.<Folder>`. Below is a comprehensive overview of the namespaces and their most critical classes.

### ⚙️ `LectureExtraction.Extraction`
Responsible for the fully automated batch-processing pipeline. It orchestrates FFmpeg processing and AI inference sequentially or concurrently.
- **`AutoExtractionSession`**: Manages the extraction pipeline on AI Studio and Vertex. It uses a **Producer-Consumer pattern** (via `System.Threading.Channels`), allowing FFmpeg to process the next chunk while Gemini is analyzing the current one. Split across three files: the core pipeline (`.cs`), the per-segment request and streaming half (`.Generation.cs`, through `GenerationLoop`), and the implicit prefix-cache warm-up (`.PrefixCache.cs`). Until 2026-10-15 each platform had its own session class; they had drifted apart, and the merge kept AI Studio's pipeline for both.
- **`IExtractionBackend`**: What differs per platform: client, names, endpoint line, remote file cleanup, audio pre-upload, whether the system instruction takes files, and the refinement client. `AiStudioExtractionBackend` uses the File API; `VertexExtractionBackend` uploads to Google Cloud Storage and purges the bucket before and after every run (`GcsWorkspace`) to prevent storage cost blowouts, keeps history files out of the text-only system instruction, and owns **`ExplicitContextCache`** (the CachedContent API, which part requests reference while it is active). The Vertex path has not been run against the real API since the merge.
- **`VertexAutoExtractionSession`**: The pre-merge Vertex session, kept unchanged as the reference implementation. It runs instead of the merged path when `UseLegacyVertexSession` is true in `VertexAutoExtractionConfig.json` (default false), so the first real Vertex run can compare the two before one is retired.
- **`VideoSegmentProducer`**: The shared FFmpeg producer half of the pipeline (splitting, resume-from-disk caching), used by both sessions.
- **`PartReuse`**: The one rule for reusing a transcribed part instead of requesting it again: the newest of `<name>-partN.tex` and its `-copy-N` files, if it is younger than the resume window. The session, its pre-upload of the next part and `lecx plan` all apply it.
- **`TexDocumentWriter`**: Builds the `.tex` part/combined-file headers shared by both sessions.
- **`ExtractionHelpers`**: What's left after splitting out the pieces below — `ResolveNonClashingTexPath` and `LogSystemInstructionDumpAsync`.
- **`HistoryFileResolver`, `FileTreeRenderer`, `VideoBatchSelector`, `YouTubeTaskPrompt`, `ModelSyncService`**: Focused pieces split out of the former `ExtractionHelpers` grab-bag.
- **`RefinementUiHelper`**: Console UI for launching and configuring the LaTeX refinement step from either extraction session.
- **`AudioTrackExtractor`**: Wraps the background AAC-extraction task used for LaTeX refinement's audio input.

### 💬 `LectureExtraction.Chat` & `LectureExtraction.Refinement`
Handles the interactive REPL (Read-Eval-Print Loop) for manual debugging, as well as deterministic Post-Processing that behaves like a chat session.
- **`LatexRefinementSession`** (`Refinement`): The post-processing engine. It feeds the overlapping `.tex` chunks generated by extraction into Gemini, instructing it to resolve duplicates and merge them into one continuous document.
- **`NonSpeechGuard`** (`Refinement`): After refinement step 2, takes everything outside the `speech` blocks back from the step's input, keeping only the comment lines and student-quote splits the prompt allows; it does nothing when the block structure cannot be matched. See `docs/cli/pipeline.md`, "Refinement step 2 changes only the `speech` blocks".
- **`DirectAiChatSessionVertex` / `DirectAiChatSessionAiStudio`** (`Chat`): The core REPL classes. They handle user terminal input, allow dynamic parameter changes (e.g. `/set temp 0.5`), process `/attach` commands, and maintain conversation history.

### 🔧 `LectureExtraction.Configuration`
Centralized configuration management.
- **`ConfigLoader<T>`**: A generic loader implementing the `Microsoft.Extensions.Configuration` binder. It layers the class's C# defaults, optional overrides from `appsettings.json` and the settings from `{Session}Config.json`; `ConfigLayerBinder` makes a list in a later layer replace the earlier one (see section 1).
- **`AppConfig`**: The global settings from `appsettings.json`: the upload and log folders and history/system-instruction paths the chat sessions and the session logger start from, and the feature flags (`IsVertexAiEnabled` — replaces the old `Program.Activate_Vertex` static field, which required a recompile to change — and `FirstChunkTimeoutMinutes`).
- **`ModelCatalog`**: The default model list every config and model picker starts from.

### 🎬 `LectureExtraction.Media`
Wraps the local `ffmpeg.exe` binary to preprocess media before sending it to the cloud.
- **`FfmpegToolkit`**: A headless command builder. Contains logic like `ProcessSplitVideoAsync` (slicing video into 3-minute chunks with a 3-minute overlap), decimating framerates to 1 FPS (`-vf fps=1`), and downmixing audio to mono (`-ac 1`).
- **`FfmpegInteractiveSession`**: A console-based UI for users to manually trigger FFmpeg compressions without running the AI pipeline.
- **`VideoDateParser`**: Helper to extract date metadata from lecture video filenames.

### 🖥️ `LectureExtraction.ConsoleUi`
Generic interactive console prompts, independent of any specific session type.
- **`ConfigurationPrompts`**: Confirm-or-change prompts for source folder (`PromptForSourceFolder`), model, and API-key profile.
- **`DirectoryTreeRenderer`, `FileSelectionPrompt`**: Folder browsing and file-tree rendering helpers.

### 🏗️ `LectureExtraction.Infrastructure`
Cross-cutting concerns like session logging and string utilities.
- **`SessionLogger`**: Automatically creates timestamped directories for every session (e.g., `folder-X-date`). It dumps raw LaTeX outputs to files and maintains a Markdown log (`chat_log.md`) of the entire interaction.
- **`StringExtensions`**: General string helpers (was `StringHelper`).

### 🌐 `LectureExtraction.GoogleAi`
- **`GoogleAiClientBuilder`**: Encapsulates the raw HTTP `HttpClient` construction and API-key/credential resolution for both AI Studio and Vertex.
- **`AttachmentUploader`**: A sophisticated file discovery system (was `AttachmentHandler`). When a user types `/attach file.pdf`, this class searches through a list of fallback directories (the `HistoryPreloadPaths` and `IncludePaths`), finds the file, and prepares it for upload. Code that already has its paths (video segments, audio, history files) calls `UploadFilesAsync(paths)`, which never parses them, so a path may contain commas. Only the typed `/attach` command is parsed (`ParseAttachCommand`): `,` and `;` separate files and `|` starts the question, except inside a path in quotes, e.g. `/attach "D:\Analysis 2, Woche 3.pdf" | Fasse zusammen`.
- **`ApiRetryPolicy`**: Contains logic to gracefully handle HTTP transient errors, such as rate limits (HTTP 429) or temporary server errors (HTTP 503). Was `ApiResilience`.
- **`GenerationLoop`**: The one streaming request loop behind Stage 1 (AI Studio and Vertex), the refinement steps and the PDF repair: streams through `ApiRetryPolicy`, sums each request's `UsageReport`, writes the thought summaries via `ThoughtLog` (an aborted request as `ABORTED`), sends a blank answer again, and sends continue prompts until the stage's completion marker arrives or the `ContinueBudget` runs out. What differs per stage (marker, continue wording, token lines, chat log, delays, console labels) goes in through `GenerationLoopOptions`. A fix to the loop belongs here, not in a stage.
- **`ApiKeyProfileResolver`**: Resolves an API-key profile index (0-3) to its environment-variable name.
- **`PrefixCacheAnchor`**: Loads and caches `dummy-part0.tex`, the shared implicit-prefix-cache anchor used by both extraction sessions.
- **`GcsWorkspace`**: The shared Vertex GCS bucket cleanup routine.
- **`ModelCapabilities`**: The single `SupportsThinking` check, shared across every session type.

### 📄 `LectureExtraction.Latex`
- **`LatexToolkit`**: Helper methods to parse raw markdown outputs from Gemini, strip out ` ```latex ` code blocks, and validate the resulting strings.
- **`LatexTimestampAdjuster`**: Tools for manipulating and adjusting embedded timestamps within the LaTeX document. Was `LatexTimestampHelper`.
- **`LatexResponseCleaner`**: Strips model chatter/formatting artifacts from a raw Gemini response before it's written to disk.

### 🚪 `LectureExtraction.App`
- **`Program`**: The entry point — just `Main()` and top-level exception handling.
- **`MainMenu`**: The top-level interactive loop.
- **`SessionFactory`**: Config-load → client-build → session-construct wiring for each of the five session types.
- **`SourceFolderMenu` / `ApiKeyProfileMenu`**: The two configuration sub-menus.

---

## 7. Known API Limitations & Troubleshooting

### Cache Priming vs. Roundtrips (Why not send everything at once?)
To save an API roundtrip, one might be tempted to send the `training-history` and the 30-minute lecture video in a single prompt. However, this is highly counterproductive:
1. **Cache Priming & Focus:** By sending the massive history first and demanding an "Acknowledgment", the model is forced to parse and build an internal context cache (Context Caching) purely for the rules. When the actual video arrives in the next prompt, the model focuses 100% on execution rather than splitting its attention.
2. **Context Overload:** Shoving thousands of lines of history and a large video into a single prompt often overwhelms the model, leading to hallucinated outputs or `500 Internal Server Errors`.

### HTTP 500 Internal Error (Google Server Crash)
If the application gets stuck in a retry-loop with an `Internal error encountered (HTTP 500)`, it means the Google backend crashed while processing the request. This is rarely a network issue, but usually a prompt/payload issue. Common culprits include:
- **Overloaded System Instruction:** If `LoadHistoryIntoSystemInstruction` is set to `true`, the pipeline injects the history into the `SystemInstruction` (using XML framing `<file path="...">` and embedding images inline via `InlineData`). Note: While inline bytes prevent external URI resolution issues, extremely massive history sets can still exceed context or payload limits. If instability occurs, set it to `false` so the history is batched and prepended to the User Message instead, which resolves System Instruction payload limits while still ensuring a perfect cache hit.
- **Thinking with Flash Models:** Enabling high `ThinkingBudget` or `ThinkingLevel: HIGH` on "Flash" models (e.g., `gemini-3.5-flash`) while processing massive video files is highly unstable and frequently causes 500 errors. **Fix:** Either disable "Thinking" for flash models or switch to a "Pro" model (`gemini-2.5-pro` / `gemini-3.1-pro-preview`), which handles reasoning natively.
- **Corrupted Video Chunks:** Occasionally, FFmpeg generates a chunk with a corrupted frame header that crashes the Gemini Vision encoder. **Fix:** Delete the video's `tmp` folder to force FFmpeg to recut the video.

---

## 8. Error Handling & ApiRetryPolicy

The `ApiRetryPolicy` class wraps all calls to the Google API and handles transient errors gracefully:
- **Rate Limits (HTTP 429):** If the API returns a `retryDelay`, the application parses it and waits exactly that long plus a 20-second buffer.
- **High Demand (HTTP 503):** If the server is overloaded ("high demand"), the application initiates a backoff (configurable via `HighDemandDelaySeconds`, defaulting to 3 minutes / 180 seconds if unspecified).
- **Internet outage (DNS / no route):** A request whose host cannot be resolved never reached Google. The application probes the host with a bare TCP connect to port 443 (no TLS, no HTTP request, no quota; a DNS lookup alone would answer from the cache during a no-route outage) every `NetworkPollIntervalSeconds` (default 15) for up to `NetworkMaxWaitMinutes` (default 30), then resends immediately; the failed attempt does not count against the retry limit. After the full wait it fails like an exhausted retry. Both keys exist in the AI Studio and Vertex configs; `NetworkAvailability` holds the helper and the classifier. Other broken connections (reset, "Error while copying content to a stream") are a counted retry after `ConnectionErrorDelaySeconds` (default 60 s; a fixed 300 s before 2026-10-23).
- **Linear Backoff:** For general 500 errors, the application uses a linear backoff (e.g., 45s, 75s, 105s) up to 8 times.
- **Pacing waits are not retries:** The waits between requests (`VideoPartDelaySeconds`, `SystemInstructionDelaySeconds`, `HistoryRateLimitDelaySeconds`, `FileActivationDelaySeconds`, Vertex's `RateLimitDelaySeconds` and each refinement step's `RateLimitDelaySeconds`) only pace the run, so `0` means no wait. Until 2026-10-26 most of them silently turned `0` into 130 s (65 s for the two warm-up delays). The retry backoffs keep a floor: `HighDemandDelaySeconds` and `ConnectionErrorDelaySeconds` fall back to their defaults at `0`, and a refinement step whose `RateLimitDelaySeconds` is `0` still starts its retry backoff at 130 s, because a failed request retried at once fails again.
- **Interactive Skip:** During any waiting period, the user can press `Enter` to force an immediate retry, or `Ctrl+C` to cancel the delay. Canceling the delay aborts the current video chunk and safely moves the batch processor to the next file without crashing the application.

<br>

---

## 🇩🇪 Deutsch

Dieses Dokument bietet einen tiefen Einblick in die Architektur, Konfigurations-Besonderheiten und API-Einschränkungen der AI Lecture Extraction & Processing Pipeline. Es richtet sich an Entwickler und fortgeschrittene Benutzer, die die inneren Abläufe des Systems verstehen möchten.

---

## Codebasis-Philosophie: Dual-Commenting Paradigma

Die gesamte Codebasis folgt strikt einem Dual-Commenting-Paradigma, um die nahtlose Zusammenarbeit zwischen Mensch und KI zu gewährleisten:
- **`[AI Context]` (Englisch):** Richtet sich an LLMs. Erklärt das *Warum* - einschließlich Architekturentscheidungen, Prompt-Einschränkungen, API-Mechaniken und Strategien zur Token-Einsparung.
- **`[Human]` (Deutsch):** Richtet sich an menschliche Entwickler. Erklärt das *Wie* - fokussiert auf Geschäftslogik, grundlegenden Ablauf und UI-Anweisungen.
Bei Änderungen am Code müssen Entwickler und KI-Agenten diese Trennung strikt beibehalten.

---

## 1. Konfigurations-Hierarchie & Listen

`ConfigLoader<T>` lädt die Einstellungen jeder Session in Ebenen; die spätere Ebene gewinnt für jeden Schlüssel, den sie setzt:
1. Die C#-Standardwerte der Konfigurationsklasse (z. B. `AiStudioAutoExtractionConfig.cs`)
2. `appsettings.json`, Abschnitt `AppConfig:<Klassenname>` (optionale globale Überschreibungen)
3. Die eigene Datei der Session (z. B. `VertexAutoExtractionConfig.json`)

`appsettings.json` enthält zusätzlich die globalen Einstellungen unter `AppConfig` (`UploadFolder`, `LogFolder`, `IsVertexAiEnabled`, ...); die Chat-Sessions und der Session-Logger starten mit diesen Pfaden.

### Listen ersetzen, sie werden nicht zusammengeführt
Allein behandelt der .NET-Configuration-Binder Listen (wie `HistoryPreloadPaths` oder `SystemInstructionPaths`) auf zwei Arten falsch: Er **hängt** ein JSON-Array an die vorhandene Liste **an** (Standard `[a, b]` + JSON `[c]` = `[a, b, c]`), und ein **leeres** JSON-Array `[]` bindet gar nichts, kann also keinen Standardwert leeren.

`ConfigLayerBinder` leert deshalb jede Liste, die eine Ebene nennt, bevor er sie bindet. Ergebnis:
- Eine Liste in der JSON **ersetzt** den Standardwert (bzw. die Liste der früheren Ebene) vollständig. `[]` ergibt eine leere Liste.
- Eine Liste, die in der JSON **fehlt, behält** ihren Wert: den C#-Standardwert oder den der früheren Ebene.

(Bis 2026-10-26 leerte der Loader vor dem Binden *jede* Liste, ein Listen-Standardwert galt also nur, wenn die JSON ihn wiederholte. `ConfigLoaderDefaultsTests` prüft jetzt, dass das Laden ganz ohne JSON genau die Klassen-Standardwerte ergibt.)

### Was in den Standardwerten steht
Die C#-Standardwerte enthalten nur neutrale Werte, die auf jedem Rechner funktionieren: Tuning-Werte (Wartezeiten, Retries, Generierungsparameter), Schalter, die Namen der API-Key-Variablen und die gemeinsame Modellliste (`ModelCatalog.Default`). Pfade, Prompt-Dateien, die Vertex-Projekt-ID und Bucket-Namen gehören zu einem Rechner oder einem Google-Cloud-Konto; sie sind standardmäßig leer und stehen in den JSON-Dateien.

---

## 2. Gemini API: Modelle & Tokenisierung

### Tokenisierung von PDFs und Bildern
Wenn ein PDF (z. B. ein akademisches Skript) an den Kontext angehängt wird, behandelt Gemini **jede Seite als Bild**. Die Token-Kosten können je nach Modellversion drastisch variieren:
- **gemini-3.5-flash / gemini-1.5-flash:** Hochgradig auf Effizienz optimiert. Eine einzelne PDF-Seite kostet möglicherweise nur einen Basis-Token-Betrag (z. B. ~258 Token).
- **gemini-2.5-pro:** Ausgerichtet auf tiefes logisches Denken und hohe Genauigkeit (insbesondere bei mathematischen Formeln). Es verarbeitet Bilder und PDF-Seiten oft in viel höherer Auflösung und zerlegt eine einzelne Seite in mehrere Kacheln (z. B. 2 bis 4 Kacheln). Folglich kann eine einzelne Seite 516 bis 1000+ Token kosten.
*Hinweis: Aus diesem Grund kann der Upload eines 400-seitigen PDFs bei Flash ~114k Token kosten, bei Pro jedoch ~228k Token.*

---

## 3. Erweitertes logisches Denken (Advanced Reasoning): Thinking Budgets & Levels

Googles Gemini-Modelle unterstützen ein erweitertes internes Nachdenken ("Thinking"), bevor sie eine Ausgabe generieren. Dies wird strikt über das `ThinkingConfig`-Objekt im API-Payload gesteuert.

### Thinking Budget
Das `ThinkingBudget` bestimmt die maximale Anzahl von Ausgabe-Token, die das Modell intern zum "Nachdenken" ausgeben darf, bevor die endgültige Antwort generiert wird.
- **Vertex AI Einschränkungen:** Stand Mitte 2026 erzwingt der Vertex AI-Endpunkt ein striktes Integer-Limit für das Thinking Budget. Unterstützte Werte liegen zwischen **128 und 32768 Token**.
- **Code-Implementierung:** Die Anwendung fängt jedes Budget über 32768 ab und klemmt es strikt auf `32768` fest, kurz bevor die Anfrage an Vertex AI gesendet wird, um `ClientError: thinking_budget is out of range` Ausnahmen zu vermeiden.

### Thinking Level
Das `ThinkingLevel` ist ein `enum` (`MINIMAL`, `LOW`, `MEDIUM`, `HIGH`), das die Tiefe des Nachdenkens vorschreibt.
- **Modell-Kompatibilität:** `ThinkingLevel` wird von den neueren **Gemini 3.x** Frontier-Modellen voll unterstützt. Modelle wie **Gemini 2.5 Pro** unterstützen den Parameter `thinkingLevel` jedoch nicht und lehnen die Anfrage ab, wenn er enthalten ist.
- **Code-Implementierung:** `ThinkingConfigFactory.Build` (eine Stelle für alle Stufen). Bei einem `gemini-3`-Modell mit gesetztem `ThinkingLevel` wird **nur das Level gesendet: `ThinkingBudget` wird ignoriert und begrenzt die Anfrage nicht** (Step 2s `ThinkingBudget: 4096` wirkt bei `MEDIUM` nicht). Das Budget (auf 32768 begrenzt) geht nur an `gemini-2.5`-Modelle oder wenn kein Level gesetzt ist. Denk-Tokens zählen zusammen mit der Antwort gegen `MaxOutputTokens`.

### Gedanken-Zusammenfassungen (`ThoughtSummaries`)
Jede generierende Stufe hat einen eigenen Block: `AiStudioAutoExtractionConfig` / `VertexAutoExtractionConfig` (Stage 1, eine Datei pro Teil), `Step1MergeAndTimestamp`, `Step2SpeechRefinement`, `Step3LastRefinement` und `PdfCompilation` (KI-Reparatur) in `LatexRefinementSessionConfig`.

- `Enabled` sendet `ThinkingConfig.IncludeThoughts = true`: Gemini liefert eine verdichtete Zusammenfassung seines Nachdenkens als `Thought`-Parts. Kosten unverändert. `ResponseParts.Split` hält sie aus dem `.tex` und der Continue-History heraus.
- `ToConsole`: gedimmt in der Konsole. `ToExistingLog`: an das Log der Stufe angehängt (Stage 1 `chat_log.md`, Schritt `<output>-prompt-log.md`, PDF-Reparatur `compile-log-step5-last_tryN.txt`). `ToSeparateFile`: `<output stem>-thoughts.md` neben der Ausgabe, ein Abschnitt pro Request mit Denk-/Output-Tokens und FinishReason; ein zweiter Lauf schreibt `-thoughts-2.md`. Der `.tex`-Header nennt die Datei.
- CLI: `--thoughts on|off|all|console,log,file` bei `run`, `extract run`, `refine run`, `pdf compile`, `batch` (nur dieser Lauf). Lesen mit `lecx thoughts list` / `lecx thoughts show [Pfad] --stage speech --request 1 --json`.
- Der AI-Studio-Warm-up sendet dasselbe `IncludeThoughts` (identische Config für den Prefix-Cache), verwirft aber seine Gedanken; Vertex' History-Bestätigung fordert keine an. Die Chat-Sessions unterstützen es noch nicht.

---

## 4. Google AI Studio vs. Google Cloud Vertex AI

Die Pipeline unterstützt beide Umgebungen, aber sie behandeln Payloads intern sehr unterschiedlich:

### Google AI Studio (Developer Tier)
- Nutzt die **Google File API**.
- Bei der Verarbeitung von Videodateien oder PDFs werden Dateien direkt in den generativen AI-Dateispeicher hochgeladen.
- Ratenlimits (RPM/TPM) sind für Free-Tier-Nutzer in der Regel strenger, aber es eignet sich hervorragend für schnelles Prototyping.

### Google Cloud Vertex AI (Enterprise Tier)
- Nutzt **Google Cloud Storage (GCS) Buckets**.
- Die Anwendung lädt lokale Dateien automatisch in deinen angegebenen `GcsBucketName` hoch und hängt die `gs://...` URI an den Gemini-Prompt an.
- **Speicherkosten-Management:** Da die Verarbeitung von hunderten überlappenden Video-Chunks die Speicherkosten in die Höhe treiben kann, nutzt die Anwendung eine `GcsWorkspace.PurgeAsync()`-Routine. Nachdem ein Chunk erfolgreich verarbeitet wurde (oder wenn eine Ausnahme auftritt), löscht die Anwendung die temporären Dateien aggressiv aus dem GCS-Bucket.
*Hinweis: Nicht gelöschte Dateien in einem Bucket verbrauchen bei zukünftigen Anfragen keine Prompt-Token, da die API nur die exakten `gs://`-URIs verarbeitet, die im jeweiligen Request-Payload gesendet werden.*

---

## 4.2 Modulares System-Prompt & Attention Map Priming

### Modulare Dateiinjektion
Statt monolithische Markdown-Blöcke zu nutzen, schickt die Pipeline mehrere getrennte Anweisungsdateien (`transcription.md`, seine `transcription/`-Dateien, `environments.md` etc.). Für Stage 1 sind sie einkompiliert: `src/Extraction/Stage1PromptFiles.cs` nennt jede System-Instruction- und History-Datei einzeln, in der Reihenfolge, in der sie gesendet wird; Ordner werden nicht geladen (seit 2026-11-08; vorher konnten die `Sources`-Listen der Konfiguration ganze Ordner nennen, deren Dateien dann in Dateisystem-Reihenfolge geladen wurden). Die Stage-1-Konfigurationen haben keine Prompt-Listen mehr; `lecx plan` warnt vor einer eingetragenen Datei, die fehlt, und vor einer Datei in den Prompt-Ordnern, die nicht eingetragen ist. Vor der Injektion jeder Datei in den Prompt wird sie mit klaren Trenn-Headern versehen (`******\n------\n******\nHere is the file 'dateiname.md':\n`). Diese explizite Trennung verhindert Aufmerksamkeitssprünge (Attention Bleeding) der KI zwischen verschiedenen Domänen.

### Attention Map Priming
Die System Instruction beginnt mit einer Liste aller Prompt-Dateien ("Folder Structure of System Instructions"): eine Zeile `- ./relativer/pfad` pro Datei, zuerst die System Instructions, dann die Training History. Seit 2026-10-03 ist das eine flache Pfadliste statt eines Emoji-Baums (halb so viele Tokens, ~1,2k pro Request), und seit 2026-11-09 behält sie die Reihenfolge, in der die Dateien gesendet werden (`Stage1PromptFiles`: jede Hub-Datei, dann ihr Ordner in der Reihenfolge ihres eigenen Index, die History Ordner für Ordner), und dient so als Inhaltsverzeichnis. `FileTreeRenderer.GenerateMarkdownFileTree` schreibt sie; der Pfad ist auch das, was ein angehängtes History-Bild mit der `.md`-Datei verbindet, die es erklärt.

### Strikte Multipart-Trennung (Vertex AI)
Vertex AI erzwingt strikte Schemareinheit im `SystemInstruction`-Feld: Es akzeptiert ausschließlich reine `text`-Parts. Werden binäre Daten (wie Bildanhänge aus der `Training History`) in `SystemInstruction` übergeben, lehnt Vertex AI die Anfrage mit einem `ClientError` ab. Die Pipeline fängt binäre History-Anhänge ab und leitet sie dynamisch in den `contents`-Körper des User-Prompts weiter, was die Kompatibilität zwischen AI Studio und Vertex garantiert.

### Prompt Assembly & Dump Logging
Um den exakten Prompt-Aufbau zu verifizieren und sicherzustellen, dass Anhänge der `Training History` miterfasst werden, wird `ExtractionHelpers.LogSystemInstructionDumpAsync` erst ausgeführt, nachdem alle Texte und Bildanhänge verknüpft wurden. Der komplette Dump wird in eine mit Zeitstempel versehene `system_instruction_dump.md` im Log-Verzeichnis geschrieben.

```
+---------------------------------------------------------------------------------+
|                         PROMPT PAYLOAD ASSEMBLY MATRIX                          |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  [SystemInstruction Payload (Strikt reiner Text)]                               |
|  ├── 1. Dateiliste (flache Pfade, in Sendereihenfolge)                          |
|  ├── 2. transcription.md (Eingefasst mit Trenn-Headern)                         |
|  ├── 3. hard-specs.md (Eingefasst mit Trenn-Headern)                            |
|  └── 4. environments.md (Eingefasst mit Trenn-Headern)                          |
|                                                                                 |
|  [User Content Payload (Multimodal)]                                            |
|  ├── Binäre Anhänge (z. B. Training History Bilder / Handschrift-Proben)        |
|  └── Videosegment-Chunk (gs://... oder File API URI)                            |
+---------------------------------------------------------------------------------+
```

---


## 4.3 Intelligentes Context Caching & MD5 Auto-Reload

Um die Verarbeitungszeiten zu minimieren und redundante Upload-Kosten beim sequenziellen Batch-Verarbeiten von Vorlesungsvideos zu eliminieren, implementiert `ContextCacheStateManager` automatisiertes Remote Caching für AI Studio und Vertex AI.

### MD5-Checksummen-Invalidierung
Wann immer eine Auto-Extraktions-Session startet, berechnet die Anwendung eine MD5-Checksumme über den gesamten Text aller konfigurierten Systemanweisungen und vorverladenen History-Anhänge.
- **Cache Match:** Existiert ein gültiger Remote-Cache (`vertex_cache_state.json` / `aistudio_cache_state.json`), ist er auf den Google-Servern nicht abgelaufen und stimmt seine MD5-Checksumme mit der frisch berechneten Checksumme überein, nutzt die App diesen Cache sofort wieder (`cachedContent/...`).
- **Cache Mismatch (Auto-Reload):** Ändert ein Entwickler auch nur ein einziges Wort in einer Markdown-Datei (z. B. in `hard-specs.md`), weicht die neue MD5-Checksumme vom gespeicherten Zustand ab. Die Anwendung erkennt diese Abweichung, sendet einen `Delete`-Befehl für den alten Google Cloud Cache und erstellt vollautomatisch einen frischen Cache für den nächsten Video-Batch.

```
+---------------------------------------------------------------------------------+
|                         REMOTE CONTEXT CACHE LEBENSZYKLUS                       |
+---------------------------------------------------------------------------------+
|  Start Auto-Extraction Session                                                  |
|         │                                                                       |
|         ▼                                                                       |
|  Berechnung lokaler MD5-Hash (Alle System-Instruktionen + Vorverladene History) |
|         │                                                                       |
|         ├─────────────────────────────────────────┐                             |
|         ▼                                         ▼                             |
|  [Cache-Datei existiert & aktiv?]          [Kein Cache / Abgelaufen / Mismatch] |
|         │                                         │                             |
|  (MD5 stimmt exakt überein)                       ▼                             |
|         │                                  Löschen alter Remote Cache           |
|         ▼                                         │                             |
|  ✅ Wiederverwendung cachedContent/URI            ▼                             |
|     (0 Prompt Upload Kosten)               Upload neuer Kontext & MD5 speichern |
+---------------------------------------------------------------------------------+
```

---


## 4.4 Impliziter Prefix-Cache Warm-Up (`PrimePrefixCacheAsync`)

Google AI Studio nutzt einen **serverseitigen impliziten Prefix-Cache**: Wenn aufeinanderfolgende API-Anfragen denselben Präfix teilen (System Instruction + früher User-Turn-Text), verarbeitet Google die tokenisierte Darstellung nicht neu. Das spart bei jedem Video-Part erheblich Prompt-Verarbeitungskosten und Latenz.

### Wie der Warm-Up funktioniert
Bevor das erste Video hochgeladen wird, sendet `AutoExtractionSession.WarmUpWithBatchedHistoryAsync` einen oder mehrere leichte "Handshake"-Requests, die nur die System Instruction (und ggf. History-Batches) enthalten. Das zwingt Google, den vollständigen Instruktionssatz zu tokenisieren und in den impliziten Cache zu legen, während FFmpeg das Video noch schneidet.

### Batch-Handshake-Strategie
Wenn die History auf mehrere Batches aufgeteilt ist (`HistoryBatchCount > 1`), sendet der Warm-Up pro Batch einen Handshake, der die System Instruction jeweils um den nächsten Batch erweitert:
- **Intermediäre Batches** haben eine unvollständige System Instruction → sie können keinen Cache-Hit für Part 1 produzieren (anderer Präfix). Diese Handshakes trainieren jedoch trotzdem die Google-Tokenisierungs-Pipeline für diese Teilzustände.
- **Letzter Batch** hat die vollständige System Instruction, identisch zu Part 1 → nur dieser Handshake kann einen echten Cache-Hit erzeugen.

### Dummy-Datei (`dummy-part0.tex`) & `PrefixCacheAnchor`
Part 1 enthält immer einen `<reference_context file="part0.tex">`-Block (ein synthetischer LaTeX-Stub) im User-Turn. Damit der Warm-Up-Präfix bit-identisch zu Part 1 ist, enthält der letzte Handshake denselben Block. `PrefixCacheAnchor.LoadPrefixCacheAnchorText()` lädt `dummy-part0.tex` einmalig vom Disk und hält ihn im Speicher für alle weiteren Aufrufe.

**`SendDummyFileWithEachWarmUpRound`** (Config-Flag): Bei `true` wird der Dummy-Block mit *jedem* Handshake gesendet (nicht nur dem letzten). Mit `InlinePrecedingLecTexParts: false` ist der User-Turn-Text für alle Parts identisch strukturiert, daher maximiert dieses Flag die Cache-Hit-Wahrscheinlichkeit auch für intermediäre Batches. Default: `false` (Token-Sparmodus — nur der letzte Batch erhält den Dummy).

### Kürzen der eingebetteten Vorgänger-Teile (`ReferenceSlimming`)
`StripCommentsFromReferenceContext` entfernt zuerst Kommentare und `hidden` aus jeder eingebetteten Datei. Danach wendet `ReferenceContextSlimmer` pro Datei ein Profil aus `ReferenceSlimming` an: `PrecedingPart` für den direkten Vorgänger, `OlderParts` für alle älteren Teile (nur mit `SlimOlderReferenceParts`). Ein Profil hat `StripCommentsAndHidden`, `KeepCommentPrefixes` (Kommentarzeilen, die erhalten bleiben, z. B. `% Lecturer(s):`, `% Final words:`), `RemoveEnvironments` (ganz entfernt), `EmptyEnvironments` (Inhalt durch `% tokenOpt` ersetzt), `RemoveCommands` (z. B. `inlinemetanote` samt Argument) und `MarkRemovedEnvironments`. `\label`s in entfernten Blöcken bleiben; Abschnitte, `content` und Satz-Umgebungen werden nur angefasst, wenn sie gelistet sind. Ungeschlossene Umgebungen bleiben stehen. Regel 5 der Präambel wird aus den Profilen erzeugt. Ausgeliefert (2026-10-18): ältere Teile ohne `speech`, `overview`, `insight`, `steps`, `note`, `student`, `pause`, `aiNote`, `tikzpicture`, `\inlinemetanote`; der direkte Vorgänger nur ohne Kommentare/`hidden`. Klassen-Defaults = Verhalten vom 2026-10-16.

### History hochladen (`InlineHistory`)
`InlineHistory` (optional, Default `true`) macht für die Training History, was `InlinePrecedingLecTexParts: false` für die Vorgänger-Teile macht. Bei `false` lädt `SystemInstructionTextBuilder.AppendHistoryFilesAsync` jede History-Textdatei mit `text/plain`-Endung (`.md`, `.txt`, `.tex`) hoch, statt sie einzubetten. Die Session setzt die Uploads (je eine Kopfzeile mit dem Pfad, dann der Datei-Link) hinter `UploadedHistoryNotice` an den Anfang jedes User-Turns, in Part-Requests und allen Warm-up-Handshakes gleich. Auch bei `LoadHistoryIntoSystemInstruction: true`, denn die System Instruction lehnt Datei-Links ab (gemini-cache-probe e13). Dateibaum und Bilder bleiben wie bisher. Ein gescheiterter Upload wird eingebettet, wo die Datei sonst stünde, mit Warnung. Hochgeladene Dateien zählen nicht ans Minutenlimit; die `Minutenlimit-Schätzung` sinkt um die ~407k Zeichen der History (2026-10-25). Ob hochgeladene History das Modell gleich gut steuert, ist offen (§9).

### ThinkingConfig-Konsistenz
Die `GenerateContentConfig` im Warm-Up verwendet dieselbe **`ThinkingConfig`** wie die echte Part-1-Anfrage (gleiches `ThinkingBudget` / `ThinkingLevel` / modellspezifisches Routing). Ein Unterschied würde einen anderen Cache-Bucket-Key erzeugen und einen Cache-Miss verursachen. Mit `DisableThinkingDuringWarmUp: true` wird für den Handshake `ThinkingBudget = 0` erzwungen (spart Denk-Tokens im Warm-Up, auf Kosten eines garantierten Cache-Misses für diese Runde).

### Achtung: Googles gemeldeter Cache-Hit ist nicht verlässlich
Die von Google gemeldete Zahl gecachter Tokens (`CachedContentTokenCount`, als "Gecacht" in Konsole, Chat-Log und Part-Headern) ist als Cache-Zahl stimmig, und der implizite Cache ist Best-Effort: Mit `gemini-cache-probe` am 2026-10-23 gemessen, meldete sie einen Hit bei 0 von 79 Anfragen mit neuem Präfix und bei 84 von 103 Wiederholungen innerhalb von 10 Minuten. Ein Hit verfällt: Am 2026-10-10 meldete der identische History-Handshake (265k Tokens) um 14:40 263k gecacht und rund eine Stunde später 0. Teil 2 und 3 melden etwa dieselbe Zahl, weil nur der System-Teil ein gemeinsamer Präfix ist.

Folgen:
- Der Cache entscheidet nicht, ob eine Anfrage die 250k pro Minute im Free Tier passiert. Geprüft wird gegen die Inline-Textzeichen der Anfrage ÷ 4 (siehe docs/cli/agents.md); was kalt zu gross ist, ist es auch warm. "0 gecacht" bei einer angenommenen Anfrage ist normal.
- Der Warm-Up verkürzt deshalb nur die Wartezeit auf die erste Antwort; eine zu grosse Anfrage bringt er nie durch.
- Eine Änderung am Prompt-Aufbau oder Warm-Up nicht anhand der Cache-Zahl eines einzelnen Laufs beurteilen.

---


## 4.5 Authentifizierungs-Setup

Je nachdem, welche Umgebung du anvisierst, erfordert die Anwendung unterschiedliche Authentifizierungs-Setups.

### Google AI Studio (API Keys)
Die Anwendung löst API-Keys dynamisch aus Windows-Umgebungsvariablen auf. Um deine Keys dauerhaft zu setzen, führe die folgenden Befehle in PowerShell oder der Eingabeaufforderung aus:

```powershell
# Setze deinen primären API-Key für die allgemeinen Sessions
setx API_KEY-ai-studio-test-project-1 "DEIN_GEMINI_API_KEY"

# Setze einen dedizierten API-Key speziell für die LatexRefinementSession
setx API_KEY_LatexRefinement "DEIN_GEMINI_API_KEY"
```
*(Hinweis: Du musst dein Terminal und geöffnete IDEs neu starten, damit `setx`-Änderungen wirksam werden.)*

### Google Cloud Vertex AI (IAM Authentifizierung)
Vertex AI verwendet Google Cloud IAM (Identity and Access Management) anstelle von statischen API-Keys. Du musst das Google Cloud SDK (`gcloud` CLI) installiert haben.

1. **Login und Standard-Anmeldeinformationen der Anwendung setzen:**
   ```powershell
   gcloud auth application-default login
   ```
2. **Dein aktives Projekt setzen** (Muss mit der `VertexProjectId` in deiner Config übereinstimmen):
   ```powershell
   gcloud config set project deine-vertex-projekt-id
   ```

## 4.6 Programm ausführen (Lokale Konsole & Parallele Ausführung)

Du kannst das Programm direkt in der Windows Eingabeaufforderung (`cmd`) oder der PowerShell starten, während du gleichzeitig in Antigravity oder Visual Studio Code daran arbeitest.

### Navigieren und Starten des Programms
1. **Terminal öffnen:** Öffne die Windows Eingabeaufforderung (`cmd`) oder die PowerShell.
2. **In das Projektverzeichnis wechseln:**
   Führe den folgenden Befehl aus:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
3. **Kompilieren und Starten:**
   Um das Projekt zu bauen und direkt auszuführen, verwende:
   ```powershell
   dotnet run
   ```
   *Hinweis: Wenn das Programm bereits läuft oder die Executable blockiert ist, kann der Kopiervorgang fehlschlagen. Verwende in diesem Fall die unten stehende Methode.*

### Ausführen mehrerer Instanzen (Parallel)

> **Veraltet.** Das Kopieren des Build-Ordners ist nicht mehr nötig: im CLI-Modus wird die
> Konfiguration standardmäßig nicht zurückgeschrieben, und `--config-dir` isoliert sie bei
> Bedarf pro Prozess. Siehe **[docs/cli/parallel.md](docs/cli/parallel.md)** und
> `lecx batch`. Der folgende Abschnitt bleibt für den rein interaktiven Betrieb gültig.

Du kannst mehrere Instanzen parallel laufen lassen, um verschiedene Videos zeitgleich zu verarbeiten:
1. **Projekt einmalig bauen:**
   ```powershell
   dotnet build
   ```
2. **Mehrere Instanzen per Direktstart ausführen:**
   Öffne separate Terminal-Fenster, navigiere in das Projekt-Hauptverzeichnis:
   ```powershell
   cd "C:\Users\miche\programming\lec-extraction-prog"
   ```
   Und starte direkt die ausführbare Datei über ihren relativen Pfad:
   ```powershell
   .\bin\Debug\net10.0\lec-extraction-prog.exe
   ```
   Der direkte Start der `.exe` verhindert Build-Konflikte bei gleichzeitigem Start.
3. **Konfigurationen isolieren (Empfohlen):**
   Damit jede Instanz ihre eigenen dauerhaften Einstellungen verwenden kann:
   - Kopiere den Ordner `bin/Debug/net10.0/` in separate Verzeichnisse (z. B. `bin/Debug/instance1/` und `bin/Debug/instance2/`).
   - Navigiere in deinen Terminal-Fenstern direkt in diese Unterordner:
     - Fenster 1:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance1"
       .\lec-extraction-prog.exe
       ```
     - Fenster 2:
       ```powershell
       cd "C:\Users\miche\programming\lec-extraction-prog\bin\Debug\instance2"
       .\lec-extraction-prog.exe
       ```
     Jede Instanz lädt und speichert ihre Konfiguration nun absolut unabhängig in ihrem jeweiligen Ordner.

---

## 5. Pipeline-Mechanik: FFmpeg & Überlappende Segmente

Um eine 90-minütige Vorlesung zu transkribieren, führt das Senden des gesamten Videos auf einmal oft zur Erschöpfung des Kontextfensters oder zu übersprungenen Details. Die Pipeline löst dies durch:

1. **FFmpeg Slicing:** Das Video wird in streng definierte Chunks (z. B. jeweils 3 Minuten) mit einer Überlappung (z. B. 180 Sekunden, d.h. die letzten X Sekunden von Chunk 1 sind die ersten X Sekunden von Chunk 2) zerschnitten.
2. **Token-Optimierung:** Die Video-Framerate wird auf `1 FPS` dezimiert. Da sich Tafeln und Folien selten im Sub-Sekunden-Bereich ändern, bleibt die visuelle Information erhalten, während der Token-Payload drastisch reduziert wird.
3. **Audio-Downmixing:** Audio wird komprimiert und auf Mono heruntergemischt (`-ac 1`).
4. **Latex Refinement:** Aufgrund der Überlappungen enthalten die extrahierten LaTeX-Chunks duplizierte Sätze oder Formeln an den Rändern. Die `LatexRefinementSession` übergibt diese Chunks an eine deterministische KI (Temperature 0.35) mit der Anweisung, sie nahtlos zu einem einzigen, kontinuierlichen LaTeX-Dokument zusammenzuführen.

```
+---------------------------------------------------------------------------------+
|                       SLIDING WINDOW CHUNK SCRIPT MERGER                        |
+---------------------------------------------------------------------------------+
|                                                                                 |
|  Vorlesungs-Zeitachse: 0m ---------- 3m ---------- 6m ---------- 9m             |
|                                                                                 |
|  Chunk 1 [0m - 3m]:      [======= Transkribierter Text A =======]               |
|                                       ▲                                         |
|                                  Überlappungszone (z.B. 180s)                   |
|                                       ▼                                         |
|  Chunk 2 [1.5m - 4.5m]:               [======= Transkribierter Text B =======]  |
|                                                                                 |
|  LatexRefinementSession (Temp = 0.0):                                           |
|  Verschmilzt Formel-Duplikate -> Gibt nahtlosen LaTeX-Stream aus                |
+---------------------------------------------------------------------------------+
```

### 🎬 FFmpeg Interactive Manager Dashboard
Zusätzlich zur automatisierten Vorverarbeitung bietet Option **3** im Hauptmenü das **FFmpeg Interactive Preprocessor Dashboard**. Es bietet volle interaktive Kontrolle über die manuelle Vorverarbeitung von Videos:
- **Interaktiver Ordner-Browser:** Navigiere durch Unterordner, übergeordnete Ordner (`..`) und wechsle Windows-Laufwerke (z. B. `d:`, `c:`), um Dateien zu finden.
- **Konvertierungs-Dashboard:** Zeigt die ausgewählten Dateien und die aktiven Einstellungen auf einen Blick.
- **Flexible Anpassung aller Parameter:**
  - **Geschwindigkeit (Speed):** Setze die Geschwindigkeit des Videos zwischen `0.1x` und `10.0x`.
  - **Bilder pro Sekunde (FPS):** Bestimme eine benutzerdefinierte Framerate.
  - **Audio-Downmix:** Wähle zwischen Mono-Downmix (optimal für KI-Transkription) oder Beibehalten der originalen Stereo-Spur.
  - **Auflösung (Skalierung):** Schalte zwischen standardmäßigem `720p` (empfohlen) oder der Originalauflösung um.
  - **Kompression (Preset):** Wähle FFmpeg x264 Presets (von `ultrafast` bis `veryslow`), um Konvertierungsgeschwindigkeit gegen Dateigröße abzuwägen.
  - **Zeitbereichs-Beschnitt (Time Range):** Schneide einen bestimmten Video-Ausschnitt aus (Startzeit und Dauer eingeben), anstatt das gesamte Video zu konvertieren.
  - **Splitting / Teilen:** Teile das Video in mehrere Stücke auf und bestimme eine eigene Überlappungszeit.
- **Custom Mode:** Ermöglicht die freie und direkte Eingabe von FFmpeg-Befehlen.

---


## 6. Architektur-Übersicht: Namespaces & Kernklassen

Der gesamte Code liegt unter `src/`, ein Ordner pro Bounded Context, mit dem Namespace `LectureExtraction.<Ordner>`. Im Folgenden findest du eine umfassende Übersicht der Namespaces und ihrer wichtigsten Klassen.

### ⚙️ `LectureExtraction.Extraction`
Verantwortlich für die vollautomatisierte Batch-Verarbeitungs-Pipeline. Orchestriert FFmpeg-Verarbeitung und KI-Inferenz sequenziell oder nebenläufig.
- **`AutoExtractionSession`**: Verwaltet die Extraktions-Pipeline auf AI Studio und Vertex. Nutzt ein **Producer-Consumer-Muster** (via `System.Threading.Channels`), was es FFmpeg ermöglicht, den nächsten Chunk zu verarbeiten, während Gemini den aktuellen analysiert. Aufgeteilt auf drei Dateien: die Kern-Pipeline (`.cs`), die Anfrage- und Streaming-Hälfte pro Segment (`.Generation.cs`, über `GenerationLoop`) und den impliziten Prefix-Cache-Warmup (`.PrefixCache.cs`). Bis 2026-10-15 hatte jede Plattform ihre eigene Session-Klasse; die beiden waren auseinandergedriftet, und die Zusammenführung hat für beide die AI-Studio-Pipeline übernommen.
- **`IExtractionBackend`**: Was je Plattform verschieden ist: Client, Namen, Endpoint-Zeile, Aufräumen hochgeladener Dateien, Audio-Vorab-Upload, ob die System Instruction Dateien annimmt, und der Refinement-Client. `AiStudioExtractionBackend` nutzt die File API; `VertexExtractionBackend` lädt in den Google Cloud Storage hoch und leert den Bucket vor und nach jedem Lauf (`GcsWorkspace`), um explodierende Speicherkosten zu verhindern, hält History-Dateien aus der reinen Text-System-Instruction heraus und besitzt den **`ExplicitContextCache`** (die CachedContent-API, auf die Part-Anfragen verweisen, solange er aktiv ist). Der Vertex-Pfad ist seit der Zusammenführung nicht gegen die echte API gelaufen.
- **`VertexAutoExtractionSession`**: Die Vertex-Session von vor der Zusammenführung, unverändert als Referenz behalten. Sie läuft statt des zusammengeführten Pfads, wenn `UseLegacyVertexSession` in `VertexAutoExtractionConfig.json` true ist (Standard false), damit der erste echte Vertex-Lauf beide vergleichen kann, bevor eine wegfällt.
- **`VideoSegmentProducer`**: Die gemeinsame FFmpeg-Producer-Hälfte der Pipeline (Splitting, Resume-from-Disk-Caching), genutzt von beiden Sessions.
- **`PartReuse`**: Die eine Regel, wann ein transkribierter Teil wiederverwendet statt neu angefragt wird: die neueste Datei aus `<name>-partN.tex` und ihren `-copy-N`-Kopien, sofern sie jünger als das Resume-Fenster ist. Die Session, ihr Vorab-Upload des nächsten Teils und `lecx plan` wenden sie alle an.
- **`TexDocumentWriter`**: Baut die `.tex`-Teil-/Gesamtdatei-Header, gemeinsam genutzt von beiden Sessions.
- **`ExtractionHelpers`**: Was übrig blieb, nachdem die untenstehenden Teile herausgelöst wurden — `ResolveNonClashingTexPath` und `LogSystemInstructionDumpAsync`.
- **`HistoryFileResolver`, `FileTreeRenderer`, `VideoBatchSelector`, `YouTubeTaskPrompt`, `ModelSyncService`**: Fokussierte Einzelteile, herausgelöst aus dem ehemaligen `ExtractionHelpers`-Sammelsurium.
- **`RefinementUiHelper`**: Konsolen-UI zum Starten und Konfigurieren des LaTeX-Refinement-Schritts aus beiden Extraktions-Sessions heraus.
- **`AudioTrackExtractor`**: Kapselt den Hintergrund-Task zur AAC-Extraktion, der als Audio-Input für das LaTeX-Refinement dient.

### 💬 `LectureExtraction.Chat` & `LectureExtraction.Refinement`
Verarbeitet die interaktive REPL (Read-Eval-Print Loop) für manuelles Debugging sowie deterministisches Post-Processing, das sich wie eine Chat-Session verhält.
- **`LatexRefinementSession`** (`Refinement`): Die Post-Processing-Engine. Füttert die überlappenden `.tex`-Chunks, die von der Extraktion generiert wurden, in Gemini ein und weist es an, Duplikate aufzulösen und sie zu einem kontinuierlichen Dokument zusammenzuführen.
- **`NonSpeechGuard`** (`Refinement`): Übernimmt nach Refinement-Schritt 2 alles außerhalb der `speech`-Blöcke wieder aus der Eingabe des Schritts; vom Modell bleiben nur die Kommentarzeilen und Zitat-Aufteilungen, die der Prompt erlaubt. Passt die Blockstruktur nicht zusammen, ändert er nichts. Siehe `docs/cli/pipeline.md`, "Refinement step 2 changes only the `speech` blocks".
- **`DirectAiChatSessionVertex` / `DirectAiChatSessionAiStudio`** (`Chat`): Die primären REPL-Klassen. Verarbeiten Benutzereingaben im Terminal, erlauben dynamische Parameteränderungen (z.B. `/set temp 0.5`), verarbeiten `/attach`-Befehle und pflegen den Gesprächsverlauf.

### 🔧 `LectureExtraction.Configuration`
Zentralisiertes Konfigurations-Management.
- **`ConfigLoader<T>`**: Ein generischer Loader, der den `Microsoft.Extensions.Configuration` Binder implementiert. Er legt die C#-Standardwerte der Klasse, optionale Überschreibungen aus `appsettings.json` und die Einstellungen aus `{Session}Config.json` übereinander; `ConfigLayerBinder` sorgt dafür, dass eine Liste der späteren Ebene die frühere ersetzt (siehe Abschnitt 1).
- **`AppConfig`**: Die globalen Einstellungen aus `appsettings.json`: Upload- und Log-Ordner sowie History-/System-Instruction-Pfade, mit denen die Chat-Sessions und der Session-Logger starten, und die Feature-Flags (`IsVertexAiEnabled` — ersetzt das alte statische Feld `Program.Activate_Vertex`, das für jede Änderung eine Neukompilierung erforderte — und `FirstChunkTimeoutMinutes`).
- **`ModelCatalog`**: Die Standard-Modellliste, mit der jede Konfiguration und jede Modellauswahl startet.

### 🎬 `LectureExtraction.Media`
Wickelt die lokale `ffmpeg.exe` Binary ab, um Medien vorzuverarbeiten, bevor sie in die Cloud gesendet werden.
- **`FfmpegToolkit`**: Ein Headless-Befehls-Builder. Enthält Logik wie `ProcessSplitVideoAsync` (Zerschneiden von Videos in 3-Minuten-Chunks mit 3 Minuten Überlappung), Dezimieren der Framerate auf 1 FPS (`-vf fps=1`) und Heruntermischen von Audio auf Mono (`-ac 1`).
- **`FfmpegInteractiveSession`**: Eine konsolenbasierte Benutzeroberfläche für Benutzer, um FFmpeg-Kompressionen manuell auszulösen, ohne die KI-Pipeline auszuführen.
- **`VideoDateParser`**: Hilfsprogramm, um Datums-Metadaten aus Dateinamen von Vorlesungsvideos zu extrahieren.

### 🖥️ `LectureExtraction.ConsoleUi`
Generische interaktive Konsolen-Prompts, unabhängig von einem konkreten Sitzungstyp.
- **`ConfigurationPrompts`**: Bestätigen-oder-Ändern-Prompts für Quellordner (`PromptForSourceFolder`), Modell und API-Key-Profil.
- **`DirectoryTreeRenderer`, `FileSelectionPrompt`**: Helfer für Ordner-Browsing und Dateibaum-Darstellung.

### 🏗️ `LectureExtraction.Infrastructure`
Querschnittsbelange wie Session-Logging und String-Hilfsfunktionen.
- **`SessionLogger`**: Erstellt automatisch Ordner mit Zeitstempeln für jede Session (z.B. `folder-X-date`). Legt rohe LaTeX-Ausgaben in Dateien ab und pflegt ein Markdown-Log (`chat_log.md`) der gesamten Interaktion.
- **`StringExtensions`**: Allgemeine String-Hilfsfunktionen (vormals `StringHelper`).

### 🌐 `LectureExtraction.GoogleAi`
- **`GoogleAiClientBuilder`**: Kapselt die rohe HTTP `HttpClient`-Erstellung sowie die API-Key-/Credential-Auflösung für AI Studio und Vertex.
- **`AttachmentUploader`**: Ein hochentwickeltes Dateisuchsystem (vormals `AttachmentHandler`). Wenn ein Benutzer `/attach file.pdf` eingibt, durchsucht diese Klasse eine Liste von Fallback-Ordnern, findet die Datei und bereitet sie für den Upload vor. Code, der seine Pfade schon kennt (Video-Teile, Audio, History-Dateien), ruft `UploadFilesAsync(paths)` auf; die Pfade werden dabei nicht zerlegt und dürfen Kommas enthalten. Nur der getippte `/attach`-Befehl wird zerlegt (`ParseAttachCommand`): `,` und `;` trennen Dateien, `|` leitet die Frage ein, außer innerhalb eines Pfads in Anführungszeichen, z. B. `/attach "D:\Analysis 2, Woche 3.pdf" | Fasse zusammen`.
- **`ApiRetryPolicy`**: Enthält Logik, um temporäre HTTP-Fehler anmutig zu behandeln, wie z.B. Ratenlimits (HTTP 429) oder temporäre Serverfehler (HTTP 503). Vormals `ApiResilience`.
- **`GenerationLoop`**: Die eine Streaming-Schleife hinter Stage 1 (AI Studio und Vertex), den Refinement-Schritten und der PDF-Reparatur: streamt über `ApiRetryPolicy`, summiert den `UsageReport` jeder Anfrage, schreibt die Gedanken-Zusammenfassungen über `ThoughtLog` (eine abgebrochene Anfrage als `ABORTED`), schickt eine leere Antwort erneut und sendet Continue-Prompts, bis der Abschluss-Marker der Stufe kommt oder das `ContinueBudget` aufgebraucht ist. Was je Stufe verschieden ist (Marker, Continue-Text, Token-Zeilen, Chat-Log, Wartezeiten, Konsolen-Texte), kommt über `GenerationLoopOptions` hinein. Eine Korrektur an der Schleife gehört hierher, nicht in eine Stufe.
- **`ApiKeyProfileResolver`**: Löst ein API-Key-Profil (0-3) zum Namen der zugehörigen Umgebungsvariable auf.
- **`PrefixCacheAnchor`**: Lädt und cached `dummy-part0.tex`, den gemeinsamen impliziten-Prefix-Cache-Anker, genutzt von beiden Extraktions-Sessions.
- **`GcsWorkspace`**: Die gemeinsame Bereinigungsroutine für den Vertex-GCS-Bucket.
- **`ModelCapabilities`**: Der einzige `SupportsThinking`-Check, gemeinsam genutzt von allen Sitzungstypen.

### 📄 `LectureExtraction.Latex`
- **`LatexToolkit`**: Hilfsmethoden, um rohe Markdown-Ausgaben von Gemini zu parsen, ` ```latex ` Codeblöcke zu entfernen und die resultierenden Strings zu validieren.
- **`LatexTimestampAdjuster`**: Werkzeuge zur Manipulation und Anpassung eingebetteter Zeitstempel im LaTeX-Dokument. Vormals `LatexTimestampHelper`.
- **`LatexResponseCleaner`**: Entfernt Modell-Geplauder/Formatierungsartefakte aus einer rohen Gemini-Antwort, bevor sie auf die Festplatte geschrieben wird.

### 🚪 `LectureExtraction.App`
- **`Program`**: Der Einstiegspunkt — nur `Main()` und die oberste Fehlerbehandlung.
- **`MainMenu`**: Die oberste interaktive Schleife.
- **`SessionFactory`**: Config-Laden → Client-Bauen → Session-Erstellen für jeden der fünf Sitzungstypen.
- **`SourceFolderMenu` / `ApiKeyProfileMenu`**: Die beiden Konfigurations-Untermenüs.

---

## 7. Bekannte API-Einschränkungen & Fehlerbehebung

### Cache Priming vs. Roundtrips (Warum nicht alles auf einmal senden?)
Um einen API-Roundtrip zu sparen, könnte man versucht sein, die `training-history` und das 30-minütige Vorlesungsvideo in einem einzigen Prompt zu senden. Dies ist jedoch höchst kontraproduktiv:
1. **Cache Priming & Fokus:** Indem man zuerst die massive Historie sendet und eine "Bestätigung" verlangt, wird das Modell gezwungen, zu parsen und einen internen Kontext-Cache aufzubauen, der rein für die Regeln zuständig ist. Wenn das eigentliche Video im nächsten Prompt eintrifft, konzentriert sich das Modell zu 100 % auf die Ausführung, anstatt seine Aufmerksamkeit zu teilen.
2. **Kontext-Überlastung:** Tausende Zeilen Historie und ein großes Video in einen einzigen Prompt zu packen, überfordert das Modell oft, was zu halluzinierten Ausgaben oder `500 Internal Server Errors` führt.

### HTTP 500 Internal Error (Google Server Absturz)
Wenn die Anwendung in einer Retry-Schleife mit einem `Internal error encountered (HTTP 500)` feststeckt, bedeutet dies, dass das Google Backend beim Verarbeiten der Anfrage abgestürzt ist. Dies ist selten ein Netzwerkproblem, sondern meist ein Prompt-/Payload-Problem. Häufige Ursachen sind:
- **Überladene System Instruction:** Wenn `LoadHistoryIntoSystemInstruction` auf `true` gesetzt ist, injiziert die Pipeline die Historie in die `SystemInstruction` (mittels XML-Framing `<file path="...">` und direktem Inlining von Bildern als `InlineData`). Hinweis: Obwohl Inline-Bytes Probleme mit externen Datei-URIs umgehen, können extrem große Historien weiterhin Nutzlastgrenzen überschreiten. Bei Instabilitäten setze es auf `false`, damit die Historie stattdessen gestaffelt in den User-Prompt geladen wird, was das Limit umgeht, aber den Cache-Hit weiterhin garantiert.
- **Thinking bei Flash-Modellen:** Die Aktivierung eines hohen `ThinkingBudget` oder `ThinkingLevel: HIGH` bei "Flash"-Modellen (z. B. `gemini-3.5-flash`) während der Verarbeitung massiver Videodateien ist sehr instabil und verursacht häufig 500er Fehler. **Lösung:** Deaktiviere "Thinking" für Flash-Modelle oder wechsle zu einem "Pro"-Modell (`gemini-2.5-pro` / `gemini-3.1-pro-preview`), welches Logik nativ verarbeitet.
- **Beschädigte Video-Chunks:** Gelegentlich generiert FFmpeg einen Chunk mit einem beschädigten Frame-Header, der den Gemini Vision Encoder zum Absturz bringt. **Lösung:** Lösche den `tmp`-Ordner des Videos, um FFmpeg zu zwingen, das Video neu zu schneiden.

---

## 8. Fehlerbehandlung & ApiRetryPolicy

Die Klasse `ApiRetryPolicy` kapselt alle Aufrufe an die Google API und behandelt transiente Fehler anmutig:
- **Ratenlimits (HTTP 429):** Wenn die API ein `retryDelay` zurückgibt, parst die Anwendung dies und wartet genau so lange plus einen Puffer von 20 Sekunden.
- **Hohe Auslastung (HTTP 503):** Wenn der Server überlastet ist ("high demand"), initiiert die Anwendung einen Backoff (konfigurierbar über `HighDemandDelaySeconds`, standardmäßig 3 Minuten / 180 Sekunden).
- **Internetausfall (DNS / keine Route):** Eine Anfrage, deren Host nicht aufgelöst werden kann, hat Google nie erreicht. Die Anwendung prüft per reinem TCP-Verbindungsaufbau zu Port 443 (kein TLS, keine HTTP-Anfrage, keine Quota; eine DNS-Abfrage allein antwortet bei fehlender Route aus dem Cache) alle `NetworkPollIntervalSeconds` (Standard 15) höchstens `NetworkMaxWaitMinutes` (Standard 30) lang und sendet sofort neu; der fehlgeschlagene Versuch zählt nicht zum Retry-Limit. Nach der vollen Wartezeit scheitert die Anfrage wie nach ausgeschöpften Retries. Beide Schlüssel stehen in der AI-Studio- und der Vertex-Config; `NetworkAvailability` enthält Hilfsfunktion und Klassifizierung. Andere Verbindungsabbrüche (Reset, "Error while copying content to a stream") sind ein gezählter Retry nach `ConnectionErrorDelaySeconds` (Standard 60 s; bis 2026-10-23 fest 300 s).
- **Linearer Backoff:** Für allgemeine 500er Fehler verwendet die Anwendung einen linearen Backoff (z. B. 45s, 75s, 105s) bis zu 8 Mal.
- **Wartezeiten zwischen Anfragen sind keine Retries:** Die Pausen zwischen Anfragen (`VideoPartDelaySeconds`, `SystemInstructionDelaySeconds`, `HistoryRateLimitDelaySeconds`, `FileActivationDelaySeconds`, `RateLimitDelaySeconds` bei Vertex und in jedem Refinement-Schritt) takten nur den Lauf, also bedeutet `0` keine Wartezeit. Bis 2026-10-26 machten die meisten davon aus `0` stillschweigend 130 s (65 s bei den beiden Warm-up-Pausen). Die Retry-Backoffs behalten eine Untergrenze: `HighDemandDelaySeconds` und `ConnectionErrorDelaySeconds` fallen bei `0` auf ihren Standard zurück, und ein Refinement-Schritt mit `RateLimitDelaySeconds: 0` beginnt seinen Retry-Backoff trotzdem bei 130 s, weil eine sofort wiederholte fehlgeschlagene Anfrage wieder fehlschlägt.
- **Interaktiver Skip:** Während jeder Wartezeit kann der Benutzer `Enter` drücken, um einen sofortigen Retry zu erzwingen, oder `Ctrl+C`, um die Verzögerung abzubrechen. Das Abbrechen der Verzögerung bricht den aktuellen Video-Chunk ab und bewegt den Batch-Prozessor sicher zur nächsten Datei, ohne dass die Anwendung abstürzt.

---

## 9. Offene Messungen (Einstellungen, die noch nicht empirisch belegt sind)

Zwei Schalter sind implementiert und getestet, aber ihr Default beruht auf
Überlegung, nicht auf Messung. Beide lassen sich nur in einem echten
(kostenpflichtigen) Extraktionslauf beantworten:

- **`DisableThinkingDuringWarmUp`** — Beim Warm-up-Handshake die `Denk-Tokens`
  in der Ausgabe ablesen und danach entscheiden, ob Thinking dort abgeschaltet
  gehört. *Aktueller Default: `false` (Thinking wie bei Part 1).*

- **`SendDummyFileWithEachWarmUpRound`** — Mit `InlinePrecedingLecTexParts: false`
  ist der User-Turn-Text aller Parts strukturell identisch, daher könnten auch
  intermediäre Batch-Handshakes von einem Cache-Slot profitieren. Ob der
  Mehrverbrauch an Tokens sich durch die höhere Cache-Trefferrate amortisiert,
  ist nicht gemessen. *Aktueller Default: `false` (Dummy nur beim letzten Batch).*

- **`InlinePrecedingLecTexParts`** — Ein kurzes Video je einmal mit `true`
  (Inline) und `false` (Upload) laufen lassen und den Default aus dem
  gemessenen Token-/Zeitverbrauch setzen.
  *Aktueller Config-Wert: `false` (Upload-Modus).*

- **`InlineHistory`** — Ein Teil mit `false` (History hochgeladen) laufen lassen:
  die `Minutenlimit-Schätzung` sollte von ~97 % auf ~57 % fallen. Dann die
  Ausgabe mit einem Inline-Lauf desselben Teils vergleichen (Eröffnungsabschnitt,
  `hh:mm:ss`-Zeitstempel, Füllwörter in jedem Viertel, TikZ-Regeln), bevor
  der Schalter in der Config auf `false` bleibt. *Default: `true` (wie bisher).*

### Struktur-Roadmap (offen)

Erledigt:

1. Die vier Streaming-Schleifen laufen über eine (`GenerationLoop`, siehe Abschnitt 6).
2. Die beiden Extraktions-Sessions sind zu `AutoExtractionSession` mit
   `IExtractionBackend` zusammengeführt (2026-10-15). Vertex war gegenüber AI Studio
   auseinandergedriftet; auf Wunsch des Nutzers folgt Vertex jetzt der AI-Studio-Pipeline
   (Setup, Prompt-Wortlaut, gestaffelter Warm-up, Resume samt Resume-Warm-up,
   YouTube-Runner, Refinement-Übergabe inkl. `UseChosenModelForRestOfPipeline`; die
   Continue-Pause ist `RateLimitDelaySeconds` statt fest 150 s). **Offen: ein echter
   Vertex-Lauf**, um das zu prüfen - bis dahin ist der Vertex-Pfad nur durch Tests
   abgedeckt. Dabei auch klären, ob Vertex Dateien in der System Instruction annimmt
   (heute bleiben History-Dateien dort heraus, wie vorher) und ob der explizite
   Context-Cache bei der jetzigen Reihenfolge (am Ende des Setups) greift.
   Die alte `VertexAutoExtractionSession` bleibt bis dahin unverändert erhalten
   (`UseLegacyVertexSession: true` in `VertexAutoExtractionConfig.json` startet sie),
   damit der Testlauf beide vergleichen kann. Erst danach entscheiden, ob sie wegfällt.

Offen, je ein eigener Durchgang ohne Verhaltensänderung:

3. **Gespeicherte Einstellungen von Laufzeit-Optionen trennen.** CLI-Overrides,
   `--thoughts` und `UseChosenModelForRestOfPipeline` ändern heute die geladene
   Config, die `ConfigLoader` zurückschreibt, und machen so `*Config.json`
   schmutzig. Einstellungen read-only machen und ein Run-Options-Objekt aus
   Einstellungen plus Overrides bauen. Dabei wandern die globalen veränderlichen
   Statics in Instanz-Zustand: `AttachmentUploader.HasJustUploaded`,
   `InteractiveDelay.LastGenerationCompletionTimeUtc`,
   `ApiRetryPolicy.DefaultHighDemandDelaySeconds`/`DelayAsync`,
   `ConfigStore.SaveEnabled`/`DirectoryOverride`.
4. **(Niedrige Priorität) Ein JSON-Manifest pro Vorlesung**, statt die
   Header-Kommentare der .tex-Dateien (`PART_START_SECONDS`, Thought summaries)
   mit Regexen zurückzulesen.
