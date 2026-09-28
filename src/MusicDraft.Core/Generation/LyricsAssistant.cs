using LLama;
using LLama.Common;
using LLama.Sampling;

namespace MusicDraft.Core.Generation;

public sealed record LyricsAssistantOptions(string ModelPath);

/// <summary>
/// Runs the small local lyrics model in-process with LLamaSharp (llama.cpp). No server process and no network:
/// the model is loaded on first use and unloaded when idle to free RAM.
/// </summary>
public sealed class LyricsAssistant : IAsyncDisposable
{
    private static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(5);

    private readonly LyricsAssistantOptions _o;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _model;
    private DateTime _lastUse = DateTime.UtcNow;
    private Timer? _idleTimer;

    public LyricsAssistant(LyricsAssistantOptions options) => _o = options;

    /// <summary>True when the model file is on disk.</summary>
    public bool FilesPresent => File.Exists(_o.ModelPath);

    /// <summary>True while the model is loaded in memory.</summary>
    public bool IsLoaded => _model != null;

    /// <summary>
    /// Loads the model if needed and asks it for lyrics. Returns the raw reply text; the caller parses it.
    /// </summary>
    /// <param name="seed">
    /// Sampling seed. Pass a value to make a draft reproducible; pass null for a fresh random seed each call.
    /// </param>
    public async Task<string> GenerateAsync(string systemPrompt, string userPrompt, CancellationToken ct, uint? seed = null)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(_o.ModelPath))
                throw new BackendException("E_LYRICS_MODEL", "The lyrics assistant model is not downloaded.");
            EnsureLoaded();
            _lastUse = DateTime.UtcNow;

            // Gemma 4's chat template uses Jinja features LLamaSharp cannot render, so build the prompt directly
            // with the model's turn tokens (verified against the pinned GGUF).
            var prompt =
                $"<|turn>system\n{systemPrompt}<turn|>\n" +
                $"<|turn>user\n{userPrompt}<turn|>\n" +
                "<|turn>model\n";
            var executor = new StatelessExecutor(_model!, new ModelParams(_o.ModelPath) { ContextSize = 4096, GpuLayerCount = 0 });
            var inference = new InferenceParams
            {
                MaxTokens = 1200,
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = 0.9f,
                    TopP = 0.95f,
                    Seed = seed ?? (uint)Random.Shared.NextInt64(0, uint.MaxValue),
                },
            };
            var sb = new System.Text.StringBuilder();
            await foreach (var token in executor.InferAsync(prompt, inference, ct))
                sb.Append(token);
            var text = sb.ToString().Trim();
            if (text.Length == 0)
                throw new BackendException("E_LYRICS_EMPTY", "The lyrics assistant returned an empty reply. Try again.");
            return text;
        }
        finally
        {
            _lastUse = DateTime.UtcNow;
            _gate.Release();
        }
    }

    private void EnsureLoaded()
    {
        if (_model != null) return;
        var parameters = new ModelParams(_o.ModelPath) { ContextSize = 4096, GpuLayerCount = 0 };
        _model = LLamaWeights.LoadFromFile(parameters);
        _idleTimer ??= new Timer(_ => _ = UnloadIfIdleAsync(), null, IdleShutdown, IdleShutdown);
    }

    private async Task UnloadIfIdleAsync()
    {
        if (_model == null || DateTime.UtcNow - _lastUse < IdleShutdown) return;
        if (!await _gate.WaitAsync(0)) return; // a request is in flight
        try { Unload(); }
        finally { _gate.Release(); }
    }

    private void Unload()
    {
        _model?.Dispose();
        _model = null;
    }

    /// <summary>Unloads the model (frees RAM). Called on shutdown.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { Unload(); }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _idleTimer?.Dispose();
        await StopAsync();
        _gate.Dispose();
    }
}
