using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using MusicDraft.Core;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Generation;
using MusicDraft.Core.Models;

namespace MusicDraft.Tests;

/// <summary>Stub backend: "generates" a synthetic WAV and replays scripted log lines and outcomes.</summary>
internal sealed class StubBackend(string outputDir) : IMusicBackend
{
    public List<JsonObject> Graphs = [];
    public Func<JsonObject, PromptResult?>? Script;       // override outcome per graph
    public List<string> ExtraLog = [];
    public double ModelSeconds = 30;
    public double AudioSeconds = 2;
    public TimeSpan Delay = TimeSpan.Zero;
    public bool StartFails;
    public int Starts;

    public Task EnsureStartedAsync(ModelProfile profile, IProgress<string>? status, CancellationToken ct)
    {
        Starts++;
        if (StartFails) throw new BackendException("E_WORKER_START", "engine failed to start");
        return Task.CompletedTask;
    }

    public Task<BackendStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new BackendStatus(true, "0.37.0", 20, 22, "stub"));

    public async Task<PromptResult> RunAsync(JsonObject graph, Action<NodeProgress>? progress, CancellationToken ct)
    {
        lock (Graphs) Graphs.Add(graph);
        if (Script?.Invoke(graph) is { } scripted) return scripted;
        foreach (var node in new[] { "abcgen", "music", "sampler", "decode", "save" })
        {
            if (!graph.ContainsKey(node)) continue;
            progress?.Invoke(new(node, 0, 10));
            try { await Task.Delay(Delay, ct); }
            catch (OperationCanceledException) { return new(PromptOutcome.Interrupted, [], ["Processing interrupted"]); }
            progress?.Invoke(new(node, 10, 10));
        }
        var outputs = new JsonObject();
        if (graph.ContainsKey("abcview")) outputs["abcview"] = new JsonObject { ["text"] = new JsonArray("X:1\nV: Vocal\nabc|") };
        if (graph["save"] is JsonObject save)
        {
            var prefix = save["inputs"]!["filename_prefix"]!.GetValue<string>();
            var sub = Path.GetDirectoryName(prefix)!.Replace('\\', '/');
            Directory.CreateDirectory(Path.Combine(outputDir, sub));
            var name = Path.GetFileName(prefix) + "_00001_.wav";
            TestAudio.Wav(Path.Combine(outputDir, sub), name, AudioSeconds);
            outputs["save"] = new JsonObject { ["audio"] = new JsonArray(new JsonObject { ["filename"] = name, ["subfolder"] = sub, ["type"] = "output" }) };
            outputs["seconds"] = new JsonObject { ["text"] = new JsonArray(ModelSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        }
        return new(PromptOutcome.Success, outputs, ["got prompt", .. ExtraLog, "Prompt executed in 1.0 seconds"]);
    }

    public string ResolveOutput(string subfolder, string fileName) => Path.Combine(outputDir, subfolder, fileName);

    public string InputDir => Path.Combine(Path.GetDirectoryName(outputDir)!, "input");

    public Task<InstrumentalScore> InstrumentalizeAsync(string abc, string style, CancellationToken ct) =>
        Task.FromResult(new InstrumentalScore(abc.Replace("Vocal", "Ins"), "Instrumental, " + style + ", no vocals.", "[Verse]\n", ScoreMode.Full, "{\"moved\":1}"));

    public Task StopAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public class GenerationServiceTests : IAsyncLifetime
{
    private readonly AppPaths _paths = new(TestAudio.TempDir());
    private CatalogDb _db = null!;
    private StubBackend _backend = null!;
    private GenerationService _svc = null!;
    private readonly ModelRegistry _reg = ModelRegistry.LoadEmbedded();
    private GenerationReadiness _ready = new(true, true, true, null);
    private readonly ConcurrentQueue<JobUpdate> _updates = new();

    private ModelProfile Profile => _reg.Profile("yue2-int8-8gb")!;

    private static GenerationRequest Req => new()
    {
        Description = "lofi hip hop, dusty piano", Lyrics = "[Verse]\nquiet rain\n\n[Chorus]\nstay awhile", MaxSeconds = 60, Seed = 5,
    };

    public Task InitializeAsync()
    {
        _paths.EnsureCreated();
        _db = new CatalogDb(_paths.Database);
        _backend = new StubBackend(Path.Combine(_paths.WorkerState, "output"));
        _svc = Make();
        return Task.CompletedTask;
    }

    private GenerationService Make()
    {
        var s = new GenerationService(_db, _paths, _reg, _ => _ready, _backend);
        s.Updated += _updates.Enqueue;
        return s;
    }

    public async Task DisposeAsync()
    {
        await _svc.DisposeAsync();
        _db.Dispose();
    }

    private async Task<JobRecord> WaitFinal(string id, int seconds = 10)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            var j = _db.GetJob(id)!;
            if (j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled) return j;
            await Task.Delay(20);
        }
        throw new TimeoutException($"job {id} stuck in {_db.GetJob(id)!.Status}/{_db.GetJob(id)!.Stage}");
    }

    [Fact]
    public async Task Successful_job_adds_generated_track_and_artifacts()
    {
        var job = _svc.Start(Req, Profile);
        var done = await WaitFinal(job.Id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        var track = _db.GetTrack(done.OutputTrackId!.Value)!;
        Assert.Equal(TrackOrigin.Generated, track.Origin);
        Assert.Equal(job.Id, track.JobId);
        Assert.StartsWith(_paths.Output, track.Path);
        Assert.True(File.Exists(track.Path));
        Assert.InRange(track.DurationSeconds, 1.9, 2.1); // decoded length, not model estimate
        var dir = _svc.JobDir(job.Id);
        foreach (var f in new[] { "request.json", "graph.json", "manifest.json", "engine.log", "score.abc" })
            Assert.True(File.Exists(Path.Combine(dir, f)), f);
        Assert.False(Directory.Exists(Path.Combine(_paths.WorkerState, "output", "md", job.Id))); // engine copy cleaned
        Assert.Contains(_updates, u => u.Stage == Stages.Music && u.StageFraction is > 0);
        Assert.Contains(_updates, u => u.Stage == Stages.Synthesis);
        Assert.Contains(_updates, u => u.Stage == Stages.Decoding);
    }

    [Fact]
    public async Task Token_budget_warning_marks_truncation()
    {
        _backend.ExtraLog.Add("[WARNING] YuE2 semantic reached its token budget before the end token.");
        _backend.ModelSeconds = 60;
        var done = await WaitFinal(_svc.Start(Req, Profile).Id);
        Assert.Contains("\"semanticTruncated\": true", done.ResultJson);
        Assert.Contains("\"reachedMaximum\": true", done.ResultJson);
        Assert.Contains("stops abruptly", done.ResultJson);
    }

    [Fact]
    public async Task Instrumental_runs_score_then_converted_music()
    {
        var done = await WaitFinal(_svc.Start(Req with { Instrumental = true, Lyrics = "" }, Profile).Id);
        Assert.Equal(JobStatus.Succeeded, done.Status);
        Assert.Equal(2, _backend.Graphs.Count);
        Assert.False(_backend.Graphs[0].ContainsKey("music"));
        var music = _backend.Graphs[1]["music"]!["inputs"]!;
        Assert.Contains("Ins", music["abc"]!.GetValue<string>());
        Assert.StartsWith("Instrumental", music["style"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_svc.JobDir(done.Id), "instrumental-check.json")));
    }

    [Fact]
    public async Task Cancel_running_job_leaves_no_track()
    {
        _backend.Delay = TimeSpan.FromSeconds(5);
        var job = _svc.Start(Req, Profile);
        while (_updates.All(u => u.Stage != Stages.Music)) await Task.Delay(10);
        _svc.Cancel();
        var done = await WaitFinal(job.Id);
        Assert.Equal(JobStatus.Cancelled, done.Status);
        Assert.Empty(_db.GetTracks());
        Assert.Empty(Directory.GetFiles(_paths.Output));
        Assert.False(_svc.IsBusy);
    }

    [Fact]
    public async Task Only_one_generation_at_a_time()
    {
        _backend.Delay = TimeSpan.FromMilliseconds(100);
        var a = _svc.Start(Req, Profile);
        Assert.True(_svc.IsBusy);
        Assert.Equal(a.Id, _svc.CurrentJobId);
        Assert.Throws<InvalidOperationException>(() => _svc.Start(Req with { Seed = 6 }, Profile));
        Assert.Single(_db.GetJobs()); // the rejected request left no record
        Assert.Equal(JobStatus.Succeeded, (await WaitFinal(a.Id)).Status);
        while (_svc.IsBusy) await Task.Delay(10);

        // Idle again: the next song can start, and the same title gets a unique file name.
        var b = _svc.Start(Req with { Seed = 7 }, Profile);
        Assert.Equal(JobStatus.Succeeded, (await WaitFinal(b.Id)).Status);
        Assert.Equal(2, _db.GetTracks().Count);
        Assert.NotEqual(_db.GetTracks()[0].Path, _db.GetTracks()[1].Path);
    }

    [Fact]
    public async Task Service_is_idle_when_final_update_is_raised()
    {
        var idleAtFinal = new TaskCompletionSource<bool>();
        _svc.Updated += u => { if (u.IsFinal) idleAtFinal.TrySetResult(!_svc.IsBusy); };
        _svc.Start(Req, Profile);
        Assert.True(await idleAtFinal.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Cancel_when_idle_is_harmless() => _svc.Cancel();

    [Fact]
    public async Task Out_of_memory_is_reported_clearly()
    {
        _backend.Script = _ => new(PromptOutcome.Error, [], ["torch.OutOfMemoryError: CUDA out of memory"], "OutOfMemoryError", "CUDA out of memory", "KSampler");
        var done = await WaitFinal(_svc.Start(Req, Profile).Id);
        Assert.Equal(JobStatus.Failed, done.Status);
        Assert.Equal("E_VRAM", done.ErrorCode);
        Assert.True(File.Exists(Path.Combine(_svc.JobDir(done.Id), "error.json")));
        Assert.Empty(_db.GetTracks());
    }

    [Fact]
    public async Task Engine_error_mid_decode_fails_without_track()
    {
        _backend.Script = g => new(PromptOutcome.Error, [], ["Traceback..."], "RuntimeError", "decode exploded", "VAEDecodeAudioTiled");
        var done = await WaitFinal(_svc.Start(Req, Profile).Id);
        Assert.Equal("E_ENGINE", done.ErrorCode);
        Assert.Contains("VAEDecodeAudioTiled", done.ErrorMessage);
    }

    [Fact]
    public async Task Missing_output_file_fails()
    {
        _backend.Script = _ => new(PromptOutcome.Success, JsonNode.Parse("""{"save":{"audio":[{"filename":"gone.flac","subfolder":"md/x","type":"output"}]}}""")!.AsObject(), []);
        var done = await WaitFinal(_svc.Start(Req, Profile).Id);
        Assert.Equal("E_NO_OUTPUT", done.ErrorCode);
    }

    [Fact]
    public async Task Not_ready_fails_fast_without_starting_engine()
    {
        _ready = new(false, false, false, "The generation runtime is not installed.");
        var done = await WaitFinal(_svc.Start(Req, Profile).Id);
        Assert.Equal("E_NOT_READY", done.ErrorCode);
        Assert.Equal(0, _backend.Starts);
    }

    [Fact]
    public async Task Engine_start_failure_is_reported()
    {
        _backend.StartFails = true;
        var done = await WaitFinal(_svc.Start(Req, Profile).Id);
        Assert.Equal("E_WORKER_START", done.ErrorCode);
    }

    [Fact]
    public void Invalid_request_is_rejected_at_submit() =>
        Assert.Throws<ArgumentException>(() => _svc.Start(Req with { Lyrics = "" }, Profile));

    [Fact]
    public async Task Restart_marks_unfinished_generation_interrupted()
    {
        var now = DateTimeOffset.UtcNow;
        _db.InsertJob(new JobRecord("old-run", JobKind.Generate, JobStatus.Running, now, now, "{}", Stages.Music, null, null, null, null));
        // Written by older builds that had a job queue.
        _db.InsertJob(new JobRecord("old-queued", JobKind.Generate, JobStatus.Queued, now, now, "{}", "Queued", null, null, null, null));
        await _svc.DisposeAsync();
        _svc = Make();
        Assert.Equal(JobStatus.Interrupted, _db.GetJob("old-run")!.Status);
        Assert.Equal(JobStatus.Interrupted, _db.GetJob("old-queued")!.Status);
        Assert.Equal("E_INTERRUPTED", _db.GetJob("old-run")!.ErrorCode);
    }

    [Fact]
    public void Stored_request_roundtrips()
    {
        var job = _svc.Start(Req with { Score = ScoreMode.Melody }, Profile);
        var r = GenerationService.ReadRequest(_db.GetJob(job.Id)!)!;
        Assert.Equal(ScoreMode.Melody, r.Request.Score);
        Assert.Equal(Profile.Id, r.ProfileId);
    }
}

public class ModelStoreTests
{
    [Fact]
    public void Plan_lists_every_download_with_terms_and_sizes()
    {
        var paths = new AppPaths(TestAudio.TempDir());
        var reg = ModelRegistry.LoadEmbedded();
        var store = new ModelStore(paths, reg, new Downloader(Downloader.CreateClient()));
        var p = reg.Profile("yue2-int8-8gb")!;
        var plan = store.Plan(p, includeCover: false);
        Assert.True(plan.NeedsExtraction);
        Assert.Equal([SetupItemKind.Extractor, SetupItemKind.RuntimeArchive, SetupItemKind.Model], plan.Items.Select(i => i.Kind));
        Assert.All(plan.Items, i =>
        {
            Assert.StartsWith("https://", i.SourceUrl);
            Assert.StartsWith(paths.Root, i.Destination);
            Assert.False(string.IsNullOrWhiteSpace(i.License));
            Assert.Equal(64, i.Sha256.Length);
        });
        Assert.Equal(602624 + 1925204508L + 3960938800L, plan.DownloadBytes);
        Assert.Equal(4, store.Plan(p, includeCover: true).Items.Count);
        Assert.False(store.Readiness(p).CanGenerate);
    }

    [Fact]
    public async Task Present_file_is_verified_not_redownloaded()
    {
        var paths = new AppPaths(TestAudio.TempDir());
        var body = new byte[1000];
        new Random(1).NextBytes(body);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
        var reg = ModelRegistry.Parse($$"""
            {"schemaVersion":1,
             "runtimes":[{"id":"rt","displayName":"rt","url":"http://127.0.0.1:1/rt.7z","fileName":"rt.7z","sizeBytes":1,"sha256":"{{new string('0', 64)}}",
               "extractedBytes":1,"installDir":"rt","archiveRoot":"r","pythonRelativePath":"python.exe","mainRelativePath":"main.py",
               "license":"x","licenseUrl":"x","sourceUrl":"x","requiresNvidia":true}],
             "files":[{"id":"gen","displayName":"Gen","repo":"a/b","revision":"r","path":"checkpoints/g.safetensors","sizeBytes":1000,"sha256":"{{sha}}"}],
             "profiles":[{"id":"p","displayName":"P","vramClass":8,"minVramGb":7,"minRamGb":16,"status":"supported","statusNote":"",
               "runtime":"rt","generatorFile":"gen","coverFile":"gen","workerFlags":[],"forceTiledDecode":true,"license":"L","licenseUrl":"u"}]}
            """);
        var store = new ModelStore(paths, reg, new Downloader(Downloader.CreateClient()));
        var f = reg.File("gen");
        Directory.CreateDirectory(Path.GetDirectoryName(store.ModelPath(f))!);
        await File.WriteAllBytesAsync(store.ModelPath(f), body);
        Assert.False(store.IsModelVerified(f));
        Assert.Empty(await store.VerifyAsync(reg.Profile("p")!, false, null, default));
        Assert.True(store.IsModelVerified(f));
        var plan = store.Plan(reg.Profile("p")!, false);
        Assert.True(plan.Items.Single(i => i.Kind == SetupItemKind.Model).AlreadyDownloaded);

        // A changed file is no longer trusted.
        body[0] ^= 0xFF;
        await File.WriteAllBytesAsync(store.ModelPath(f), body);
        File.SetLastWriteTimeUtc(store.ModelPath(f), DateTime.UtcNow.AddMinutes(1));
        Assert.False(store.IsModelVerified(f));
        Assert.NotEmpty(await store.VerifyAsync(reg.Profile("p")!, false, null, default));
    }
}
