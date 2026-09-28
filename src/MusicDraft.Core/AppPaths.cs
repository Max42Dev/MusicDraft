namespace MusicDraft.Core;

/// <summary>Per-user directories. Nothing is ever written next to the installed EXE.</summary>
public sealed class AppPaths
{
    public AppPaths(string root, string? modelsOverride = null, string? outputOverride = null)
    {
        Root = root;
        Models = modelsOverride ?? Path.Combine(root, "models");
        Output = outputOverride ?? Path.Combine(root, "library");
    }

    /// <summary>Default root: %LOCALAPPDATA%\MusicDraft, or %MUSICDRAFT_HOME% if set (tests, portable use).</summary>
    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("MUSICDRAFT_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicDraft");

    public string Root { get; }
    public string Models { get; }
    /// <summary>Where finished generated tracks are written (user-relocatable).</summary>
    public string Output { get; }
    public string Runtime => Path.Combine(Root, "runtime");
    public string Downloads => Path.Combine(Root, "downloads");
    public string Jobs => Path.Combine(Root, "jobs");
    public string WorkerState => Path.Combine(Root, "worker");
    public string Logs => Path.Combine(Root, "logs");
    /// <summary>Microphone recordings used as melody references (local only).</summary>
    public string Recordings => Path.Combine(Root, "recordings");
    public string Database => Path.Combine(Root, "musicdraft.db");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, Models, Output, Runtime, Downloads, Jobs, WorkerState, Logs })
            Directory.CreateDirectory(d);
    }
}
