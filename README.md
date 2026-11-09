# 🤖🎥 AI Lecture Extraction & Processing Pipeline

[![.NET 10.0](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Google Gemini](https://img.shields.io/badge/Google%20Gemini-Multimodal%20AI-4285F4?style=flat-square&logo=google)](https://ai.google.dev/)
[![Vertex AI](https://img.shields.io/badge/Google%20Cloud-Vertex%20AI-4285F4?style=flat-square&logo=googlecloud)](https://cloud.google.com/vertex-ai)
[![FFmpeg](https://img.shields.io/badge/FFmpeg-Audio%2FVideo-007808?style=flat-square&logo=ffmpeg)](https://ffmpeg.org/)

*(See below for the German version / Deutsche Version unten)*

<p align="center">
  <a href="#-english"><b>🇬🇧 English</b></a> •
  <a href="#-deutsch"><b>🇩🇪 Deutsch</b></a> •
  <a href="Documentation.md"><b>📖 Detailed Documentation</b></a>
</p>

---

<a id="-english"></a>
## 🇬🇧 English

This project is a C# console utility designed to automate the transcription and translation of academic lecture videos into LaTeX documents using Google Gemini's multimodal capabilities.

It bridges the gap between local video preprocessing (FFmpeg) and cloud-based AI inference, supporting both an **Interactive Chat Mode** and an **Automated Batch Extraction Pipeline**. It supports free/developer environments via **Google AI Studio** and enterprise workloads via **Google Cloud Vertex AI**.

---

### 📐 System Architecture & Workflow

```
+------------------------------------------------------------------------------------+
|                                 INPUT VIDEO FOLDER                                 |
+------------------------------------------------------------------------------------+
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  🎬 Media: Preprocessing & Token Optimization                                      |
|  • Compress Video -> 1 FPS (Ideal for blackboard/formula capture)                  |
|  • Downmix Audio -> Mono AAC (Reduces bandwidth & upload latency)                  |
|  • Slice into overlapping 3-minute segments (Prevents boundary context loss)       |
+------------------------------------------------------------------------------------+
                                           │
               ┌───────────────────────────┴───────────────────────────┐
               ▼                                                       ▼
+-------------------------------------+ +--------------------------------------------+
| 🌐 Google AI Studio (File API)      | | ☁️ Google Cloud Vertex AI (Enterprise)     |
| • Producer-Consumer Channel Pipeline| | • Managed GCS Bucket Uploads               |
| • Automated Context Caching         | | • Auto-Purging after each chunk            |
+-------------------------------------+ +--------------------------------------------+
               └───────────────────────────┬───────────────────────────┘
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  📝 Fragmented LaTeX Transcripts (.tex chunks with custom spoken/formula tags)     |
+------------------------------------------------------------------------------------+
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  ✍️ Refinement: LaTeX Refinement Merger                                            |
|  • Deterministic AI Pass (Temp = 0.0) | Merges overlaps & resolves syntax errors   |
+------------------------------------------------------------------------------------+
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  📄 COMPILABLE ACADEMIC LATEX DOCUMENT & FINAL PDF                                 |
+------------------------------------------------------------------------------------+
```

---

### ✨ Core Features

- **Two AI Ecosystems:** Switch between Google AI Studio (using the File API) and Vertex AI (using Google Cloud Storage buckets).
- **Intelligent Context Caching & Auto-Reload:** Automatically hashes modular Markdown instruction trees (`transcription.md`, `hard-specs.md`, etc.). If any file changes on disk, the remote Google Cloud Cache is automatically invalidated and refreshed, saving massive prompt upload costs across video batches.
- **File list first:** The system instruction opens with a flat list of every prompt file, in the order the files follow, so Gemini gets a table of contents before the rules.
- **Strict Payload Purity:** Enforces strict compliance with Vertex AI `SystemInstruction` text-only constraints by dynamically routing binary historical attachments (e.g., handwriting samples) to the `contents` payload.
- **Automated Extraction Pipeline:** Process folders of lecture videos. Videos are sliced into overlapping chunks to prevent context loss, transcribed sequentially by the AI, and cached locally.
- **LaTeX Refinement & Merging:** A post-processing session that merges overlapping `.tex` chunks generated by the AI into a single, compilable document.
- **FFmpeg Token Optimization:** Compresses video framerates to 1 FPS (ideal for blackboard content) and downmixes audio to mono, saving API tokens, cloud storage costs, and upload time.
- **Flexible Extraction Options:** Toggle `GenerateOffsetFiles` to automatically generate timestamp-corrected LaTeX chunks, or use `GenerateAudioFile` to extract an AAC of the entire lecture before processing.
- **Interactive Multimodal REPL:** Chat directly with the models. Use the `/attach` command to load code, PDFs, or large videos directly into the model's context (put a path that contains a comma in quotes).

---

### 🎯 Target Use-Cases & Market Potential (AI generated)

This pipeline solves a notoriously difficult problem that traditional closed-captioning systems fail at: converting highly technical, math-heavy video lectures into structured text. 

- **Accessibility for Deaf and Hard of Hearing (DHH) Students:** Standard auto-captions fail at STEM subjects (e.g., calculus, physics). This tool parses visual blackboard formulas and audio simultaneously to output perfectly formatted LaTeX, helping universities comply with strict accessibility obligations.
- **University "Lecture Capture" Integrations:** Easily runs alongside existing platforms (like Panopto or Echo360) to automatically process recorded lectures overnight, attaching beautifully formatted LaTeX PDFs to the video player by morning.
- **Study Material Platforms (B2C):** A backend engine for consumer-facing apps where STEM students can paste a YouTube link (e.g., MIT OpenCourseWare) and receive a `.tex` file and compiled PDF.
- **Textbook Publishers & EdTech:** Allows platforms with massive back-catalogs of educational video content to automatically generate high-quality textbook companions, study guides, or supplementary reading materials.

---

### 🧠 Codebase Philosophy: Dual-Commenting

This project is built for human-AI collaboration. To ensure long-term maintainability, the codebase utilizes a strict dual-commenting paradigm:
- **`[AI Context]` (English):** Explains the *why* to future LLMs (e.g., prompt engineering rationale, token-saving strategies, API constraints).
- **`[Human]` (German):** Explains the *how* to human developers, keeping business logic clear and accessible.

---

### 📂 Project Structure & Architecture

All code lives under `src/`, one folder per bounded context, namespaced `LectureExtraction.<Folder>`.

#### ⚙️ 1. `LectureExtraction.Extraction` (The Batch Pipeline)
An automated pipeline for processing video folders.

- **`AutoExtractionSession`**: Utilizes a **Producer-Consumer pattern** via `System.Threading.Channels`. FFmpeg processes videos in a background task (Producer) while Gemini processes the output sequentially via the API (Consumer) to improve throughput. One session for both platforms: an `IExtractionBackend` (`AiStudioExtractionBackend`, `VertexExtractionBackend`) carries what differs, e.g. Vertex's purge of the temporary Google Cloud Storage bucket before and after every run to prevent unnecessary cloud storage billing.

#### 💬 2. `LectureExtraction.Chat` & `LectureExtraction.Refinement` (Interactive & Post-Processing)
Handles all manual interactions, file resolution, and the final LaTeX assembly.

- **`LatexRefinementSession`** (`Refinement`): The final step in the pipeline. It reads all fragmented `.tex` files generated by the extraction phase and tasks a deterministic Gemini model (Temperature = 0.35) to merge overlapping sentences and equations into a final `refined_output.tex`.
- **`DirectAiChatSessionAiStudio` & `DirectAiChatSessionVertex`** (`Chat`): The core REPL managers for interactive chatting, dynamic parameter tuning (`/set temp`), and manual prompt debugging.
- **`AttachmentUploader`** (`GoogleAi`): A file discovery system. Finds local files across multiple fallback directories and orchestrates uploads (Text is embedded directly; Media is pushed to File API or GCS).
- **`SessionLogger`** (`Infrastructure`): Automatically creates timestamped directories for every session, storing Markdown logs (`chat_log.md`) and numbering all raw LaTeX outputs.

#### 🎬 3. `LectureExtraction.Media`
Responsible for local audio and video processing prior to AI upload.

- **`FfmpegToolkit`**: Headless FFmpeg command builder. Used for `ProcessSplitVideoAsync` (cutting lectures into exactly 3-minute overlapping segments to prevent sentence truncation) and `ProcessGeneralVideoAsync`.

#### 📦 4. Additional Namespaces & Infrastructure
Supporting modules that ensure reliability and clean modularization.

- **`LectureExtraction.GoogleAi`**: Client builders (`GoogleAiClientBuilder`), upload orchestration (`AttachmentUploader`), and retry handling (`ApiRetryPolicy`, exponential backoff).
- **`LectureExtraction.Latex`**: Tools like `LatexCompiler`, `LatexResponseCleaner`, and `LatexTimestampAdjuster` for cleaning and synchronizing timestamps across `.tex` chunks.
- **`LectureExtraction.Configuration`**: Type-safe JSON configuration models (`AppConfig`, `VertexAutoExtractionConfig`, etc.), plus feature flags read from `appsettings.json` (e.g. `IsVertexAiEnabled`).
- **`LectureExtraction.App`**: The entry point (`Program`), the main menu loop (`MainMenu`), and the config-load/client-build/session-construct wiring (`SessionFactory`).

#### 🚀 5. Main Menu Workflow (`App/MainMenu.cs`)
Upon starting the application, you are presented with 7 operational modes:
1. **Automated Content Extraction:** The batch processing pipeline.
2. **Google AI Studio Chat:** Interactive developer endpoint session.
3. **Vertex AI Chat:** Interactive enterprise endpoint session (disabled by default — see `IsVertexAiEnabled` in `appsettings.json`).
4. **FFmpeg Manager:** Manual, local video optimization utility.
5. **LaTeX Refinement:** Post-processing merger for generated transcripts.
6. **Source Folders:** Inspect/update source folders across all session profiles.
7. **API Key Profiles:** Inspect/update the active API-key profile per session type.

---

### 🛠️ Prerequisites & Setup

1. **FFmpeg:** Must be installed on the system and accessible as a global environment variable (PATH).
2. **Google AI Studio:** Requires environment variables for specific API Keys depending on the mode:
   - `API_KEY-ai-studio-test-project-1` (Interactive Chat)
   - `API_KEY-automated-content-extraction` (AutoExtraction Pipeline)
   - `API_KEY-latex-refinement` (Refinement Pipeline)
3. **Google Cloud Vertex AI:** Requires the Google Cloud CLI (`gcloud`) to be installed and authenticated via `gcloud auth application-default login`. The linked project must have an active Billing Account.
4. **System Instruction (`gemini.md`):** The application relies on a comprehensive system prompt file that dictates the strict LaTeX formatting rules and custom environments (e.g., `\begin{speech}`). Its absolute path goes into the session's `*Config.json` (`Sources.SystemInstructionPaths`, or `SystemInstructionPath` for the chats) before running; the configuration classes ship no personal paths.
5. **Build & checks:** `dotnet build` and `dotnet test lec-extraction-prog.slnx`. The coding rules from `gemini.md` §3 run as build warnings (`.editorconfig`, `Directory.Build.props`); the GitHub Actions workflow (`.github/workflows/build.yml`) builds and tests every push and PR on Windows and treats every warning as an error.

---

### 🚨 Troubleshooting: HTTP 500 & Retry Loops

If the application gets stuck with `[Exception Caught] Type: ServerError` (HTTP 500), it means the Google backend crashed while processing your specific prompt or video chunk.
- **Do not use "Thinking" with Flash Models:** Combining `gemini-3.5-flash` with a high `ThinkingBudget` and a 30-minute video chunk is highly unstable. Switch to a "Pro" model (e.g., `gemini-2.5-pro` or `gemini-3.1-pro-preview`) if you want to use reasoning.
- **ThinkingConfig during Warm-Up:** By default, the warm-up handshake uses the **same `ThinkingConfig`** as the actual Part-1 request so the Google implicit prefix-cache slot matches. If that wastes tokens, set `DisableThinkingDuringWarmUp: true` to force `ThinkingBudget = 0` for the handshake only.
- **Do not overload the System Instruction:** Setting `LoadHistoryIntoSystemInstruction` to `true` embeds history files and images directly into the System Instruction via XML framing and InlineData. While much more stable, massive history payloads can still exceed API limits. If you encounter issues, set it to `false` so the history is batched into the User Prompt instead.
- **Don't trust the "Gecacht" figure:** Google's reported cached-token count is not reliable and the implicit cache is best-effort - the same prefix can show a large hit in one run and 0 in the next. Never plan rate limits or cost around it (see Documentation §4.4).
- **Skip the Error:** Press `Ctrl+C` during a retry delay to gracefully skip the corrupted video chunk and proceed to the next file.

For more details on why cache-priming is used instead of single-prompt processing, see the Documentation.

---

### ⌨️ Command Line / Kommandozeile

Besides the interactive menu, the whole pipeline runs headlessly — one command per stage,
built so a script or an AI agent can drive it. **Starting the program without arguments
still opens the familiar menu**; arguments select the CLI.

```bash
lecx plan --folder "D:/lecture-videos/analysis2"   # what a run would do — free, no API call
lecx run  --video "…/02-19-2026-thursday-week1-….mp4"   # the whole pipeline, one video
lecx batch --folder "…" --workers "profile=1,profile=2" # several videos, parallel processes
```

Stages are separately runnable (`media segment`, `extract run`, `refine run`,
`pdf compile`), config writeback is off by default so an unattended run cannot change your
settings, and exit code `6` means *partial* success.

**→ [Full CLI documentation](docs/cli/README.md)** · agents should start at
**[docs/cli/agents.md](docs/cli/agents.md)**.

---

### 📖 Detailed Documentation / Detail-Dokumentation

For a deeper dive into the system's architecture, including configuration quirks (Array Merging), API constraints, multimodal tokenization differences between Gemini versions, and advanced reasoning parameters (`ThinkingBudget`, `ThinkingLevel`), please refer to the **[Detailed System Documentation](Documentation.md)**.

<br>

---

<a id="-deutsch"></a>
## 🇩🇪 Deutsch

Dieses Projekt ist ein C#-Konsolenwerkzeug, das entwickelt wurde, um die Transkription und Übersetzung von akademischen Vorlesungsvideos in LaTeX-Skripte mithilfe von Google Geminis multimodalen Fähigkeiten zu automatisieren.

Es schlägt die Brücke zwischen lokaler Videovorverarbeitung (FFmpeg) und cloudbasierter KI-Inferenz und unterstützt sowohl einen **interaktiven Chat-Modus** als auch eine **automatisierte Batch-Extraktions-Pipeline**. Es unterstützt kostenlose/Developer-Umgebungen via **Google AI Studio** sowie Enterprise-Workloads via **Google Cloud Vertex AI**.

---

### 📐 Systemarchitektur & Ablauf

```
+------------------------------------------------------------------------------------+
|                                 EINGABE-VIDEOORDNER                                |
+------------------------------------------------------------------------------------+
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  🎬 Media: Vorverarbeitung & Token-Optimierung                                     |
|  • Videokomprimierung auf 1 FPS (Perfekt für Tafelbilder und Formelentwicklung)    |
|  • Audio-Downmix auf Mono AAC (Minimiert Upload-Zeit und Bandbreite)               |
|  • Schnitt in überlappende 3-Minuten-Segmente (Verhindert Satzabbruch an Grenzen)  |
+------------------------------------------------------------------------------------+
                                           │
               ┌───────────────────────────┴───────────────────────────┐
               ▼                                                       ▼
+-------------------------------------+ +--------------------------------------------+
| 🌐 Google AI Studio (File API)      | | ☁️ Google Cloud Vertex AI (Enterprise)     |
| • Producer-Consumer Channel Pipeline| | • Verwaltete GCS Bucket Uploads            |
| • Automatisches Context Caching     | | • Automatische Bereinigung pro Segment     |
+-------------------------------------+ +--------------------------------------------+
               └───────────────────────────┬───────────────────────────┘
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  📝 Fragmentierte LaTeX-Transkripte (.tex Fragmente mit Spoken-/Formel-Tags)       |
+------------------------------------------------------------------------------------+
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  ✍️ Refinement: LaTeX Refinement Merger                                            |
|  • Deterministischer KI-Durchlauf (Temp = 0.0) | Verschmelzung & Syntaxkorrektur   |
+------------------------------------------------------------------------------------+
                                           │
                                           ▼
+------------------------------------------------------------------------------------+
|  📄 KOMPILIERBARES AKADEMISCHES LATEX-DOKUMENT & FINALES PDF                       |
+------------------------------------------------------------------------------------+
```

---

### ✨ Hauptfeatures

- **Zwei KI-Ökosysteme:** Wechsel zwischen Google AI Studio (über die File API) und Vertex AI (über Google Cloud Storage Buckets).
- **Intelligentes Context Caching & Auto-Reload:** Berechnet automatische Checksummen über modulare System-Instruction-Bäume (`transcription.md`, `hard-specs.md` etc.). Bei Textänderungen wird der Remote-Cache bei Google vollautomatisch invalidiert und erneuert.
- **Dateiliste zuerst:** Die System Instruction beginnt mit einer flachen Liste aller Prompt-Dateien in Sendereihenfolge, ein Inhaltsverzeichnis vor den Regeln.
- **Strikte Payload-Reinheit:** Hält Vertex AI `SystemInstruction`-Vorgaben ein, indem binäre Trainingsverläufe (z. B. Handschrift-Bilder) dynamisch in den `contents`-Körper ausgelagert werden.
- **Automatisierte Extraktions-Pipeline:** Verarbeitet Ordner von Vorlesungsvideos. Videos werden in überlappende Segmente geschnitten (um Kontextverlust zu verhindern), sequenziell von der KI transkribiert und lokal zwischengespeichert.
- **LaTeX Refinement & Merging:** Ein Post-Processing-Schritt, der überlappende `.tex`-Stücke zu einem einzigen, kompilierbaren Gesamtdokument verschmilzt.
- **FFmpeg Token-Optimierung:** Komprimiert Videos auf 1 FPS (ideal für Tafeln) und mischt Audio zu Mono ab, was API-Tokens, Cloud-Speicherkosten und Upload-Zeit spart.
- **Flexible Extraktions-Optionen:** Aktiviere `GenerateOffsetFiles` für automatisch zeitkorrigierte LaTeX-Stücke, oder nutze `GenerateAudioFile`, um vor der Verarbeitung eine komplette AAC der Vorlesung zu exportieren.
- **Interaktive REPL:** Chatte direkt mit den Modellen. Nutze den Befehl `/attach`, um Code, PDFs oder große Videos direkt in den Kontext des Modells zu laden (einen Pfad mit Komma in Anführungszeichen setzen).

---

### 🎯 Zielgruppen & Marktpotenzial

Diese Pipeline löst ein notorisch schwieriges Problem, an dem traditionelle Untertitelsysteme scheitern: die Umwandlung hochtechnischer, mathematiklastiger Vorlesungsvideos in strukturierten Text.

- **Barrierefreiheit für gehörlose und schwerhörige (DHH) Studierende:** Standard-Untertitel versagen bei MINT-Fächern (z. B. Analysis, Physik). Dieses Tool analysiert visuelle Tafelbilder und Audio gleichzeitig, um perfekt formatiertes LaTeX auszugeben, was Universitäten hilft, ihre strengen Verpflichtungen zur Barrierefreiheit zu erfüllen.
- **Integration in universitäre "Lecture Capture"-Systeme:** Kann problemlos parallel zu bestehenden Plattformen (wie Panopto oder Echo360) betrieben werden, um aufgezeichnete Vorlesungen über Nacht automatisch zu verarbeiten und bis zum nächsten Morgen formatierte LaTeX-PDFs an den Videoplayer anzuhängen.
- **Plattformen für Lernmaterialien (B2C):** Eine Backend-Engine für kundenorientierte Apps, bei denen MINT-Studierende einen YouTube-Link (z. B. MIT OpenCourseWare) einfügen können und eine `.tex`-Datei sowie ein kompiliertes PDF erhalten.
- **Schulbuchverlage & EdTech:** Ermöglicht es Plattformen mit massiven Archiven an Video-Lerninhalten, automatisch hochwertige Lehrbuchbegleiter, Studienführer oder ergänzende Lesematerialien zu generieren.

---

### 🧠 Codebasis-Philosophie: Dual-Commenting

Dieses Projekt ist für die Zusammenarbeit zwischen Mensch und KI konzipiert. Um die langfristige Wartbarkeit zu sichern, nutzt der Code ein striktes Dual-Commenting-Paradigma:
- **`[AI Context]` (Englisch):** Erklärt zukünftigen KIs das *Warum* (z.B. Prompting-Gründe, Strategien zum Token-Sparen, API-Limits).
- **`[Human]` (Deutsch):** Erklärt menschlichen Entwicklern das *Wie*, um die Geschäftslogik klar und zugänglich zu halten.

---

### 📂 Projektstruktur & Architektur

Der gesamte Code liegt unter `src/`, ein Ordner pro Bounded Context, mit dem Namespace `LectureExtraction.<Ordner>`.

#### ⚙️ 1. `LectureExtraction.Extraction` (Die Batch Pipeline)
Eine automatisierte Pipeline zur Verarbeitung von Video-Ordnern.

- **`AutoExtractionSession`**: Nutzt ein **Producer-Consumer-Muster** via `System.Threading.Channels`. FFmpeg verarbeitet Videos im Hintergrund (Producer), während Gemini die Ergebnisse sequenziell abarbeitet (Consumer), um den Durchsatz zu verbessern. Eine Session für beide Plattformen: ein `IExtractionBackend` (`AiStudioExtractionBackend`, `VertexExtractionBackend`) trägt die Unterschiede, z.B. bei Vertex das Leeren des temporären Google Cloud Storage Buckets vor und nach jedem Lauf, um unnötige Cloud-Kosten zu verhindern.

#### 💬 2. `LectureExtraction.Chat` & `LectureExtraction.Refinement` (Interaktiv & Post-Processing)
Kümmert sich um manuelle Interaktionen, Dateiauflösung und den finalen LaTeX-Zusammenbau.

- **`LatexRefinementSession`** (`Refinement`): Der letzte Schritt in der Pipeline. Er liest alle von der Extraktion generierten `.tex`-Fragmente und beauftragt ein deterministisches Gemini-Modell (Temperature = 0.35), überlappende Sätze und Gleichungen in einem `refined_output.tex` zusammenzuführen.
- **`DirectAiChatSessionAiStudio` & `DirectAiChatSessionVertex`** (`Chat`): Die Haupt-REPL-Manager für das interaktive Chatten, dynamische Parameter-Tuning (`/set temp`) und manuelle Prompt-Debugging.
- **`AttachmentUploader`** (`GoogleAi`): Ein Dateisuchsystem. Sucht in konfigurierten Ordnern nach Dateien und orchestriert den Upload.
- **`SessionLogger`** (`Infrastructure`): Erstellt automatisch für jede Sitzung einen Ordner (`folder-X-date`) und sichert dort Markdown-Logs sowie fortlaufend nummerierte `.tex`-Outputs.

#### 🎬 3. `LectureExtraction.Media`
Verantwortlich für die lokale Videoverarbeitung vor dem KI-Upload.

- **`FfmpegToolkit`**: Headless FFmpeg Builder. Essenziell für `ProcessSplitVideoAsync` (schneidet Vorlesungen in überlappende 3-Minuten-Fragmente, damit Sätze nicht in der Mitte abbrechen).

#### 📦 4. Weitere Namespaces & Infrastruktur
Ergänzende Module für Stabilität, Logging und saubere Modularisierung.

- **`LectureExtraction.GoogleAi`**: Client-Builder (`GoogleAiClientBuilder`), Upload-Orchestrierung (`AttachmentUploader`) und Wiederholungslogik (`ApiRetryPolicy`, Exponential Backoff).
- **`LectureExtraction.Latex`**: Tools wie `LatexCompiler`, `LatexResponseCleaner` und `LatexTimestampAdjuster` zur Bereinigung und Zeitstempelsynchronisation.
- **`LectureExtraction.Configuration`**: Typensichere JSON-Konfigurationsmodelle (`AppConfig`, `VertexAutoExtractionConfig` u.a.) sowie Feature-Flags aus `appsettings.json` (z.B. `IsVertexAiEnabled`).
- **`LectureExtraction.App`**: Der Einstiegspunkt (`Program`), die Hauptmenü-Schleife (`MainMenu`) und das Config-Laden/Client-Bauen/Session-Erstellen (`SessionFactory`).

#### 🚀 5. Hauptmenü Workflow (`App/MainMenu.cs`)
Beim Start der Anwendung stehen 7 Betriebsmodi zur Verfügung:
1. **Automated Content Extraction:** Die vollautomatisierte Batch-Pipeline.
2. **Google AI Studio Chat:** Interaktive Chat-Sitzung (Developer Endpoint).
3. **Vertex AI Chat:** Interaktive Chat-Sitzung (Enterprise Endpoint, standardmäßig deaktiviert — siehe `IsVertexAiEnabled` in `appsettings.json`).
4. **FFmpeg Manager:** Lokale, manuelle Video-Optimierung.
5. **LaTeX Refinement:** Post-Processing, um die generierten Transkripte zu verschmelzen.
6. **Quellordner:** Quellordner über alle Sitzungsprofile hinweg ansehen/ändern.
7. **API-Key Profile:** Das aktive API-Key-Profil pro Sitzungstyp ansehen/ändern.

---

### 🛠️ Vorbedingungen & Setup

1. **FFmpeg:** Muss auf dem System installiert und als globale Umgebungsvariable (PATH) erreichbar sein.
2. **Google AI Studio:** Erfordert Windows-Umgebungsvariablen für spezifische API-Keys (je nach Modus):
   - `API_KEY-ai-studio-test-project-1` (Für den interaktiven Chat)
   - `API_KEY-automated-content-extraction` (Für die AutoExtraction Pipeline)
   - `API_KEY-latex-refinement` (Für das Post-Processing)
3. **Google Cloud Vertex AI:** Setzt voraus, dass du die Google Cloud CLI (`gcloud`) installiert hast und über `gcloud auth application-default login` authentifiziert bist. Das verknüpfte Projekt muss über ein aktives Rechnungskonto (Billing Account) verfügen.
4. **System Instruction (`gemini.md`):** Die Anwendung benötigt zwingend eine System-Instruktionsdatei, die der KI die genauen LaTeX-Formatierungsregeln und Custom-Environments (z.B. `\begin{speech}`) vorgibt. Der absolute Pfad zu dieser Datei gehört vor dem Start in die `*Config.json` der Session (`Sources.SystemInstructionPaths`, bei den Chats `SystemInstructionPath`); die Konfigurationsklassen enthalten keine persönlichen Pfade.
5. **Build & Prüfungen:** `dotnet build` und `dotnet test lec-extraction-prog.slnx`. Die Coding-Regeln aus `gemini.md` §3 laufen als Build-Warnungen (`.editorconfig`, `Directory.Build.props`); der GitHub-Actions-Workflow (`.github/workflows/build.yml`) baut und testet jeden Push und PR unter Windows und behandelt jede Warnung als Fehler.

---

### 🚨 Troubleshooting: HTTP 500 & Retry Loops

Wenn die Anwendung mit `[Exception Caught] Type: ServerError` (HTTP 500) stehen bleibt, bedeutet das meist, dass das Google-Backend bei einem spezifischen Prompt oder Video-Segment ins Stocken geraten ist:
- **Kein "Thinking" bei Flash-Modellen:** Die Kombination aus `gemini-3.5-flash` mit einem hohen `ThinkingBudget` und 30 Minuten langen Videos ist instabil. Wechsle für intensives Reasoning lieber auf ein "Pro"-Modell (z. B. `gemini-2.5-pro` oder `gemini-3.1-pro-preview`).
- **System Instructions nicht überladen:** Wenn `LoadHistoryIntoSystemInstruction` auf `true` gesetzt ist, werden Historie und Bilder direkt eingebettet. Das läuft generell stabil, aber zu riesige Payloads können die API-Limits überschreiten. Falls Instabilitäten auftreten, setze es auf `false`, damit die History stattdessen gestaffelt in den User-Prompt geladen wird.
- **Der "Gecacht"-Zahl nicht trauen:** Googles gemeldete Zahl gecachter Tokens ist nicht verlässlich und der implizite Cache ist Best-Effort - derselbe Präfix kann in einem Lauf einen grossen Hit zeigen und im nächsten 0. Rate-Limits und Kosten nie darauf planen (siehe Documentation §4.4).
- **Fehler überspringen:** Drücke einfach `Strg+C` während der Wartezeit (Retry Delay), um das betroffene Video-Segment sauber zu überspringen und mit der nächsten Datei weiterzumachen.

Weitere Details dazu, warum wir Cache-Priming statt einzelner Riesen-Prompts nutzen, findest du in der ausführlichen Dokumentation.

---

### ⌨️ Kommandozeile

Neben dem interaktiven Menü läuft die komplette Pipeline auch ohne Menü — ein Befehl pro
Stufe, gebaut für Skripte und KI-Agenten. **Ohne Argumente startet weiterhin das gewohnte
Menü**; erst Argumente wählen den CLI-Modus.

```bash
lecx plan --folder "D:/lecture-videos/analysis2"   # was ein Lauf tun würde — kostenlos
lecx run  --video "…/02-19-2026-thursday-week1-….mp4"   # komplette Pipeline, ein Video
lecx batch --folder "…" --workers "profile=1,profile=2" # mehrere Videos parallel
```

Die Stufen (`media segment`, `extract run`, `refine run`, `pdf compile`) sind einzeln
ausführbar, die Konfiguration wird im CLI-Modus standardmäßig **nicht** zurückgeschrieben,
und Exit-Code `6` bedeutet *teilweiser* Erfolg.

**→ [Vollständige CLI-Dokumentation](docs/cli/README.md)** · für Agenten:
**[docs/cli/agents.md](docs/cli/agents.md)**.

---

### 📖 Detailed Documentation / Detail-Dokumentation

Weiterführende Informationen zur Systemarchitektur, den Konfigurations-Besonderheiten (Array Merging), API-Einschränkungen und den Reasoning-Parametern (`ThinkingBudget`, `ThinkingLevel`) findest du in der **[Ausführlichen System-Dokumentation](Documentation.md)**.
