namespace LectureExtraction.Configuration;

public class LatexRefinementSessionConfig {
    public bool Enabled { get; set; } = false;
    public bool UseVertex { get; set; } = false;

    // AI Studio Config
    public string[] AiStudioApiKeyEnvNames { get; set; } = [
        "API_KEY-latex-refinement",
        "API_KEY-ai-studio-test-project-1",
        "API_KEY-ai-studio-test-project-2",
        "API_KEY-ai-studio-test-project-3"
    ];
    public int AiStudioActiveApiProfile { get; set; } = 3; // 0 = Dedicated, 1-3 = General Keys

    // Vertex Config (project and bucket belong to one Google Cloud account: set in the JSON)
    public string VertexProjectId { get; set; } = "";
    public string VertexLocation { get; set; } = "global";
    public string VertexGcsBucketName { get; set; } = "";

    public string TargetFolder { get; set; } = "";
    public string SourceFolder { get; set; } = "";

    /// <summary>
    /// [AI Context] How many characters of a cut-off response the continue prompt quotes back
    /// (refinement steps 1-3 and the PDF repair). The whole response is in the history anyway (as the
    /// model turn); the quote is only the anchor for "resume exactly here". Values &lt;= 0 fall back to
    /// ContinueBudget.EchoLength.
    /// [Human] Anzahl Zeichen, die der Continue-Prompt vom abgeschnittenen Text als Anker wiederholt.
    /// </summary>
    public int ContinuePromptEchoCharacters { get; set; } = 500;

    public PdfCompilationConfig PdfCompilation { get; set; } = new PdfCompilationConfig();

    // Each step's prompt files (SystemInstructionPaths) live outside this repository, so the JSON lists them.
    public RefinementStepConfig Step1MergeAndTimestamp { get; set; } = new();

    public RefinementStepConfig Step2SpeechRefinement { get; set; } = new();

    public RefinementStepConfig Step3LastRefinement { get; set; } = new();

    /// <summary>Creates a deep copy so runtime overrides (e.g. disabling step 1 for 1-part videos) never mutate the loaded configuration.</summary>
    public LatexRefinementSessionConfig Clone() =>
        Newtonsoft.Json.JsonConvert.DeserializeObject<LatexRefinementSessionConfig>(Newtonsoft.Json.JsonConvert.SerializeObject(this))!;
}
