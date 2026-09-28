using System.Text.Json.Nodes;
using MusicDraft.Core;
using MusicDraft.Core.Audio;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Covers;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Generation;
using MusicDraft.Core.Models;

namespace MusicDraft.Tests;

public class ReferenceRulesTests
{
    private static MelodyReference Ref(double seconds, double start = 0, bool whole = false) =>
        new() { SourcePath = @"C:\x\a.wav", SourceSeconds = seconds, Start = start, WholeClip = whole };

    [Theory]
    [InlineData(0, false)]
    [InlineData(4.9, false)]
    [InlineData(5, true)]
    [InlineData(19, true)]
    [InlineData(600, true)]
    [InlineData(601, false)]
    public void Source_length_limits(double seconds, bool ok) => Assert.Equal(ok, ReferenceRules.CheckSource(seconds) == null);

    [Fact]
    public void Short_clips_use_the_whole_clip()
    {
        Assert.Equal(new ReferenceWindow(0, null), ReferenceRules.Window(12, 3, wholeClip: false));
        Assert.Equal(new ReferenceWindow(0, null), ReferenceRules.Window(20, 0, wholeClip: false)); // exactly 20 s: whole
        Assert.Null(ReferenceRules.Validate(Ref(12, start: 3)));
    }

    [Fact]
    public void Long_clips_use_a_20_second_excerpt_starting_in_the_first_third()
    {
        Assert.Equal(new ReferenceWindow(10, 20), ReferenceRules.Window(60, 10, false));
        Assert.Equal(new ReferenceWindow(20, 20), ReferenceRules.Window(60, 45, false)); // clamped to 60/3
        Assert.Equal(new ReferenceWindow(5, 20), ReferenceRules.Window(25, 9, false));   // clamped to 25-20
        Assert.Equal(new ReferenceWindow(0, null), ReferenceRules.Window(60, 10, true));  // whole song chosen
        Assert.Null(ReferenceRules.Validate(Ref(60, 20)));
        Assert.NotNull(ReferenceRules.Validate(Ref(60, 21)));
        Assert.Null(ReferenceRules.Validate(Ref(60, 21, whole: true)));
    }

    [Fact]
    public void Validate_requires_a_path_and_minimum_length()
    {
        Assert.NotNull(ReferenceRules.Validate(Ref(30) with { SourcePath = "" }));
        Assert.Contains("at least 5", ReferenceRules.Validate(Ref(3))!);
    }

    [Fact]
    public void Describe_range()
    {
        Assert.Equal("0:10–0:30 of 1:00", ReferenceRules.Describe(Ref(60, 10)));
        Assert.Equal("whole clip (0:12)", ReferenceRules.Describe(Ref(12)));
    }

