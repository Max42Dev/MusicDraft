using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MusicDraft.Core.Models;
using MusicDraft.Core.Native;

namespace MusicDraft.Core.Generation;

public sealed record ComfyWorkerOptions(
    string PythonExe,
    string MainPy,
    string ModelsDir,
    string WorkDir,
    string LogDir,
    string InstrumentalScript);

/// <summary>
/// Supervises a hidden, loopback-only ComfyUI process (pinned v0.37.0 portable build) and drives it with API graphs.
/// No custom nodes, API nodes or browser UI are enabled. The process is killed with MusicDraft via a job object.
/// </summary>
public sealed class ComfyWorker : IMusicBackend
{
    private readonly ComfyWorkerOptions _o;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly KillOnCloseJob _job = new();
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly object _logGate = new();
    private readonly List<string> _log = [];
    private long _logBase; // number of lines trimmed from the front of _log
    private Process? _proc;
    private StreamWriter? _logFile;
    private string? _profileKey;
    private int _port;
    private readonly string _clientId = Guid.NewGuid().ToString("N");

    public ComfyWorker(ComfyWorkerOptions options) => _o = options;

    public string OutputDir => Path.Combine(_o.WorkDir, "output");
    public string InputDir => Path.Combine(_o.WorkDir, "input");
    public bool IsRunning => _proc is { HasExited: false };

    private Uri Api(string path) => new($"http://127.0.0.1:{_port}{path}");

    // ---------- process lifecycle ----------

