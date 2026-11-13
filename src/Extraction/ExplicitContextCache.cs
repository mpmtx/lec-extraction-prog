using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.GenAI;
using Google.GenAI.Types;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using LectureExtraction.GoogleAi;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Extraction;

/// <summary>
/// [AI Context] Vertex's explicit context cache - the CachedContent API - for Stage 1. AI Studio has
/// only the implicit prefix cache (AutoExtractionSession.PrefixCache.cs), while Vertex can create,
/// validate and extend a server-side cache resource holding the system instruction and the history.
/// While <see cref="Name"/> is set, part requests reference it instead of sending the system
/// instruction. Moved out of VertexAutoExtractionSession.ContextCache.cs in the session merge.
/// [Human] Vertex-eigenes explizites Context-Caching; AI Studio hat keine Entsprechung.
/// </summary>
public sealed class ExplicitContextCache(Client client, VertexAutoExtractionConfig config) {
    /// <summary>The active CachedContent resource name, or null when requests send the system instruction.</summary>
    public string? Name { get; private set; }

    /// <summary>
    /// [AI Context] Creates or reuses the remote cache for the system instruction and history. With
    /// caching switched off in the config, deletes a cache a previous run left behind instead.
    /// [Human] Prüft, ob der Google-Kontext-Cache noch gültig ist oder neu angelegt werden muss.
    /// </summary>
    public async Task InitializeAsync(string systemInstructionText, IReadOnlyList<Part> historyParts) {
        if (!config.UseContextCaching) {
            var state = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
            if (!string.IsNullOrEmpty(state.CacheName)) {
                Ui.Info("Context Caching wurde in Konfiguration deaktiviert. Lösche aktiven Cache bei Google...", "ContextCache");
                await ContextCacheStateManager.DeleteRemoteAsync(client, state.CacheName);
                ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
            }
            return;
        }

        bool hasSys = !string.IsNullOrWhiteSpace(systemInstructionText);
        bool hasHist = config.LoadHistoryIntoSystemInstruction && historyParts.Count > 0;
        if (!hasSys && !hasHist) return;

        var sysParts = new List<Part>();
        if (hasSys) sysParts.Add(new() { Text = systemInstructionText });
        var cacheContents = new List<Content>();
        if (hasHist) {
            var textOnly = historyParts.Where(p => p.FileData == null && p.InlineData == null && !string.IsNullOrEmpty(p.Text)).ToList();
            var nonText = historyParts.Where(p => p.FileData != null || p.InlineData != null).ToList();
            sysParts.AddRange(textOnly);
            if (nonText.Count > 0) {
                cacheContents.Add(new() { Role = "user", Parts = nonText });
            }
        }

        string combinedChecksum = ContextCacheStateManager.ComputeChecksum(systemInstructionText + (hasHist ? $"_hist_{historyParts.Count}" : ""));
        var savedState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);

        bool match = ContextCacheStateManager.MatchesConfig(
            savedState,
            config.CurrentModel,
            config.Temperature,
            config.TopP,
            config.TopK,
            ModelCapabilities.ResolveMaxOutputTokens(config.CurrentModel, config.MaxOutputTokens),
            config.ThinkingBudget,
            ModelCapabilities.ResolveThinkingLevel(config.CurrentModel, config.ThinkingLevel),
            combinedChecksum
        );

        if (match && await ContextCacheStateManager.IsValidRemoteAsync(client, savedState.CacheName!)) {
            Name = savedState.CacheName;
            Ui.Info($"Nutze bestehenden Google Kontext-Cache: {Name} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})", "ContextCache");
            return;
        }

        if (!string.IsNullOrEmpty(savedState.CacheName)) {
            await ContextCacheStateManager.DeleteRemoteAsync(client, savedState.CacheName);
        }

