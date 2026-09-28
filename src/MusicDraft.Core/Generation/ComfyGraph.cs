using System.Text.Json.Nodes;

namespace MusicDraft.Core.Generation;

/// <summary>
/// Builds ComfyUI API-format prompt graphs for the built-in YuE2 nodes (ComfyUI v0.37.0). Node wiring follows the
/// official text-to-music template, flattened (no UI subgraphs), as validated in the Phase 0 spike.
/// </summary>
public static class ComfyGraph
{
    public const string NodeCheckpoint = "ckpt", NodeScore = "abcgen", NodeScoreView = "abcview", NodeMusic = "music",
        NodeSeconds = "seconds", NodeNegative = "neg", NodeLatent = "latent", NodeSampler = "sampler", NodeDecode = "decode",
        NodeSave = "save", NodeLoadAudio = "load", NodeTrim = "trim", NodeEncoder = "enc", NodeSheet = "sheet";

    /// <summary>Section-only lyrics used to plan an instrumental score (upstream yue2-music PLANNING_LYRICS).</summary>
    public const string InstrumentalPlanningLyrics = "[Intro]\n\n[Verse]\n\n[Chorus]\n\n[Outro]\n";

    private static JsonArray Link(string node, int output) => [node, output];

    private static JsonObject Node(string type, JsonObject inputs) => new() { ["class_type"] = type, ["inputs"] = inputs };

    private static string ModeName(ScoreMode m) => m == ScoreMode.Melody ? "melody" : "full";

    private static void AddCheckpoint(JsonObject g, string ckpt) =>
        g[NodeCheckpoint] = Node("CheckpointLoaderSimple", new JsonObject { ["ckpt_name"] = ckpt });

    private static void AddScore(JsonObject g, GenerationRequest r, string style, string lyrics, ScoreMode mode)
    {
        g[NodeScore] = Node("YuE2GenerateABC", new JsonObject
        {
            ["clip"] = Link(NodeCheckpoint, 1),
            ["style"] = style,
            ["lyrics"] = lyrics,
            ["seed"] = r.Seed,
            ["mode"] = ModeName(mode),
            ["max_abc_tokens"] = r.MaxScoreTokens,
            ["temperature"] = r.ScoreTemperature,
            ["top_p"] = r.ScoreTopP,
            ["top_k"] = r.ScoreTopK,
            ["repetition_penalty"] = r.ScoreRepetitionPenalty,
            ["penalty_window"] = 100,
        });
        g[NodeScoreView] = Node("PreviewAny", new JsonObject { ["source"] = Link(NodeScore, 0) });
    }

    /// <summary>Score only: used as step 1 of the instrumental recipe.</summary>
    public static JsonObject ScoreOnly(GenerationRequest r, string ckpt, string style, string lyrics, ScoreMode mode)
    {
        var g = new JsonObject();
        AddCheckpoint(g, ckpt);
        AddScore(g, r, style, lyrics, mode);
        return g;
    }

    /// <summary>
    /// Melody from audio (official cover template, flattened; validated in the Phase 0 spike):
    /// <c>LoadAudio</c> → optional <c>TrimAudioDuration</c> → <c>SheetSage2AudioToABC(mode=melody)</c> with the encoder from
    /// <c>AudioEncoderLoader</c> → <c>PreviewAny</c> (the ABC text is read back from <see cref="NodeScoreView"/>).
    /// <paramref name="inputFile"/> is a file name inside the engine's input folder.
    /// </summary>
    public static JsonObject Transcribe(string inputFile, string encoderFile, Covers.ReferenceWindow window)
    {
        var g = new JsonObject
        {
            [NodeLoadAudio] = Node("LoadAudio", new JsonObject { ["audio"] = inputFile }),
            [NodeEncoder] = Node("AudioEncoderLoader", new JsonObject { ["audio_encoder_name"] = encoderFile }),
        };
        var src = Link(NodeLoadAudio, 0);
        if (window.Length is { } len)
        {
            g[NodeTrim] = Node("TrimAudioDuration", new JsonObject
            {
                ["audio"] = src, ["start_index"] = Math.Round(window.Start, 2), ["duration"] = Math.Round(len, 2),
            });
            src = Link(NodeTrim, 0);
        }
        g[NodeSheet] = Node("SheetSage2AudioToABC", new JsonObject
        {
            ["audio_encoder"] = Link(NodeEncoder, 0), ["audio"] = src, ["mode"] = "melody",
        });
        g[NodeScoreView] = Node("PreviewAny", new JsonObject { ["source"] = Link(NodeSheet, 0) });
        return g;
    }

