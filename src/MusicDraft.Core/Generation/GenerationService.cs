using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicDraft.Core.Audio;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Hardware;
using MusicDraft.Core.Models;

namespace MusicDraft.Core.Generation;

public static class Stages
{
    public const string Starting = "Starting engine", Preparing = "Preparing reference audio", Transcribing = "Transcribing melody",
        Planning = "Planning score", Arranging = "Arranging instrumental",
        Music = "Generating music", Synthesis = "Synthesizing audio", Decoding = "Decoding audio", Saving = "Saving", Done = "Done";
}

/// <summary>Live state of the current generation; raised on a background thread.</summary>
public sealed record JobUpdate(string JobId, JobStatus Status, string Stage, double? StageFraction, string? Message, GenerationResult? Result = null)
{
    public bool IsFinal => Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Cancelled;
}

/// <summary>
/// Runs exactly one generation at a time; there is no queue. <see cref="Start"/> is rejected while a generation is running.
/// Each generation is recorded in the catalog (for track provenance) and its artifacts (graphs, score, log, manifest)
/// go to <c>jobs/{id}</c>; a track is only added after decoded audio is verified.
/// </summary>
public sealed class GenerationService : IAsyncDisposable
{
    public const long MinFreeDiskBytes = 300L * 1024 * 1024;

    private readonly CatalogDb _db;
    private readonly AppPaths _paths;
    private readonly ModelRegistry _reg;
    private readonly Func<ModelProfile, GenerationReadiness> _readiness;
    private readonly IMusicBackend _backend;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task _running = Task.CompletedTask;
    private bool _engineChecked;
    private string? _lastStage; // only touched by the running generation

    public GenerationService(CatalogDb db, AppPaths paths, ModelRegistry reg, Func<ModelProfile, GenerationReadiness> readiness, IMusicBackend backend)
    {
        _db = db;
        _paths = paths;
        _reg = reg;
        _readiness = readiness;
        _backend = backend;
        MarkUnfinishedAsInterrupted();
    }

    public event Action<JobUpdate>? Updated;

    /// <summary>Id of the generation that is running now, or null when idle.</summary>
    public string? CurrentJobId { get; private set; }

    public bool IsBusy => CurrentJobId != null;

    /// <summary>A generation can't be resumed mid-inference after the app closes; mark leftover records so they aren't "running" forever.</summary>
    private void MarkUnfinishedAsInterrupted()
    {
        // Queued only exists in catalogs written by older builds that had a job queue.
        foreach (var j in _db.GetJobs().Where(j => j.Status is JobStatus.Running or JobStatus.Queued))
        {
            _db.UpdateJob(j.Id, JobStatus.Interrupted, errorCode: "E_INTERRUPTED",
                errorMessage: "MusicDraft closed before this song finished.");
            WriteJson(Path.Combine(JobDir(j.Id), "error.json"), new { code = "E_INTERRUPTED", at = DateTimeOffset.UtcNow });
        }
    }

    public string JobDir(string id) => Path.Combine(_paths.Jobs, id);

    // ---------- start / cancel ----------

