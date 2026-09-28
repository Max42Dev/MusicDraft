using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using MusicDraft.Core.Hardware;
using MusicDraft.Core.Models;

namespace MusicDraft.Core.Downloads;

public enum SetupItemKind { Extractor, RuntimeArchive, Model }

/// <summary>One line of the consent screen: what is fetched, from where, how big, where it goes and under which terms.</summary>
public sealed record SetupItem(
    SetupItemKind Kind,
    string DisplayName,
    string SourceUrl,
    string SourcePage,
    long DownloadBytes,
    string Destination,
    string Sha256,
    string License,
    string LicenseUrl,
    bool AlreadyDownloaded,
    bool PresentUnverified = false)
{
    public DownloadItem ToDownload() => new(DisplayName, new Uri(SourceUrl), Destination, DownloadBytes, Sha256);
    public bool NeedsDownload => !AlreadyDownloaded && !PresentUnverified;
}

public sealed record SetupPlan(
    ModelProfile Profile,
    bool IncludeCover,
    IReadOnlyList<SetupItem> Items,
    bool NeedsExtraction,
    long ExtractedBytes,
    long FreeBytes)
{
    public long DownloadBytes => Items.Where(i => i.NeedsDownload).Sum(i => i.DownloadBytes);
    public long VerifyBytes => Items.Where(i => i.PresentUnverified).Sum(i => i.DownloadBytes);
    /// <summary>Downloads + extraction + a safety reserve. The runtime archive is deleted after extraction.</summary>
    public long RequiredFreeBytes => DownloadBytes + (NeedsExtraction ? ExtractedBytes : 0) + Downloader.DiskReserveBytes;
    public bool EnoughDisk => FreeBytes >= RequiredFreeBytes;
    public bool NothingToDo => Items.All(i => i.AlreadyDownloaded) && !NeedsExtraction;
}

public sealed record GenerationReadiness(bool RuntimeInstalled, bool GeneratorPresent, bool CoverPresent, string? Problem)
{
    public bool CanGenerate => RuntimeInstalled && GeneratorPresent;
}

/// <summary>Setup plan for the always-present local lyrics assistant (a small GGUF run in-process).</summary>
public sealed record TextAssistantPlan(IReadOnlyList<SetupItem> Items, long FreeBytes)
{
    public long DownloadBytes => Items.Where(i => i.NeedsDownload).Sum(i => i.DownloadBytes);
    public long VerifyBytes => Items.Where(i => i.PresentUnverified).Sum(i => i.DownloadBytes);
    public long RequiredFreeBytes => DownloadBytes + Downloader.DiskReserveBytes;
    public bool EnoughDisk => FreeBytes >= RequiredFreeBytes;
    public bool NothingToDo => Items.All(i => i.AlreadyDownloaded);
}

public sealed record TextAssistantReadiness(bool ModelPresent, string? Problem)
{
    public bool Ready => ModelPresent;
}

public sealed record SetupProgress(string Step, int StepIndex, int StepCount, long Done, long Total, double BytesPerSecond, string Phase)
{
    public double? Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : null;
}

/// <summary>
/// Manages the pinned runtime and model files under the app data folder. Downloads only registry URLs, verifies
/// SHA-256 before installing, and remembers verified files (size + timestamp) so large files are hashed once.
/// </summary>
public sealed class ModelStore
{
    private readonly AppPaths _paths;
    private readonly ModelRegistry _reg;
    private readonly Downloader _dl;
    private readonly object _cacheGate = new();

    public ModelStore(AppPaths paths, ModelRegistry reg, Downloader downloader)
    {
        _paths = paths;
        _reg = reg;
        _dl = downloader;
    }

    public ModelRegistry Registry => _reg;

    // ---------- locations ----------

