using MusicDraft.Core.Catalog;

namespace MusicDraft.Tests;

public class CatalogDbTests
{
    private readonly string _dir = TestAudio.TempDir();
    private string DbPath => Path.Combine(_dir, "catalog.db");

    private string File(string name) { var p = Path.Combine(_dir, name); System.IO.File.WriteAllText(p, "x"); return p; }

    [Fact]
    public void Persists_across_reopen()
    {
        long playlistId;
        using (var db = new CatalogDb(DbPath))
        {
            var a = db.AddOrGetTrack(File("a.wav"), "A", 10, TrackOrigin.Imported);
            var b = db.AddOrGetTrack(File("b.wav"), "B", 20, TrackOrigin.Generated, "job-1");
            playlistId = db.CreatePlaylist("Mix").Id;
            db.AddToPlaylist(playlistId, [a.Id, b.Id, a.Id]);
            db.SaveQueue([b.Id, a.Id]);
            db.SetDouble("volume", 0.42);
        }
        using (var db = new CatalogDb(DbPath))
        {
            Assert.Equal(1, db.SchemaVersion);
            Assert.Equal(2, db.GetTracks().Count);
            Assert.Equal(3, db.GetPlaylistTrackIds(playlistId).Count);
            Assert.Equal(2, db.GetQueue().Count);
            Assert.Equal(0.42, db.GetDouble("volume", 1));
            var gen = db.GetTracks().Single(t => t.Origin == TrackOrigin.Generated);
            Assert.Equal("job-1", gen.JobId);
        }
    }

    [Fact]
    public void Adding_same_path_is_idempotent_case_insensitive()
    {
        using var db = new CatalogDb(DbPath);
        var p = File("Song.wav");
        var a = db.AddOrGetTrack(p, "A", 1, TrackOrigin.Imported);
        var b = db.AddOrGetTrack(p.ToUpperInvariant(), "B", 1, TrackOrigin.Imported);
        Assert.Equal(a.Id, b.Id);
    }

    [Fact]
    public void Reorder_and_remove_keep_positions_contiguous()
    {
        using var db = new CatalogDb(DbPath);
        var ids = Enumerable.Range(0, 4).Select(i => db.AddOrGetTrack(File($"{i}.wav"), $"{i}", 1, TrackOrigin.Imported).Id).ToList();
        var pl = db.CreatePlaylist("P").Id;
        db.AddToPlaylist(pl, ids);
        db.SetPlaylistOrder(pl, [ids[3], ids[1], ids[0], ids[2]]);
        db.RemoveFromPlaylistAt(pl, 1);
        Assert.Equal([ids[3], ids[0], ids[2]], db.GetPlaylistTrackIds(pl));
        db.AddToPlaylist(pl, [ids[1]]);
        Assert.Equal(ids[1], db.GetPlaylistTrackIds(pl)[^1]);
    }

    [Fact]
    public void Removing_track_drops_reference_but_keeps_file()
    {
        using var db = new CatalogDb(DbPath);
        var path = File("keep.wav");
        var t = db.AddOrGetTrack(path, "K", 1, TrackOrigin.Imported);
        var other = db.AddOrGetTrack(File("other.wav"), "O", 1, TrackOrigin.Imported);
        var pl = db.CreatePlaylist("P").Id;
        db.AddToPlaylist(pl, [t.Id, other.Id, t.Id]);
        db.SaveQueue([t.Id, other.Id]);
        db.RemoveTrack(t.Id);
        Assert.True(System.IO.File.Exists(path));
        Assert.Equal([other.Id], db.GetPlaylistTrackIds(pl));
        Assert.Equal([other.Id], db.GetQueue());
    }

    [Fact]
    public void Deleting_playlist_cascades_items()
    {
        using var db = new CatalogDb(DbPath);
        var t = db.AddOrGetTrack(File("a.wav"), "A", 1, TrackOrigin.Imported);
        var pl = db.CreatePlaylist("P").Id;
        db.AddToPlaylist(pl, [t.Id]);
        db.DeletePlaylist(pl);
        Assert.Empty(db.GetPlaylists());
        Assert.Empty(db.GetPlaylistTrackIds(pl));
        Assert.Single(db.GetTracks());
    }

    [Fact]
    public void Missing_and_changed_files_are_detected()
    {
        using var db = new CatalogDb(DbPath);
        var p = File("m.wav");
        var t = db.AddOrGetTrack(p, "M", 1, TrackOrigin.Imported);
        Assert.True(t.Exists);
        Assert.False(t.ChangedExternally);
        System.IO.File.WriteAllText(p, "changed content");
        Assert.True(db.GetTrack(t.Id)!.ChangedExternally);
        System.IO.File.Delete(p);
        Assert.False(db.GetTrack(t.Id)!.Exists);
    }

    [Fact]
    public async Task Concurrent_writers_do_not_corrupt()
    {
        using var db = new CatalogDb(DbPath);
        var paths = Enumerable.Range(0, 40).Select(i => File($"c{i}.wav")).ToList();
        var pl = db.CreatePlaylist("P").Id;
        await Task.WhenAll(paths.Select(p => Task.Run(() =>
        {
            var t = db.AddOrGetTrack(p, Path.GetFileName(p), 1, TrackOrigin.Imported);
            db.AddToPlaylist(pl, [t.Id]);
            db.SetDouble("volume", Random.Shared.NextDouble());
            _ = db.GetTracks();
        })));
        Assert.Equal(40, db.GetTracks().Count);
        Assert.Equal(40, db.GetPlaylistTrackIds(pl).Distinct().Count());
    }

    [Fact]
    public void Jobs_roundtrip()
    {
        using var db = new CatalogDb(DbPath);
        var now = DateTimeOffset.UtcNow;
        db.InsertJob(new JobRecord("j1", JobKind.Generate, JobStatus.Queued, now, now, "{}", null, null, null, null, null));
        db.UpdateJob("j1", JobStatus.Failed, "decode", "E_VRAM", "Out of memory");
        var j = db.GetJob("j1")!;
        Assert.Equal(JobStatus.Failed, j.Status);
        Assert.Equal("decode", j.Stage);
        Assert.Equal("E_VRAM", j.ErrorCode);
    }
}