    [Theory]
    [InlineData("X:1\nL:1/8\nM:4/4\nK:C\nV:Vocal\ncde|", true)]
    [InlineData("X:1\nK:C\n% only a comment with A B C\nz4|z4|", false)]
    [InlineData("X:1\nK:G\n\"G\" z4 | [K:D] z4 |", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Abc_note_detection(string? abc, bool hasNotes) => Assert.Equal(hasNotes, ReferenceRules.AbcHasNotes(abc));

    [Fact]
    public void Request_validates_its_reference()
    {
        var r = new GenerationRequest { Description = "pop song", Lyrics = "[Verse]\na\n\n[Chorus]\nb", Seed = 1 };
        Assert.Empty((r with { Reference = Ref(30, 5) }).Validate());
        Assert.NotEmpty((r with { Reference = Ref(3) }).Validate());
        // Score mode is not used with a reference, so Off does not block an instrumental cover.
        Assert.Empty((r with { Instrumental = true, Lyrics = "", Score = ScoreMode.Off, Reference = Ref(30) }).Validate());
    }
}

public class RecordingFilesTests
{
    [Fact]
    public void New_path_is_timestamped_wav_and_unique()
    {
        var dir = TestAudio.TempDir();
        var t = new DateTime(2026, 9, 27, 14, 30, 12);
        var a = RecordingFiles.NewPath(dir, t);
        Assert.Equal(Path.Combine(dir, "recording-20260927-143012.wav"), a);
        File.WriteAllText(a, "x");
        Assert.Equal(Path.Combine(dir, "recording-20260927-143012 (2).wav"), RecordingFiles.NewPath(dir, t));
    }

    [Fact]
    public void Only_own_recordings_are_recognised()
    {
        var dir = TestAudio.TempDir();
        Assert.True(RecordingFiles.IsRecording(Path.Combine(dir, "recording-1.wav"), dir));
        Assert.True(RecordingFiles.IsRecording(Path.Combine(dir, "recording-1.wav"), dir + Path.DirectorySeparatorChar));
        Assert.False(RecordingFiles.IsRecording(Path.Combine(dir, "song.wav"), dir));
        Assert.False(RecordingFiles.IsRecording(Path.Combine(dir, "recording-1.mp3"), dir));
        Assert.False(RecordingFiles.IsRecording(Path.Combine(dir, "sub", "recording-1.wav"), dir));
        Assert.False(RecordingFiles.IsRecording(Path.Combine(TestAudio.TempDir(), "recording-1.wav"), dir));
    }
}

public class TranscribeGraphTests
{
    [Fact]
    public void Excerpt_graph_trims_then_transcribes_in_melody_mode()
    {
        var g = ComfyGraph.Transcribe("md-ref-j.wav", "sheetsage2_bf16.safetensors", new ReferenceWindow(10, 20));
        Assert.Equal(["load", "enc", "trim", "sheet", "abcview"], g.Select(kv => kv.Key).ToArray());
        Assert.Equal("LoadAudio", g["load"]!["class_type"]!.GetValue<string>());
        Assert.Equal("md-ref-j.wav", g["load"]!["inputs"]!["audio"]!.GetValue<string>());
        Assert.Equal("AudioEncoderLoader", g["enc"]!["class_type"]!.GetValue<string>());
        Assert.Equal("sheetsage2_bf16.safetensors", g["enc"]!["inputs"]!["audio_encoder_name"]!.GetValue<string>());
        var trim = g["trim"]!;
        Assert.Equal("TrimAudioDuration", trim["class_type"]!.GetValue<string>());
        Assert.Equal(10.0, trim["inputs"]!["start_index"]!.GetValue<double>());
        Assert.Equal(20.0, trim["inputs"]!["duration"]!.GetValue<double>());
        Assert.Equal("load", trim["inputs"]!["audio"]![0]!.GetValue<string>());
        var sheet = g["sheet"]!;
        Assert.Equal("SheetSage2AudioToABC", sheet["class_type"]!.GetValue<string>());
        Assert.Equal("melody", sheet["inputs"]!["mode"]!.GetValue<string>());
        Assert.Equal("trim", sheet["inputs"]!["audio"]![0]!.GetValue<string>());
        Assert.Equal("enc", sheet["inputs"]!["audio_encoder"]![0]!.GetValue<string>());
        Assert.Equal("sheet", g["abcview"]!["inputs"]!["source"]![0]!.GetValue<string>());
        AssertLinks(g);
    }

    [Fact]
    public void Whole_clip_graph_has_no_trim()
    {
        var g = ComfyGraph.Transcribe("a.wav", "enc.safetensors", new ReferenceWindow(0, null));
        Assert.Null(g["trim"]);
        Assert.Equal("load", g["sheet"]!["inputs"]!["audio"]![0]!.GetValue<string>());
        AssertLinks(g);
    }

    [Fact]
    public void Melody_music_graph_uses_melody_mode_and_transcribed_abc()
    {
        var r = new GenerationRequest { Description = "folk", Lyrics = "[Verse]\na\n\n[Chorus]\nb", Seed = 3, MaxSeconds = 60 };
        var g = ComfyGraph.Music(r, "c.safetensors", "folk", r.Lyrics, JsonValue.Create("X:1\nK:C\ncde|")!, ScoreMode.Melody, "md/j/song", true);
        var music = g["music"]!["inputs"]!;
        Assert.Equal("melody", music["mode"]!.GetValue<string>());
        Assert.Equal("X:1\nK:C\ncde|", music["abc"]!.GetValue<string>());
        Assert.Null(g["abcgen"]);
        AssertLinks(g);
    }

    internal static void AssertLinks(JsonObject g)
    {
        foreach (var (_, node) in g)
            foreach (var (_, v) in node!["inputs"]!.AsObject())
                if (v is JsonArray { Count: 2 } a && a[0] is JsonValue s && s.TryGetValue<string>(out var target))
                    Assert.True(g.ContainsKey(target), $"dangling link to {target}");
    }
}

public class MelodyGenerationServiceTests : IAsyncLifetime
{
    private readonly AppPaths _paths = new(TestAudio.TempDir());
    private readonly ModelRegistry _reg = ModelRegistry.LoadEmbedded();
    private CatalogDb _db = null!;
    private StubBackend _backend = null!;
    private GenerationService _svc = null!;
    private GenerationReadiness _ready = new(true, true, true, null);

    private ModelProfile Profile => _reg.Profile("yue2-int8-8gb")!;

    public Task InitializeAsync()
    {
        _paths.EnsureCreated();
        _db = new CatalogDb(_paths.Database);
        _backend = new StubBackend(Path.Combine(_paths.WorkerState, "output"));
        _svc = new GenerationService(_db, _paths, _reg, _ => _ready, _backend);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _svc.DisposeAsync();
        _db.Dispose();
    }

    private GenerationRequest Req(string source, double seconds, double start = 0) => new()
    {
        Description = "acoustic folk, warm male vocal", Lyrics = "[Verse]\nquiet rain\n\n[Chorus]\nstay awhile", MaxSeconds = 60, Seed = 5,
        Reference = new MelodyReference { SourcePath = source, SourceSeconds = seconds, Start = start },
    };

    private async Task<JobRecord> WaitFinal(string id)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            var j = _db.GetJob(id)!;
            if (j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled && !_svc.IsBusy) return j;
            await Task.Delay(20);
        }
        throw new TimeoutException();
    }