    public string RuntimeDir(RuntimeEntry r) => Path.Combine(_paths.Runtime, r.InstallDir);
    public string PythonExe(RuntimeEntry r) => Path.Combine(RuntimeDir(r), r.PythonRelativePath);
    public string MainPy(RuntimeEntry r) => Path.Combine(RuntimeDir(r), r.MainRelativePath);
    public string ArchivePath(RuntimeEntry r) => Path.Combine(_paths.Downloads, r.FileName);
    public string ExtractorPath(RuntimeEntry r) => Path.Combine(_paths.Downloads, r.Extractor?.FileName ?? "7zr.exe");
    public string ModelPath(ModelFile f) => Path.Combine(_paths.Models, f.Folder, f.FileName);
    private string VerifiedCachePath => Path.Combine(_paths.Models, ".verified.json");

    public bool IsRuntimeInstalled(RuntimeEntry r)
    {
        if (!File.Exists(PythonExe(r)) || !File.Exists(MainPy(r))) return false;
        var versionFile = Path.Combine(Path.GetDirectoryName(MainPy(r))!, "comfyui_version.py");
        if (string.IsNullOrEmpty(r.Version)) return true;
        try
        {
            var m = Regex.Match(File.ReadAllText(versionFile), "__version__\\s*=\\s*\"([^\"]+)\"");
            return m.Success && m.Groups[1].Value == r.Version;
        }
        catch (IOException) { return false; }
    }

    /// <summary>Cheap check: file present with the registry size and previously hash-verified at this timestamp.</summary>
    public bool IsModelVerified(ModelFile f) => IsVerifiedCached(ModelPath(f), f.SizeBytes, f.Sha256);

    public bool IsModelPresent(ModelFile f) => new FileInfo(ModelPath(f)) is { Exists: true } fi && fi.Length == f.SizeBytes;

    public GenerationReadiness Readiness(ModelProfile p)
    {
        var rt = _reg.Runtime(p.Runtime);
        var runtime = IsRuntimeInstalled(rt);
        var gen = IsModelVerified(_reg.File(p.GeneratorFile));
        var cover = IsModelVerified(_reg.File(p.CoverFile));
        string? problem = null;
        if (!runtime) problem = "The generation runtime is not installed.";
        else if (!gen) problem = IsModelPresent(_reg.File(p.GeneratorFile))
            ? "The model file needs to be verified."
            : "The model has not been downloaded.";
        return new(runtime, gen, cover, problem);
    }

    // ---------- planning ----------

    public SetupPlan Plan(ModelProfile p, bool includeCover)
    {
        var rt = _reg.Runtime(p.Runtime);
        var items = new List<SetupItem>();
        var runtimeInstalled = IsRuntimeInstalled(rt);
        bool Present(string path, long size) => new FileInfo(path) is { Exists: true } fi && fi.Length == size;
        SetupItem Item(SetupItemKind kind, string name, string url, string page, long size, string dest, string sha, string lic, string licUrl)
        {
            var verified = IsVerifiedCached(dest, size, sha);
            return new(kind, name, url, page, size, dest, sha, lic, licUrl, verified, !verified && Present(dest, size));
        }
        if (!runtimeInstalled)
        {
            if (rt.Extractor is { } x)
                items.Add(Item(SetupItemKind.Extractor, x.DisplayName, x.Url, x.Url, x.SizeBytes, ExtractorPath(rt), x.Sha256, x.License, x.LicenseUrl));
            items.Add(Item(SetupItemKind.RuntimeArchive, rt.DisplayName, rt.Url, rt.SourceUrl, rt.SizeBytes, ArchivePath(rt), rt.Sha256, rt.License, rt.LicenseUrl));
        }
        foreach (var id in includeCover ? new[] { p.GeneratorFile, p.CoverFile } : [p.GeneratorFile])
        {
            var f = _reg.File(id);
            items.Add(Item(SetupItemKind.Model, f.DisplayName, f.Url, f.SourcePage, f.SizeBytes, ModelPath(f), f.Sha256, p.License, p.LicenseUrl));
        }
        return new(p, includeCover, items, !runtimeInstalled, rt.ExtractedBytes, HardwareProbe.FreeDiskBytes(_paths.Root));
    }

    // ---------- local lyrics assistant (small GGUF, run in-process) ----------

    /// <summary>The always-present text model entry, or null if the registry has none.</summary>
    public TextAssistantEntry? TextAssistant => _reg.TextAssistant;

