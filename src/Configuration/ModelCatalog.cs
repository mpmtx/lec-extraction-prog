namespace LectureExtraction.Configuration;

/// <summary>
/// [AI Context] The model list a config starts with when its JSON lists none, and the list the model
/// pickers offer. Was the same five-entry array written out in every config class and picker; a new
/// model is now added here once. Each config's JSON keeps its own <c>ModelSelection.Available</c>.
/// [Human] Die Standard-Modellliste, an einer Stelle statt in jeder Konfigurationsklasse.
/// </summary>
public static class ModelCatalog {
    /// <summary>
    /// Default model catalog (keeps gemini-3.8-flash at index 0 as default, with gemini-4-hydrogen and older
    /// Flash releases available). Returns a copy so callers cannot mutate the default.
    /// </summary>
    public static string[] Default => ["gemini-3.8-flash", "gemini-4-hydrogen", "gemini-3.7-flash", "gemini-3.6-flash", "gemini-3.5-flash", "gemini-3-flash-preview"];
}