    [Fact]
    public async Task Reference_is_transcribed_then_rendered_in_melody_mode()
    {
        var src = TestAudio.Wav(TestAudio.TempDir(), "hum.wav", 30, rate: 22050, channels: 1);
        var done = await WaitFinal(_svc.Start(Req(src, 30, start: 8), Profile).Id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Equal(JobKind.Cover, done.Kind);
        Assert.Equal(2, _backend.Graphs.Count);

        var t = _backend.Graphs[0];
        var input = t["load"]!["inputs"]!["audio"]!.GetValue<string>();
        Assert.Equal(GenerationService.ReferenceInputName(done.Id), input);
        Assert.Equal(8.0, t["trim"]!["inputs"]!["start_index"]!.GetValue<double>());
        Assert.Equal("melody", t["sheet"]!["inputs"]!["mode"]!.GetValue<string>());
        Assert.Equal(_reg.File(Profile.CoverFile).FileName, t["enc"]!["inputs"]!["audio_encoder_name"]!.GetValue<string>());

        var music = _backend.Graphs[1]["music"]!["inputs"]!;
        Assert.Equal("melody", music["mode"]!.GetValue<string>());
        Assert.Contains("abc|", music["abc"]!.GetValue<string>()); // the transcribed score, as text
        Assert.Contains("quiet rain", music["lyrics"]!.GetValue<string>());
        Assert.Null(_backend.Graphs[1]["abcgen"]);

        var dir = _svc.JobDir(done.Id);
        foreach (var f in new[] { "reference.json", "graph-transcribe.json", "score-reference.abc", "score.abc", "graph.json" })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
        Assert.False(File.Exists(Path.Combine(_backend.InputDir, input))); // engine working copy removed
        Assert.True(File.Exists(src)); // original untouched
    }

    [Fact]
    public async Task Short_reference_is_used_whole()
    {
        var src = TestAudio.Wav(TestAudio.TempDir(), "short.wav", 8);
        var done = await WaitFinal(_svc.Start(Req(src, 8), Profile).Id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Null(_backend.Graphs[0]["trim"]);
    }

    [Fact]
    public async Task Missing_transcription_model_fails_before_the_engine_runs()
    {
        _ready = _ready with { CoverPresent = false };
        var src = TestAudio.Wav(TestAudio.TempDir(), "hum.wav", 10);
        var done = await WaitFinal(_svc.Start(Req(src, 10), Profile).Id);
        Assert.Equal(JobStatus.Failed, done.Status);
        Assert.Equal("E_NOT_READY", done.ErrorCode);
        Assert.Empty(_backend.Graphs);
    }

    [Fact]
    public async Task Silent_reference_fails_clearly()
    {
        var src = TestAudio.Wav(TestAudio.TempDir(), "silence.wav", 10, silent: true);
        var done = await WaitFinal(_svc.Start(Req(src, 10), Profile).Id);
        Assert.Equal(JobStatus.Failed, done.Status);
        Assert.Equal("E_REFERENCE_SILENT", done.ErrorCode);
        Assert.Empty(_backend.Graphs);
    }

    [Fact]
    public async Task Empty_transcription_fails_without_rendering()
    {
        _backend.Script = g => g.ContainsKey("sheet")
            ? new PromptResult(PromptOutcome.Success, new JsonObject { ["abcview"] = new JsonObject { ["text"] = new JsonArray("X:1\nK:C\nz4|") } }, [])
            : null;
        var src = TestAudio.Wav(TestAudio.TempDir(), "hum.wav", 10);
        var done = await WaitFinal(_svc.Start(Req(src, 10), Profile).Id);
        Assert.Equal(JobStatus.Failed, done.Status);
        Assert.Equal("E_EMPTY_SCORE", done.ErrorCode);
        Assert.Single(_backend.Graphs);
    }

    [Fact]
    public async Task Transcription_token_limit_is_reported_as_warning()
    {
        _backend.ExtraLog.Add("WARNING SheetSage2 reached its token limit; the transcription may be incomplete.");
        var src = TestAudio.Wav(TestAudio.TempDir(), "hum.wav", 10);
        var done = await WaitFinal(_svc.Start(Req(src, 10), Profile).Id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Contains("token limit", done.ResultJson);
    }

    [Fact]
    public async Task Instrumental_reference_moves_the_melody_to_instruments()
    {
        var src = TestAudio.Wav(TestAudio.TempDir(), "hum.wav", 10);
        var done = await WaitFinal(_svc.Start(Req(src, 10) with { Instrumental = true, Lyrics = "" }, Profile).Id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        var music = _backend.Graphs[1]["music"]!["inputs"]!;
        Assert.Contains("Ins", music["abc"]!.GetValue<string>());
        Assert.Equal("melody", music["mode"]!.GetValue<string>());
        Assert.StartsWith("Instrumental", music["style"]!.GetValue<string>());
    }
}