    public ModelFile TextModelFile => _reg.File(_reg.TextAssistant!.ModelFile);

    public string TextModelPath => Path.Combine(_paths.Models, "text", TextModelFile.FileName);

    public bool IsTextModelPresent => new FileInfo(TextModelPath) is { Exists: true } fi && fi.Length == TextModelFile.SizeBytes;
    public bool IsTextModelVerified => IsVerifiedCached(TextModelPath, TextModelFile.SizeBytes, TextModelFile.Sha256);

    public TextAssistantReadiness TextReadiness()
    {
        var model = IsTextModelVerified;
        string? problem = model ? null
            : IsTextModelPresent ? "The lyrics assistant model needs to be verified."
            : "The lyrics assistant model has not been downloaded.";
        return new(model, problem);
    }

    /// <summary>The lyrics assistant needs only its GGUF model (the runtime is an in-process NuGet dependency).</summary>
    public TextAssistantPlan PlanTextAssistant()
    {
        var f = TextModelFile;
        var verified = IsVerifiedCached(TextModelPath, f.SizeBytes, f.Sha256);
        var present = new FileInfo(TextModelPath) is { Exists: true } fi && fi.Length == f.SizeBytes;
        var item = new SetupItem(SetupItemKind.Model, f.DisplayName, f.Url, f.SourcePage, f.SizeBytes, TextModelPath, f.Sha256,
            _reg.TextAssistant!.License, _reg.TextAssistant.LicenseUrl, verified, !verified && present);
        return new([item], HardwareProbe.FreeDiskBytes(_paths.Root));
    }

    /// <summary>Downloads/verifies the assistant model. Safe to cancel and re-run: partial downloads resume.</summary>
    public async Task InstallTextAssistantAsync(TextAssistantPlan plan, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var steps = plan.Items.Count;
        var free = HardwareProbe.FreeDiskBytes(_paths.Root);
        if (free < plan.RequiredFreeBytes)
            throw new DownloadException(DownloadErrorCode.DiskFull,
                $"Setup needs about {Downloader.Gb(plan.RequiredFreeBytes)} free on {Path.GetPathRoot(_paths.Root)}; {Downloader.Gb(free)} is available.");

        var index = 0;
        foreach (var item in plan.Items)
        {
            index++;
            ct.ThrowIfCancellationRequested();
            var i = index;
            var dp = new Progress<DownloadProgress>(d => progress?.Report(new(item.DisplayName, i, steps, d.Done, d.Total, d.BytesPerSecond, d.Phase)));
            if (IsVerifiedCached(item.Destination, item.DownloadBytes, item.Sha256)) continue;
            if (Downloader.IsPresent(item.ToDownload()))
            {
                progress?.Report(new(item.DisplayName, i, steps, 0, item.DownloadBytes, 0, "Verifying"));
                var hash = await Downloader.HashFileAsync(item.Destination,
                    done => progress?.Report(new(item.DisplayName, i, steps, done, item.DownloadBytes, 0, "Verifying")), ct);
                if (hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) { RememberVerified(item.Destination, item.Sha256); continue; }
                File.Delete(item.Destination);
            }
            await _dl.DownloadAsync(item.ToDownload(), dp, ct);
            RememberVerified(item.Destination, item.Sha256);
        }
    }

    public void RemoveTextAssistant()
    {
        TryDelete(TextModelPath);
        TryDelete(TextModelPath + ".part");
        Forget(TextModelPath);
    }

    // ---------- install ----------

