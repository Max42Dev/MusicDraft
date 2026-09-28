using MusicDraft.Core;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Generation;
using MusicDraft.Core.Models;
using Xunit.Abstractions;

namespace MusicDraft.Tests;

/// <summary>
/// Real end-to-end test of the in-process lyrics assistant (LLamaSharp + the pinned Gemma 4 E2B GGUF).
/// Opt-in: set MUSICDRAFT_LYRICS_TESTS=1 and MUSICDRAFT_GPU_HOME to an app-data root that already contains the model.
/// </summary>
public class LyricsAssistantIntegrationTests(ITestOutputHelper output)
{
    private static string? Home =>
        Environment.GetEnvironmentVariable("MUSICDRAFT_LYRICS_TESTS") == "1" ? Environment.GetEnvironmentVariable("MUSICDRAFT_GPU_HOME") : null;

    [Fact]
    public async Task Generates_a_verse_and_chorus()
    {
        if (Home is not { } home) { output.WriteLine("Skipped: set MUSICDRAFT_LYRICS_TESTS=1 and MUSICDRAFT_GPU_HOME."); return; }
        var paths = new AppPaths(home);
        var store = new ModelStore(paths, ModelRegistry.LoadEmbedded(), new Downloader(Downloader.CreateClient()));
        Assert.True(store.IsTextModelPresent, $"Model not found at {store.TextModelPath}");

        await using var assistant = new LyricsAssistant(new LyricsAssistantOptions(store.TextModelPath));
        var reply = await assistant.GenerateAsync(LyricsPrompt.System,
            LyricsPrompt.BuildUserPrompt("dreamy synth-pop, analog pads, breathy female vocal, 100 BPM", "Neon Nights"),
            CancellationToken.None);
        output.WriteLine(reply);

        var (verse, chorus) = LyricsPrompt.Parse(reply);
        Assert.False(string.IsNullOrWhiteSpace(verse), "No verse parsed from:\n" + reply);
        Assert.False(string.IsNullOrWhiteSpace(chorus), "No chorus parsed from:\n" + reply);
    }
}
