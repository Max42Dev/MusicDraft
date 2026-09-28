using System.Globalization;
using MusicDraft.Core.Audio;
using MusicDraft.Core.Catalog;

namespace MusicDraft.Core.Playback;

/// <summary>
/// Coordinates the play queue, the audio device and persisted settings. All public members must be called on one
/// thread (the UI thread); audio-device callbacks are marshalled onto it via <c>post</c>.
/// </summary>
public sealed class PlaybackController : IDisposable
{
    public const double RestartThresholdSeconds = 3.0;

    private readonly CatalogDb _db;
    private readonly IAudioPlayer _player;
    private readonly Action<Action> _post;
    private long? _openTrackId;
    private int _endGeneration;

    public PlaybackController(CatalogDb db, IAudioPlayer player, Action<Action> post, Random? rng = null)
    {
        _db = db;
        _player = player;
        _post = post;
        Queue = new PlayQueue(IsPlayable, rng);

        _player.Volume = (float)Math.Clamp(_db.GetDouble("playback.volume", 0.8), 0, 1);
        _player.Muted = _db.GetSetting("playback.muted") == "1";
        Queue.Shuffle = _db.GetSetting("playback.shuffle") == "1";
        Queue.Repeat = Enum.TryParse<RepeatMode>(_db.GetSetting("playback.repeat"), out var r) ? r : RepeatMode.Off;

        var saved = _db.GetQueue();
        var savedIndex = int.TryParse(_db.GetSetting("playback.queueIndex"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : -1;
        Queue.Replace(saved);
        if (savedIndex >= 0 && Queue.JumpTo(savedIndex)) TryOpen(Queue.Current!.Value);

        Queue.Changed += OnQueueChanged;
        _player.TrackEnded += OnDeviceTrackEnded;
        _player.PlaybackFailed += OnDeviceFailed;
    }

    public PlayQueue Queue { get; }

    /// <summary>Raised whenever current track, play state, modes or volume change.</summary>
    public event Action? StateChanged;
    /// <summary>User-visible playback problem (missing file, decode failure, device lost).</summary>
    public event Action<string>? Error;

    public Track? CurrentTrack => Queue.Current is { } id ? _db.GetTrack(id) : null;
    public bool IsPlaying => _player.IsPlaying;
    public TimeSpan Position => _player.IsOpen ? _player.Position : TimeSpan.Zero;
    public TimeSpan Duration => _player.IsOpen ? _player.Duration : TimeSpan.Zero;

    public float Volume
    {
        get => _player.Volume;
        set
        {
            _player.Volume = value;
            _db.SetDouble("playback.volume", _player.Volume);
            StateChanged?.Invoke();
        }
    }

    public bool Muted
    {
        get => _player.Muted;
        set
        {
            _player.Muted = value;
            _db.SetSetting("playback.muted", value ? "1" : "0");
            StateChanged?.Invoke();
        }
    }

    public bool Shuffle
    {
        get => Queue.Shuffle;
        set
        {
            Queue.Shuffle = value;
            _db.SetSetting("playback.shuffle", value ? "1" : "0");
            StateChanged?.Invoke();
        }
    }

    public RepeatMode Repeat
    {
        get => Queue.Repeat;
        set
        {
            Queue.Repeat = value;
            _db.SetSetting("playback.repeat", value.ToString());
            StateChanged?.Invoke();
        }
    }

    public RepeatMode CycleRepeat() =>
        Repeat = Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };

    public bool IsPlayable(long trackId) => _db.GetTrack(trackId) is { } t && t.Exists;

    // ---------- commands ----------

    /// <summary>Replace the queue with <paramref name="trackIds"/> and play from <paramref name="startIndex"/>.</summary>
    public void PlayList(IReadOnlyList<long> trackIds, int startIndex = 0)
    {
        Queue.Replace(trackIds);
        if (trackIds.Count == 0) { StopAndClose(); return; }
        if (Queue.JumpTo(startIndex)) StartCurrent();
        else
        {
            Report(DescribeUnplayable(trackIds[Math.Clamp(startIndex, 0, trackIds.Count - 1)]));
            // Fall forward to the next playable track, if any.
            if (Queue.Next() != null) StartCurrent(); else StopAndClose();
        }
    }

    public void Enqueue(IEnumerable<long> trackIds) => Queue.Append(trackIds);

    public void PlayQueueIndex(int index)
    {
        if (index < 0 || index >= Queue.Items.Count) return;
        if (Queue.JumpTo(index)) StartCurrent();
        else Report(DescribeUnplayable(Queue.Items[index]));
    }

    public void RemoveFromQueue(int index)
    {
        var wasCurrent = index == Queue.CurrentIndex;
        var wasPlaying = _player.IsPlaying;
        Queue.RemoveAt(index);
        if (!wasCurrent) return;
        StopAndClose();
        if (wasPlaying && Queue.Next() != null) StartCurrent();
    }

    public void TogglePlayPause()
    {
        if (_player.IsPlaying) { _player.Pause(); StateChanged?.Invoke(); return; }
        if (Queue.Current is not { } id)
        {
            // Nothing selected: start the first playable queue item.
            if (Queue.Items.Count == 0 || Queue.Next() == null) return;
            StartCurrent();
            return;
        }
        if (_openTrackId != id && !TryOpen(id)) { SkipAfterFailure(); return; }
        _player.Play();
        StateChanged?.Invoke();
    }

    public void Stop()
    {
        _endGeneration++;
        _player.Stop();
        StateChanged?.Invoke();
    }

    public void Next()
    {
        var wasPlaying = _player.IsPlaying || Queue.Current == null;
        if (Queue.Next() is null) return;
        if (wasPlaying) StartCurrent(); else OpenCurrentPaused();
    }

    public void Previous()
    {
        if (_player.IsOpen && _player.Position.TotalSeconds > RestartThresholdSeconds)
        {
            _player.Position = TimeSpan.Zero;
            StateChanged?.Invoke();
            return;
        }
        var wasPlaying = _player.IsPlaying;
        var before = Queue.CurrentIndex;
        Queue.Previous();
        if (Queue.CurrentIndex == before) { _player.Position = TimeSpan.Zero; StateChanged?.Invoke(); return; }
        if (wasPlaying) StartCurrent(); else OpenCurrentPaused();
    }

    public void Seek(TimeSpan position)
    {
        if (!_player.IsOpen) return;
        // Seeking to (or past) the end behaves like the track finishing, deterministically.
        if (_player.Duration > TimeSpan.Zero && position >= _player.Duration - TimeSpan.FromMilliseconds(50))
        {
            HandleTrackEnded();
            return;
        }
        _player.Position = position;
        StateChanged?.Invoke();
    }

    /// <summary>Call after tracks were removed from the library so the queue drops them.</summary>
    public void SyncWithCatalog()
    {
        for (var i = Queue.Items.Count - 1; i >= 0; i--)
            if (_db.GetTrack(Queue.Items[i]) == null) RemoveFromQueue(i);
    }

    // ---------- internals ----------

    private void StartCurrent()
    {
        var guard = Queue.Items.Count + 1;
        while (guard-- > 0 && Queue.Current is { } id)
        {
            if (TryOpen(id))
            {
                _player.Play();
                StateChanged?.Invoke();
                return;
            }
            if (Queue.Next() == null) break;
        }
        StopAndClose();
    }

    private void OpenCurrentPaused()
    {
        if (Queue.Current is { } id && !TryOpen(id)) Report(DescribeUnplayable(id));
        StateChanged?.Invoke();
    }

    private bool TryOpen(long id)
    {
        _endGeneration++;
        var t = _db.GetTrack(id);
        if (t == null || !t.Exists)
        {
            Report(DescribeUnplayable(id));
            _openTrackId = null;
            return false;
        }
        if (t.ChangedExternally) Report($"\"{t.Title}\" was changed outside MusicDraft since it was added.");
        try
        {
            _player.Open(t.Path);
            _openTrackId = id;
            return true;
        }
        catch (Exception e)
        {
            _openTrackId = null;
            Report($"Could not play \"{t.Title}\": {e.Message.Trim()}");
            return false;
        }
    }

    private void SkipAfterFailure()
    {
        if (Queue.Next() != null) StartCurrent(); else StopAndClose();
    }

    private void StopAndClose()
    {
        _endGeneration++;
        _player.Close();
        _openTrackId = null;
        StateChanged?.Invoke();
    }

    private void OnDeviceTrackEnded()
    {
        var gen = _endGeneration;
        _post(() =>
        {
            // Ignore stale end events from a track the user already skipped away from.
            if (gen == _endGeneration) HandleTrackEnded();
        });
    }

    private void HandleTrackEnded()
    {
        var next = Queue.OnTrackEnded();
        if (next == null) { StopAndClose(); return; }
        if (next == _openTrackId && Queue.Repeat == RepeatMode.One)
        {
            _endGeneration++;
            _player.Stop();
            _player.Position = TimeSpan.Zero;
            _player.Play();
            StateChanged?.Invoke();
            return;
        }
        StartCurrent();
    }

    private void OnDeviceFailed(string message) => _post(() =>
    {
        Report($"Playback stopped: {message}");
        _openTrackId = null;
        StateChanged?.Invoke();
    });

    private void OnQueueChanged()
    {
        _db.SaveQueue(Queue.Items);
        _db.SetSetting("playback.queueIndex", Queue.CurrentIndex.ToString(CultureInfo.InvariantCulture));
        StateChanged?.Invoke();
    }

    private string DescribeUnplayable(long id) =>
        _db.GetTrack(id) is { } t ? $"\"{t.Title}\" is missing: {t.Path}" : "That track is no longer in the library.";

    private void Report(string message) => Error?.Invoke(message);

    public void Dispose()
    {
        _player.TrackEnded -= OnDeviceTrackEnded;
        _player.PlaybackFailed -= OnDeviceFailed;
        Queue.Changed -= OnQueueChanged;
    }
}
