using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Vertex's explicit context caching - the CachedContent API. This half has no AI Studio
/// counterpart at all: AI Studio has only the implicit prefix cache (see the .PrefixCache.cs files on
/// both sides), while Vertex can create, validate and extend a server-side cache resource. It is split
/// out for exactly that reason - it is the part of this class with no twin, so keeping it separate
/// stops it from being mistaken for shared pipeline code.
/// Member Index:
/// - InitializeContextCachingAsync: Creates or revalidates the remote CachedContent resource.
/// - ConfigureCachingSettings: Interactive prompts for the cache duration settings.
/// [Human] Vertex-eigenes explizites Context-Caching; hat bei AI Studio keine Entsprechung.
/// </summary>
public partial class VertexAutoExtractionSession {
    /// <summary>
    /// [AI Context] Initializes or validates the remote Google Cloud Context Cache for system instructions.
    /// [Human] Prüft beim Start, ob der Google-Kontext-Cache noch gültig ist oder neu angelegt werden muss.
    /// </summary>
    private async Task InitializeContextCachingAsync() {
        if (!_config.UseContextCaching) {
            var state = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
            if (!string.IsNullOrEmpty(state.CacheName)) {
                Ui.Info("Context Caching wurde in Konfiguration deaktiviert. Lösche aktiven Cache bei Google...", "ContextCache");
                await ContextCacheStateManager.DeleteRemoteAsync(_client, state.CacheName);
                ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
            }
            return;
        }

        bool hasSys = !string.IsNullOrWhiteSpace(_systemInstructionText);
        bool hasHist = _config.LoadHistoryIntoSystemInstruction && _historyParts.Count > 0;
        if (!hasSys && !hasHist) return;

        var sysParts = new List<Part>();
        if (hasSys) sysParts.Add(new() { Text = _systemInstructionText });
        var cacheContents = new List<Content>();
        if (hasHist) {
            var (textOnly, nonSystem) = SystemInstructionTextBuilder.PartitionForTextOnlySystemInstruction(_historyParts);
            sysParts.AddRange(textOnly);
            if (nonSystem.Count > 0) {
                cacheContents.Add(new() { Role = "user", Parts = nonSystem });
            }
        }

        string combinedChecksum = ContextCacheStateManager.ComputeChecksum(_systemInstructionText + (hasHist ? $"_hist_{_historyParts.Count}" : ""));
        var savedState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);

        int resolvedMaxOutputTokens = ModelCapabilities.ResolveMaxOutputTokens(_config.CurrentModel, _config.MaxOutputTokens);
        bool match = ContextCacheStateManager.MatchesConfig(
            savedState,
            _config.CurrentModel,
            _config.Temperature,
            _config.TopP,
            _config.TopK,
            resolvedMaxOutputTokens,
            _config.ThinkingBudget,
            _config.ThinkingLevel,
            combinedChecksum
        );

        if (match && await ContextCacheStateManager.IsValidRemoteAsync(_client, savedState.CacheName!)) {
            _cachedContentName = savedState.CacheName;
            Ui.Info($"Nutze bestehenden Google Kontext-Cache: {_cachedContentName} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})", "ContextCache");
            return;
        }

        if (!string.IsNullOrEmpty(savedState.CacheName)) {
            await ContextCacheStateManager.DeleteRemoteAsync(_client, savedState.CacheName);
        }

        Ui.Info("Erstelle neuen Kontext-Cache bei Google (dies kann einen Moment dauern)...", "ContextCache");
        try {
            var cacheConfig = new CreateCachedContentConfig {
                SystemInstruction = sysParts.Count > 0 ? new Content { Role = "system", Parts = sysParts } : null,
                Contents = cacheContents.Count > 0 ? cacheContents : null,
                DisplayName = "vertex-sys-cache",
                Ttl = $"{_config.ContextCachingMinutes * 60}s"
            };
            var created = await _client.Caches.CreateAsync(_config.CurrentModel, cacheConfig);
            if (created != null && !string.IsNullOrEmpty(created.Name)) {
                _cachedContentName = created.Name;
                savedState.CacheName = _cachedContentName;
                savedState.Model = _config.CurrentModel;
                savedState.Temperature = _config.Temperature;
                savedState.TopP = _config.TopP;
                savedState.TopK = _config.TopK;
                savedState.MaxOutputTokens = resolvedMaxOutputTokens;
                savedState.ThinkingBudget = _config.ThinkingBudget;
                savedState.ThinkingLevel = _config.ThinkingLevel;
                savedState.SystemInstructionChecksum = combinedChecksum;
                savedState.ExpireTimeUtc = DateTime.UtcNow.AddMinutes(_config.ContextCachingMinutes);
                if (created != null && created.ExpireTime.HasValue) {
                    savedState.ExpireTimeUtc = created.ExpireTime.Value.ToUniversalTime();
                }
                ContextCacheStateManager.SaveState(savedState, ContextCacheStateManager.StateFileVertex);
                Ui.Success($"Google Kontext-Cache erfolgreich angelegt: {_cachedContentName} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})", "ContextCache");
            }
        }
        catch (Exception ex) {
            Ui.Error($"Konnte Kontext-Cache nicht erstellen: {ex.Describe()}. Falle auf normalen Upload zurück.", "ContextCache");
            _cachedContentName = null;
        }
    }

    private void ConfigureCachingSettings() {
        Ui.Step("Context Caching Einstellungen");
        Ui.Detail($"UseContextCaching: {_config.UseContextCaching}");
        Ui.Detail($"ContextCachingMinutes: {_config.ContextCachingMinutes} min");
        Ui.Detail($"ContextCachingIncrementMinutes: {_config.ContextCachingIncrementMinutes} min");

        _config.UseContextCaching = Ui.Confirm("Context Caching aktivieren?", _config.UseContextCaching);
        _config.ContextCachingMinutes = Ui.Ask("Neue Standarddauer in Minuten:", _config.ContextCachingMinutes);
        _config.ContextCachingIncrementMinutes = Ui.Ask("Neues Verlängerungsintervall in Minuten:", _config.ContextCachingIncrementMinutes);

        ConfigLoader<VertexAutoExtractionConfig>.Save(_config);
        Ui.Success("Einstellungen in VertexAutoExtractionConfig.json gespeichert.");
    }
}
