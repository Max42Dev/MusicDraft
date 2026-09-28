using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicDraft.Core.Covers;
using MusicDraft.Core.Playback;

namespace MusicDraft.App.ViewModels;

/// <summary>Bottom transport bar and queue panel. Position is read from the real audio stream on a UI timer.</summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _s;
    private readonly PlaybackController _pc;
    private readonly DispatcherTimer _timer;
    private bool _updatingPosition;

    public PlayerViewModel(AppServices s)
    {
        _s = s;
        _pc = s.Playback;
        _pc.StateChanged += Sync;
        _s.CatalogChanged += RebuildQueue;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
        _volume = _pc.Volume * 100;
        Sync();
    }

    public ObservableCollection<TrackEntry> QueueEntries { get; } = [];

    [ObservableProperty] private string _title = "Nothing playing";
    [ObservableProperty] private string _subtitle = "Add music to your library to start";
    [ObservableProperty] private bool _hasTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds = 1;
    [ObservableProperty] private string _positionText = "0:00";
    [ObservableProperty] private string _durationText = "0:00";
    [ObservableProperty] private double _volume;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private RepeatMode _repeat;
    [ObservableProperty] private bool _isGenerated;
    [ObservableProperty] private string _queueSummary = "Queue empty";
    [ObservableProperty] private TrackEntry? _selectedQueueEntry;

    public long? CurrentTrackId { get; private set; }
    public event Action<long?>? CurrentTrackChanged;

    public string PlayPauseGlyph => IsPlaying ? "\uE769" : "\uE768";
    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";
    public string VolumeGlyph => Muted || Volume <= 0 ? "\uE74F" : Volume < 34 ? "\uE993" : Volume < 67 ? "\uE994" : "\uE995";
    public string MuteLabel => Muted ? "Unmute" : "Mute";
    public string RepeatGlyph => Repeat == RepeatMode.One ? "\uE8ED" : "\uE8EE";
    public bool RepeatActive => Repeat != RepeatMode.Off;
    public string RepeatLabel => Repeat switch { RepeatMode.All => "Repeat: all", RepeatMode.One => "Repeat: one", _ => "Repeat: off" };
    public string ShuffleLabel => Shuffle ? "Shuffle: on" : "Shuffle: off";

    partial void OnIsPlayingChanged(bool value) { OnPropertyChanged(nameof(PlayPauseGlyph)); OnPropertyChanged(nameof(PlayPauseLabel)); }
    partial void OnMutedChanged(bool value) { OnPropertyChanged(nameof(VolumeGlyph)); OnPropertyChanged(nameof(MuteLabel)); }
    partial void OnShuffleChanged(bool value) => OnPropertyChanged(nameof(ShuffleLabel));
    partial void OnRepeatChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(RepeatGlyph));
        OnPropertyChanged(nameof(RepeatActive));
        OnPropertyChanged(nameof(RepeatLabel));
    }

    partial void OnVolumeChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeGlyph));
        var v = (float)(value / 100.0);
        if (Math.Abs(_pc.Volume - v) > 0.001f) _pc.Volume = v;
    }

    partial void OnPositionSecondsChanged(double value)
    {
        PositionText = ExcerptRules.Format(value);
        if (!_updatingPosition) _pc.Seek(TimeSpan.FromSeconds(value));
    }

    private void Tick()
    {
        if (!HasTrack) return;
        _updatingPosition = true;
        var d = _pc.Duration.TotalSeconds;
        if (d > 0 && Math.Abs(d - DurationSeconds) > 0.01) { DurationSeconds = d; DurationText = ExcerptRules.Format(d); }
        PositionSeconds = Math.Min(_pc.Position.TotalSeconds, DurationSeconds);
        _updatingPosition = false;
        if (IsPlaying != _pc.IsPlaying) IsPlaying = _pc.IsPlaying;
    }

    private void Sync()
    {
        var t = _pc.CurrentTrack;
        HasTrack = t != null;
        Title = t?.Title ?? "Nothing playing";
        Subtitle = t == null ? (_pc.Queue.Items.Count == 0 ? "Add music to your library to start" : "Press play to start the queue")
                             : Path.GetFileName(t.Path);
        IsGenerated = t?.Origin == Core.Catalog.TrackOrigin.Generated;
        IsPlaying = _pc.IsPlaying;
        Muted = _pc.Muted;
        Shuffle = _pc.Shuffle;
        Repeat = _pc.Repeat;
        _updatingPosition = true;
        var d = _pc.Duration.TotalSeconds;
        DurationSeconds = d > 0 ? d : Math.Max(1, t?.DurationSeconds ?? 1);
        DurationText = t == null ? "0:00" : ExcerptRules.Format(DurationSeconds);
        PositionSeconds = _pc.Position.TotalSeconds;
        _updatingPosition = false;

        if (CurrentTrackId != t?.Id)
        {
            CurrentTrackId = t?.Id;
            CurrentTrackChanged?.Invoke(CurrentTrackId);
        }
        RebuildQueue();
    }

    private void RebuildQueue()
    {
        var ids = _pc.Queue.Items;
        var same = QueueEntries.Count == ids.Count && QueueEntries.Select(e => e.Item.Id).SequenceEqual(ids);
        if (!same)
        {
            var sel = SelectedQueueEntry?.Position;
            QueueEntries.Clear();
            for (var i = 0; i < ids.Count; i++)
                if (_s.Db.GetTrack(ids[i]) is { } tr) QueueEntries.Add(new TrackEntry(i, new TrackItem(tr)));
            SelectedQueueEntry = sel is { } p ? QueueEntries.FirstOrDefault(e => e.Position == p) : null;
        }
        foreach (var e in QueueEntries) e.IsCurrent = e.Position == _pc.Queue.CurrentIndex;
        QueueSummary = ids.Count == 0 ? "Queue empty" : $"{(_pc.Queue.CurrentIndex >= 0 ? _pc.Queue.CurrentIndex + 1 : 0)} of {ids.Count}";
    }

    [RelayCommand] private void PlayPause() => _pc.TogglePlayPause();
    [RelayCommand] private void Next() => _pc.Next();
    [RelayCommand] private void Previous() => _pc.Previous();
    [RelayCommand] private void ToggleMute() => _pc.Muted = !_pc.Muted;
    [RelayCommand] private void ToggleShuffle() => _pc.Shuffle = !_pc.Shuffle;
    [RelayCommand] private void CycleRepeat() => _pc.CycleRepeat();
    [RelayCommand] private void SeekBy(string seconds) => _pc.Seek(_pc.Position + TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)));
    [RelayCommand] private void VolumeBy(string delta) => Volume = Math.Clamp(Volume + double.Parse(delta, System.Globalization.CultureInfo.InvariantCulture), 0, 100);

    [RelayCommand]
    private void PlayQueueEntry(TrackEntry? e)
    {
        e ??= SelectedQueueEntry;
        if (e != null) _pc.PlayQueueIndex(e.Position);
    }

    [RelayCommand]
    private void RemoveQueueEntry(TrackEntry? e)
    {
        e ??= SelectedQueueEntry;
        if (e != null) _pc.RemoveFromQueue(e.Position);
    }

    [RelayCommand]
    private void ClearQueue() => _pc.PlayList([]);

    public void MoveQueue(int from, int to) => _pc.Queue.Move(from, to);

    public void Dispose()
    {
        _timer.Stop();
        _pc.StateChanged -= Sync;
        _s.CatalogChanged -= RebuildQueue;
    }
}
