using MusicDraft.Core.Generation;
using MusicDraft.Core.Models;

namespace MusicDraft.Tests;

public class LyricsPromptTests
{
    [Fact]
    public void Parses_plain_headings()
    {
        var (v, c) = LyricsPrompt.Parse("Verse:\nMorning light\nAcross the city\n\nChorus:\nWe are running\nNever growing old");
        Assert.Equal("Morning light\nAcross the city", v);
        Assert.Equal("We are running\nNever growing old", c);
    }

    [Fact]
    public void Parses_bracket_and_bold_headings()
    {
        var (v, c) = LyricsPrompt.Parse("[Verse]\nline one\n\n**Chorus**\nline two");
        Assert.Equal("line one", v);
        Assert.Equal("line two", c);
    }

    [Fact]
    public void Hook_and_refrain_are_the_chorus()
    {
        var (v, c) = LyricsPrompt.Parse("Verse:\na\nHook:\nb");
        Assert.Equal("a", v);
        Assert.Equal("b", c);
    }

    [Fact]
    public void Text_before_any_heading_is_the_verse()
    {
        var (v, c) = LyricsPrompt.Parse("just a line\nanother line");
        Assert.Equal("just a line\nanother line", v);
        Assert.Equal("", c);
    }

    [Fact]
    public void Empty_reply_is_empty()
    {
        Assert.Equal(("", ""), LyricsPrompt.Parse(""));
        Assert.Equal(("", ""), LyricsPrompt.Parse(null));
    }

    [Fact]
    public void User_prompt_includes_title_and_description()
    {
        var p = LyricsPrompt.BuildUserPrompt("dreamy synth-pop", "Neon Nights");
        Assert.Contains("Neon Nights", p);
        Assert.Contains("dreamy synth-pop", p);
    }

    [Fact]
    public void User_prompt_has_a_fallback_description()
    {
        Assert.Contains("upbeat pop song", LyricsPrompt.BuildUserPrompt("", null));
    }

    [Theory]
    [InlineData(60, 8)]    // 60 / 7.5 = 8
    [InlineData(90, 12)]   // 90 / 7.5 = 12
    [InlineData(120, 16)]  // 120 / 7.5 = 16
    [InlineData(180, 24)]  // 180 / 7.5 = 24
    [InlineData(600, 48)]  // clamped to the maximum
    [InlineData(10, 4)]    // clamped to the minimum
    public void Verse_lines_scale_with_length(double seconds, int expected)
    {
        Assert.Equal(expected, LyricsPrompt.VerseLines(seconds));
    }

    [Fact]
    public void Chorus_is_always_four_lines()
    {
        Assert.Equal(4, LyricsPrompt.ChorusLines);
    }

    [Fact]
    public void User_prompt_states_the_requested_line_counts()
    {
        var p = LyricsPrompt.BuildUserPrompt("dreamy synth-pop", "Neon Nights", 12, 6);
        Assert.Contains("about 12 short lines for the Verse", p);
        Assert.Contains("about 6 for the Chorus", p);
    }
}

public class TextAssistantRegistryTests
{
    private static readonly ModelRegistry Reg = ModelRegistry.LoadEmbedded();

    [Fact]
    public void Registry_has_a_text_assistant()
    {
        Assert.NotNull(Reg.TextAssistant);
        Assert.NotNull(Reg.File(Reg.TextAssistant!.ModelFile));
    }

    [Fact]
    public void Text_assistant_model_is_pinned()
    {
        var f = Reg.File(Reg.TextAssistant!.ModelFile);
        Assert.Equal(64, f.Sha256.Length);
        Assert.True(f.SizeBytes > 0);
        Assert.EndsWith(".gguf", f.FileName);
    }

    [Fact]
    public void Text_assistant_model_uses_a_huggingface_url()
    {
        var f = Reg.File(Reg.TextAssistant!.ModelFile);
        Assert.StartsWith("https://huggingface.co/", f.Url);
    }
}
