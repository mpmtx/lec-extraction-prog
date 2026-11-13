using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.Extraction.Model;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Outcome of the debug "Hello" roundtrip. The runner deliberately does not touch the
/// session's counters or preamble itself - it reports what happened and lets the caller fold the
/// result in. That is what allows the roundtrip to live outside the session class at all: the
/// mutable state stays in one place instead of being written from two.
/// [Human] Ergebnis des Debug-Roundtrips. Der Runner verändert den Session-Zustand nicht selbst,
/// sondern liefert ihn zurück.
/// </summary>
public sealed record DebugRoundtripResult(bool Succeeded, TokenUsage Usage, Content? UserTurn, Content? ModelTurn);

/// <summary>
/// [AI Context] Sends a single tiny "Hello" request before the real pipeline starts, gated behind
/// <c>DebugHelloRoundtrip</c> (default <c>false</c>). Its purpose is to prove the API key, the
/// model name and the assembled system instruction all work before a long, expensive batch begins -
/// failing here costs one cheap request instead of a full video upload.
///
/// <para>Extracted from <c>AiStudioAutoExtractionSession</c> (Phase 11). It keeps its own
/// hand-rolled retry loop rather than using <see cref="GoogleAi.ApiRetryPolicy"/>: a fixed
/// 3-attempt / 10s-increment backoff is deliberate for a pre-flight check, where waiting out a
/// full rate-limit backoff would defeat the point of a quick smoke test.</para>
/// [Human] Kleiner "Hello"-Testaufruf vor dem eigentlichen Lauf, um API-Key, Modell und System
/// Instruction zu prüfen, bevor teure Uploads starten.
/// </summary>
public static class DebugRoundtripRunner {
    private const string HelloPrompt = "Hi, this is a debug roundtrip. Please reply with a short 'Hello' or 'Hi'.";

    public static async Task<DebugRoundtripResult> RunAsync(
        Client client, IAutoExtractionConfig config, List<Part> systemInstructionParts) {

        Ui.Detail("Starte 'Hello' Roundtrip (DebugHelloRoundtrip = true)...");

        var requestConfig = new GenerateContentConfig {
            Temperature = config.Temperature,
            TopP = config.TopP,
            TopK = config.TopK,
            MaxOutputTokens = 200,
            AutomaticContinuation = false
        };

        if (systemInstructionParts.Count > 0) {
            requestConfig.SystemInstruction = new Content { Role = "system", Parts = systemInstructionParts };
        }

        var userTurn = new Content { Role = "user", Parts = [new() { Text = HelloPrompt }] };
        var debugContent = new List<Content> { userTurn };

        const int maxRetries = 3;
        int backoff = 10;

        for (int attempt = 0; attempt < maxRetries; attempt++) {
            try {
                var response = await client.Models.GenerateContentAsync(config.CurrentModel, debugContent, requestConfig);
                string fullResponse = response.Text ?? "";

                var usage = response.UsageMetadata != null
                    ? new TokenUsage(
                        response.UsageMetadata.PromptTokenCount ?? 0,
                        response.UsageMetadata.CandidatesTokenCount ?? 0,
                        response.UsageMetadata.CachedContentTokenCount ?? 0)
                    : default;

                Ui.Detail($"Total Prompt: {usage.Input:N0} | Gecacht: {usage.Cached:N0} | Frisch: {usage.Fresh:N0} | Output: {usage.Output:N0}", "Tokens");
                Ui.Detail($"{fullResponse.Trim()}", "Gemini Antwort");

                var modelTurn = new Content { Role = "model", Parts = [new() { Text = fullResponse }] };
                return new DebugRoundtripResult(true, usage, userTurn, modelTurn);
            }
            catch (Exception ex) {
                Ui.Error($"{ex.Describe()}", "Debug Roundtrip");
                if (attempt < maxRetries - 1) {
                    Ui.Detail($"Retry in {backoff}s...");
                    await Task.Delay(backoff * 1000);
                    backoff += 10;
                }
            }
        }

        Ui.Error("Debug Roundtrip fehlgeschlagen.");
        return new DebugRoundtripResult(false, default, null, null);
    }
}
