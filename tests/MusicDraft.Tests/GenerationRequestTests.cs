using System.Text.Json.Nodes;
using MusicDraft.Core.Generation;

namespace MusicDraft.Tests;

public class GenerationRequestTests
{
    private static GenerationRequest Vocal => new()
    {
        Description = "upbeat indie pop, bright guitar, 110 BPM",
        Lyrics = "[Verse]\nMorning light\n\n[Chorus]\nWe are flying",
        MaxSeconds = 60,
        Seed = 42,
    };

    [Fact]
    public void Valid_vocal_request() => Assert.Empty(Vocal.Validate());

    [Fact]
    public void Valid_instrumental_needs_no_lyrics() =>
        Assert.Empty((Vocal with { Instrumental = true, Lyrics = "" }).Validate());

    [Theory]
    [InlineData("", "[Verse]\nhi", false)]                  // no description
    [InlineData("pop", "", false)]                          // vocals without lyrics
    [InlineData("pop song", "just words", false)]           // no section tags
    [InlineData("pop song", "[Verse]\n[Chorus]", false)]    // tags only
    public void Invalid_requests(string desc, string lyrics, bool instrumental) =>
        Assert.NotEmpty((Vocal with { Description = desc, Lyrics = lyrics, Instrumental = instrumental }).Validate());

    [Theory]
    [InlineData(19)]
    [InlineData(181)]
    public void Length_bounds(double s) => Assert.NotEmpty((Vocal with { MaxSeconds = s }).Validate());

    [Fact]
    public void Advanced_bounds()
    {
        Assert.NotEmpty((Vocal with { Temperature = 0 }).Validate());
        Assert.NotEmpty((Vocal with { Steps = 1 }).Validate());
        Assert.NotEmpty((Vocal with { Seed = -1 }).Validate());
        Assert.NotEmpty((Vocal with { Instrumental = true, Score = ScoreMode.Off }).Validate());
    }

    [Fact]
    public void Title_from_description()
    {
        Assert.Equal("upbeat indie pop, bright guitar, 110 BPM", Vocal.EffectiveTitle);
        Assert.Equal("Mine", (Vocal with { Title = " Mine " }).EffectiveTitle);
        Assert.EndsWith("…", (Vocal with { Description = new string('a', 100) }).EffectiveTitle);
    }

    [Fact]
    public void Safe_file_name() => Assert.Equal("a_b_ c", GenerationService.SafeName("a/b: c."));

    [Fact]
    public void Composed_lyrics_validate() =>
        Assert.Empty((Vocal with { Lyrics = LyricsComposer.Compose("Morning light", "We are flying") }).Validate());

    [Fact]
    public void Lyrics_check_can_be_skipped_for_ui() =>
        Assert.Empty((Vocal with { Lyrics = "" }).Validate(includeLyrics: false));
}

public class LyricsComposerTests
{
    [Fact]
    public void Composes_verse_then_chorus_with_tags()
    {
        Assert.Equal("[Verse]\nMorning light\nEvery window gold\n\n[Chorus]\nWe are flying",
            LyricsComposer.Compose("Morning light\r\nEvery window gold", "We are flying"));
    }

    [Fact]
    public void Trims_lines_and_drops_blank_lines()
    {
        Assert.Equal("[Verse]\na\nb\n\n[Chorus]\nc", LyricsComposer.Compose("  a  \n\n\n   \n b\n", "\n c \n"));
    }

    [Fact]
    public void Strips_lines_that_are_only_tags()
    {
        Assert.Equal("[Verse]\nhello\n\n[Chorus]\nworld [loud]",
            LyricsComposer.Compose("[Verse]\nhello\n[Chorus]", " [Chorus] \nworld [loud]"));
        Assert.True(LyricsComposer.HasTagLines("x\n[Bridge]"));
        Assert.False(LyricsComposer.HasTagLines("a line with [brackets] inside"));
    }

    [Theory]
    [InlineData("", "chorus")]
    [InlineData("   \n  ", "chorus")]
    [InlineData("verse", "")]
    [InlineData("verse", "[Chorus]")]
    [InlineData("[Verse]", "chorus")]
    public void Both_boxes_are_mandatory(string verse, string chorus) =>
        Assert.Single(LyricsComposer.Validate(verse, chorus));

    [Fact]
    public void Both_empty_reports_both() => Assert.Equal(2, LyricsComposer.Validate("", " ").Count);

    [Fact]
    public void Tag_only_box_gets_specific_message() =>
        Assert.Contains("tag", LyricsComposer.SectionProblem("chorus", "[Chorus]")!);

    [Fact]
    public void Valid_boxes_have_no_problems()
    {
        Assert.Empty(LyricsComposer.Validate("a", "b"));
        Assert.Null(LyricsComposer.SectionProblem("verse", "a"));
    }