    /// <summary>Starts generating one song in the background. Throws <see cref="InvalidOperationException"/> if one is already running.</summary>
    public JobRecord Start(GenerationRequest request, ModelProfile profile)
    {
        var problems = request.Validate();
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) throw new ObjectDisposedException(nameof(GenerationService));
            if (CurrentJobId != null)
                throw new InvalidOperationException("A song is already being created. Wait for it to finish or cancel it first.");
            var id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
            var now = DateTimeOffset.UtcNow;
            var stored = new StoredRequest(request, profile.Id);
            var job = new JobRecord(id, request.Reference != null ? JobKind.Cover : JobKind.Generate, JobStatus.Running, now, now,
                JsonSerializer.Serialize(stored, GenerationRequest.Json), Stages.Starting, null, null, null, null);
            _db.InsertJob(job);
            Directory.CreateDirectory(JobDir(id));
            WriteJson(Path.Combine(JobDir(id), "request.json"), stored);
            var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            _cts = cts;
            CurrentJobId = id;
            _running = Task.Run(() => RunGuardedAsync(id, cts));
            return job;
        }
    }

    public static StoredRequest? ReadRequest(JobRecord j)
    {
        try { return JsonSerializer.Deserialize<StoredRequest>(j.RequestJson, GenerationRequest.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Cancels the running generation, if any.</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private async Task RunGuardedAsync(string id, CancellationTokenSource cts)
    {
        try { await RunJobAsync(id, cts.Token); }
        catch (Exception e) { Fail(id, Stages.Done, "E_INTERNAL", $"Unexpected error: {e.Message}", e.ToString()); }
        finally
        {
            TryDelete(Path.Combine(_backend.InputDir, ReferenceInputName(id))); // the engine's working copy of the reference
            Release(id);
            cts.Dispose();
        }
    }

    /// <summary>Marks the service idle. Called before the final update is raised so a handler can start the next song at once.</summary>
    private void Release(string id)
    {
        lock (_gate)
        {
            if (CurrentJobId != id) return;
            CurrentJobId = null;
            _cts = null;
        }
    }

    private async Task RunJobAsync(string id, CancellationToken ct)
    {
        var job = _db.GetJob(id)!;
        var stored = ReadRequest(job) ?? throw new InvalidOperationException("The job request could not be read.");
        var req = stored.Request;
        var profile = _reg.Profile(stored.ProfileId);
        var dir = JobDir(id);
        Directory.CreateDirectory(dir);
        var warnings = new List<string>();
        var log = new List<string>();
        var sw = Stopwatch.StartNew();
        var stage = Stages.Starting;
        _lastStage = null;
        void Stage(string s, double? f = null, string? msg = null)
        {
            if (s != stage) _db.UpdateJob(id, JobStatus.Running, stage: s);
            stage = s;
            _lastStage = s;
            Raise(new(id, JobStatus.Running, s, f, msg));
        }

        if (profile == null) { Fail(id, stage, "E_PROFILE", $"The model profile '{stored.ProfileId}' is not available in this version."); return; }
        var ready = _readiness(profile);
        if (!ready.CanGenerate) { Fail(id, stage, "E_NOT_READY", (ready.Problem ?? "Generation is not set up.") + " Open Settings → Set up generation."); return; }
        if (req.Reference != null && !ready.CoverPresent)
        {
            Fail(id, stage, "E_NOT_READY", "Melody from audio needs the SheetSage2 transcription model. Set it up from Create or Settings, then retry.");
            return;
        }
        if (HardwareProbe.FreeDiskBytes(_paths.Output) < MinFreeDiskBytes || HardwareProbe.FreeDiskBytes(_paths.Jobs) < MinFreeDiskBytes)
        {
            Fail(id, stage, "E_DISK", $"Less than {MinFreeDiskBytes / 1_000_000} MB of disk space is free. Free some space and retry.");
            return;
        }

        _db.UpdateJob(id, JobStatus.Running, stage: stage);
        Stage(Stages.Starting, null, "Loading the generation engine (first run can take a minute)…");
        try
        {
            await _backend.EnsureStartedAsync(profile, new Progress<string>(m => Raise(new(id, JobStatus.Running, Stages.Starting, null, m))), ct);
        }
        catch (OperationCanceledException) { Finish(id, JobStatus.Cancelled, stage, "Cancelled."); return; }
        catch (BackendException e) { Fail(id, stage, e.Code, e.Message); return; }

        if (!_engineChecked)
        {
            _engineChecked = true;
            var st = await _backend.GetStatusAsync(ct);
            if (st.VramFreeGb is { } free && free + 0.25 < profile.MinVramGb)
                warnings.Add($"Only {free:0.0} GB of GPU memory was free when the engine started (profile needs about {profile.MinVramGb:0.#} GB). " +
                             "Close other GPU-heavy apps if generation fails or is very slow.");
        }

        var ckpt = _reg.File(profile.GeneratorFile);
        var tiled = req.TiledDecode || profile.ForceTiledDecode;
        var prefix = $"md/{id}/song";
        string? abc = null;
        JsonObject musicGraph;
        var scoreTruncated = false;

        try
        {
            if (req.Reference is { } reference)
            {
                // Melody from audio: transcribe the reference to a melody-only score, then render it in melody mode.
                var transcribed = await TranscribeReferenceAsync(id, reference, profile, dir, log, warnings, s => Stage(s, 0), ct);
                if (transcribed == null) return; // already failed
                abc = transcribed;
                if (req.Instrumental)
                {
                    Stage(Stages.Arranging);
                    var ins = await _backend.InstrumentalizeAsync(abc, req.Description.Trim(), ct);
                    abc = ins.Abc;
                    File.WriteAllText(Path.Combine(dir, "score.abc"), abc);
                    File.WriteAllText(Path.Combine(dir, "instrumental-check.json"), ins.CheckJson);
                    // SheetSage2 scores may carry no section comments, so the helper can return no tags.
                    var lyrics = string.IsNullOrWhiteSpace(ins.Lyrics) ? ComfyGraph.InstrumentalPlanningLyrics : ins.Lyrics;
                    musicGraph = ComfyGraph.Music(req, ckpt.FileName, ins.Style, lyrics, JsonValue.Create(abc)!, ScoreMode.Melody, prefix, tiled);
                }
                else
                {
                    File.WriteAllText(Path.Combine(dir, "score.abc"), abc);
                    musicGraph = ComfyGraph.Music(req, ckpt.FileName, req.Description.Trim(), req.Lyrics.Trim(), JsonValue.Create(abc)!,
                        ScoreMode.Melody, prefix, tiled);
                }
            }
            else if (req.Instrumental)
            {
                // Upstream instrumental recipe: plan a score from section-only lyrics, move Vocal notes to Ins, then render.
                Stage(Stages.Planning, 0);
                var scoreGraph = ComfyGraph.ScoreOnly(req, ckpt.FileName, req.Description.Trim(), ComfyGraph.InstrumentalPlanningLyrics, ScoreMode.Full);
                WriteJson(Path.Combine(dir, "graph-score.json"), scoreGraph);
                var sr = await RunAsync(scoreGraph, id, log, ct, _ => (Stages.Planning, true));
                if (sr.Outcome != PromptOutcome.Success) { HandleFailure(id, sr, stage, dir, log); return; }
                scoreTruncated = Scan(sr.Log, "YuE2 abc reached its token budget");
                var planned = ComfyGraph.ReadText(sr.Outputs, ComfyGraph.NodeScoreView);
                if (string.IsNullOrWhiteSpace(planned)) { Fail(id, stage, "E_EMPTY_SCORE", "The model did not produce a score. Try another seed or description."); return; }
                File.WriteAllText(Path.Combine(dir, "score-planned.abc"), planned);

                Stage(Stages.Arranging);
                var ins = await _backend.InstrumentalizeAsync(planned, req.Description.Trim(), ct);
                abc = ins.Abc;
                File.WriteAllText(Path.Combine(dir, "score.abc"), abc);
                File.WriteAllText(Path.Combine(dir, "instrumental-check.json"), ins.CheckJson);
                musicGraph = ComfyGraph.Music(req, ckpt.FileName, ins.Style, ins.Lyrics, JsonValue.Create(ins.Abc)!, ins.Mode, prefix, tiled);
            }
            else
            {
                musicGraph = ComfyGraph.TextToMusic(req, ckpt.FileName, prefix, tiled);
            }
        }
        catch (OperationCanceledException) { Finish(id, JobStatus.Cancelled, stage, "Cancelled."); Cleanup(id); return; }
        catch (BackendException e) { File.WriteAllLines(Path.Combine(dir, "engine.log"), log); Fail(id, stage, e.Code, e.Message); return; }

        WriteJson(Path.Combine(dir, "graph.json"), musicGraph);
        Stage(req.Score == ScoreMode.Off || req.Instrumental || req.Reference != null ? Stages.Music : Stages.Planning, 0);
        PromptResult r;
        try
        {
            r = await RunAsync(musicGraph, id, log, ct, node => node switch
            {
                ComfyGraph.NodeScore or ComfyGraph.NodeScoreView => (Stages.Planning, true),
                ComfyGraph.NodeMusic => (Stages.Music, true),
                ComfyGraph.NodeSampler => (Stages.Synthesis, true),
                ComfyGraph.NodeDecode => (Stages.Decoding, false),
                ComfyGraph.NodeSave => (Stages.Saving, false),
                _ => (null, false),
            });
        }
        catch (OperationCanceledException) { Finish(id, JobStatus.Cancelled, stage, "Cancelled."); Cleanup(id); return; }
        catch (BackendException e) { File.WriteAllLines(Path.Combine(dir, "engine.log"), log); Fail(id, stage, e.Code, e.Message); Cleanup(id); return; }

        File.WriteAllLines(Path.Combine(dir, "engine.log"), log);
        if (r.Outcome != PromptOutcome.Success) { HandleFailure(id, r, stage, dir, log); Cleanup(id); return; }

        // ---------- verify and promote the output ----------
        Stage(Stages.Saving);
        if (!req.Instrumental && req.Reference == null)
        {
            abc = ComfyGraph.ReadText(r.Outputs, ComfyGraph.NodeScoreView);
            if (abc != null) File.WriteAllText(Path.Combine(dir, "score.abc"), abc);
            scoreTruncated = Scan(r.Log, "YuE2 abc reached its token budget");
        }
        var semanticTruncated = Scan(r.Log, "YuE2 semantic reached its token budget");
        double? modelSeconds = double.TryParse(ComfyGraph.ReadText(r.Outputs, ComfyGraph.NodeSeconds), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var ms) ? ms : null;

        if (ComfyGraph.ReadSavedAudio(r.Outputs, ComfyGraph.NodeSave) is not { } saved)
        {
            Fail(id, Stages.Saving, "E_NO_OUTPUT", "The engine finished but did not report an audio file.");
            return;
        }
        string produced;
        try { produced = _backend.ResolveOutput(saved.Subfolder, saved.FileName); }
        catch (BackendException e) { Fail(id, Stages.Saving, e.Code, e.Message); return; }
        if (!File.Exists(produced)) { Fail(id, Stages.Saving, "E_NO_OUTPUT", $"The engine reported {saved.FileName} but the file is missing."); return; }

        AudioProbe probe;
        try { probe = AudioDecoder.Probe(produced, ct); }
        catch (AudioDecodeException e) { Fail(id, Stages.Saving, "E_DECODE", "The generated file could not be decoded: " + e.Message); return; }
        catch (OperationCanceledException) { Finish(id, JobStatus.Cancelled, stage, "Cancelled."); Cleanup(id); return; }

        if (probe.Silent) warnings.Add("The generated audio is silent or nearly silent. Try another seed or description.");
        var reachedMax = modelSeconds is { } s0 && s0 >= req.MaxSeconds - 0.5;
        if (semanticTruncated)
            warnings.Add($"The model reached the {FormatSec(req.MaxSeconds)} length limit before the song ended, so it stops abruptly. Increase the maximum length or shorten the lyrics.");
        if (scoreTruncated)
            warnings.Add("Score planning hit its token limit; the arrangement may be incomplete.");
        if (!req.Instrumental && req.Reference == null && modelSeconds is { } s1 && s1 < req.MaxSeconds * 0.5)
            warnings.Add($"The song ended at {FormatSec(s1)}, well under the {FormatSec(req.MaxSeconds)} maximum; length follows the lyrics and score.");
        if (req.Instrumental)
            warnings.Add("Instrumental mode uses the upstream score-conversion recipe; vocals are not guaranteed to be fully absent.");

        Directory.CreateDirectory(_paths.Output);
        var dest = UniquePath(_paths.Output, SafeName(req.EffectiveTitle), Path.GetExtension(produced));
        try
        {
            File.Copy(produced, dest + ".part", overwrite: true);
            File.Move(dest + ".part", dest);
        }
        catch (IOException e)
        {
            TryDelete(dest + ".part");
            Fail(id, Stages.Saving, "E_DISK", $"Could not save the song to {_paths.Output}: {e.Message}");
            return;
        }

        var result = new GenerationResult(dest, dir, profile.Id, ckpt.FileName, ckpt.Revision, req.Seed, req.MaxSeconds, modelSeconds,
            probe.DurationSeconds, reachedMax, semanticTruncated, scoreTruncated, probe.Silent, req.Instrumental, warnings, sw.Elapsed.TotalSeconds);
        WriteJson(Path.Combine(dir, "manifest.json"), new
        {
            schemaVersion = 1,
            jobId = id,
            completedAt = DateTimeOffset.UtcNow,
            request = req,
            profile = profile.Id,
            model = new { ckpt.Repo, ckpt.Revision, ckpt.Path, ckpt.Sha256 },
            runtime = _reg.Runtime(profile.Runtime).Id,
            result,
        });
        var track = _db.AddOrGetTrack(dest, req.EffectiveTitle, probe.DurationSeconds, TrackOrigin.Generated, id);
        var resultJson = JsonSerializer.Serialize(result, GenerationRequest.Json);
        _db.UpdateJob(id, JobStatus.Succeeded, stage: Stages.Done, outputTrackId: track.Id, resultJson: resultJson);
        Cleanup(id);
        Raise(new(id, JobStatus.Succeeded, Stages.Done, 1, $"Saved {Path.GetFileName(dest)}", result));
    }

    /// <summary>File name of the engine-side working copy of a job's reference audio (inside the engine input folder).</summary>
    public static string ReferenceInputName(string jobId) => $"md-ref-{jobId}.wav";

    /// <summary>
    /// Decodes the reference to a 48 kHz stereo WAV in the engine's local input folder (the original is only read), runs
    /// SheetSage2 in melody mode on the chosen window and returns the ABC, or null after reporting a failure.
    /// </summary>
    private async Task<string?> TranscribeReferenceAsync(string id, Covers.MelodyReference reference, ModelProfile profile, string dir,
        List<string> log, List<string> warnings, Action<string> stage, CancellationToken ct)
    {
        stage(Stages.Preparing);
        var inputName = ReferenceInputName(id);
        var inputPath = Path.Combine(_backend.InputDir, inputName);
        double seconds;
        bool silent;
        try
        {
            Directory.CreateDirectory(_backend.InputDir);
            (seconds, silent) = await Task.Run(() => AudioDecoder.ExtractToWav(reference.SourcePath, inputPath, 0, null, ct), ct);
        }
        catch (AudioDecodeException e)
        {
            Fail(id, Stages.Preparing, "E_REFERENCE", "The reference audio could not be read: " + e.Message);
            return null;
        }
        if (silent) { Fail(id, Stages.Preparing, "E_REFERENCE_SILENT", "The reference audio is silent. Choose another file or record again."); return null; }
        // Re-apply the rules to the decoded length (the file may have changed since it was chosen).
        var measured = reference with { SourceSeconds = seconds };
        if (Covers.ReferenceRules.Validate(measured) is { } problem) { Fail(id, Stages.Preparing, "E_REFERENCE", problem); return null; }
        var window = measured.Window;
        WriteJson(Path.Combine(dir, "reference.json"), new
        {
            source = reference.SourcePath, reference.Recorded, decodedSeconds = seconds, start = window.Start,
            length = window.Length ?? seconds, wholeClip = window.IsWhole,
        });

        stage(Stages.Transcribing);
        var graph = ComfyGraph.Transcribe(inputName, _reg.File(profile.CoverFile).FileName, window);
        WriteJson(Path.Combine(dir, "graph-transcribe.json"), graph);
        var r = await RunAsync(graph, id, log, ct, node => node == ComfyGraph.NodeSheet ? (Stages.Transcribing, true) : (null, false));
        if (r.Outcome != PromptOutcome.Success) { HandleFailure(id, r, Stages.Transcribing, dir, log); return null; }
        var abc = ComfyGraph.ReadText(r.Outputs, ComfyGraph.NodeScoreView);
        if (abc != null) File.WriteAllText(Path.Combine(dir, "score-reference.abc"), abc);
        if (!Covers.ReferenceRules.AbcHasNotes(abc))
        {
            Fail(id, Stages.Transcribing, "E_EMPTY_SCORE",
                "No melody was found in the reference. Choose a part with a clear tune (singing, humming or a lead instrument).");
            return null;
        }
        if (Scan(r.Log, "SheetSage2 reached its token limit"))
            warnings.Add("Melody transcription hit its token limit, so only the first part of the reference was used.");
        return abc;
    }

    private async Task<PromptResult> RunAsync(JsonObject graph, string id, List<string> log, CancellationToken ct,
        Func<string, (string? Stage, bool HasUnits)> map)
    {
        var r = await _backend.RunAsync(graph, p =>
        {
            var (s, units) = map(p.Node);
            if (s == null) return;
            double? f = units && p.Max > 0 ? Math.Clamp((double)p.Value / p.Max, 0, 1) : null;
            if (_lastStage != s) { _lastStage = s; _db.UpdateJob(id, JobStatus.Running, stage: s); }
            Raise(new(id, JobStatus.Running, s, f, null));
        }, ct);
        log.AddRange(r.Log);
        if (r.Outcome == PromptOutcome.Interrupted) throw new OperationCanceledException();
        return r;
    }

    private void HandleFailure(string id, PromptResult r, string stage, string dir, List<string> log)
    {
        stage = _lastStage ?? stage;
        File.WriteAllLines(Path.Combine(dir, "engine.log"), log);
        var text = (r.ErrorType + " " + r.ErrorMessage + " " + string.Join('\n', r.Log.TakeLast(40)));
        if (text.Contains("OutOfMemory", StringComparison.OrdinalIgnoreCase) || text.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Allocation on device", StringComparison.OrdinalIgnoreCase))
        {
            Fail(id, stage, "E_VRAM", "The GPU ran out of memory. Close other GPU apps, shorten the maximum length, or choose a lower-memory profile in Settings.",
                r.ErrorMessage);
            return;
        }
        var where = r.ErrorNode != null ? $" in {r.ErrorNode}" : "";
        Fail(id, stage, "E_ENGINE", $"Generation failed{where}: {r.ErrorMessage ?? r.ErrorType ?? "unknown error"}", string.Join('\n', r.Log.TakeLast(60)));
    }

    private void Fail(string id, string stage, string code, string message, string? detail = null)
    {
        _db.UpdateJob(id, JobStatus.Failed, stage: stage, errorCode: code, errorMessage: message);
        WriteJson(Path.Combine(JobDir(id), "error.json"), new { code, message, stage, detail, at = DateTimeOffset.UtcNow });
        Raise(new(id, JobStatus.Failed, stage, null, message));
    }

    private void Finish(string id, JobStatus status, string stage, string message)
    {
        _db.UpdateJob(id, status, stage: stage, errorMessage: status == JobStatus.Cancelled ? message : null);
        Raise(new(id, status, stage, null, message));
    }

    /// <summary>Removes the engine's copy of outputs for this job (the library copy and job artifacts are kept).</summary>
    private void Cleanup(string id)
    {
        try
        {
            var d = _backend.ResolveOutput("md", id);
            if (Directory.Exists(d)) Directory.Delete(d, true);
        }
        catch (Exception) { /* best effort */ }
    }

    private void Raise(JobUpdate u)
    {
        if (u.IsFinal) Release(u.JobId);
        try { Updated?.Invoke(u); } catch (Exception) { /* UI handler errors must not kill the generation */ }
    }

    private static bool Scan(IEnumerable<string> log, string needle) => log.Any(l => l.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static string FormatSec(double s) => Covers.ExcerptRules.Format(s);

    public static string SafeName(string title)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string(title.Select(c => bad.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (s.Length > 80) s = s[..80].TrimEnd();
        return s.Length == 0 ? "Untitled" : s;
    }

    private static string UniquePath(string dir, string name, string ext)
    {
        var p = Path.Combine(dir, name + ext);
        for (var i = 2; File.Exists(p) || File.Exists(p + ".part"); i++) p = Path.Combine(dir, $"{name} ({i}){ext}");
        return p;
    }

    private static void WriteJson(string path, object value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value is JsonNode n ? n.ToJsonString(GenerationRequest.Json) : JsonSerializer.Serialize(value, GenerationRequest.Json));
        }
        catch (IOException) { /* artifacts are best-effort; job state lives in the catalog */ }
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    public async ValueTask DisposeAsync()
    {
        Task running;
        lock (_gate)
        {
            if (_shutdown.IsCancellationRequested) return;
            _shutdown.Cancel();
            running = _running;
        }
        try { await running.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        await _backend.DisposeAsync();
    }
}
