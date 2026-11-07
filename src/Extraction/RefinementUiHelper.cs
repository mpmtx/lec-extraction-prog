using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Refinement;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] The interactive front door to the refinement pipeline. The selection sequence
/// (options → folder → step → .tex file → audio → scope) runs as a step machine so every prompt can
/// offer "back": each case knows which step precedes it, and picking the wrong .tex file no longer
/// means restarting from the main menu.
/// [Human] Menüführung für das LaTeX-Refinement; jede Frage lässt sich mit "Zurück" korrigieren.
/// </summary>
public static class RefinementUiHelper {
    private const string NoAudioChoice = "(Ohne Audio fortfahren)";

    public static async Task StartInteractiveRefinementAsync(LatexRefinementSessionConfig refinementConfig, IAutoExtractionConfig extractionConfig) {
        Ui.Header("Interaktiver LaTeX Refinement Modus");

        refinementConfig = ConfigLoader<LatexRefinementSessionConfig>.Load();
        if (!AppConfig.IsVertexAiEnabled && refinementConfig.UseVertex) {
            Ui.Warn("Google Cloud Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false)! Wechsle für LaTeX Refinement automatisch auf AI Studio.", "Kostenschutz");
            refinementConfig.UseVertex = false;
            ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
        }

        var uiConfig = ConfigLoader<RefinementUiHelperConfig>.Load();

        string searchFolder = uiConfig.PredefinedPath;
        string[] texFiles = [];
        string stepChoice = "4";
        string selectedTex = "";
        string? selectedAudio = null;
        bool runToEnd = false;

        int step = 0;

        while (true) {
            switch (step) {
                case 0:
                    if (!ShowBackendOptionsMenu(refinementConfig)) return;
                    step = 1;
                    break;

                case 1: {
                    var folder = ConfigurationPrompts.PromptForSourceFolder(searchFolder, newFolder => {
                        uiConfig.PredefinedPath = newFolder;
                        ConfigLoader<RefinementUiHelperConfig>.Save(uiConfig);
                    });
                    if (folder.IsBack) { step = 0; break; }
                    if (!folder.IsValue) return;
                    searchFolder = folder.Value!;

                    if (!Directory.Exists(searchFolder)) {
                        Ui.Error($"Verzeichnis {searchFolder} nicht gefunden.");
                        break; // ask for a folder again rather than dropping the user to the main menu
                    }

                    var allTexFiles = Directory.GetFiles(searchFolder, "*.tex", SearchOption.AllDirectories);
                    texFiles = [.. allTexFiles
                                    .Where(f => {
                                        var parts = Path.GetRelativePath(searchFolder, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                                        return !Array.Exists(parts, p => string.Equals(p, "old", StringComparison.OrdinalIgnoreCase)) &&
                                               !Array.Exists(parts, p => string.Equals(p, "tmp", StringComparison.OrdinalIgnoreCase));
                                    })
                                    .OrderByDescending(File.GetLastWriteTime)];

                    if (texFiles.Length == 0 && allTexFiles.Length > 0) {
                        texFiles = [.. allTexFiles.OrderByDescending(File.GetLastWriteTime)];
                    }

                    if (texFiles.Length == 0) {
                        Ui.Warn($"Keine passenden .tex Dateien in {searchFolder} oder den Unterordnern gefunden.");
                        break;
                    }

                    step = 2;
                    break;
                }

                case 2: {
                    var selectedStep = Ui.Select("Welchen Schritt möchtest du ausführen?", [
                        "4) Komplette Pipeline (Alle 3 Schritte)",
                        "1) Offset Correction / Merge (Schritt 1)",
                        "2) Speech Refinement (Schritt 2)",
                        "3) Last Refinement (Schritt 3)"
                    ]);
                    if (selectedStep.IsBack) { step = 1; break; }
                    if (!selectedStep.IsValue) return;

                    stepChoice = selectedStep.Value![..1];
                    step = 3;
                    break;
                }

                case 3: {
                    var fileChoices = texFiles.Select(f => (Path.GetRelativePath(searchFolder, f), f));
                    var file = Ui.Select("Wähle die .tex Datei für das Refinement:", fileChoices, pageSize: 15);
                    if (file.IsBack) { step = 2; break; }
                    if (!file.IsValue) return;

                    selectedTex = file.Value!;
                    Ui.Success($"Ausgewählte Datei: {Path.GetRelativePath(searchFolder, selectedTex)}");
                    step = 4;
                    break;
                }

                case 4: {
                    var audio = SelectAudioFile(selectedTex, searchFolder, stepChoice);
                    if (audio.IsBack) { step = 3; break; }
                    if (!audio.IsValue) return;

                    selectedAudio = audio.Value;
                    step = 5;
                    break;
                }

                case 5: {
                    if (stepChoice == "4") {
                        runToEnd = true;
                        step = 6;
                        break;
                    }

                    var scope = Ui.ConfirmOrBack("Möchtest du ab diesem Schritt die restliche Pipeline bis zum Ende (inkl. Schritt 4: PDF-Kompilierung) ausführen?", false);
                    if (scope.IsBack) { step = 4; break; }
                    if (!scope.IsValue) return;

                    runToEnd = scope.Value;
                    step = 6;
                    break;
                }

                default:
                    ApplyStepSelection(refinementConfig, stepChoice, runToEnd);
                    Ui.Info($"Starte Refinement für: {Path.GetFileName(selectedTex)}");
                    await RunRefinementAsync(refinementConfig, extractionConfig, selectedTex, selectedAudio);
                    return;
            }
        }
    }

    /// <summary>
    /// [AI Context] The backend / profile / model menu that precedes file selection. Returns false
    /// when the user cancels; the config is saved as each change is made, so cancelling still keeps
    /// what was already changed - the behaviour this menu has always had.
    /// [Human] Menü für Backend, API-Profil und Modell. Liefert false, wenn abgebrochen wurde.
    /// </summary>
    private static bool ShowBackendOptionsMenu(LatexRefinementSessionConfig refinementConfig) {
        while (true) {
            string backendDisplay = refinementConfig.UseVertex ? "Vertex AI" : "AI Studio";
            string profileDisplay = refinementConfig.AiStudioActiveApiProfile == 0 ? "Dediziert (API_KEY-latex-refinement)" : $"Profil {refinementConfig.AiStudioActiveApiProfile}";

            string currentModel = refinementConfig.UseVertex
                ? refinementConfig.Step1MergeAndTimestamp.Vertex.CurrentModel
                : refinementConfig.Step1MergeAndTimestamp.AiStudio.CurrentModel;

            Ui.Detail($"Backend:    {backendDisplay}");
            if (!refinementConfig.UseVertex) {
                Ui.Detail($"API-Profil: {profileDisplay}");
            }
            else {
                Ui.Detail($"Project ID: {refinementConfig.VertexProjectId}");
            }
            Ui.Detail($"Modell:     {currentModel}");

            var menuChoice = Ui.Select("Optionen:", [
                ("1) Refinement fortsetzen (Dateien wählen)", 1),
                ($"2) Backend wechseln (Aktuell: {backendDisplay})", 2),
                ("3) API Key Profil ändern (Nur für AI Studio)", 3),
                ("4) Modell ändern (Für aktuelles Backend)", 4)
            ], backLabel: "5) 🚪 Abbrechen");

            if (!menuChoice.IsValue) return false;

            switch (menuChoice.Value) {
                case 1:
                    return true;

                case 2:
                    if (!AppConfig.IsVertexAiEnabled && !refinementConfig.UseVertex) {
                        Ui.Warn("Google Cloud Vertex AI ist deaktiviert (AppConfig.IsVertexAiEnabled = false). Wechsel auf Vertex nicht möglich.", "Kostenschutz");
                        break;
                    }
                    refinementConfig.UseVertex = !refinementConfig.UseVertex;
                    ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                    Ui.Info($"Backend gewechselt auf: {(refinementConfig.UseVertex ? "Vertex AI" : "AI Studio")}");
                    break;

                case 3:
                    if (refinementConfig.UseVertex) {
                        Ui.Warn("API Profile sind nur für AI Studio relevant.");
                        break;
                    }
                    refinementConfig.AiStudioActiveApiProfile = ConfigurationPrompts.ConfirmOrChangeApiKeyProfile(
                        refinementConfig.AiStudioActiveApiProfile,
                        "LaTeX Refinement Session",
                        newProfile => {
                            refinementConfig.AiStudioActiveApiProfile = newProfile;
                            ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
                        },
                        refinementConfig.AiStudioApiKeyEnvNames
                    ).Or(refinementConfig.AiStudioActiveApiProfile);
                    break;

                default:
                    ChangeRefinementModel(refinementConfig);
                    break;
            }
        }
    }

    private static void ChangeRefinementModel(LatexRefinementSessionConfig refinementConfig) {
        var newModel = Ui.Select("Wähle ein Modell:", ModelCatalog.Default);
        if (!newModel.IsValue || string.IsNullOrEmpty(newModel.Value)) return;

        if (refinementConfig.UseVertex) {
            refinementConfig.Step1MergeAndTimestamp.Vertex.CurrentModel = newModel.Value;
            refinementConfig.Step2SpeechRefinement.Vertex.CurrentModel = newModel.Value;
            refinementConfig.Step3LastRefinement.Vertex.CurrentModel = newModel.Value;
        }
        else {
            refinementConfig.Step1MergeAndTimestamp.AiStudio.CurrentModel = newModel.Value;
            refinementConfig.Step2SpeechRefinement.AiStudio.CurrentModel = newModel.Value;
            refinementConfig.Step3LastRefinement.AiStudio.CurrentModel = newModel.Value;
        }
        ConfigLoader<LatexRefinementSessionConfig>.Save(refinementConfig);
        Ui.Success($"Modell auf '{newModel.Value}' aktualisiert.");
    }

    /// <summary>
    /// [AI Context] Audio is only meaningful before step 3, and only when the .tex file's own folder
    /// holds any. Both skip cases return a value rather than Back, so the step machine advances.
    /// [Human] Audio-Auswahl; entfällt bei Schritt 3 oder wenn keine Dateien vorhanden sind.
    /// </summary>
    private static PromptResult<string?> SelectAudioFile(string selectedTex, string searchFolder, string stepChoice) {
        if (stepChoice == "3") {
            Ui.Info("Überspringe Audio-Auswahl für 'Last Refinement' (Schritt 3 benötigt kein Audio).");
            return PromptResult.FromValue<string?>(null);
        }

        string selectedDir = Path.GetDirectoryName(selectedTex) ?? searchFolder;
        var audioFiles = Directory.GetFiles(selectedDir, "*.aac");

        if (audioFiles.Length == 0) {
            Ui.Info("Keine Audio-Dateien in diesem Ordner gefunden.");
            return PromptResult.FromValue<string?>(null);
        }

        var choices = new List<(string Label, string? Value)> { (NoAudioChoice, null) };
        choices.AddRange(audioFiles.Select(f => (Path.GetFileName(f), (string?)f)));

        var choice = Ui.Select("Wähle die Audio-Datei:", choices);
        if (!choice.IsValue) return choice;

        if (choice.Value != null) {
            Ui.Success($"Ausgewählte Audio-Datei: {Path.GetFileName(choice.Value)}");
        }
        return choice;
    }

    /// <summary>
    /// [AI Context] Turns the chosen step plus the "run to the end" answer into the config's enabled
    /// flags. Written as one function over the raw answers rather than mutated across the prompts,
    /// so stepping back and choosing differently cannot leave a stale flag enabled.
    /// [Human] Setzt die Enabled-Flags aus der Schritt-Auswahl - immer vollständig, nie inkrementell.
    /// </summary>
    public static void ApplyStepSelection(LatexRefinementSessionConfig refinementConfig, string stepChoice, bool runToEnd) {
        bool all = stepChoice == "4";

        refinementConfig.Step1MergeAndTimestamp.Enabled = all || stepChoice == "1";
        refinementConfig.Step2SpeechRefinement.Enabled = all || stepChoice == "2" || (runToEnd && stepChoice == "1");
        refinementConfig.Step3LastRefinement.Enabled = all || stepChoice == "3" || (runToEnd && (stepChoice == "1" || stepChoice == "2"));

        if (refinementConfig.PdfCompilation != null) {
            refinementConfig.PdfCompilation.Enabled = all || runToEnd;
        }

        if (all) return;

        if (runToEnd) {
            Ui.Info("Pipeline wird ab dem gewählten Schritt bis zum Ende (inkl. Schritt 4: PDF-Kompilierung) ausgeführt.");
        }
        else {
            Ui.Info($"Es wird ausschließlich Schritt {stepChoice} ausgeführt (ohne nachfolgende PDF-Kompilierung).");
        }
    }

    /// <summary>
    /// [AI Context] The environment variable holding the AI Studio key refinement runs under: the
    /// active profile's entry, or the dedicated refinement key when the list has none for it.
    /// [Human] Name der Umgebungsvariable mit dem API-Key für das Refinement.
    /// </summary>
    public static string ResolveRefinementKeyEnvName(LatexRefinementSessionConfig refinementConfig) {
        string? extractedRefinementEnvName = (refinementConfig.AiStudioApiKeyEnvNames != null && refinementConfig.AiStudioApiKeyEnvNames.Length > refinementConfig.AiStudioActiveApiProfile)
            ? refinementConfig.AiStudioApiKeyEnvNames[refinementConfig.AiStudioActiveApiProfile]
            : null;
        return !string.IsNullOrEmpty(extractedRefinementEnvName)
            ? extractedRefinementEnvName
            : "API_KEY-latex-refinement";
    }

    public static async Task<bool> RunRefinementAsync(LatexRefinementSessionConfig refinementConfig, IAutoExtractionConfig? extractionConfig, string selectedTex, string? selectedAudio, int? overlapSeconds = null) {
        Client refinementClient;
        if (refinementConfig.UseVertex && AppConfig.IsVertexAiEnabled) {
            refinementClient = GoogleAiClientBuilder.BuildVertexClient(
                refinementConfig.VertexProjectId,
                refinementConfig.VertexLocation
            );
        }
        else {
            string envName = ResolveRefinementKeyEnvName(refinementConfig);
            string? refinementApiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName);
            if (string.IsNullOrEmpty(refinementApiKey)) {
                Ui.Error($"Der API-Key für das Refinement ('{envName}') ist nicht gesetzt.", "Refinement");
                return false;
            }
            refinementClient = GoogleAiClientBuilder.BuildAiStudioClient(refinementApiKey);
        }

        var refinementSession = new LatexRefinementSession(
            refinementClient,
            RefinementOptions.ForFile(refinementConfig, selectedTex, extractionConfig, selectedAudio) with { OverlapSeconds = overlapSeconds }
        );

        return await refinementSession.StartAsync();
    }
}