    public async Task EnsureStartedAsync(ModelProfile profile, IProgress<string>? status, CancellationToken ct)
    {
        await _startGate.WaitAsync(ct);
        try
        {
            var key = profile.Id + "|" + string.Join(' ', profile.WorkerFlags);
            if (IsRunning && _profileKey == key && await PingAsync(ct)) return;
            await StopCoreAsync();

            if (!File.Exists(_o.PythonExe) || !File.Exists(_o.MainPy))
                throw new BackendException("E_RUNTIME_MISSING", "The generation runtime is not installed. Open Settings and set up generation.");

            foreach (var d in new[] { "input", "output", "temp", "user" }) Directory.CreateDirectory(Path.Combine(_o.WorkDir, d));
            Directory.CreateDirectory(_o.LogDir);
            _port = FreePort();

            var psi = new ProcessStartInfo(_o.PythonExe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(_o.MainPy)!,
            };
            foreach (var a in new[]
                     {
                         "-s", _o.MainPy, "--listen", "127.0.0.1", "--port", _port.ToString(), "--disable-auto-launch",
                         "--disable-api-nodes", "--disable-all-custom-nodes",
                         "--models-directory", _o.ModelsDir,
                         "--input-directory", Path.Combine(_o.WorkDir, "input"),
                         "--output-directory", OutputDir,
                         "--temp-directory", Path.Combine(_o.WorkDir, "temp"),
                         "--user-directory", Path.Combine(_o.WorkDir, "user"),
                         "--log-stdout",
                     }) psi.ArgumentList.Add(a);
            foreach (var f in profile.WorkerFlags) psi.ArgumentList.Add(f);
            psi.Environment["PYTHONUNBUFFERED"] = "1";
            psi.Environment["PYTHONIOENCODING"] = "utf-8";

            var logPath = Path.Combine(_o.LogDir, $"comfyui-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logFile = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };
            status?.Report("Starting the generation engine…");
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLog(e.Data); };
            try { p.Start(); }
            catch (Exception e) { throw new BackendException("E_WORKER_START", $"Could not start the generation engine: {e.Message}", e); }
            _job.Assign(p);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            _profileKey = key;

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(3);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (p.HasExited)
                    throw new BackendException("E_WORKER_START", "The generation engine exited while starting.\n" + LogTail(25));
                if (await PingAsync(ct)) break;
                if (DateTime.UtcNow > deadline)
                {
                    await StopCoreAsync();
                    throw new BackendException("E_WORKER_START", "The generation engine did not become ready within 3 minutes.\n" + LogTail(25));
                }
                await Task.Delay(500, ct);
            }
            status?.Report("Generation engine ready.");
        }
        finally { _startGate.Release(); }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var r = await _http.GetAsync(Api("/system_stats"), cts.Token);
            return r.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }

    public async Task<BackendStatus> GetStatusAsync(CancellationToken ct)
    {
        if (!IsRunning) return new(false, null, null, null, null);
        try
        {
            var j = JsonNode.Parse(await _http.GetStringAsync(Api("/system_stats"), ct))!;
            var dev = j["devices"]?[0];
            return new(true, j["system"]?["comfyui_version"]?.GetValue<string>(),
                dev?["vram_free"]?.GetValue<long>() / 1073741824.0, dev?["vram_total"]?.GetValue<long>() / 1073741824.0,
                dev?["name"]?.GetValue<string>());
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return new(false, null, null, null, null); }
    }

    public Task StopAsync() => StopCoreAsync();

    private async Task StopCoreAsync()
    {
        var p = _proc;
        _proc = null;
        _profileKey = null;
        if (p != null)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
            }
            catch (Exception) { /* already gone */ }
            p.Dispose();
        }
        _logFile?.Dispose();
        _logFile = null;
    }

    // ---------- logs ----------

    private void AppendLog(string line)
    {
        lock (_logGate)
        {
            _log.Add(line);
            if (_log.Count > 20000) { _log.RemoveRange(0, 5000); _logBase += 5000; }
            try { _logFile?.WriteLine(line); } catch (ObjectDisposedException) { }
        }
    }

    private long LogPosition { get { lock (_logGate) return _logBase + _log.Count; } }

    private List<string> LogSince(long pos)
    {
        lock (_logGate)
        {
            var start = (int)Math.Max(0, pos - _logBase);
            return start >= _log.Count ? [] : _log.GetRange(start, _log.Count - start);
        }
    }

    public string LogTail(int lines)
    {
        lock (_logGate) return string.Join('\n', _log.Skip(Math.Max(0, _log.Count - lines)));
    }

    // ---------- prompts ----------

    public async Task<PromptResult> RunAsync(JsonObject graph, Action<NodeProgress>? progress, CancellationToken ct)
    {
        if (!IsRunning) throw new BackendException("E_WORKER_DOWN", "The generation engine is not running.");
        var promptId = Guid.NewGuid().ToString();
        var logStart = LogPosition;
        var done = new TaskCompletionSource<PromptOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        JsonObject? errorInfo = null;

        using var wsCts = new CancellationTokenSource();
        using var ws = new ClientWebSocket();
        try { await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{_port}/ws?clientId={_clientId}"), ct); }
        catch (Exception e) when (e is not OperationCanceledException) { AppendLog($"[MusicDraft] websocket unavailable, polling only: {e.Message}"); }
        var wsTask = ws.State == WebSocketState.Open
            ? Task.Run(() => ReadEventsAsync(ws, promptId, progress, done, info => errorInfo = info, wsCts.Token), CancellationToken.None)
            : Task.CompletedTask;

        try
        {
            var body = new JsonObject { ["prompt"] = graph.DeepClone(), ["client_id"] = _clientId, ["prompt_id"] = promptId };
            using (var resp = await _http.PostAsync(Api("/prompt"), new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct))
            {
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new BackendException("E_BAD_GRAPH", "The generation engine rejected the request: " + DescribeValidationError(text));
            }

            using var reg = ct.Register(() => _ = InterruptAsync(promptId));
            var interruptedAt = (DateTime?)null;
            while (true)
            {
                var winner = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(ct.IsCancellationRequested ? 1 : 5), CancellationToken.None));
                if (winner == done.Task) break;
                if (!IsRunning)
                    throw new BackendException("E_WORKER_CRASH", "The generation engine stopped unexpectedly.\n" + LogTail(30));
                if (await TryHistoryAsync(promptId) is { } h) // fallback if websocket events were missed
                {
                    var s = h["status"]?["status_str"]?.GetValue<string>();
                    var msgs = h["status"]?["messages"] as JsonArray;
                    var interrupted = msgs?.Any(m => m?[0]?.GetValue<string>() == "execution_interrupted") == true;
                    done.TrySetResult(interrupted ? PromptOutcome.Interrupted : s == "success" ? PromptOutcome.Success : PromptOutcome.Error);
                    if (s != "success" && !interrupted)
                        errorInfo ??= msgs?.LastOrDefault(m => m?[0]?.GetValue<string>() == "execution_error")?[1] as JsonObject;
                    break;
                }
                if (ct.IsCancellationRequested)
                {
                    interruptedAt ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - interruptedAt > TimeSpan.FromSeconds(45))
                    {
                        AppendLog("[MusicDraft] engine did not stop after interrupt; restarting it");
                        await StopCoreAsync();
                        done.TrySetResult(PromptOutcome.Interrupted);
                    }
                }
            }

            var outcome = await done.Task;
            // Give the log pump a moment to flush lines written just before completion.
            await Task.Delay(300, CancellationToken.None);
            var outputs = outcome == PromptOutcome.Success && await TryHistoryAsync(promptId) is { } hist
                ? hist["outputs"] as JsonObject ?? []
                : [];
            if (outcome == PromptOutcome.Success && outputs.Count == 0)
            {
                // History can lag the websocket success event briefly.
                for (var i = 0; i < 10 && outputs.Count == 0; i++)
                {
                    await Task.Delay(300, CancellationToken.None);
                    if (await TryHistoryAsync(promptId) is { } h2) outputs = h2["outputs"] as JsonObject ?? [];
                }
            }
            return new PromptResult(outcome, (JsonObject)outputs.DeepClone(), LogSince(logStart),
                errorInfo?["exception_type"]?.ToString(), errorInfo?["exception_message"]?.ToString()?.Trim(),
                errorInfo?["node_type"]?.ToString() ?? errorInfo?["node_id"]?.ToString());
        }
        finally
        {
            wsCts.Cancel();
            try { await wsTask.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* closing */ }
        }
    }

    private static string DescribeValidationError(string body)
    {
        try
        {
            var j = JsonNode.Parse(body);
            var msg = j?["error"]?["message"]?.ToString() ?? body;
            if (j?["node_errors"] is JsonObject ne)
                foreach (var (node, v) in ne)
                    foreach (var err in v?["errors"] as JsonArray ?? [])
                        msg += $" [{node}: {err?["message"]} {err?["details"]}]";
            return msg;
        }
        catch (JsonException) { return body.Length > 500 ? body[..500] : body; }
    }

    private async Task ReadEventsAsync(ClientWebSocket ws, string promptId, Action<NodeProgress>? progress,
        TaskCompletionSource<PromptOutcome> done, Action<JsonObject> onError, CancellationToken ct)
    {
        var buf = new byte[64 * 1024];
        var ms = new MemoryStream();
        string? currentNode = null;
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                ms.SetLength(0);
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, ct);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Text) continue; // binary preview frames
                JsonNode? m;
                try { m = JsonNode.Parse(ms.ToArray()); } catch (JsonException) { continue; }
                var type = m?["type"]?.GetValue<string>();
                var data = m?["data"];
                if (data?["prompt_id"]?.ToString() is { } pid && pid != promptId) continue;
                switch (type)
                {
                    case "executing":
                        currentNode = data?["node"]?.ToString();
                        if (currentNode != null) progress?.Invoke(new(currentNode, 0, 0));
                        break;
                    case "progress":
                        var node = data?["node"]?.ToString() ?? currentNode ?? "";
                        progress?.Invoke(new(node, data?["value"]?.GetValue<int>() ?? 0, data?["max"]?.GetValue<int>() ?? 0));
                        break;
                    case "execution_success": done.TrySetResult(PromptOutcome.Success); return;
                    case "execution_interrupted": done.TrySetResult(PromptOutcome.Interrupted); return;
                    case "execution_error":
                        if (data is JsonObject o) onError((JsonObject)o.DeepClone());
                        done.TrySetResult(PromptOutcome.Error);
                        return;
                }
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (WebSocketException) { /* fall back to history polling */ }
    }

    private async Task<JsonNode?> TryHistoryAsync(string promptId)
    {
        try
        {
            var j = JsonNode.Parse(await _http.GetStringAsync(Api($"/history/{promptId}")));
            return j?[promptId];
        }
        catch (Exception) { return null; }
    }

    private async Task InterruptAsync(string promptId)
    {
        try
        {
            // Remove it if still queued, then interrupt it if running.
            await _http.PostAsync(Api("/queue"), new StringContent(new JsonObject { ["delete"] = new JsonArray(promptId) }.ToJsonString(), Encoding.UTF8, "application/json"));
            await _http.PostAsync(Api("/interrupt"), new StringContent(new JsonObject { ["prompt_id"] = promptId }.ToJsonString(), Encoding.UTF8, "application/json"));
        }
        catch (Exception e) { AppendLog($"[MusicDraft] interrupt failed: {e.Message}"); }
    }

    public string ResolveOutput(string subfolder, string fileName)
    {
        var root = Path.GetFullPath(OutputDir);
        var full = Path.GetFullPath(Path.Combine(root, subfolder, fileName));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new BackendException("E_BAD_OUTPUT", "The engine reported an output outside its output folder.");
        return full;
    }

    // ---------- instrumental helper ----------

    public async Task<InstrumentalScore> InstrumentalizeAsync(string abc, string style, CancellationToken ct)
    {
        if (!File.Exists(_o.InstrumentalScript))
            throw new BackendException("E_HELPER_MISSING", $"The instrumental helper is missing: {_o.InstrumentalScript}");
        var psi = new ProcessStartInfo(_o.PythonExe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, StandardInputEncoding = new UTF8Encoding(false),
        };
        psi.ArgumentList.Add("-s");
        psi.ArgumentList.Add("-I");
        psi.ArgumentList.Add(_o.InstrumentalScript);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi) ?? throw new BackendException("E_HELPER", "Could not start the instrumental helper.");
        _job.Assign(p);
        await p.StandardInput.WriteAsync(new JsonObject { ["abc"] = abc, ["style"] = style }.ToJsonString());
        p.StandardInput.Close();
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(60), ct); }
        catch (Exception) { try { p.Kill(true); } catch { } throw; }
        var stdout = await outTask;
        var err = await errTask;
        JsonNode? j;
        try { j = JsonNode.Parse(stdout); }
        catch (JsonException) { throw new BackendException("E_HELPER", "The instrumental helper returned invalid output. " + err.Trim()); }
        if (j?["ok"]?.GetValue<bool>() != true)
            throw new BackendException("E_HELPER", "Could not convert the score to an instrumental: " + (j?["error"]?.ToString() ?? err.Trim()));
        return new InstrumentalScore(j["abc"]!.GetValue<string>(), j["style"]!.GetValue<string>(), j["lyrics"]!.GetValue<string>(),
            j["mode"]?.GetValue<string>() == "melody" ? ScoreMode.Melody : ScoreMode.Full, j["check"]?.ToJsonString() ?? "{}");
    }

    public async ValueTask DisposeAsync()
    {
        await StopCoreAsync();
        _job.Dispose();
        _http.Dispose();
    }
}