        Ui.Info("Erstelle neuen Kontext-Cache bei Google (dies kann einen Moment dauern)...", "ContextCache");
        try {
            var cacheConfig = new CreateCachedContentConfig {
                SystemInstruction = sysParts.Count > 0 ? new Content { Role = "system", Parts = sysParts } : null,
                Contents = cacheContents.Count > 0 ? cacheContents : null,
                DisplayName = "vertex-sys-cache",
                Ttl = $"{config.ContextCachingMinutes * 60}s"
            };
            var created = await client.Caches.CreateAsync(config.CurrentModel, cacheConfig);
            if (created != null && !string.IsNullOrEmpty(created.Name)) {
                Name = created.Name;
                savedState.CacheName = Name;
                savedState.Model = config.CurrentModel;
                savedState.Temperature = config.Temperature;
                savedState.TopP = config.TopP;
                savedState.TopK = config.TopK;
                savedState.MaxOutputTokens = ModelCapabilities.ResolveMaxOutputTokens(config.CurrentModel, config.MaxOutputTokens);
                savedState.ThinkingBudget = config.ThinkingBudget;
                savedState.ThinkingLevel = ModelCapabilities.ResolveThinkingLevel(config.CurrentModel, config.ThinkingLevel);
                savedState.SystemInstructionChecksum = combinedChecksum;
                savedState.ExpireTimeUtc = DateTime.UtcNow.AddMinutes(config.ContextCachingMinutes);
                if (created.ExpireTime.HasValue) {
                    savedState.ExpireTimeUtc = created.ExpireTime.Value.ToUniversalTime();
                }
                ContextCacheStateManager.SaveState(savedState, ContextCacheStateManager.StateFileVertex);
                Ui.Success($"Google Kontext-Cache erfolgreich angelegt: {Name} (Gültig bis {savedState.ExpireTimeUtc.ToLocalTime():t})", "ContextCache");
            }
        }
        catch (Exception ex) {
            Ui.Error($"Konnte Kontext-Cache nicht erstellen: {ex.Describe()}. Falle auf normalen Upload zurück.", "ContextCache");
            Name = null;
        }
    }

    /// <summary>
    /// [AI Context] Runs before every part request: extends the cache when it is about to expire and
    /// recreates it when it is gone, so a long video never sends a part against a dead cache name.
    /// [Human] Verlängert den Cache vor jedem Teil oder legt ihn neu an.
    /// </summary>
    public async Task EnsureValidAsync(string systemInstructionText, IReadOnlyList<Part> historyParts) {
        if (!config.UseContextCaching) return;

        var cacheState = ContextCacheStateManager.LoadState(ContextCacheStateManager.StateFileVertex);
        double remainingMin = ContextCacheStateManager.GetRemainingMinutes(cacheState);
        bool cacheValid = false;

        if (!string.IsNullOrEmpty(Name) && remainingMin > 0) {
            if (remainingMin < config.ContextCachingMinimumRemainingMinutes) {
                Ui.Info($"Nur noch {remainingMin:F1} min verbleibend (Schwellenwert: {config.ContextCachingMinimumRemainingMinutes} min). Verlängere automatisch um {config.ContextCachingIncrementMinutes} min...", "Cache");
                var updatedState = await ContextCacheStateManager.ExtendCacheAsync(client, cacheState, config.ContextCachingIncrementMinutes, ContextCacheStateManager.StateFileVertex);
                if (updatedState != null) {
                    Ui.Info($"Cache verlängert. Gültig bis {updatedState.ExpireTimeUtc.ToLocalTime():t}.", "Cache");
                    cacheValid = true;
                }
            }
            else {
                cacheValid = await ContextCacheStateManager.IsValidRemoteAsync(client, Name);
            }
        }

        if (!cacheValid) {
            Ui.Info("Kein gültiger Cache aktiv oder Cache abgelaufen. Erstelle neuen Google Kontext-Cache für diesen Teil...", "Cache");
            ContextCacheStateManager.ClearState(ContextCacheStateManager.StateFileVertex);
            Name = null;
            await InitializeAsync(systemInstructionText, historyParts);
        }
    }
}
