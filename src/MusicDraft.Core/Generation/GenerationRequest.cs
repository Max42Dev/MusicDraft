using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MusicDraft.Core.Generation;

/// <summary>Score planning ("chain of thought") mode. Off skips the ABC score and conditions on text only.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ScoreMode>))]
public enum ScoreMode { Full, Melody, Off }

/// <summary>
/// A validated text-to-music request. Defaults are the upstream ComfyUI node defaults used in the Phase 0 spike;
/// bounds are deliberately tighter than the node limits until other values are measured.
/// </summary>
public sealed partial record GenerationRequest
{
    public const double MinSeconds = 20, MaxSecondsLimit = 180;
    public const int MaxDescriptionChars = 1000, MaxLyricsChars = 6000;
    public const long MaxSeed = (1L << 48) - 1;

    public int SchemaVersion { get; init; } = 1;
    public string? Title { get; init; }
    public string Description { get; init; } = "";
    public string Lyrics { get; init; } = "";
    public bool Instrumental { get; init; }
    /// <summary>Ceiling passed to YuE2GenerateMusic.max_duration; the song may end sooner.</summary>
    public double MaxSeconds { get; init; } = 90;
    public long Seed { get; init; }
    public ScoreMode Score { get; init; } = ScoreMode.Full;
    /// <summary>
    /// Optional local audio whose melody guides the song ("melody from audio"). When set, the score comes from
    /// SheetSage2 transcription in melody mode and <see cref="Score"/> is ignored.
    /// </summary>
    public Covers.MelodyReference? Reference { get; init; }

    // Music-token sampling (YuE2GenerateMusic)
    public double Temperature { get; init; } = 1.0;
    public double TopP { get; init; } = 0.95;
    public int TopK { get; init; } = 100;
    public double RepetitionPenalty { get; init; } = 1.2;
    public double CfgScale { get; init; } = 1.0;

    // Score sampling (YuE2GenerateABC)
    public double ScoreTemperature { get; init; } = 0.7;
    public double ScoreTopP { get; init; } = 0.9;
    public int ScoreTopK { get; init; } = 30;
    public double ScoreRepetitionPenalty { get; init; } = 1.005;
    public int MaxScoreTokens { get; init; } = 8192;

    // Acoustic synthesis and decode
    public int Steps { get; init; } = 32;
    public bool TiledDecode { get; init; } = true;

    public static GenerationRequest Defaults { get; } = new();

    public static long NewSeed() => Random.Shared.NextInt64(0, MaxSeed);

    public string EffectiveTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Title)) return Title.Trim();
            var first = Description.Trim().Split('\n')[0].Trim().TrimEnd('.', ',');
            if (first.Length == 0) return "Untitled";
            return first.Length <= 60 ? first : first[..57].TrimEnd() + "…";
        }
    }

    [GeneratedRegex(@"^\s*\[[^\]\r\n]{1,40}\]\s*$", RegexOptions.Multiline)]
    private static partial Regex SectionTag();

    /// <summary>
    /// Validates the request. <see cref="Lyrics"/> is the already-composed, section-tagged string
    /// (see <see cref="LyricsComposer.Compose"/>); pass <paramref name="includeLyrics"/> = false when the UI reports
    /// lyric problems per input box itself.
    /// </summary>
    public IReadOnlyList<string> Validate(bool includeLyrics = true)
    {
        var e = new List<string>();
        var desc = Description.Trim();
        if (desc.Length < 3) e.Add("Describe the music you want (genre, instruments, mood, tempo).");
        if (desc.Length > MaxDescriptionChars) e.Add($"The description is too long (max {MaxDescriptionChars} characters).");
        if (!Instrumental && includeLyrics)
        {
            var lyrics = Lyrics.Trim();
            if (lyrics.Length == 0) e.Add("Vocal songs need verse and chorus lyrics, or switch to Instrumental.");
            else
            {
                // Composed by LyricsComposer, so these only fire for hand-built requests.
                if (!SectionTag().IsMatch(lyrics)) e.Add("The lyrics are missing section tags.");
                if (SectionTag().Replace(lyrics, "").Trim().Length == 0) e.Add("The lyrics have no words to sing.");
            }
            if (lyrics.Length > MaxLyricsChars) e.Add($"The lyrics are too long (max {MaxLyricsChars} characters).");
        }
        else if (Instrumental && Score == ScoreMode.Off && Reference == null)
        {
            e.Add("Instrumental mode needs a planned score; set score planning to Full.");
        }
        if (Reference != null && Covers.ReferenceRules.Validate(Reference) is { } refProblem) e.Add(refProblem);
        if (MaxSeconds < MinSeconds || MaxSeconds > MaxSecondsLimit)
            e.Add($"Length must be between {MinSeconds:0} s and {MaxSecondsLimit / 60:0} minutes.");
        if (Seed < 0 || Seed > MaxSeed) e.Add("The seed is out of range.");
        Range(e, "Temperature", Temperature, 0.1, 2.0);
        Range(e, "Top-p", TopP, 0.05, 1.0);
        Range(e, "Top-k", TopK, 1, 1000);
        Range(e, "Repetition penalty", RepetitionPenalty, 1.0, 2.0);
        Range(e, "Text guidance", CfgScale, 1.0, 3.0);
        Range(e, "Score temperature", ScoreTemperature, 0.1, 2.0);
        Range(e, "Score top-p", ScoreTopP, 0.05, 1.0);
        Range(e, "Score top-k", ScoreTopK, 1, 1000);
        Range(e, "Score repetition penalty", ScoreRepetitionPenalty, 1.0, 2.0);
        Range(e, "Score token limit", MaxScoreTokens, 512, 20000);
        Range(e, "Synthesis steps", Steps, 8, 100);
        return e;
    }

    private static void Range(List<string> e, string name, double v, double min, double max)
    {
        if (double.IsNaN(v) || v < min || v > max) e.Add($"{name} must be between {min} and {max}.");
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>What is stored in <c>jobs.request_json</c>.</summary>
public sealed record StoredRequest(GenerationRequest Request, string ProfileId);

public sealed record GenerationResult(
    string OutputPath,
    string ArtifactsDir,
    string ProfileId,
    string ModelFile,
    string ModelRevision,
    long Seed,
    double TargetSeconds,
    double? ModelSeconds,
    double DecodedSeconds,
    bool ReachedMaximum,
    bool SemanticTruncated,
    bool ScoreTruncated,
    bool Silent,
    bool Instrumental,
    IReadOnlyList<string> Warnings,
    double ElapsedSeconds);
