using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicDraft.Core.Audio;

public interface IAudioPlayer : IDisposable
{
    /// <summary>Raised (on a background thread) when a track plays to its end naturally.</summary>
    event Action? TrackEnded;
    /// <summary>Raised when playback fails (device lost, decode error mid-stream).</summary>
    event Action<string>? PlaybackFailed;

    void Open(string path);
    void Play();
    void Pause();
    void Stop();
    /// <summary>Stops and releases the file handle.</summary>
    void Close();
    bool IsPlaying { get; }
    bool IsOpen { get; }
    TimeSpan Position { get; set; }
    TimeSpan Duration { get; }
    /// <summary>App volume 0..1, independent of the Windows output device master volume.</summary>
    float Volume { get; set; }
    bool Muted { get; set; }
}

/// <summary>Windows playback via Media Foundation (MP3/WAV/FLAC/AAC decoders built into Windows 10+).</summary>
public sealed class MediaFoundationPlayer : IAudioPlayer
{
    private readonly object _gate = new();
    private WaveOutEvent? _out;
    private MediaFoundationReader? _reader;
    private VolumeSampleProvider? _volume;
    private float _vol = 0.8f;
    private bool _muted;
    private bool _stopRequested;

    public event Action? TrackEnded;
    public event Action<string>? PlaybackFailed;

    public void Open(string path)
    {
        lock (_gate)
        {
            CloseInternal();
            _reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
            _volume = new VolumeSampleProvider(_reader.ToSampleProvider()) { Volume = EffectiveVolume };
            _out = new WaveOutEvent { DesiredLatency = 150 };
            _out.Init(_volume);
            _out.PlaybackStopped += OnStopped;
        }
    }

    private float EffectiveVolume => _muted ? 0f : _vol;

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (!ReferenceEquals(sender, _out)) return;
        if (e.Exception != null) { PlaybackFailed?.Invoke(e.Exception.Message); return; }
        if (_stopRequested) { _stopRequested = false; return; }
        TrackEnded?.Invoke();
    }

    public void Play()
    {
        lock (_gate)
        {
            if (_out == null) return;
            _stopRequested = false;
            _out.Play();
        }
    }

    public void Pause()
    {
        lock (_gate) _out?.Pause();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_out == null) return;
            _stopRequested = true;
            _out.Stop();
            if (_reader != null) _reader.Position = 0;
        }
    }

    public void Close()
    {
        lock (_gate) CloseInternal();
    }

    public bool IsPlaying => _out?.PlaybackState == PlaybackState.Playing;
    public bool IsOpen => _reader != null;

    public TimeSpan Position
    {
        get { lock (_gate) return _reader?.CurrentTime ?? TimeSpan.Zero; }
        set
        {
            lock (_gate)
            {
                if (_reader == null) return;
                var clamped = value < TimeSpan.Zero ? TimeSpan.Zero : value > _reader.TotalTime ? _reader.TotalTime : value;
                _reader.CurrentTime = clamped;
            }
        }
    }

    public TimeSpan Duration
    {
        get { lock (_gate) return _reader?.TotalTime ?? TimeSpan.Zero; }
    }

    public float Volume
    {
        get => _vol;
        set
        {
            _vol = Math.Clamp(value, 0f, 1f);
            if (_volume != null) _volume.Volume = EffectiveVolume;
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            _muted = value;
            if (_volume != null) _volume.Volume = EffectiveVolume;
        }
    }

    private void CloseInternal()
    {
        if (_out != null)
        {
            _out.PlaybackStopped -= OnStopped;
            _out.Stop();
            _out.Dispose();
            _out = null;
        }
        _reader?.Dispose();
        _reader = null;
        _volume = null;
    }

    public void Dispose()
    {
        lock (_gate) CloseInternal();
    }
}