    /// <summary>
    /// Downloads/verifies every item, then extracts the runtime. Existing correct files are hash-checked, not re-downloaded.
    /// Safe to cancel and re-run: partial downloads resume; a partial extraction is discarded.
    /// </summary>
    public async Task InstallAsync(SetupPlan plan, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var steps = plan.Items.Count + (plan.NeedsExtraction ? 1 : 0);
        var free = HardwareProbe.FreeDiskBytes(_paths.Root);
        if (free < plan.RequiredFreeBytes)
            throw new DownloadException(DownloadErrorCode.DiskFull,
                $"Setup needs about {Downloader.Gb(plan.RequiredFreeBytes)} free on {Path.GetPathRoot(_paths.Root)}; {Downloader.Gb(free)} is available.");

        var index = 0;
        foreach (var item in plan.Items)
        {
            index++;
            ct.ThrowIfCancellationRequested();
            var i = index;
            var dp = new Progress<DownloadProgress>(d => progress?.Report(new(item.DisplayName, i, steps, d.Done, d.Total, d.BytesPerSecond, d.Phase)));
            if (IsVerifiedCached(item.Destination, item.DownloadBytes, item.Sha256)) continue;

            if (Downloader.IsPresent(item.ToDownload()))
            {
                // Present from an earlier run or manual copy: verify rather than re-download.
                progress?.Report(new(item.DisplayName, i, steps, 0, item.DownloadBytes, 0, "Verifying"));
                var hash = await Downloader.HashFileAsync(item.Destination,
                    done => progress?.Report(new(item.DisplayName, i, steps, done, item.DownloadBytes, 0, "Verifying")), ct);
                if (hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) { RememberVerified(item.Destination, item.Sha256); continue; }
                File.Delete(item.Destination); // damaged: replace it
            }
            await _dl.DownloadAsync(item.ToDownload(), dp, ct);
            RememberVerified(item.Destination, item.Sha256);
        }

        if (plan.NeedsExtraction)
        {
            var rt = _reg.Runtime(plan.Profile.Runtime);
            await ExtractRuntimeAsync(rt, p => progress?.Report(new($"Installing {rt.DisplayName}", steps, steps, p, 100, 0, "Extracting")), ct);
        }
    }

