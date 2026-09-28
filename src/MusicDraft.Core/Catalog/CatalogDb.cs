using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MusicDraft.Core.Catalog;

/// <summary>
/// SQLite catalog of tracks, playlists, queue, settings and jobs. Serialised through a lock so it is safe to call
/// from the UI thread and from background job/download threads.
/// </summary>
public sealed class CatalogDb : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    private static readonly string[] Migrations =
    [
        // v1
        """
        CREATE TABLE tracks(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            path TEXT NOT NULL UNIQUE COLLATE NOCASE,
            title TEXT NOT NULL,
            duration REAL NOT NULL DEFAULT 0,
            origin INTEGER NOT NULL DEFAULT 0,
            job_id TEXT NULL,
            added_at TEXT NOT NULL,
            file_size INTEGER NOT NULL DEFAULT 0,
            file_modified TEXT NULL);
        CREATE TABLE playlists(
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            name TEXT NOT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE playlist_items(
            playlist_id INTEGER NOT NULL REFERENCES playlists(id) ON DELETE CASCADE,
            position INTEGER NOT NULL,
            track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE,
            PRIMARY KEY(playlist_id, position));
        CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);
        CREATE TABLE jobs(
            id TEXT PRIMARY KEY,
            kind INTEGER NOT NULL,
            status INTEGER NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            request_json TEXT NOT NULL,
            stage TEXT NULL,
            error_code TEXT NULL,
            error_message TEXT NULL,
            output_track_id INTEGER NULL,
            result_json TEXT NULL);
        CREATE TABLE queue_items(position INTEGER PRIMARY KEY, track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE);
        """,
    ];

    public CatalogDb(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        Migrate();
    }

    public int SchemaVersion => Convert.ToInt32(Scalar("PRAGMA user_version"));

    private void Migrate()
    {
        lock (_gate)
        {
            var version = SchemaVersion;
            for (var i = version; i < Migrations.Length; i++)
            {
                using var tx = _conn.BeginTransaction();
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = Migrations[i] + $"\nPRAGMA user_version = {i + 1};";
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }

    // ---------- tracks ----------

    public Track AddOrGetTrack(string path, string title, double duration, TrackOrigin origin, string? jobId = null)
    {
        path = Path.GetFullPath(path);
        lock (_gate)
        {
            var existing = GetTrackByPath(path);
            if (existing != null) return existing;
            var fi = new FileInfo(path);
            Exec("""
                 INSERT INTO tracks(path,title,duration,origin,job_id,added_at,file_size,file_modified)
                 VALUES($p,$t,$d,$o,$j,$a,$s,$m)
                 """,
                ("$p", path), ("$t", title), ("$d", duration), ("$o", (int)origin), ("$j", jobId),
                ("$a", Now()), ("$s", fi.Exists ? fi.Length : 0), ("$m", fi.Exists ? Iso(fi.LastWriteTimeUtc) : null));
            return GetTrackByPath(path)!;
        }
    }

    public Track? GetTrack(long id) => QueryTracks("WHERE id=$id", ("$id", id)).FirstOrDefault();
    public Track? GetTrackByPath(string path) => QueryTracks("WHERE path=$p", ("$p", Path.GetFullPath(path))).FirstOrDefault();
    public IReadOnlyList<Track> GetTracks() => QueryTracks("ORDER BY added_at DESC, id DESC");

    public void UpdateTrackFileInfo(long id, double duration)
    {
        var t = GetTrack(id);
        if (t == null) return;
        var fi = new FileInfo(t.Path);
        Exec("UPDATE tracks SET duration=$d, file_size=$s, file_modified=$m WHERE id=$id",
            ("$d", duration), ("$s", fi.Exists ? fi.Length : 0), ("$m", fi.Exists ? Iso(fi.LastWriteTimeUtc) : null), ("$id", id));
    }

    public void RenameTrack(long id, string title) => Exec("UPDATE tracks SET title=$t WHERE id=$id", ("$t", title), ("$id", id));

    /// <summary>Removes the library reference only. The audio file is left untouched.</summary>
    public void RemoveTrack(long id)
    {
        lock (_gate)
        {
            var affected = QueryList("SELECT DISTINCT playlist_id FROM playlist_items WHERE track_id=$id", r => r.GetInt64(0), ("$id", id));
            Exec("DELETE FROM tracks WHERE id=$id", ("$id", id));
            foreach (var p in affected) Renumber(p);
            RenumberQueue();
        }
    }

    private List<Track> QueryTracks(string where, params (string, object?)[] args) =>
        QueryList($"SELECT id,path,title,duration,origin,job_id,added_at,file_size,file_modified FROM tracks {where}", r => new Track(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3), (TrackOrigin)r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetString(5), ParseTime(r.GetString(6)), r.GetInt64(7),
            r.IsDBNull(8) ? null : ParseTime(r.GetString(8))), args);

    // ---------- playlists ----------

    public Playlist CreatePlaylist(string name)
    {
        lock (_gate)
        {
            var order = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(sort_order)+1,0) FROM playlists"));
            Exec("INSERT INTO playlists(name,sort_order) VALUES($n,$o)", ("$n", name), ("$o", order));
            var id = Convert.ToInt64(Scalar("SELECT last_insert_rowid()"));
            return new Playlist(id, name, order);
        }
    }

    public IReadOnlyList<Playlist> GetPlaylists() =>
        QueryList("SELECT id,name,sort_order FROM playlists ORDER BY sort_order, id", r => new Playlist(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));

    public void RenamePlaylist(long id, string name) => Exec("UPDATE playlists SET name=$n WHERE id=$id", ("$n", name), ("$id", id));
    public void DeletePlaylist(long id) => Exec("DELETE FROM playlists WHERE id=$id", ("$id", id));

    public IReadOnlyList<long> GetPlaylistTrackIds(long playlistId) =>
        QueryList("SELECT track_id FROM playlist_items WHERE playlist_id=$p ORDER BY position", r => r.GetInt64(0), ("$p", playlistId));

    public void AddToPlaylist(long playlistId, IEnumerable<long> trackIds)
    {
        lock (_gate)
        {
            var pos = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(position)+1,0) FROM playlist_items WHERE playlist_id=$p", ("$p", playlistId)));
            foreach (var t in trackIds)
                Exec("INSERT INTO playlist_items(playlist_id,position,track_id) VALUES($p,$pos,$t)", ("$p", playlistId), ("$pos", pos++), ("$t", t));
        }
    }

    public void SetPlaylistOrder(long playlistId, IReadOnlyList<long> trackIds)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            ExecTx(tx, "DELETE FROM playlist_items WHERE playlist_id=$p", ("$p", playlistId));
            for (var i = 0; i < trackIds.Count; i++)
                ExecTx(tx, "INSERT INTO playlist_items(playlist_id,position,track_id) VALUES($p,$pos,$t)", ("$p", playlistId), ("$pos", i), ("$t", trackIds[i]));
            tx.Commit();
        }
    }

    public void RemoveFromPlaylistAt(long playlistId, int position)
    {
        lock (_gate)
        {
            Exec("DELETE FROM playlist_items WHERE playlist_id=$p AND position=$pos", ("$p", playlistId), ("$pos", position));
            Renumber(playlistId);
        }
    }

    private void Renumber(long playlistId) => SetPlaylistOrder(playlistId, GetPlaylistTrackIds(playlistId));

    // ---------- queue ----------

    public IReadOnlyList<long> GetQueue() => QueryList("SELECT track_id FROM queue_items ORDER BY position", r => r.GetInt64(0));

    public void SaveQueue(IReadOnlyList<long> trackIds)
    {
        lock (_gate)
        {
            using var tx = _conn.BeginTransaction();
            ExecTx(tx, "DELETE FROM queue_items");
            for (var i = 0; i < trackIds.Count; i++)
                ExecTx(tx, "INSERT INTO queue_items(position,track_id) VALUES($i,$t)", ("$i", i), ("$t", trackIds[i]));
            tx.Commit();
        }
    }

    private void RenumberQueue() => SaveQueue(GetQueue());

    // ---------- settings ----------

    public string? GetSetting(string key) => Scalar("SELECT value FROM settings WHERE key=$k", ("$k", key)) as string;

    public void SetSetting(string key, string? value)
    {
        if (value == null) Exec("DELETE FROM settings WHERE key=$k", ("$k", key));
        else Exec("INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$k", key), ("$v", value));
    }

    public double GetDouble(string key, double fallback) =>
        double.TryParse(GetSetting(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public void SetDouble(string key, double value) => SetSetting(key, value.ToString("R", CultureInfo.InvariantCulture));

    // ---------- jobs ----------

    public void InsertJob(JobRecord j) => Exec("""
        INSERT INTO jobs(id,kind,status,created_at,updated_at,request_json,stage,error_code,error_message,output_track_id,result_json)
        VALUES($id,$k,$s,$c,$u,$r,$st,$ec,$em,$o,$res)
        """, ("$id", j.Id), ("$k", (int)j.Kind), ("$s", (int)j.Status), ("$c", Iso(j.CreatedAt)), ("$u", Iso(j.UpdatedAt)),
        ("$r", j.RequestJson), ("$st", j.Stage), ("$ec", j.ErrorCode), ("$em", j.ErrorMessage), ("$o", j.OutputTrackId), ("$res", j.ResultJson));

    public void UpdateJob(string id, JobStatus status, string? stage = null, string? errorCode = null, string? errorMessage = null,
        long? outputTrackId = null, string? resultJson = null) =>
        Exec("""
             UPDATE jobs SET status=$s, updated_at=$u, stage=COALESCE($st,stage), error_code=$ec, error_message=$em,
               output_track_id=COALESCE($o,output_track_id), result_json=COALESCE($res,result_json) WHERE id=$id
             """, ("$s", (int)status), ("$u", Now()), ("$st", stage), ("$ec", errorCode), ("$em", errorMessage),
            ("$o", outputTrackId), ("$res", resultJson), ("$id", id));

    public JobRecord? GetJob(string id) => QueryJobs("WHERE id=$id", ("$id", id)).FirstOrDefault();
    public IReadOnlyList<JobRecord> GetJobs() => QueryJobs("ORDER BY created_at DESC");

    private List<JobRecord> QueryJobs(string where, params (string, object?)[] args) =>
        QueryList($"SELECT id,kind,status,created_at,updated_at,request_json,stage,error_code,error_message,output_track_id,result_json FROM jobs {where}",
            r => new JobRecord(r.GetString(0), (JobKind)r.GetInt32(1), (JobStatus)r.GetInt32(2), ParseTime(r.GetString(3)), ParseTime(r.GetString(4)),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetInt64(9), r.IsDBNull(10) ? null : r.GetString(10)), args);

    // ---------- helpers ----------

    private static string Now() => Iso(DateTimeOffset.UtcNow);
    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static string Iso(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseTime(string s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private void Exec(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, args);
            cmd.ExecuteNonQuery();
        }
    }

    private void ExecTx(SqliteTransaction tx, string sql, params (string, object?)[] args)
    {
        using var cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        Bind(cmd, args);
        cmd.ExecuteNonQuery();
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, args);
            var v = cmd.ExecuteScalar();
            return v is DBNull ? null : v;
        }
    }

    private List<T> QueryList<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            Bind(cmd, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    private static void Bind(SqliteCommand cmd, (string, object?)[] args)
    {
        foreach (var (k, v) in args) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
    }

    public void Dispose()
    {
        lock (_gate) _conn.Dispose();
    }
}
