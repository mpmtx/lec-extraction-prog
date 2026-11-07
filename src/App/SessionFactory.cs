using System;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Chat;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;
using LectureExtraction.Media;
using LectureExtraction.Refinement;

namespace LectureExtraction.App;

/// <summary>
/// [AI Context] Wires up each of MainMenu's session types: loads the relevant config(s), resolves
/// credentials, builds the Google GenAI client, and constructs + starts the session. Extracted from
/// Program.cs (Phase 6) — this is the "manual Dependency Injection" bootstrapping the file's original
/// doc comment described, now named and separated from the menu loop that triggers it.
/// [Human] Baut jede der Sessions aus dem Hauptmenü zusammen: lädt Konfiguration, löst Credentials auf,
/// erstellt den Google-Client und startet die Session.
/// </summary>
public static class SessionFactory {
    public static async Task RunDirectAiStudioChatAsync() {
        var config = ConfigLoader<DirectAiChatSessionAiStudioConfig>.Load();
        var profile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
            config.ActiveApiProfile,
            "Direct AI Studio Chat",
            newProfile => {
                config.ActiveApiProfile = newProfile;
                ConfigLoader<DirectAiChatSessionAiStudioConfig>.Save(config);
            },
            config.AiStudioApiKeyEnvNames
        );
        if (!profile.IsValue) return;
        config.ActiveApiProfile = profile.Value;

