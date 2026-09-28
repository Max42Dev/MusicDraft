using MusicDraft.Core;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Generation;
using MusicDraft.Core.Models;
using Xunit.Abstractions;

namespace MusicDraft.Tests;

/// <summary>
/// Real GPU end-to-end tests against the installed pinned ComfyUI runtime. Opt-in: set MUSICDRAFT_GPU_TESTS=1 and
/// MUSICDRAFT_GPU_HOME to an app-data root that already contains runtime/comfyui-0.37.0 and the int8 checkpoint.
/// </summary>
[Collection("gpu")]
public class ComfyIntegrationTests(ITestOutputHelper output)
{
    private static string? Home =>
        Environment.GetEnvironmentVariable("MUSICDRAFT_GPU_TESTS") == "1" ? Environment.GetEnvironmentVariable("MUSICDRAFT_GPU_HOME") : null;

    private static (GenerationService, CatalogDb, AppPaths, ComfyWorker) Build(string home)
    {
        var shared = new AppPaths(home);
        // Isolated catalog/jobs/output; shared runtime and models.
        var paths = new AppPaths(TestAudio.TempDir(), modelsOverride: shared.Models);
        paths.EnsureCreated();
        var reg = ModelRegistry.LoadEmbedded();
        var sharedStore = new ModelStore(shared, reg, new Downloader(Downloader.CreateClient()));
        var rt = reg.Runtimes[0];
        var worker = new ComfyWorker(new ComfyWorkerOptions(sharedStore.PythonExe(rt), sharedStore.MainPy(rt), shared.Models,
            paths.WorkerState, paths.Logs, WorkerScripts.InstrumentalScript));
        var db = new CatalogDb(paths.Database);
        // Hash-verifies present files once; cached by size+mtime afterwards.
        var problems = sharedStore.VerifyAsync(reg.Profile("yue2-int8-8gb")!, includeCover: CoverRequested, null, default).GetAwaiter().GetResult();
        Assert.Empty(problems);
        var svc = new GenerationService(db, paths, reg, p => sharedStore.Readiness(p), worker);
        return (svc, db, paths, worker);
    }

    /// <summary>Waits for the generation to finish and the service to be idle (only one generation can run at a time).</summary>
    private static async Task<JobRecord> Wait(GenerationService svc, CatalogDb db, string id, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            var j = db.GetJob(id)!;
            if (j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled && !svc.IsBusy) return j;
            await Task.Delay(250);
        }
        throw new TimeoutException();
    }

    /// <summary>Set MUSICDRAFT_GPU_COVER=1 as well to run the melody-from-audio test (needs the SheetSage2 encoder).</summary>
    private static bool CoverRequested => Environment.GetEnvironmentVariable("MUSICDRAFT_GPU_COVER") == "1";

    [Fact]
    public async Task Melody_from_audio_excerpt()
    {
        if (Home is not { } home || !CoverRequested) return;
        var (svc, db, _, worker) = Build(home);
        var profile = ModelRegistry.LoadEmbedded().Profile("yue2-int8-8gb")!;
        try
        {
            var src = TestAudio.Melody(TestAudio.TempDir(), "synthetic-melody.wav", 36);
            var req = new GenerationRequest
            {
                Description = "gentle acoustic folk, fingerpicked guitar, warm female vocal",
                Lyrics = LyricsComposer.Compose("Morning light across the city\nEvery window burning gold", "We are running, we are flying"),
                MaxSeconds = 30, Seed = GenerationRequest.NewSeed(),
                Reference = new Core.Covers.MelodyReference { SourcePath = src, SourceSeconds = 36, Start = 6 },
            };
            var t0 = DateTime.UtcNow;
            var j = await Wait(svc, db, svc.Start(req, profile).Id, TimeSpan.FromMinutes(10));
            output.WriteLine($"cover: {j.Status} {j.ErrorCode} {j.ErrorMessage} in {(DateTime.UtcNow - t0).TotalSeconds:0}s");
            output.WriteLine(j.ResultJson ?? "");
            var abcPath = Path.Combine(svc.JobDir(j.Id), "score-reference.abc");
            if (File.Exists(abcPath)) output.WriteLine("ABC:\n" + File.ReadAllText(abcPath));
            Assert.Equal(JobStatus.Succeeded, j.Status);
            Assert.EndsWith(".flac", db.GetTrack(j.OutputTrackId!.Value)!.Path);
        }
        finally
        {
            output.WriteLine("engine log tail:\n" + worker.LogTail(20));
            await svc.DisposeAsync();
            db.Dispose();
        }
    }

    [Fact]
    public async Task Vocal_song_then_cancel_then_instrumental()
    {
        if (Home is not { } home) return;
        var (svc, db, paths, worker) = Build(home);
        var reg = ModelRegistry.LoadEmbedded();
        var profile = reg.Profile("yue2-int8-8gb")!;
        var stages = new List<string>();
        svc.Updated += u => { lock (stages) if (stages.Count == 0 || stages[^1] != u.Stage) stages.Add(u.Stage); };
        try
        {
            // 1. Vocal song, full score, 30 s ceiling.
            var req = new GenerationRequest
            {
                Description = "upbeat indie pop, bright electric guitar, warm female vocal, 110 BPM",
                Lyrics = "[Verse]\nMorning light across the city\nEvery window burning gold\n\n[Chorus]\nWe are running, we are flying\nNever growing old",
                MaxSeconds = 30, Seed = GenerationRequest.NewSeed(),
            };
            var t0 = DateTime.UtcNow;
            var a = await Wait(svc, db, svc.Start(req, profile).Id, TimeSpan.FromMinutes(10));
            output.WriteLine($"vocal: {a.Status} {a.ErrorCode} {a.ErrorMessage} in {(DateTime.UtcNow - t0).TotalSeconds:0}s");
            output.WriteLine("stages: " + string.Join(" > ", stages));
            output.WriteLine(a.ResultJson ?? "");
            Assert.Equal(JobStatus.Succeeded, a.Status);
            var track = db.GetTrack(a.OutputTrackId!.Value)!;
            Assert.EndsWith(".flac", track.Path);
            Assert.InRange(track.DurationSeconds, 5, 31);
            Assert.Contains(Stages.Music, stages);
            Assert.Contains(Stages.Synthesis, stages);

            // 2. Cancel during music-token generation.
            var b = svc.Start(req with { MaxSeconds = 180, Seed = GenerationRequest.NewSeed() }, profile);
            while (db.GetJob(b.Id)!.Stage != Stages.Music) await Task.Delay(200);
            await Task.Delay(3000);
            svc.Cancel();
            var bj = await Wait(svc, db, b.Id, TimeSpan.FromMinutes(2));
            output.WriteLine($"cancel: {bj.Status} {bj.ErrorMessage}");
            Assert.Equal(JobStatus.Cancelled, bj.Status);
            Assert.Null(bj.OutputTrackId);

            // 3. Instrumental recipe after cancel (engine must still be usable).
            var c = await Wait(svc, db, svc.Start(req with { Instrumental = true, Lyrics = "", MaxSeconds = 40, Seed = GenerationRequest.NewSeed() }, profile).Id,
                TimeSpan.FromMinutes(10));
            output.WriteLine($"instrumental: {c.Status} {c.ErrorCode} {c.ErrorMessage}");
            output.WriteLine(c.ResultJson ?? "");
            Assert.Equal(JobStatus.Succeeded, c.Status);
        }
        finally
        {
            output.WriteLine("engine log tail:\n" + worker.LogTail(15));
            await svc.DisposeAsync();
            db.Dispose();
        }
    }
}