    [Fact]
    public void Per_box_length_limit_and_combined_fits_request_limit()
    {
        var big = new string('a', LyricsComposer.MaxSectionChars + 1);
        Assert.Single(LyricsComposer.Validate(big, "b"));
        var max = new string('a', LyricsComposer.MaxSectionChars);
        var composed = LyricsComposer.Compose(max, max);
        Assert.True(composed.Length <= GenerationRequest.MaxLyricsChars);
        Assert.Empty(new GenerationRequest { Description = "pop song", Lyrics = composed }.Validate());
    }

    [Fact]
    public void Empty_sections_are_left_out() =>
        Assert.Equal("", LyricsComposer.Compose("  ", "[Chorus]"));

    [Fact]
    public void Splits_legacy_tagged_drafts()
    {
        Assert.Equal(("a\nb", "c"), LyricsComposer.Split("[Verse]\na\nb\n\n[Chorus]\nc\n\n[Verse 2]\nd"));
        Assert.Equal(("plain", ""), LyricsComposer.Split("plain"));
        Assert.Equal(("", ""), LyricsComposer.Split("[Verse]\n\n[Chorus]\n"));
    }
}

public class ComfyGraphTests
{
    private static readonly GenerationRequest R = new()
    {
        Description = "upbeat indie pop", Lyrics = "[Verse]\nla", MaxSeconds = 120, Seed = 7, Steps = 32,
    };

    [Fact]
    public void Text_graph_matches_validated_spike_wiring()
    {
        var g = ComfyGraph.TextToMusic(R, "yue2_3b_int8_convrot.safetensors", "md/j/song", tiled: true);
        Assert.Equal("CheckpointLoaderSimple", g["ckpt"]!["class_type"]!.GetValue<string>());
        Assert.Equal("YuE2GenerateABC", g["abcgen"]!["class_type"]!.GetValue<string>());
        var music = g["music"]!["inputs"]!;
        Assert.Equal("abcgen", music["abc"]![0]!.GetValue<string>());
        Assert.Equal(120.0, music["max_duration"]!.GetValue<double>());
        Assert.Equal("full", music["mode"]!.GetValue<string>());
        Assert.Equal("music", g["latent"]!["inputs"]!["seconds"]![0]!.GetValue<string>());
        Assert.Equal(1, g["latent"]!["inputs"]!["seconds"]![1]!.GetValue<int>());
        Assert.Equal("VAEDecodeAudioTiled", g["decode"]!["class_type"]!.GetValue<string>());
        Assert.Equal(2, g["decode"]!["inputs"]!["vae"]![1]!.GetValue<int>());
        // DynamicCombo must be a flat string.
        Assert.Equal("flac", g["save"]!["inputs"]!["format"]!.GetValue<string>());
        Assert.Equal(7, g["sampler"]!["inputs"]!["seed"]!.GetValue<long>());
        AssertAllLinksResolve(g);
    }

    [Fact]
    public void Off_mode_sends_empty_abc_without_score_node()
    {
        var g = ComfyGraph.TextToMusic(R with { Score = ScoreMode.Off }, "c", "p", tiled: false);
        Assert.Null(g["abcgen"]);
        Assert.Equal("", g["music"]!["inputs"]!["abc"]!.GetValue<string>());
        Assert.Equal("VAEDecodeAudio", g["decode"]!["class_type"]!.GetValue<string>());
        AssertAllLinksResolve(g);
    }

    [Fact]
    public void Score_only_graph()
    {
        var g = ComfyGraph.ScoreOnly(R, "c", "style", ComfyGraph.InstrumentalPlanningLyrics, ScoreMode.Full);
        Assert.Equal(["ckpt", "abcgen", "abcview"], g.Select(kv => kv.Key).ToArray());
        AssertAllLinksResolve(g);
    }

    [Fact]
    public void Reads_history_outputs()
    {
        var outputs = JsonNode.Parse("""
            {"seconds":{"text":["73.56"]},"abcview":{"text":["X:1"]},
             "save":{"audio":[{"filename":"song_00001_.flac","subfolder":"md/j","type":"output"}]}}
            """)!.AsObject();
        Assert.Equal("73.56", ComfyGraph.ReadText(outputs, "seconds"));
        Assert.Equal(("song_00001_.flac", "md/j"), ComfyGraph.ReadSavedAudio(outputs, "save"));
        Assert.Null(ComfyGraph.ReadSavedAudio(new JsonObject(), "save"));
    }

    private static void AssertAllLinksResolve(JsonObject g)
    {
        foreach (var (_, node) in g)
            foreach (var (_, v) in node!["inputs"]!.AsObject())
                if (v is JsonArray { Count: 2 } a && a[0] is JsonValue s && s.TryGetValue<string>(out var target))
                    Assert.True(g.ContainsKey(target), $"dangling link to {target}");
    }
}
