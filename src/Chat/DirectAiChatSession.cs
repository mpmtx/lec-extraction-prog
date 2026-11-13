using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] Core REPL (Read-Eval-Print Loop) manager for conversational AI interfaces across AI Studio and Vertex backends.
/// Maintains stateful chat history and handles API interactions using the Google.GenAI SDK.
/// [Human] Das Herzstück des Chatbots für AI Studio und Vertex. Liest Benutzereingaben, verarbeitet Befehle, sendet Anfragen und streamt Antworten.
/// </summary>
public class DirectAiChatSession {
    public static readonly string[] AvailableModels = [.. ModelCatalog.Default, "gemini-2.5-flash"];

    private readonly IChatConfig _config;
    private readonly string UploadFolderPath;
    private readonly string[] HistoryPreloadPaths;
    private readonly string InitialHistoryPrompt = "Here is the material from my history. In the history, you may find some tex code from the previous weeks of the lecture. Don't treat them as source-material for the transcription. Please read it carefully. Acknowledge the receipt without exception with exactly the following text: '[AI-Model: {0}] Material [...] received and analyzed. I am standing by for your instructions.' Wait for my next instructions afterwards.";
    private readonly string GcsBucketName;
    private readonly string LogFolderPath;
    private readonly string SystemInstructionPath;
    private string? _systemInstructionText;
    private readonly DirectAiChatSessionAiStudioGenerationConfig AIParams;
    private readonly bool IsAiStudio;
    private readonly AttachmentUploader _attachmentHandler;
    private readonly SessionLogger _sessionLogger;
    private Client _client;
    private int _activeApiProfile;
    private readonly string[] _apiProfileEnvNames;
    private readonly bool _canChangeApiKeyProfile;
    private readonly Func<int, Client?>? _rebuildClient;
    private readonly ResponseStreamPrinter _streamPrinter = new();
    private string _activeModel = "";

    public DirectAiChatSession(
        Client client,
        IChatConfig config,
        SessionLogger logger,
        AttachmentUploader attachmentHandler,
        bool isAiStudio,
        bool canChangeApiKeyProfile = false,
        int activeApiProfile = 0,
        string[]? apiKeyProfileEnvNames = null,
        Func<int, Client?>? rebuildClient = null) {
        _config = config;
        _client = client;
        _sessionLogger = logger;
        _attachmentHandler = attachmentHandler;
        IsAiStudio = isAiStudio;
        _canChangeApiKeyProfile = canChangeApiKeyProfile;
        _activeApiProfile = activeApiProfile;
        _apiProfileEnvNames = apiKeyProfileEnvNames ?? [];
        _rebuildClient = rebuildClient;
        UploadFolderPath = config.UploadFolder;
        HistoryPreloadPaths = config.HistoryPreloadPaths;
        LogFolderPath = config.LogFolder;
        GcsBucketName = config.GcsBucketName;
        SystemInstructionPath = config.SystemInstructionPath;
        _activeModel = config.CurrentModel;

        AIParams = new DirectAiChatSessionAiStudioGenerationConfig {
            Temperature = config.Temperature,
            TopP = config.TopP,
            TopK = config.TopK,
            MaxOutputTokens = config.MaxOutputTokens,
            ThinkingBudget = config.ThinkingBudget,
            ThinkingLevel = config.ThinkingLevel,
            UseGoogleSearch = config.UseGoogleSearch
        };
    }

