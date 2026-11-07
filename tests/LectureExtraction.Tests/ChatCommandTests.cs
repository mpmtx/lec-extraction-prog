using System;
using System.CommandLine;
using System.Threading.Tasks;
using LectureExtraction.Cli;
using LectureExtraction.Cli.Commands;
using LectureExtraction.Configuration;
using LectureExtraction.ConsoleUi;
using Xunit;

namespace LectureExtraction.Tests;

/// <summary>
/// The chat command's options and its two exits that need no API key.
///
/// <para>The RunAsync tests install a <see cref="PresetPromptSource"/> process-wide (the chat REPL
/// path an <see cref="InteractivePromptSource"/>), so the class joins the console collection and
/// puts the globals back: outside it, PromptResultTests intermittently met the preset source and
/// failed with UnattendedPromptException.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class ChatCommandTests : IDisposable {
    // RunAsync installs these process-wide; put back what the other tests expect.
    private readonly IPromptSource _promptSource = Ui.PromptSource;
    private readonly bool _saveEnabled = ConfigStore.SaveEnabled;
    private readonly string? _directoryOverride = ConfigStore.DirectoryOverride;

    public void Dispose() {
        Ui.PromptSource = _promptSource;
        ConfigStore.SaveEnabled = _saveEnabled;
        ConfigStore.DirectoryOverride = _directoryOverride;
    }

    private static ParseResult Parse(params string[] args) => CliBootstrapper.BuildRootCommand().Parse(args);

    [Fact]
    public void ChatCommand_AcceptsAllOptions() {
        var result = Parse(
            "chat",
            "--backend", "aistudio",
            "--model", "gemini-3.6-flash",
            "--profile", "1",
            "--skip-setup",
            "--system-prompt", "custom.md",
            "--no-system-prompt",
            "--no-history");

        Assert.Empty(result.Errors);
        Assert.Equal("aistudio", result.GetValue(ChatCommand.Backend));
        Assert.Equal("gemini-3.6-flash", result.GetValue(ChatCommand.Model));
        Assert.Equal(1, result.GetValue(ChatCommand.Profile));
        Assert.True(result.GetValue(ChatCommand.SkipSetup));
        Assert.Equal("custom.md", result.GetValue(ChatCommand.SystemPrompt));
        Assert.True(result.GetValue(ChatCommand.NoSystemPrompt));
        Assert.True(result.GetValue(ChatCommand.NoHistory));
    }

    [Fact]
    public async Task ChatCommand_DryRun_ReturnsSuccess() {
        int code = await CliBootstrapper.RunAsync(["chat", "--dry-run"]);
        Assert.Equal(ExitCodes.Success, code);
    }

    [Fact]
    public async Task ChatCommand_UnknownBackend_ReturnsUsage() {
        int code = await CliBootstrapper.RunAsync(["chat", "--backend", "unknown"]);
        Assert.Equal(ExitCodes.Usage, code);
    }

    [Fact]
    public void ChatSessions_InheritFromUnifiedDirectAiChatSession() {
        var aiStudioConfig = new DirectAiChatSessionAiStudioConfig();
        var vertexConfig = new DirectAiChatSessionVertexConfig();

        var aiStudioSession = new LectureExtraction.Chat.DirectAiChatSessionAiStudio(null!, aiStudioConfig, null!, null!);
        var vertexSession = new LectureExtraction.Chat.DirectAiChatSessionVertex(null!, vertexConfig, null!, null!);

        Assert.IsAssignableFrom<LectureExtraction.Chat.DirectAiChatSession>(aiStudioSession);
        Assert.IsAssignableFrom<LectureExtraction.Chat.DirectAiChatSession>(vertexSession);
    }
}