        string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);

        // ResolveApiKeyByName has already printed which variable is missing; stop here instead of
        // building a client with a placeholder key that only fails at the first request.
        string? apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName);
        if (apiKey == null) {
            Ui.Detail("Umgebungsvariable setzen oder ein anderes API-Key-Profil wählen.", "Direct AI Studio Chat");
            return;
        }
        Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
        var attachmentHandler = new AttachmentUploader(client, config.UploadFolder, config.IncludePaths, true, config.GcsBucketName);
        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var chatSession = new DirectAiChatSessionAiStudio(client, config, sessionLogger, attachmentHandler, isAiStudio: true);
        await chatSession.StartAsync();
    }

    public static async Task RunDirectVertexChatAsync() {
        var config = ConfigLoader<DirectAiChatSessionVertexConfig>.Load();
        Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
        var attachmentHandler = new AttachmentUploader(client, config.UploadFolder, config.IncludePaths, false, config.GcsBucketName);
        var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
        var chatSession = new DirectAiChatSessionVertex(client, config, sessionLogger, attachmentHandler);
        await chatSession.StartAsync();
    }

    public static async Task RunFfmpegSessionAsync() {
        var ffmpegConfig = ConfigLoader<FfmpegSessionConfig>.Load();
        var ffmpegMenu = new FfmpegInteractiveSession(ffmpegConfig);
        await ffmpegMenu.StartAsync();
    }

    /// <summary>
    /// [AI Context] The extraction setup wizard. The backend choice and the per-backend setup are
    /// separate loops so that "back" out of the first setup prompt lands on the backend choice
    /// again, and "back" out of *that* lands in the main menu.
    /// [Human] Setup-Assistent für die Extraktion: Backend wählen, dann die Einstellungen -
    /// "Zurück" springt jeweils einen Schritt zurück.
    /// </summary>
    public static async Task RunAutoExtractionAsync() {
        while (true) {
            bool useVertex = false;

            if (AppConfig.IsVertexAiEnabled) {
                var backend = Ui.Select(
                    "Welche API soll für die automatisierte Extraktion genutzt werden?",
                    [("1) Google AI Studio", false), ("2) Google Cloud Vertex AI", true)],
                    backLabel: Ui.ExitChoiceLabel);

                if (!backend.IsValue) return;
                useVertex = backend.Value;
            }
            else {
                Ui.Warn("Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false in appsettings.json). Starte automatisch mit Google AI Studio...", "Kostenschutz");
            }

            bool wentBackPastFirstStep = useVertex
                ? await RunVertexAutoExtractionAsync()
                : await RunAiStudioAutoExtractionAsync();

            // Backing out of the first setup prompt returns to the backend choice - unless there was
            // none, in which case the only step further back is the main menu.
            if (!wentBackPastFirstStep || !AppConfig.IsVertexAiEnabled) return;
        }
    }

    private enum SetupStepResult { Advanced, WentBack, Aborted }

    private static SetupStepResult PromptFolderStep<TConfig>(TConfig config) where TConfig : class, IAutoExtractionConfig, new() {
        var folder = ConfigurationPrompts.PromptForSourceFolder(config.SourceFolder, newFolder => {
            config.SourceFolder = newFolder;
            ConfigLoader<TConfig>.Save(config);
        }, config.PredefinedSourceFolders);
        if (folder.IsBack) return SetupStepResult.WentBack;
        if (!folder.IsValue) return SetupStepResult.Aborted;
        config.SourceFolder = folder.Value!;
        return SetupStepResult.Advanced;
    }

    private static SetupStepResult PromptModelStep<TConfig>(TConfig config, string sessionTitle, bool isVertex) where TConfig : class, IAutoExtractionConfig, new() {
        var model = ConfigurationPrompts.ConfirmOrChangeModel(config.CurrentModel, sessionTitle, config.Model, newModel => {
            int idx = Array.IndexOf(config.Model, newModel);
            if (idx >= 0) config.CurrentModelIndex = idx;
            config.CurrentModel = newModel;
            ConfigLoader<TConfig>.Save(config);
            ModelSyncService.SyncModelToRefinementConfig(newModel, isVertex);
        });
        if (model.IsBack) return SetupStepResult.WentBack;
        if (!model.IsValue) return SetupStepResult.Aborted;

        config.CurrentModel = model.Value!;
        config.CurrentModelIndex = Math.Max(0, Array.IndexOf(config.Model, config.CurrentModel));
        ModelSyncService.SyncModelToRefinementConfig(config.CurrentModel, isVertex);
        DirectoryTreeRenderer.DisplayFolderSummary(config.SourceFolder);
        return SetupStepResult.Advanced;
    }

    /// <summary>
    /// [AI Context] AI Studio extraction setup: source folder → model → API key profile → session.
    /// Returns <c>true</c> when the user stepped back out of the first prompt, so the caller can
    /// re-ask the backend question.
    /// [Human] Einrichtung der AI-Studio-Extraktion in drei Schritten, jeweils mit "Zurück".
    /// </summary>
    private static async Task<bool> RunAiStudioAutoExtractionAsync() {
        var config = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
        int step = 0;

        while (true) {
            switch (step) {
                case 0: {
                    var res = PromptFolderStep(config);
                    if (res == SetupStepResult.WentBack) return true;
                    if (res == SetupStepResult.Aborted) return false;
                    step = 1;
                    break;
                }

                case 1: {
                    var res = PromptModelStep(config, "AI Studio Auto-Extraktion", isVertex: false);
                    if (res == SetupStepResult.WentBack) { step = 0; break; }
                    if (res == SetupStepResult.Aborted) return false;
                    step = 2;
                    break;
                }

                case 2: {
                    var profile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                        config.ActiveApiProfile,
                        "AI Studio Auto-Extraktion",
                        newProfile => {
                            config.ActiveApiProfile = newProfile;
                            ConfigLoader<AiStudioAutoExtractionConfig>.Save(config);
                        },
                        config.AiStudioApiKeyEnvNames
                    );
                    if (profile.IsBack) { step = 1; break; }
                    if (!profile.IsValue) return false;
                    config.ActiveApiProfile = profile.Value;
                    step = 3;
                    break;
                }

                default: {
                    string envName = ApiKeyProfileResolver.Resolve(config.ActiveApiProfile, config.AiStudioApiKeyEnvNames);
                    // Fail before FFmpeg and uploads run, not at the first request (the variable's name is already printed).
                    string? apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName);
                    if (apiKey == null) {
                        Ui.Detail("Umgebungsvariable setzen oder ein anderes API-Key-Profil wählen.", "AI Studio Auto-Extraktion");
                        step = 2;
                        break;
                    }
                    Client client = GoogleAiClientBuilder.BuildAiStudioClient(apiKey);
                    var attachmentHandler = new AttachmentUploader(client, config.SourceFolder, [config.SourceFolder], true, "", config.GoogleVideoFps, config.InlineHistoryImages, config.FileActivationDelaySeconds, config.VideoUploadTimeoutSeconds, config.VideoUploadMaxRetries) {
                        ClientFactory = () => GoogleAiClientBuilder.BuildAiStudioClient(apiKey)
                    };
                    var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
                    var latexRefinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();
                    var session = new AutoExtractionSession(new AiStudioExtractionBackend(client, config), config, attachmentHandler, sessionLogger, latexRefinementConfig);
                    await session.StartAsync();
                    return false;
                }
            }
        }
    }

    /// <summary>
    /// [AI Context] Vertex extraction setup: source folder → model → session. Same contract as
    /// <see cref="RunAiStudioAutoExtractionAsync"/>; Vertex authenticates via ADC, so there is no
    /// API-key step.
    /// [Human] Einrichtung der Vertex-Extraktion; ohne API-Key-Schritt, da Vertex ADC nutzt.
    /// </summary>
    private static async Task<bool> RunVertexAutoExtractionAsync() {
        var config = ConfigLoader<VertexAutoExtractionConfig>.Load();
        int step = 0;

        while (true) {
            switch (step) {
                case 0: {
                    var res = PromptFolderStep(config);
                    if (res == SetupStepResult.WentBack) return true;
                    if (res == SetupStepResult.Aborted) return false;
                    step = 1;
                    break;
                }

                case 1: {
                    var res = PromptModelStep(config, "Vertex AI Auto-Extraktion", isVertex: true);
                    if (res == SetupStepResult.WentBack) { step = 0; break; }
                    if (res == SetupStepResult.Aborted) return false;
                    step = 2;
                    break;
                }

                default: {
                    Client client = GoogleAiClientBuilder.BuildVertexClient(config.ProjectId, config.Location);
                    var attachmentHandler = new AttachmentUploader(client, config.SourceFolder, [config.SourceFolder], false, config.GcsBucketName, config.GoogleVideoFps);
                    var sessionLogger = new SessionLogger(ConfigLoader<SessionLoggerConfig>.Load());
                    var latexRefinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();
                    // Both Vertex sessions stay until a real run shows which one to keep.
                    if (config.UseLegacyVertexSession) {
                        Ui.Info("UseLegacyVertexSession = true: Starte die alte VertexAutoExtractionSession.", "Vertex");
                        await new VertexAutoExtractionSession(client, config, attachmentHandler, sessionLogger, latexRefinementConfig).StartAsync();
                    }
                    else {
                        await new AutoExtractionSession(new VertexExtractionBackend(client, config), config, attachmentHandler, sessionLogger, latexRefinementConfig).StartAsync();
                    }
                    return false;
                }
            }
        }
    }

    public static async Task RunLatexRefinementAsync() {
        var config = ConfigLoader<LatexRefinementSessionConfig>.Load();
        var extractionConfig = ConfigLoader<AiStudioAutoExtractionConfig>.Load();
        await RefinementUiHelper.StartInteractiveRefinementAsync(config, extractionConfig);
    }
}
