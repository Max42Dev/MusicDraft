namespace MusicDraft.Core.Generation;

/// <summary>Locates the Python helper scripts shipped next to the app (copied from <c>worker/</c> at build time).</summary>
public static class WorkerScripts
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "worker");
    public static string InstrumentalScript => Path.Combine(Root, "yue2_instrumental", "md_instrumental.py");
}
