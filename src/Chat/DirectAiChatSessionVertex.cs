using Google.GenAI;
using LectureExtraction.Configuration;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Chat;

/// <summary>
/// [AI Context] Core REPL manager specifically for Google Cloud Vertex AI interactions.
/// [Human] Der Manager für Chat-Sitzungen, die über Google Cloud Vertex AI laufen. Getrennt vom normalen AI Studio, da es eigene Abrechnungs- und Zugriffsregeln hat.
/// </summary>
public class DirectAiChatSessionVertex : DirectAiChatSession {
    public DirectAiChatSessionVertex(
        Client client,
        DirectAiChatSessionVertexConfig config,
        SessionLogger logger,
        AttachmentUploader attachmentHandler)
        : base(
            client,
            config,
            logger,
            attachmentHandler,
            isAiStudio: false,
            canChangeApiKeyProfile: false,
            activeApiProfile: 0,
            apiKeyProfileEnvNames: [],
            rebuildClient: null) {
    }
}
