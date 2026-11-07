using System;
using System.IO;
using System.Text;
using LectureExtraction.ConsoleUi;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using LectureExtraction.Infrastructure;

namespace LectureExtraction.Configuration;

/// <summary>
/// Generic configuration loader implementing the hierarchy:
/// corresponding .json > appsettings.json > C# default properties. A list in a JSON file replaces the
/// list below it as a whole; a list the JSON leaves out keeps the value below it.
/// Runs ConfigMigrator before binding to ensure legacy flat JSON keys are migrated seamlessly.
/// </summary>
public static class ConfigLoader<T> where T : class, new() {
    public static T Load(string? sectionName = null) {
        sectionName ??= typeof(T).Name;
        var basePath = ConfigStore.ResolveDirectory();
        string fileName = $"{typeof(T).Name}.json";
        string filePath = Path.Combine(basePath, fileName);

        // When running from repo root (e.g. dotnet run), sync from current directory if that copy is newer
        if (string.IsNullOrWhiteSpace(ConfigStore.DirectoryOverride)) {
            string currentDirFile = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            if (!string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(currentDirFile), StringComparison.OrdinalIgnoreCase)
                && File.Exists(currentDirFile)) {
                if (!File.Exists(filePath) || File.GetLastWriteTimeUtc(currentDirFile) > File.GetLastWriteTimeUtc(filePath)) {
                    try {
                        File.Copy(currentDirFile, filePath, overwrite: true);
                    }
                    catch (Exception ex) {
                        Ui.Warn($"Konnte '{fileName}' aus Arbeitsverzeichnis nicht synchronisieren: {ex.Describe()}");
                    }
                }
            }
        }

        // Run JSON migrator on disk file if legacy flat keys exist. With saving disabled (CLI default)
        // the migrated JSON is bound from memory only: an unattended run must not rewrite the file,
        // and with --config-dir that file is the user's live configuration.
        string? migratedInMemory = null;
        if (File.Exists(filePath)) {
            try {
                string rawText = File.ReadAllText(filePath);
                if (!string.IsNullOrWhiteSpace(rawText)) {
                    var jObj = JObject.Parse(rawText);
                    if (ConfigMigrator.Migrate(jObj, typeof(T))) {
                        string migrated = JsonCommentPreserver.Merge(rawText, jObj, ConfigMigrator.RemapAnchor);
                        if (ConfigStore.SaveEnabled) {
                            File.WriteAllText(filePath, migrated);
                        }
                        else {
                            migratedInMemory = migrated;
                            Ui.Detail($"'{fileName}' enthält alte Schlüssel; migriert nur im Arbeitsspeicher (Konfiguration ist schreibgeschützt).", "ConfigMigrator");
                        }
                    }
                }
            }
            catch (Exception ex) {
                Ui.Warn($"Migration in '{fileName}' fehlgeschlagen: {ex.Describe()}", "ConfigMigrator");
            }
        }

        // Build configuration object with clear hierarchy.
        var builder = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: true);
        if (migratedInMemory != null) {
            builder.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(migratedInMemory)));
        }
        else {
            builder.AddJsonFile(fileName, optional: true);
        }
        var configuration = builder.Build();

        var config = new T();

        ConfigLayerBinder.Bind(configuration.GetSection("AppConfig").GetSection(sectionName), config);
        ConfigLayerBinder.Bind(configuration, config);

        return config;
    }

    /// <summary>
    /// [AI Context] Serializes the configuration object to a formatted JSON string while preserving existing JSON comments and formatting.
    /// [Human] Speichert die Konfiguration in die JSON-Datei und sorgt dafür, dass bestehende Kommentare nicht verloren gehen.
    /// </summary>
    private static string SerializePreservingComments(string filePath, T config) {
        if (File.Exists(filePath)) {
            try {
                string existingJson = File.ReadAllText(filePath);
                return JsonCommentPreserver.Merge(existingJson, JObject.FromObject(config), ConfigMigrator.RemapAnchor);
            }
            catch (Exception ex) {
                Ui.Warn($"Kommentare in '{Path.GetFileName(filePath)}' konnten beim Speichern nicht erhalten werden: {ex.Describe()}", "AppConfig");
            }
        }
        return JsonConvert.SerializeObject(config, Formatting.Indented);
    }

    public static void Save(T config) {
        string fileName = $"{typeof(T).Name}.json";

        if (!ConfigStore.SaveEnabled) {
            // Silence here would be worse than noise: a caller who expected a setting to stick
            // needs to know why it did not.
            Ui.Detail($"'{fileName}' nicht gespeichert (Konfiguration ist schreibgeschützt).", "AppConfig");
            return;
        }

        var basePath = ConfigStore.ResolveDirectory();
        string filePath = Path.Combine(basePath, fileName);

        string jsonString = SerializePreservingComments(filePath, config);
        File.WriteAllText(filePath, jsonString);

        // The second write keeps the working copy beside the executable's copy in step when the
        // app is started with `dotnet run` from the repository root. An explicit --config-dir is a
        // statement about *which* copy is authoritative, so it suppresses the mirror.
        if (!string.IsNullOrWhiteSpace(ConfigStore.DirectoryOverride)) {
            return;
        }

        try {
            string currentDirFile = Path.Combine(Directory.GetCurrentDirectory(), fileName);
            if (!string.Equals(Path.GetFullPath(filePath), Path.GetFullPath(currentDirFile), StringComparison.OrdinalIgnoreCase)
                && File.Exists(currentDirFile)) {
                string currentDirJson = SerializePreservingComments(currentDirFile, config);
                File.WriteAllText(currentDirFile, currentDirJson);
            }
        }
        catch (Exception ex) {
            Ui.Error($"[Exception gefangen] {ex.Describe()}");
        }
    }
}
