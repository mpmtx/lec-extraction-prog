using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] Core REPL (Read-Eval-Print Loop) manager for the conversational AI interface (AI Studio backend).
/// Maintains stateful chat history and handles API interactions using the Google.GenAI SDK.
/// [Human] Das Herzstück des Chatbots für Google AI Studio. Hier werden deine Eingaben gelesen, an Google gesendet und die Antworten in der Konsole ausgegeben.
/// </summary> 
public class DirectAiChatSessionAiStudio : DirectAiChatSession {
    public DirectAiChatSessionAiStudio(
        Client client,
        DirectAiChatSessionAiStudioConfig config,
        SessionLogger logger,
        AttachmentUploader attachmentHandler,
        bool isAiStudio = true)
        : base(
            client,
            config,
            logger,
            attachmentHandler,
            isAiStudio: isAiStudio,
            canChangeApiKeyProfile: isAiStudio,
            activeApiProfile: config.ActiveApiProfile,
            apiKeyProfileEnvNames: config.AiStudioApiKeyEnvNames,
            rebuildClient: newProfile => {
                string envName = ApiKeyProfileResolver.Resolve(newProfile, config.AiStudioApiKeyEnvNames);
                string? apiKey = GoogleAiClientBuilder.ResolveApiKeyByName(envName);
                return apiKey != null ? GoogleAiClientBuilder.BuildAiStudioClient(apiKey) : null;
            }) {
    }
}
