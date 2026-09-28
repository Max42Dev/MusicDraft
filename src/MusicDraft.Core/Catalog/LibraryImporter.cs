using MusicDraft.Core.Audio;

namespace MusicDraft.Core.Catalog;

public sealed record ImportResult(IReadOnlyList<Track> Added, IReadOnlyList<(string Path, string Error)> Failed, int SkippedUnsupported);

/// <summary>Adds local audio files/folders to the catalog. Files are referenced in place, never copied or modified.</summary>
public static class LibraryImporter
{
    public static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }); }
                catch (Exception) { continue; }
                foreach (var f in files.Order(StringComparer.OrdinalIgnoreCase)) yield return f;
            }
            else yield return p;
        }
    }

    /// <summary>Runs on the calling thread; call from a background task. Safe to cancel between files.</summary>
    public static ImportResult Import(CatalogDb db, IEnumerable<string> paths, IProgress<(int Done, string Current)>? progress = null,
        CancellationToken ct = default)
    {
        var added = new List<Track>();
        var failed = new List<(string, string)>();
        var skipped = 0;
        var done = 0;
        foreach (var path in Expand(paths))
        {
            ct.ThrowIfCancellationRequested();
            if (!AudioDecoder.IsSupported(path)) { skipped++; continue; }
            progress?.Report((done, path));
            try
            {
                if (db.GetTrackByPath(path) is { } existing) { added.Add(existing); continue; }
                var probe = AudioDecoder.Probe(path, ct);
                added.Add(db.AddOrGetTrack(path, Path.GetFileNameWithoutExtension(path), probe.DurationSeconds, TrackOrigin.Imported));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { failed.Add((path, e.Message)); }
            finally { done++; }
        }
        return new ImportResult(added, failed, skipped);
    }
}
