using MusicDraft.Core.Audio;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Playback;

namespace MusicDraft.Tests;

internal sealed class FakePlayer : IAudioPlayer
{
    public event Action? TrackEnded;
    public event Action<string>? PlaybackFailed;
    public string? OpenPath;
    public List<string> Opened = [];
    public HashSet<string> FailOpen = new(StringComparer.OrdinalIgnoreCase);

    public void Open(string path)
    {
        if (FailOpen.Contains(path)) throw new AudioDecodeException("bad file");
        OpenPath = path; Opened.Add(path); Position = TimeSpan.Zero; IsPlaying = false;
    }
    public void Play() { if (OpenPath != null) IsPlaying = true; }
    public void Pause() => IsPlaying = false;
    public void Stop() { IsPlaying = false; Position = TimeSpan.Zero; }
    public void Close() { Stop(); OpenPath = null; }
    public bool IsPlaying { get; private set; }
    public bool IsOpen => OpenPath != null;
    public TimeSpan Position { get; set; }
    public TimeSpan Duration => TimeSpan.FromSeconds(60);
    public float Volume { get; set; }
    public bool Muted { get; set; }
    public void End() { IsPlaying = false; TrackEnded?.Invoke(); }
    public void Fail(string m) { IsPlaying = false; PlaybackFailed?.Invoke(m); }
    public void Dispose() { }
}

public class PlaybackControllerTests : IDisposable
{
    private readonly string _dir = TestAudio.TempDir();
    private readonly CatalogDb _db;
    private readonly FakePlayer _p = new();
    private readonly List<string> _errors = [];

    public PlaybackControllerTests() => _db = new CatalogDb(Path.Combine(_dir, "c.db"));

    private PlaybackController Make()
    {
        var c = new PlaybackController(_db, _p, a => a(), new Random(1));
        c.Error += _errors.Add;
        return c;
    }

    private long Track(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "x");
        return _db.AddOrGetTrack(path, name, 60, TrackOrigin.Imported).Id;
    }

    [Fact]
    public void Plays_through_and_stops_at_end()
    {
        var ids = new[] { Track("a"), Track("b") };
        var c = Make();
        c.PlayList(ids);
        Assert.True(c.IsPlaying);
        _p.End();
        Assert.Equal(ids[1], c.CurrentTrack!.Id);
        _p.End();
        Assert.False(c.IsPlaying);
        Assert.Null(c.CurrentTrack);
    }

    [Fact]
    public void Missing_file_is_skipped_with_error()
    {
        var a = Track("a"); var b = Track("b"); var d = Track("d");
        File.Delete(Path.Combine(_dir, "b"));
        var c = Make();
        c.PlayList([a, b, d]);
        _p.End();
        Assert.Equal(d, c.CurrentTrack!.Id);
        c.PlayList([b, d]);
        Assert.Equal(d, c.CurrentTrack!.Id);
        Assert.Contains(_errors, e => e.Contains("missing"));
    }

    [Fact]
    public void Undecodable_file_is_skipped()
    {
        var a = Track("a"); var b = Track("b");
        _p.FailOpen.Add(Path.Combine(_dir, "a"));
        var c = Make();
        c.PlayList([a, b]);
        Assert.Equal(b, c.CurrentTrack!.Id);
        Assert.True(c.IsPlaying);
        Assert.Contains(_errors, e => e.Contains("Could not play"));
    }

    [Fact]
    public void All_missing_stops_without_hanging()
    {
        var a = Track("a"); var b = Track("b");
        File.Delete(Path.Combine(_dir, "a")); File.Delete(Path.Combine(_dir, "b"));
        var c = Make();
        c.Repeat = RepeatMode.All;
        c.PlayList([a, b]);
        Assert.False(c.IsPlaying);
    }

    [Fact]
    public void Seek_to_end_advances_like_natural_end()
    {
        var ids = new[] { Track("a"), Track("b") };
        var c = Make();
        c.PlayList(ids);
        c.Seek(TimeSpan.FromSeconds(60));
        Assert.Equal(ids[1], c.CurrentTrack!.Id);
        Assert.True(c.IsPlaying);
    }

    [Fact]
    public void Stale_end_event_after_skip_is_ignored()
    {
        var ids = new[] { Track("a"), Track("b"), Track("c") };
        Action? pending = null;
        var c = new PlaybackController(_db, _p, a => pending = a);
        c.PlayList(ids);
        _p.End();          // end event for "a" queued
        c.Next();          // user skips to "b" before it's delivered
        pending!();        // stale event must not skip "b"
        Assert.Equal(ids[1], c.CurrentTrack!.Id);
    }

    [Fact]
    public void Previous_restarts_after_threshold_otherwise_goes_back()
    {
        var ids = new[] { Track("a"), Track("b") };
        var c = Make();
        c.PlayList(ids, 0);
        c.Next();
        _p.Position = TimeSpan.FromSeconds(10);
        c.Previous();
        Assert.Equal(ids[1], c.CurrentTrack!.Id);
        Assert.Equal(TimeSpan.Zero, _p.Position);
        c.Previous();
        Assert.Equal(ids[0], c.CurrentTrack!.Id);
    }

    [Fact]
    public void Repeat_one_replays_same_track()
    {
        var ids = new[] { Track("a"), Track("b") };
        var c = Make();
        c.Repeat = RepeatMode.One;
        c.PlayList(ids);
        _p.End();
        Assert.Equal(ids[0], c.CurrentTrack!.Id);
        Assert.True(c.IsPlaying);
    }

    [Fact]
    public void Settings_and_queue_persist()
    {
        var ids = new[] { Track("a"), Track("b"), Track("c") };
        var c = Make();
        c.Volume = 0.33f; c.Muted = true; c.Shuffle = true; c.Repeat = RepeatMode.All;
        c.PlayList(ids, 1);
        c.Dispose();

        var p2 = new FakePlayer();
        var c2 = new PlaybackController(_db, p2, a => a());
        Assert.Equal(0.33f, p2.Volume, 3);
        Assert.True(p2.Muted);
        Assert.True(c2.Shuffle);
        Assert.Equal(RepeatMode.All, c2.Repeat);
        Assert.Equal(ids, c2.Queue.Items);
        Assert.Equal(ids[1], c2.CurrentTrack!.Id);
        Assert.False(c2.IsPlaying); // restored paused, never auto-plays
    }

    [Fact]
    public void Removing_current_while_playing_moves_on()
    {
        var ids = new[] { Track("a"), Track("b") };
        var c = Make();
        c.PlayList(ids);
        c.RemoveFromQueue(0);
        Assert.Equal(ids[1], c.CurrentTrack!.Id);
        Assert.True(c.IsPlaying);
    }

    [Fact]
    public void Library_removal_syncs_queue()
    {
        var ids = new[] { Track("a"), Track("b") };
        var c = Make();
        c.PlayList(ids);
        _db.RemoveTrack(ids[0]);
        c.SyncWithCatalog();
        Assert.Equal([ids[1]], c.Queue.Items);
    }

    [Fact]
    public void Device_failure_reports_error()
    {
        var c = Make();
        c.PlayList([Track("a")]);
        _p.Fail("device removed");
        Assert.Contains(_errors, e => e.Contains("device removed"));
    }

    [Fact]
    public void Toggle_on_empty_queue_is_noop()
    {
        var c = Make();
        c.TogglePlayPause();
        c.Next(); c.Previous(); c.Seek(TimeSpan.FromSeconds(5));
        Assert.False(c.IsPlaying);
    }

    public void Dispose() => _db.Dispose();
}
