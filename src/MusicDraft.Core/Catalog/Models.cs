namespace MusicDraft.Core.Catalog;

public enum TrackOrigin { Imported = 0, Generated = 1 }

public sealed record Track(
    long Id,
    string Path,
    string Title,
    double DurationSeconds,
    TrackOrigin Origin,
    string? JobId,
    DateTimeOffset AddedAt,
    long FileSize,
    DateTimeOffset? FileModified)
{
    public bool Exists => File.Exists(Path);

    /// <summary>True when the file on disk no longer matches what was catalogued (size or timestamp changed).</summary>
    public bool ChangedExternally
    {
        get
        {
            try
            {
                var fi = new FileInfo(Path);
                if (!fi.Exists) return false;
                return fi.Length != FileSize ||
                       (FileModified is { } m && Math.Abs((fi.LastWriteTimeUtc - m.UtcDateTime).TotalSeconds) > 2);
            }
            catch (IOException) { return false; }
        }
    }
}

public sealed record Playlist(long Id, string Name, int SortOrder);

public enum JobKind { Generate = 0, Cover = 1, Transcribe = 2 }

public enum JobStatus { Queued = 0, Running = 1, Succeeded = 2, Failed = 3, Cancelled = 4, Interrupted = 5 }

public sealed record JobRecord(
    string Id,
    JobKind Kind,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string RequestJson,
    string? Stage,
    string? ErrorCode,
    string? ErrorMessage,
    long? OutputTrackId,
    string? ResultJson);
