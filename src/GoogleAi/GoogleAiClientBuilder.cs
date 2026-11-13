using System;
using System.Runtime.CompilerServices;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.ConsoleUi;

namespace LectureExtraction.GoogleAi;

/// <summary>
/// [AI Context] Encapsulates the configuration, credential resolution, and instantiation of the Google GenAI SDK Client.
/// Bypasses the SDK for raw REST API diagnostics when necessary.
/// [Human] Kümmert sich komplett um die Verbindung zu Google (API-Keys laden, Client bauen, Modelle abfragen).
/// </summary>
public static class GoogleAiClientBuilder {
    private static readonly ConditionalWeakTable<Client, string> ClientEnvNames = [];
    // [AI Context] Which environment variable each resolved key value came from, so BuildAiStudioClient can
    // tag a client with its key name exactly, whatever was resolved in between (a "last resolved key" static
    // mis-tagged clients built later, e.g. the uploader's ClientFactory after the refinement key was read).
    // [Human] Merkt sich zu jedem gelesenen API-Key den Namen seiner Umgebungsvariable.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> EnvNameByKey = new();

    /// <summary>The environment variable name of the AI Studio client that most recently sent a request.</summary>
    public static string? LastActiveEnvVarName { get; internal set; }

    /// <summary>Returns the environment variable name the given AI Studio client was built from, or null (e.g. Vertex).</summary>
    public static string? GetEnvVarName(Client client) =>
        ClientEnvNames.TryGetValue(client, out string? envName) ? envName : null;

    /// <summary>
    /// [AI Context] Environment variable parser for robust credential loading across OS environments.
    /// [Human] Sucht deinen API Key in den Windows Umgebungsvariablen. So musst du ihn nicht unsicher in den Code schreiben.
    /// Unterstützt bis zu drei verschiedene Schlüssel (z.B. privat, Uni, Arbeit), zwischen denen du im Code wechseln kannst.
    /// </summary>
    public static string? ResolveApiKey(int activeKeyProfile = 1) {
        // [AI Context] Checks Process, User, and Machine environment variables to handle Windows permission contexts gracefully.
        // Required because User/Machine level environment variables are not always automatically inherited by the running Process unless explicitly reloaded or restarted.
        // [AI Context] Dynamically resolves the environment variable name based on the requested profile index. No hardcoded switch needed.
        string envVarName = $"API_KEY-ai-studio-test-project-{activeKeyProfile}";
        string? apiKey = System.Environment.GetEnvironmentVariable(envVarName)
                      ?? System.Environment.GetEnvironmentVariable(envVarName, EnvironmentVariableTarget.User)
                      ?? System.Environment.GetEnvironmentVariable(envVarName, EnvironmentVariableTarget.Machine);

        Ui.Detail($"Verwende {envVarName} (Projekt {activeKeyProfile})");

        if (string.IsNullOrEmpty(apiKey)) {
            Ui.Error($"Der API-Key '{envVarName}' wurde in den Umgebungsvariablen nicht gefunden.");
            return null;
        }

        EnvNameByKey[apiKey] = envVarName;
        return apiKey;
    }

    /// <summary>
    /// [AI Context] Initializes the GenAI Client for Google AI Studio (Developer API).
    /// [Human] Erstellt den Client für die Google AI Studio API mit dem übergebenen API-Key. Setzt ein Timeout von 20 Minuten für lange Anfragen.
    /// </summary>
    public static Client BuildAiStudioClient(string apiKey) {
        // Google.GenAI 2.0+ switched the Gemini Developer API default from v1beta to v1; pin v1beta to preserve 1.11.0 preview model, ThinkingConfig, Files and Long Decode support.
        var options = new HttpOptions {
            ApiVersion = "v1beta",
            Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
        };
        Ui.Detail("Verbinde mit Google AI Studio API...");
        var client = new Client(apiKey: apiKey, httpOptions: options);
        if (EnvNameByKey.TryGetValue(apiKey, out string? envName)) {
            ClientEnvNames.AddOrUpdate(client, envName);
            LastActiveEnvVarName = envName;
        }
        return client;
    }

    /// <summary>
    /// [AI Context] Environment variable parser for a specific, named credential profile.
    /// [Human] Sucht gezielt nach einem API-Key anhand seines exakten Namens in den Umgebungsvariablen (z.B. exklusiv für das LaTeX-Refinement).
    /// </summary>
    public static string? ResolveApiKeyByName(string envVarName) {
        string? apiKey = LookUpApiKey(envVarName);

        Ui.Detail($"Verwende {envVarName}");

        if (string.IsNullOrEmpty(apiKey)) {
            Ui.Error($"Der API-Key '{envVarName}' wurde in den Umgebungsvariablen nicht gefunden.");
            return null;
        }

        EnvNameByKey[apiKey] = envVarName;
        return apiKey;
    }

    /// <summary>
    /// The same three-target environment lookup as <see cref="ResolveApiKeyByName"/>, without the
    /// console output. Reporting commands need to know *whether* a profile resolves without
    /// announcing it or printing an error for a profile the user merely asked about.
    /// </summary>
    public static string? LookUpApiKey(string envVarName) =>
        System.Environment.GetEnvironmentVariable(envVarName)
        ?? System.Environment.GetEnvironmentVariable(envVarName, EnvironmentVariableTarget.User)
        ?? System.Environment.GetEnvironmentVariable(envVarName, EnvironmentVariableTarget.Machine);

    /// <summary>Whether a named API-key profile is present in the environment. Never returns the key.</summary>
    public static bool IsApiKeyPresent(string envVarName) => !string.IsNullOrEmpty(LookUpApiKey(envVarName));

    /// <summary>
    /// [AI Context] Initializes the GenAI Client for Google Cloud Vertex AI (Enterprise API).
    /// [Human] Baut die Verbindung zur professionellen Google Cloud Vertex AI auf, authentifiziert automatisch über dein Cloud-Projekt (IAM).
    /// </summary>
    public static Client BuildVertexClient(string projectId, string location) {
        var options = new HttpOptions {
            Timeout = (int)TimeSpan.FromMinutes(20).TotalMilliseconds
        };
        if (string.IsNullOrWhiteSpace(projectId)) {
            // The project id has no default since it names one Google Cloud account; say where it goes
            // instead of leaving the first request to fail with an SDK message.
            Ui.Warn("Keine Vertex-Projekt-ID gesetzt: ProjectId in VertexAutoExtractionConfig.json / DirectAiChatSessionVertexConfig.json bzw. VertexProjectId in LatexRefinementSessionConfig.json eintragen.", "Vertex");
        }
        Ui.Detail($"Verbinde mit Google Cloud Vertex AI (Projekt: {projectId})...");
        return new Client(
            vertexAI: true,
            project: projectId,
            location: location,
            httpOptions: options
        );
    }
}