    public async Task StartAsync(bool skipSetup = false, bool loadSystemPrompt = true, bool loadHistory = true) {
        if (skipSetup) {
            bool skipLoadedSysPrompt = false;
            string? skipInitialInput = null;

            if (loadSystemPrompt && !string.IsNullOrWhiteSpace(SystemInstructionPath) && System.IO.File.Exists(SystemInstructionPath)) {
                _systemInstructionText = await System.IO.File.ReadAllTextAsync(SystemInstructionPath);
                Ui.Success($"System-Prompt '{Path.GetFileName(SystemInstructionPath)}' erfolgreich als System Instruction geladen!");
                skipLoadedSysPrompt = true;
            }

            if (loadHistory) {
                var historyPrompt = GetInitialHistoryCommand(_activeModel);
                if (historyPrompt.IsValue) {
                    skipInitialInput = historyPrompt.Value;
                }
            }

            await CleanupGcsBucketAsync();
            _sessionLogger.InitializeSession();
            _sessionLogger.SetSessionMetadata(skipLoadedSysPrompt, skipInitialInput != null);
            await _sessionLogger.LogSessionSetupAsync();

            await RunChatSessionAsync(skipInitialInput);
            return;
        }

        int step = 0;
        bool loadedSysPrompt = false;
        string? initialInput = null;

        while (true) {
            switch (step) {
                case 0: {
                    string platformLabel = IsAiStudio ? "AI Studio" : "Vertex AI";
                    var model = ConfigurationPrompts.ConfirmOrChangeModel(_config.CurrentModel, platformLabel, _config.Model, newModel => {
                        int idx = Array.IndexOf(_config.Model, newModel);
                        if (idx >= 0) _config.CurrentModelIndex = idx;
                        _config.CurrentModel = newModel;
                        _config.Save();
                    });
                    if (!model.IsValue) return;
                    _activeModel = model.Value!;
                    step = 1;
                    break;
                }

                case 1: {
                    if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
                        Ui.Blank();
                        Ui.Info("Folgende System Instruction ist konfiguriert:", "Setup");
                        FileTreeRenderer.PrintFileTree([SystemInstructionPath]);
                    }

                    var answer = _canChangeApiKeyProfile
                        ? SetupQuestionPrompt.Ask("[Setup] System Instruction laden?", ChangeApiKeyProfileInteractive)
                        : SetupQuestionPrompt.Ask("[Setup] System Instruction laden?");
                    if (answer.IsRestart || answer.IsBack) { step = 0; break; }
                    if (!answer.IsValue) return;

                    loadedSysPrompt = false;
                    _systemInstructionText = null;

                    if (answer.Value) {
                        if (!string.IsNullOrWhiteSpace(SystemInstructionPath) && System.IO.File.Exists(SystemInstructionPath)) {
                            _systemInstructionText = await System.IO.File.ReadAllTextAsync(SystemInstructionPath);
                            Ui.Success($"System-Prompt '{Path.GetFileName(SystemInstructionPath)}' erfolgreich als System Instruction geladen!");
                            loadedSysPrompt = true;
                        }
                        else {
                            Ui.Warn($"System-Prompt-Datei nicht gefunden: {SystemInstructionPath}");
                        }
                    }
                    else {
                        Ui.Info("System Instruction wird ignoriert.");
                    }

                    step = 2;
                    break;
                }

                case 2: {
                    var history = GetInitialHistoryCommand(_activeModel);
                    if (history.IsRestart) { step = 0; break; }
                    if (history.IsBack) { step = 1; break; }
                    if (!history.IsValue) return;
                    initialInput = history.Value;
                    step = 3;
                    break;
                }

                default: {
                    await CleanupGcsBucketAsync();

                    _sessionLogger.InitializeSession();
                    _sessionLogger.SetSessionMetadata(loadedSysPrompt, initialInput != null);
                    await _sessionLogger.LogSessionSetupAsync();

                    await RunChatSessionAsync(initialInput);
                    return;
                }
            }
        }
    }

    private async Task RunChatSessionAsync(string? initialInput) {
        var history = new List<Content>();
        var initialHistory = new List<Content>(history);
        string userName = IsAiStudio ? "AI Studio User" : "Vertex AI User";

        string headerLabel = IsAiStudio
            ? $"Chat gestartet ({_activeModel} | API Profil: {_activeApiProfile})"
            : $"Vertex Chat gestartet ({_activeModel})";
        Ui.Header(headerLabel);
        WriteCommandHelp();

        while (true) {
            var turnCancel = new ConsoleCancelScope();

            try {
                string? input;
                if (initialInput != null) {
                    input = initialInput;
                    Ui.Blank();
                    Ui.RawLine($"{userName}: {input}");
                    initialInput = null;
                }
                else {
                    if (!Console.IsInputRedirected) {
                        while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                    }
                    Ui.Blank();
                    Ui.Raw($"{userName}: ");
                    input = Console.ReadLine();
                }

                if (string.IsNullOrWhiteSpace(input)) continue;
                if (input.Equals("exit", StringComparison.CurrentCultureIgnoreCase) || input.Equals("quit", StringComparison.CurrentCultureIgnoreCase)) break;

                var parts = new List<Part>();
                string promptText = input;

                bool isCommandHandled = await TryHandleBuiltInCommandsAsync(input, history, initialHistory, parts, newPrompt => promptText = newPrompt, turnCancel.Token);

                if (isCommandHandled) {
                    if (!input.Trim().TrimStart('/').StartsWith("attach ", StringComparison.OrdinalIgnoreCase)) {
                        continue;
                    }
                    if (parts.Count == 0) continue;
                }

                if (!string.IsNullOrWhiteSpace(promptText)) parts.Add(new Part { Text = promptText });
                else if (parts.Count == 0) continue;

                history.Add(new Content { Role = "user", Parts = parts });

                try {
                    await StreamGeminiResponseAsync(_activeModel, history, input, promptText, userName);
                }
                catch (Exception ex) {
                    Ui.Blank();
                    Ui.Error($"Der Fehler konnte nicht durch einen automatischen Retry behoben werden: {ex.Describe()}", "Abbruch");

                    if (ex.Message.Contains("Service agents are being provisioned", StringComparison.OrdinalIgnoreCase)) {
                        Ui.Blank();
                        Ui.Info("Google Cloud richtet gerade im Hintergrund die Zugriffsrechte (Service Agents) für deinen Bucket ein. Das passiert meistens nur beim allerersten Mal im Projekt. Bitte warte einfach 2-3 Minuten und versuche die Anfrage dann erneut!", "Vertex");
                    }

                    if (history.Count > 0 && history.Last().Role == "user") {
                        history.RemoveAt(history.Count - 1);
                    }
                }
            }
            finally {
                turnCancel.Dispose();
            }
        }

        Ui.Blank();
        Ui.Info("Chat beendet. Räume temporäre Dateien im Cloud Storage auf...");
        await CleanupGcsBucketAsync();
    }

    private void ChangeApiKeyProfileInteractive() {
        if (!_canChangeApiKeyProfile) return;
        var profile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
            _activeApiProfile,
            "Direct AI Studio Chat",
            envNames: _apiProfileEnvNames,
            allowBack: false);

        if (profile.IsValue && profile.Value != _activeApiProfile) {
            ApplyApiKeyProfile(profile.Value);
        }
    }

    private void WriteCommandHelp() {
        Ui.Step("📋 Befehle");
        Ui.Detail("📜 help / commands         -> Zeigt diese Befehlsübersicht erneut an");
        Ui.Detail("🚪 exit / quit             -> Beendet den Chat");
        Ui.Detail("🧹 clear / reset           -> Löscht den bisherigen Chat-Verlauf (Gedächtnis)");
        Ui.Detail("📎 attach datei1, datei2 | Frage   -> Hängt Dateien an und stellt eine Frage dazu. Pfade mit Komma in \"...\" setzen.");
        Ui.Detail("                           (Tipp: Das '|' trennt Dateien und Frage. Ohne '|' wird nochmal nachgefragt.)");
        Ui.Detail("🌡️  set temp [wert]         -> Ändert die Temperatur für die nächste Antwort (z.B. set temp 0.5)");
        Ui.Detail("🔢 set tokens [wert]       -> Ändert das MaxOutputTokens-Limit dynamisch (z.B. set tokens 8192)");
        Ui.Detail("🧠 set thinking-budget [w] -> Setzt das Thinking Budget für Gemini 2.5 (z.B. 4096)");
        Ui.Detail("🧠 set thinking-level [l]  -> Setzt das Thinking Level für Gemini 3.x/4 (z.B. HIGH, XHIGH)");
        Ui.Detail("🔍 set grounding [on/off]  -> Aktiviert/Deaktiviert Google Search Grounding (Websuche)");
        Ui.Detail("🤖 set model [name/index]  -> Ändert das aktive Modell mitten im Chat");
        if (_canChangeApiKeyProfile) {
            Ui.Detail("🔑 change-key [0-3]        -> Wechselt das API-Key Profil für diese Session (0 für dediziert)");
        }
    }

    private async Task<bool> TryHandleBuiltInCommandsAsync(string input, List<Content> history, List<Content> initialHistory, List<Part> parts, Action<string> updatePromptText, CancellationToken cancellationToken) {
        var command = ChatCommandParser.Parse(input);

        if (command.Kind == ChatCommandKind.ChangeApiKeyProfile && !_canChangeApiKeyProfile) {
            return false;
        }

        if (!command.IsValid) {
            Ui.Error(command.Error!);
            return true;
        }

        switch (command.Kind) {
            case ChatCommandKind.Help:
                WriteCommandHelp();
                return true;

            case ChatCommandKind.Clear:
                history.Clear();
                history.AddRange(initialHistory);
                Ui.Blank();
                Ui.Info(IsAiStudio ? "Gedächtnis gelöscht! Gemini startet komplett frisch." : "Gedächtnis gelöscht! Vertex Modell startet frisch.");
                return true;

            case ChatCommandKind.SetTemperature:
                AIParams.Temperature = command.Number;
                Ui.Info($"Temperatur für die nächste(n) Antwort(en) auf {AIParams.Temperature:F1} gesetzt.");
                return true;

            case ChatCommandKind.SetMaxTokens:
                AIParams.MaxOutputTokens = command.Integer;
                int effectiveTokens = ModelCapabilities.ResolveMaxOutputTokens(_activeModel, AIParams.MaxOutputTokens);
                Ui.Info(effectiveTokens == AIParams.MaxOutputTokens
                    ? $"MaxOutputTokens für die nächste(n) Antwort(en) auf {AIParams.MaxOutputTokens} gesetzt."
                    : $"MaxOutputTokens auf {AIParams.MaxOutputTokens} gesetzt (effektiv {effectiveTokens} für älteres Modell '{_activeModel}').");
                return true;

            case ChatCommandKind.SetThinkingBudget:
                AIParams.ThinkingBudget = command.Integer;
                Ui.Info($"ThinkingBudget für die nächste(n) Antwort(en) auf {AIParams.ThinkingBudget} gesetzt (relevant für Gemini 2.5 Modelle).");
                return true;

            case ChatCommandKind.SetThinkingLevel:
                AIParams.ThinkingLevel = command.Text;
                string? effectiveLevel = ModelCapabilities.ResolveThinkingLevel(_activeModel, AIParams.ThinkingLevel);
                Ui.Info(string.Equals(effectiveLevel, AIParams.ThinkingLevel, StringComparison.OrdinalIgnoreCase)
                    ? $"ThinkingLevel für die nächste(n) Antwort(en) auf '{AIParams.ThinkingLevel}' gesetzt (relevant für Gemini 3.x/4 Modelle)."
                    : $"ThinkingLevel auf '{AIParams.ThinkingLevel}' gesetzt (effektiv '{effectiveLevel}' für älteres Modell '{_activeModel}').");
                return true;

            case ChatCommandKind.SetGrounding:
                AIParams.UseGoogleSearch = command.Flag;
                if (command.Flag) {
                    Ui.Info("Google Search Grounding für die nächste(n) Antwort(en) AKTIVIERT.");
                }
                else {
                    Ui.Info("Google Search Grounding für die nächste(n) Antwort(en) DEAKTIVIERT.");
                }
                return true;

            case ChatCommandKind.ChangeApiKeyProfile:
                ApplyApiKeyProfile(command.Integer);
                return true;

            case ChatCommandKind.SetModel:
                ApplySetModel(command.Text);
                return true;

            case ChatCommandKind.Attach: {
                var (success, parsedPrompt, attachmentParts) = await _attachmentHandler.ProcessAttachmentsAsync(input.TrimStart('/'), cancellationToken: cancellationToken);
                if (!success) return true;

                parts.AddRange(attachmentParts);
                updatePromptText(parsedPrompt);
                return true;
            }

            default:
                return false;
        }
    }

    private void ApplySetModel(string arg) {
        string newModel = string.IsNullOrEmpty(arg)
            ? ChatModelPrompt.Pick(_config.Model.Length > 0 ? _config.Model : AvailableModels)
            : ChatModelPrompt.Resolve(arg, _config.Model.Length > 0 ? _config.Model : AvailableModels);

        if (string.IsNullOrEmpty(newModel)) return;

        _activeModel = newModel;
        Ui.Info($"Aktives Modell für die nächste(n) Antwort(en) auf '{_activeModel}' geändert.");

        if (Ui.Confirm("Möchten Sie diese Änderung permanent in der Konfiguration speichern?", true)) {
            int idx = Array.IndexOf(_config.Model, _activeModel);
            if (idx >= 0) _config.CurrentModelIndex = idx;
            _config.CurrentModel = _activeModel;
            _config.Save();
            Ui.Success("💾 Das neue Modell wurde permanent in der Konfiguration gespeichert.");
        }
        else {
            Ui.Info("Die Änderung ist nur vorübergehend.");
        }
    }

    private async Task StreamGeminiResponseAsync(string selectedModel, List<Content> history, string input, string promptText, string userName) {
        Ui.Blank();
        Ui.Raw($"{selectedModel} (Drücke Strg+C zum Abbrechen): ");

        var (config, apiContents) = BuildChatRequestConfig(selectedModel, history);
        var (fullResponse, inputTokens, outputTokens, cachedTokens) =
            await _streamPrinter.StreamAsync(_client, selectedModel, apiContents, config, IsAiStudio ? WaitForQuotaWindowAsync : null);

        if (!string.IsNullOrWhiteSpace(fullResponse)) {
            history.Add(new Content { Role = "model", Parts = [new() { Text = fullResponse }] });
            await _sessionLogger.LogChatAsync(input, promptText, selectedModel, fullResponse, userName, inputTokens, outputTokens, cachedTokens);
        }
        else {
            if (history.Count > 0) {
                history.RemoveAt(history.Count - 1);
            }
        }
    }

    private (GenerateContentConfig Config, List<Content> ApiContents) BuildChatRequestConfig(string selectedModel, List<Content> history) {
        var config = new GenerateContentConfig {
            Temperature = AIParams.Temperature,
            TopP = AIParams.TopP,
            TopK = AIParams.TopK,
            MaxOutputTokens = ModelCapabilities.ResolveMaxOutputTokens(selectedModel, AIParams.MaxOutputTokens),
            AutomaticContinuation = ModelCapabilities.ResolveSdkAutomaticContinuation(selectedModel, enableForLongDecode: true)
        };

        if (AIParams.UseGoogleSearch) {
            config.Tools = [new Tool { GoogleSearch = new GoogleSearch() }];
        }

        config.ThinkingConfig = ThinkingConfigFactory.Build(selectedModel, AIParams.ThinkingBudget, AIParams.ThinkingLevel);

        var apiContents = history;

        if (!string.IsNullOrWhiteSpace(_systemInstructionText)) {
            if (ModelCapabilities.RequiresSystemInstructionInFirstUserTurn(selectedModel)) {
                var modifiedHistory = new List<Content>(history.Count);
                bool prepended = false;
                foreach (var content in history) {
                    if (!prepended && content.Role == "user") {
                        var newParts = content.Parts?.ToList() ?? [];
                        newParts.Insert(0, new Part { Text = $"System Instruction:\n{_systemInstructionText}\n\n---\n\nUser Request:\n" });
                        modifiedHistory.Add(new Content { Role = "user", Parts = newParts });
                        prepended = true;
                    }
                    else {
                        modifiedHistory.Add(content);
                    }
                }
                apiContents = modifiedHistory;
                config.SystemInstruction = null;
            }
            else {
                config.SystemInstruction = new Content { Role = "system", Parts = [new() { Text = _systemInstructionText }] };
            }
        }

        return (config, apiContents);
    }

    private static async Task<bool> WaitForQuotaWindowAsync() {
        bool proceed = true;
        if (!InteractiveDelay.IsInSmartDelay) {
            proceed = await InteractiveDelay.SmartDelayAsync(130, "Warte 130 Sekunden vor API-Request an Google AI Studio (Token-Refill Schutz für Max-Token/Quota)...");
        }
        AttachmentUploader.HasJustUploaded = false;
        return proceed;
    }

    private PromptResult<string?> GetInitialHistoryCommand(string selectedModel) {
        if (HistoryPreloadPaths == null || HistoryPreloadPaths.Length == 0) {
            return PromptResult.FromValue<string?>(null);
        }

        var allHistoryFiles = new List<string>();
        var notFoundPaths = new List<string>();

        foreach (var path in HistoryPreloadPaths.Where(p => !string.IsNullOrWhiteSpace(p))) {
            if (System.IO.File.Exists(path)) {
                if (!Extraction.HistoryFileResolver.IsDeletedPromptFile(path)) {
                    allHistoryFiles.Add(Path.GetFullPath(path));
                }
            }
            else if (Directory.Exists(path)) {
                // Folders are never expanded: only files named one by one are loaded, in their order.
                Ui.Warn($"History-Pfad ist ein Ordner und wird nicht geladen (nur einzeln genannte Dateien): {path}", "Setup");
            }
            else {
                notFoundPaths.Add(path);
            }
        }

        if (notFoundPaths.Count > 0) {
            Ui.Blank();
            Ui.Warn("Folgende History-Pfade wurden nicht gefunden:", "Setup");
            foreach (var path in notFoundPaths) {
                Ui.Detail($"- {path}");
            }
        }

        var distinctFiles = allHistoryFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (!string.IsNullOrWhiteSpace(SystemInstructionPath)) {
            distinctFiles = [.. distinctFiles.Where(f => !string.Equals(f, Path.GetFullPath(SystemInstructionPath), StringComparison.OrdinalIgnoreCase))];
        }

        if (distinctFiles.Count == 0) {
            return PromptResult.FromValue<string?>(null);
        }

        Ui.Blank();
        Ui.Info("Folgende History-Dateien wurden in den konfigurierten Pfaden gefunden:", "Setup");
        FileTreeRenderer.PrintFileTree(distinctFiles);

        var answer = _canChangeApiKeyProfile
            ? SetupQuestionPrompt.Ask("Sollen diese Dateien als History geladen werden?", ChangeApiKeyProfileInteractive)
            : SetupQuestionPrompt.Ask("Sollen diese Dateien als History geladen werden?");
        if (!answer.IsValue) return new PromptResult<string?>(answer.Outcome, null);

        if (!answer.Value) return PromptResult.FromValue<string?>(null);

        string fileList = string.Join(", ", distinctFiles.Select(p => $"\"{p}\""));
        return PromptResult.FromValue<string?>($"attach {fileList} | {string.Format(InitialHistoryPrompt, selectedModel)}");
    }

    private void ApplyApiKeyProfile(int newProfile) {
        if (!_canChangeApiKeyProfile || _rebuildClient == null) return;

        var newClient = _rebuildClient(newProfile);
        if (newClient != null) {
            _client = newClient;
            _attachmentHandler.UpdateClient(_client);
            _activeApiProfile = newProfile;
            Ui.Success($"API-Key Profil für diese Session erfolgreich auf {newProfile} gewechselt!");
        }
        else {
            Ui.Error($"Konnte API-Key für Profil {newProfile} nicht finden. Der Wechsel wurde abgebrochen.");
        }
    }

    private async Task CleanupGcsBucketAsync() {
        if (IsAiStudio) return;

        Ui.Blank();
        Ui.Info("Initiating Vertex AI Enterprise Session...", "System");
        await GcsWorkspace.PurgeAsync(GcsBucketName, _config.VerboseConsoleOutput);
    }
}