    /// <summary>Music + synthesis + decode + FLAC save, conditioned on <paramref name="abc"/> (text or a link).</summary>
    public static JsonObject Music(GenerationRequest r, string ckpt, string style, string lyrics, JsonNode abc, ScoreMode mode,
        string filenamePrefix, bool tiled)
    {
        var g = new JsonObject();
        AddCheckpoint(g, ckpt);
        AddMusic(g, r, style, lyrics, abc, mode, filenamePrefix, tiled);
        return g;
    }

    /// <summary>Text (+lyrics) to FLAC in one graph: optional score planning feeding music generation.</summary>
    public static JsonObject TextToMusic(GenerationRequest r, string ckpt, string filenamePrefix, bool tiled)
    {
        var g = new JsonObject();
        AddCheckpoint(g, ckpt);
        JsonNode abc;
        var mode = r.Score;
        if (r.Score == ScoreMode.Off)
        {
            abc = JsonValue.Create("")!; // empty ABC => node runs in off mode automatically
            mode = ScoreMode.Full;
        }
        else
        {
            AddScore(g, r, r.Description.Trim(), r.Lyrics.Trim(), r.Score);
            abc = Link(NodeScore, 0);
        }
        AddMusic(g, r, r.Description.Trim(), r.Lyrics.Trim(), abc, mode, filenamePrefix, tiled);
        return g;
    }

    private static void AddMusic(JsonObject g, GenerationRequest r, string style, string lyrics, JsonNode abc, ScoreMode mode,
        string filenamePrefix, bool tiled)
    {
        g[NodeMusic] = Node("YuE2GenerateMusic", new JsonObject
        {
            ["clip"] = Link(NodeCheckpoint, 1),
            ["style"] = style,
            ["lyrics"] = lyrics,
            ["abc"] = abc,
            ["seed"] = r.Seed,
            ["mode"] = ModeName(mode),
            ["max_duration"] = r.MaxSeconds,
            ["temperature"] = r.Temperature,
            ["top_p"] = r.TopP,
            ["top_k"] = r.TopK,
            ["repetition_penalty"] = r.RepetitionPenalty,
            ["cfg_scale"] = r.CfgScale,
        });
        g[NodeSeconds] = Node("PreviewAny", new JsonObject { ["source"] = Link(NodeMusic, 1) });
        g[NodeNegative] = Node("ConditioningZeroOut", new JsonObject { ["conditioning"] = Link(NodeMusic, 0) });
        g[NodeLatent] = Node("EmptyYuE2LatentAudio", new JsonObject { ["seconds"] = Link(NodeMusic, 1), ["batch_size"] = 1 });
        g[NodeSampler] = Node("KSampler", new JsonObject
        {
            ["model"] = Link(NodeCheckpoint, 0),
            ["positive"] = Link(NodeMusic, 0),
            ["negative"] = Link(NodeNegative, 0),
            ["latent_image"] = Link(NodeLatent, 0),
            ["seed"] = r.Seed,
            ["steps"] = r.Steps,
            ["cfg"] = 1.0,
            ["sampler_name"] = "dpm_2",
            ["scheduler"] = "sgm_uniform",
            ["denoise"] = 1.0,
        });
        g[NodeDecode] = tiled
            ? Node("VAEDecodeAudioTiled", new JsonObject
            {
                ["samples"] = Link(NodeSampler, 0), ["vae"] = Link(NodeCheckpoint, 2), ["tile_size"] = 512, ["overlap"] = 64,
            })
            : Node("VAEDecodeAudio", new JsonObject { ["samples"] = Link(NodeSampler, 0), ["vae"] = Link(NodeCheckpoint, 2) });
        // SaveAudioAdvanced.format is a DynamicCombo: send the flat option key.
        g[NodeSave] = Node("SaveAudioAdvanced", new JsonObject
        {
            ["audio"] = Link(NodeDecode, 0), ["filename_prefix"] = filenamePrefix, ["format"] = "flac",
        });
    }

    // ---------- reading /history outputs ----------

    /// <summary>First text of a PreviewAny node output: <c>{"text": ["..."]}</c>.</summary>
    public static string? ReadText(JsonObject outputs, string node) =>
        outputs[node]?["text"] is JsonArray a && a.Count > 0 ? a[0]?.GetValue<string>() : null;

    /// <summary>The saved audio file of a SaveAudioAdvanced node: <c>{"audio": [{"filename","subfolder","type"}]}</c>.</summary>
    public static (string FileName, string Subfolder)? ReadSavedAudio(JsonObject outputs, string node)
    {
        if (outputs[node]?["audio"] is not JsonArray a || a.Count == 0 || a[0] is not JsonObject o) return null;
        var name = o["filename"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return null;
        return (name, o["subfolder"]?.GetValue<string>() ?? "");
    }
}
