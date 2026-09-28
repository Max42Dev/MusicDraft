using System.Text.Json.Nodes;

namespace MusicDraft.Core.Generation;

public enum PromptOutcome { Success, Interrupted, Error }

/// <summary>A progress event from the backend: which graph node is running and, if it reports it, value/max.</summary>
public readonly record struct NodeProgress(string Node, int Value, int Max);

public sealed record PromptResult(
    PromptOutcome Outcome,
    JsonObject Outputs,
    IReadOnlyList<string> Log,
    string? ErrorType = null,
    string? ErrorMessage = null,
    string? ErrorNode = null);

public sealed class BackendException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}

public sealed record BackendStatus(bool Running, string? Version, double? VramFreeGb, double? VramTotalGb, string? Device);

/// <summary>
/// The inference boundary. GPU work is serialised by the caller; one prompt runs at a time.
/// Implemented by <see cref="ComfyWorker"/> and by a stub in tests.
/// </summary>
public interface IMusicBackend : IAsyncDisposable
{
    /// <summary>Starts (or reuses) the worker process for the given profile and waits until it accepts requests.</summary>
    Task EnsureStartedAsync(Models.ModelProfile profile, IProgress<string>? status, CancellationToken ct);

    Task<BackendStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>Runs an API-format graph to completion. Cancellation interrupts the running prompt.</summary>
    Task<PromptResult> RunAsync(JsonObject graph, Action<NodeProgress>? progress, CancellationToken ct);

    /// <summary>Absolute path of a file the backend saved (from a SaveAudio output entry).</summary>
    string ResolveOutput(string subfolder, string fileName);

    /// <summary>The engine's local input folder; files placed here can be read by <c>LoadAudio</c> by file name.</summary>
    string InputDir { get; }

    /// <summary>Runs the pinned instrumental helper (CPU, stdlib-only Python) on a planned score.</summary>
    Task<InstrumentalScore> InstrumentalizeAsync(string abc, string style, CancellationToken ct);

    Task StopAsync();
}

public sealed record InstrumentalScore(string Abc, string Style, string Lyrics, ScoreMode Mode, string CheckJson);
