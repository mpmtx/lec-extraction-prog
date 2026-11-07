using System.Threading.Tasks;
using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] AI Studio: API-key clients, files in Google's File API (they expire on their own, so
/// there is nothing to clean up), and a refinement client built from the refinement config's own key.
/// [Human] Das AI-Studio-Backend der Extraktions-Session.
/// </summary>
public sealed class AiStudioExtractionBackend(Client client, AiStudioAutoExtractionConfig config) : IExtractionBackend {
    public Client Client { get; } = client;

    public string Name => "AI Studio";

    public string ProviderName => "Google AI Studio";

    public bool SystemInstructionAcceptsFiles => true;

    public bool RefinementUsesVertex => false;

    public ExplicitContextCache? ContextCache => null;

    public void DescribeEndpoint() {
        if (config.ActiveApiProfile == 0) {
            Ui.Detail("API-Key:         Dedizierter Key für automatisierte Extraktion");
        }
        else {
            Ui.Detail($"API-Key:         Profil {config.ActiveApiProfile} (API_KEY-ai-studio-test-project-{config.ActiveApiProfile})");
        }
    }

    public Task CleanUpRemoteFilesAsync() => Task.CompletedTask;

    public AttachmentUploader CreateAudioUploader(Client? refinementClient, string folder) =>
        new(refinementClient ?? Client, folder, [folder], true, "", null, false,
            config.FileActivationDelaySeconds, config.VideoUploadTimeoutSeconds, config.VideoUploadMaxRetries);

    public Client CreateRefinementClient(LatexRefinementSessionConfig refinementConfig) {
        string? extractedRefinementEnvName = (refinementConfig?.AiStudioApiKeyEnvNames != null && refinementConfig.AiStudioApiKeyEnvNames.Length > refinementConfig.AiStudioActiveApiProfile)
            ? refinementConfig.AiStudioApiKeyEnvNames[refinementConfig.AiStudioActiveApiProfile]
            : null;
        string envName = !string.IsNullOrEmpty(extractedRefinementEnvName)
            ? extractedRefinementEnvName
            : "API_KEY-latex-refinement";
        string? refinementApiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName);
        if (string.IsNullOrEmpty(refinementApiKey)) {
            throw new InvalidOperationException($"Der API-Key für das LaTeX-Refinement ('{envName}') ist nicht gesetzt.");
        }
        return GoogleAiClientBuilder.BuildAiStudioClient(refinementApiKey);
    }
}
