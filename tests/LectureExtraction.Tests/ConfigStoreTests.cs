using System;
using System.IO;
using LectureExtraction.Cli;
using LectureExtraction.Configuration;

namespace LectureExtraction.Tests;

/// <summary>
/// Covers the config-writeback seam.
///
/// <para>The property under test is the one that makes unattended and parallel runs safe: the app
/// calls <c>ConfigLoader.Save</c> at several points during a normal run, so without this seam a
/// scripted run silently rewrites the settings the interactive user comes back to, and two
/// concurrent runs fight over the same file.</para>
///
/// <para>Joins the console collection because <see cref="ConfigStore"/> is a process-wide global
/// (two classes mutating it in parallel would race) and because the read-only path reports through
/// <c>Ui</c>.</para>
/// </summary>
[Collection(ConsoleTestCollection.Name)]
public class ConfigStoreTests : IDisposable {
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "lecx-cfg-" + Guid.NewGuid().ToString("N"));

    public ConfigStoreTests() => Directory.CreateDirectory(_directory);

    public void Dispose() {
        ConfigStore.Reset();
        try {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) {
            // A leftover temp folder must not fail the suite, but it should be visible.
            Console.WriteLine($"[Test cleanup] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string ConfigFile => Path.Combine(_directory, $"{nameof(FfmpegSessionConfig)}.json");

    [Fact]
    public void ResolveDirectory_DefaultsToTheExecutablesFolder() {
        ConfigStore.Reset();

        Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, ConfigStore.ResolveDirectory());
    }

    [Fact]
    public void ResolveDirectory_UsesTheOverride() {
        ConfigStore.DirectoryOverride = _directory;

        Assert.Equal(Path.GetFullPath(_directory), ConfigStore.ResolveDirectory());
    }

    [Fact]
    public void Save_WritesNothingWhenSavingIsDisabled() {
        ConfigStore.DirectoryOverride = _directory;
        ConfigStore.SaveEnabled = false;

        ConfigLoader<FfmpegSessionConfig>.Save(new FfmpegSessionConfig());

        Assert.False(File.Exists(ConfigFile));
    }

    [Fact]
    public void Save_WritesOnlyIntoTheOverriddenDirectory() {
        // An explicit --config-dir is a statement about which copy is authoritative, so the
        // mirror-write into the current directory is suppressed. The working directory already
        // holds its own copy of this file (the build copies the configs next to the test host), so
        // the assertion is that the content did not change - not that no file is there.
        string mirror = Path.Combine(Directory.GetCurrentDirectory(), $"{nameof(FfmpegSessionConfig)}.json");
        string? mirrorBefore = File.Exists(mirror) ? File.ReadAllText(mirror) : null;

        ConfigStore.DirectoryOverride = _directory;
        ConfigStore.SaveEnabled = true;

        ConfigLoader<FfmpegSessionConfig>.Save(new FfmpegSessionConfig());

        Assert.True(File.Exists(ConfigFile));

        string? mirrorAfter = File.Exists(mirror) ? File.ReadAllText(mirror) : null;
        Assert.Equal(mirrorBefore, mirrorAfter);
    }

    [Fact]
    public void SaveEnabled_DefaultsToOn_SoTheInteractiveAppIsUnaffected() {
        ConfigStore.Reset();

        Assert.True(ConfigStore.SaveEnabled);
        Assert.Null(ConfigStore.DirectoryOverride);
    }

    [Fact]
    public void Load_MigratesInMemoryOnly_WhenSavingIsDisabled() {
        // With --config-dir the file is the user's live config; a read-only run must leave it as it
        // was, legacy keys included, and still bind the migrated values.
        string file = Path.Combine(_directory, $"{nameof(AiStudioAutoExtractionConfig)}.json");
        string legacy = "{\n  \"Temperature\": 0.11,\n  \"TopK\": 33\n}\n";
        File.WriteAllText(file, legacy);
        ConfigStore.DirectoryOverride = _directory;
        ConfigStore.SaveEnabled = false;

        var loaded = ConfigLoader<AiStudioAutoExtractionConfig>.Load();

        Assert.Equal(legacy, File.ReadAllText(file));
        Assert.Equal(0.11f, loaded.Temperature, 3);
        Assert.Equal(33, loaded.TopK);
    }

    [Fact]
    public void Load_RewritesALegacyFile_WhenSavingIsEnabled() {
        string file = Path.Combine(_directory, $"{nameof(AiStudioAutoExtractionConfig)}.json");
        string legacy = "{\n  \"Temperature\": 0.11,\n  \"TopK\": 33\n}\n";
        File.WriteAllText(file, legacy);
        ConfigStore.DirectoryOverride = _directory;
        ConfigStore.SaveEnabled = true;

        var loaded = ConfigLoader<AiStudioAutoExtractionConfig>.Load();

        Assert.NotEqual(legacy, File.ReadAllText(file));
        Assert.Equal(0.11f, loaded.Temperature, 3);
    }

    [Fact]
    public void Load_SyncsFromCurrentDirectory_WhenCurrentDirectoryIsNewer() {
        ConfigStore.Reset();
        string fileName = $"{nameof(FfmpegSessionConfig)}.json";
        string outputDirFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
        string currentDirFile = Path.Combine(Directory.GetCurrentDirectory(), fileName);

        if (File.Exists(outputDirFile) && File.Exists(currentDirFile)) {
            File.SetLastWriteTimeUtc(outputDirFile, DateTime.UtcNow.AddMinutes(-10));
            File.SetLastWriteTimeUtc(currentDirFile, DateTime.UtcNow);

            var loaded = ConfigLoader<FfmpegSessionConfig>.Load();
            Assert.NotNull(loaded);
            Assert.True(File.GetLastWriteTimeUtc(outputDirFile) >= File.GetLastWriteTimeUtc(currentDirFile));
        }
    }
}

/// <summary>Covers the scalar conversion behind <c>config set</c>.</summary>
public class ConfigWritePathTests {
    [Fact]
    public void TryWritePath_SetsAString() {
        var config = new AiStudioAutoExtractionConfig();

        Assert.True(ConfigSectionRegistry.TryWritePath(config, "Paths.SourceFolder", @"D:\new", out _));
        Assert.Equal(@"D:\new", config.SourceFolder);
    }

    [Fact]
    public void TryWritePath_SetsAnInt() {
        var config = new AiStudioAutoExtractionConfig();

        Assert.True(ConfigSectionRegistry.TryWritePath(config, "NumberOfParts", "7", out _));
        Assert.Equal(7, (int)config.NumberOfParts);
    }

    [Fact]
    public void TryWritePath_SetsAuto() {
        var config = new AiStudioAutoExtractionConfig();

        Assert.True(ConfigSectionRegistry.TryWritePath(config, "NumberOfParts", "auto", out _));
        Assert.True(config.NumberOfParts.IsAuto);
        Assert.Equal(NumberOfParts.Auto, config.NumberOfParts);
    }

    [Fact]
    public void TryWritePath_SetsABool() {
        var config = new AiStudioAutoExtractionConfig();

        Assert.True(ConfigSectionRegistry.TryWritePath(config, "GoIntoLatexRefinement", "false", out _));
        Assert.False(config.GoIntoLatexRefinement);
    }

    [Fact]
    public void TryWritePath_UsesInvariantCultureForDecimals() {
        // The dev machine is de-DE, where the decimal separator is a comma. A CLI value must not
        // change meaning with the machine's locale.
        var config = new AiStudioAutoExtractionConfig();

        Assert.True(ConfigSectionRegistry.TryWritePath(config, "SpeedMultiplier", "1.5", out _));
        Assert.Equal(1.5, config.SpeedMultiplier);
    }

    [Fact]
    public void TryWritePath_RejectsAnArray() {
        var config = new AiStudioAutoExtractionConfig();

        Assert.False(ConfigSectionRegistry.TryWritePath(config, "Paths.PredefinedSourceFolders", "a,b", out string? error));
        Assert.Contains("scalar", error);
    }

    [Fact]
    public void TryWritePath_ReportsAFailedConversion() {
        var config = new AiStudioAutoExtractionConfig();

        Assert.False(ConfigSectionRegistry.TryWritePath(config, "NumberOfParts", "seven", out string? error));
        Assert.Contains("cannot convert", error);
    }

    [Fact]
    public void TryWritePath_ReportsAnUnknownProperty() {
        var config = new AiStudioAutoExtractionConfig();

        Assert.False(ConfigSectionRegistry.TryWritePath(config, "Paths.Nope", "x", out string? error));
        Assert.Contains("Nope", error);
    }
}