    private async Task ExtractRuntimeAsync(RuntimeEntry rt, Action<int> percent, CancellationToken ct)
    {
        var target = RuntimeDir(rt);
        // Short staging path keeps deep PyTorch license paths within limits; same volume so the final move is a rename.
        var staging = Path.Combine(_paths.Runtime, "x" + Environment.ProcessId);
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            var psi = new ProcessStartInfo(ExtractorPath(rt))
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var a in new[] { "x", ArchivePath(rt), "-o" + staging, "-y", "-bsp1", "-bso0", "-bse1" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new IOException("Could not start the extractor.");
            var errors = new List<string>();
            var reader = Task.Run(async () =>
            {
                var buf = new char[4096];
                int n;
                var sb = new System.Text.StringBuilder();
                while ((n = await p.StandardOutput.ReadAsync(buf)) > 0)
                {
                    sb.Append(buf, 0, n);
                    var s = sb.ToString();
                    foreach (Match m in Regex.Matches(s, @"(\d{1,3})%")) percent(int.Parse(m.Groups[1].Value));
                    foreach (var line in s.Split('\n').Where(l => l.Contains("ERROR", StringComparison.OrdinalIgnoreCase)))
                        lock (errors) errors.Add(line.Trim());
                    sb.Clear();
                    var lastNl = s.LastIndexOf('\n');
                    if (lastNl >= 0 && lastNl < s.Length - 1) sb.Append(s[(lastNl + 1)..]);
                }
            }, CancellationToken.None);
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
            await reader;
            // 7-Zip: 0 = ok, 1 = warnings, 2+ = fatal.
            if (p.ExitCode >= 2)
                throw new IOException($"Extracting the runtime failed (7-Zip exit code {p.ExitCode}). " + string.Join(" ", errors.Take(3)));

            var root = Path.Combine(staging, rt.ArchiveRoot);
            if (!Directory.Exists(root)) throw new IOException($"The runtime archive did not contain '{rt.ArchiveRoot}'.");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(root, target);
            if (!IsRuntimeInstalled(rt)) throw new IOException("The extracted runtime is incomplete or has an unexpected version.");
            File.WriteAllText(Path.Combine(target, ".musicdraft-runtime.json"),
                JsonSerializer.Serialize(new { rt.Id, rt.Version, rt.Sha256, InstalledAt = DateTimeOffset.UtcNow }));
            // The archive is no longer needed once installed and verified.
            TryDelete(ArchivePath(rt));
            Forget(ArchivePath(rt));
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* best effort */ }
        }
    }

    /// <summary>Hashes present model files that are not yet verified (e.g. copied in manually). Returns problems found.</summary>
    public async Task<IReadOnlyList<string>> VerifyAsync(ModelProfile p, bool includeCover, IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var problems = new List<string>();
        var files = includeCover ? new[] { p.GeneratorFile, p.CoverFile } : [p.GeneratorFile];
        var i = 0;
        foreach (var f in files.Select(_reg.File))
        {
            i++;
            var path = ModelPath(f);
            if (IsModelVerified(f)) continue;
            if (!IsModelPresent(f)) { problems.Add($"{f.DisplayName} is not downloaded."); continue; }
            var n = i;
            var hash = await Downloader.HashFileAsync(path, d => progress?.Report(new(f.DisplayName, n, files.Length, d, f.SizeBytes, 0, "Verifying")), ct);
            if (hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) RememberVerified(path, f.Sha256);
            else problems.Add($"{f.DisplayName} is damaged (checksum mismatch) and must be downloaded again.");
        }
        return problems;
    }

    public void RemoveModel(ModelFile f)
    {
        TryDelete(ModelPath(f));
        TryDelete(ModelPath(f) + ".part");
        Forget(ModelPath(f));
    }

    /// <summary>Hashes the lyrics assistant model if it is present but not yet verified. Returns problems found.</summary>
    public async Task<IReadOnlyList<string>> VerifyTextAssistantAsync(IProgress<SetupProgress>? progress, CancellationToken ct)
    {
        var problems = new List<string>();
        var f = TextModelFile;
        if (!IsTextModelVerified)
        {
            if (!IsTextModelPresent) problems.Add($"{f.DisplayName} is not downloaded.");
            else
            {
                var hash = await Downloader.HashFileAsync(TextModelPath,
                    d => progress?.Report(new(f.DisplayName, 1, 1, d, f.SizeBytes, 0, "Verifying")), ct);
                if (hash.Equals(f.Sha256, StringComparison.OrdinalIgnoreCase)) RememberVerified(TextModelPath, f.Sha256);
                else problems.Add($"{f.DisplayName} is damaged (checksum mismatch) and must be downloaded again.");
            }
        }
        return problems;
    }

    // ---------- verified cache ----------

    private sealed record VerifiedEntry(long Size, long MtimeTicks, string Sha256);

    private Dictionary<string, VerifiedEntry> LoadCache()
    {
        try
        {
            return File.Exists(VerifiedCachePath)
                ? JsonSerializer.Deserialize<Dictionary<string, VerifiedEntry>>(File.ReadAllText(VerifiedCachePath)) ?? []
                : [];
        }
        catch (Exception) { return []; }
    }

    private void SaveCache(Dictionary<string, VerifiedEntry> c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(VerifiedCachePath)!);
        var tmp = VerifiedCachePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(c));
        File.Move(tmp, VerifiedCachePath, overwrite: true);
    }

    private static string Key(string path) => Path.GetFullPath(path).ToLowerInvariant();

    private bool IsVerifiedCached(string path, long size, string sha)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists || fi.Length != size) return false;
        lock (_cacheGate)
            return LoadCache().TryGetValue(Key(path), out var e) && e.Size == size && e.MtimeTicks == fi.LastWriteTimeUtc.Ticks &&
                   e.Sha256.Equals(sha, StringComparison.OrdinalIgnoreCase);
    }

    private void RememberVerified(string path, string sha)
    {
        var fi = new FileInfo(path);
        lock (_cacheGate)
        {
            var c = LoadCache();
            c[Key(path)] = new VerifiedEntry(fi.Length, fi.LastWriteTimeUtc.Ticks, sha.ToLowerInvariant());
            SaveCache(c);
        }
    }

    private void Forget(string path)
    {
        lock (_cacheGate)
        {
            var c = LoadCache();
            if (c.Remove(Key(path))) SaveCache(c);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
