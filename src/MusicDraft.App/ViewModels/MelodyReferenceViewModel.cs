using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicDraft.Core.Audio;
using MusicDraft.Core.Covers;

namespace MusicDraft.App.ViewModels;

/// <summary>
/// The optional "Melody from audio" section of Create: a local file (picked or dropped) or a microphone recording whose
/// melody guides the new song. Audio stays on this PC; recordings are saved under the app data folder.
/// </summary>
public sealed partial class MelodyReferenceViewModel : ObservableObject, IDisposable
{
    private readonly AppServices _s;
    private readonly Func<string, bool> _inUse;
    private readonly MicRecorder _recorder = new();
    private readonly DispatcherTimer _meter;
    private MediaFoundationPlayer? _preview;
    private DispatcherTimer? _previewTimer;
    private int _probeVersion;

    /// <param name="inUse">True for a source path the running generation still reads (such recordings are not deleted).</param>
    public MelodyReferenceViewModel(AppServices s, Func<string, bool> inUse)
    {
        _s = s;
        _inUse = inUse;
        _recorder.Stopped += r => Dispatcher.UIThread.Post(() => OnRecordingStopped(r));
        _meter = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) =>
        {
            Level = Math.Max(_recorder.ReadLevel() * 100, Level * 0.6); // quick attack, smooth release
            ElapsedText = ExcerptRules.Format(_recorder.Elapsed.TotalSeconds);
        });
    }

    /// <summary>The chosen reference, or null. Raised as a property change for Create to revalidate.</summary>
    [ObservableProperty] private MelodyReference? _reference;
    [ObservableProperty] private bool _isProbing;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isPreviewing;
    [ObservableProperty] private string _elapsedText = "0:00";
    [ObservableProperty] private double _level;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private double _start;
    [ObservableProperty] private bool _wholeClip;

    public bool HasReference => Reference != null;
    public bool NoReference => Reference == null && !IsProbing && !IsRecording;
    public bool HasMessage => Message != null;
    public bool CanRecord => !IsRecording && !IsProbing;
    public string FileName => Reference is { } r ? (r.Recorded ? "Microphone recording" : Path.GetFileName(r.SourcePath)) : "";
    public string DurationText => Reference is { } r ? ExcerptRules.Format(r.SourceSeconds) : "";
    public double Duration => Reference?.SourceSeconds ?? 0;
    /// <summary>An excerpt is possible only when the clip is longer than the 20-second window.</summary>
    public bool CanExcerpt => Duration > ExcerptRules.ExcerptSeconds;
    public bool ShowStart => CanExcerpt && !WholeClip;
    public double MaxStart => ExcerptRules.MaxStart(Duration);
    public string RangeText => Reference is { } r ? "Uses " + ReferenceRules.Describe(Current(r)) : "";
    public string ExcerptHint => !CanExcerpt
        ? $"Clips of {ExcerptRules.ExcerptSeconds:0} seconds or less are used whole."
        : WholeClip
            ? "The whole recording is transcribed. Long sources take more time and memory."
            : $"A {ExcerptRules.ExcerptSeconds:0}-second excerpt is used. Its start must be in the first third (up to {ExcerptRules.Format(MaxStart)}).";
    public string PreviewLabel => IsPreviewing ? "Stop" : "Preview";
    public string PreviewGlyph => IsPreviewing ? "\uE71A" : "\uE768";

    /// <summary>The reference with the current excerpt settings applied.</summary>
    public MelodyReference? Effective => Reference is { } r ? Current(r) : null;

    private MelodyReference Current(MelodyReference r) => r with
    {
        Start = ExcerptRules.ClampStart(Start, r.SourceSeconds),
        WholeClip = WholeClip || !ReferenceRules.UsesExcerpt(r.SourceSeconds, false),
    };

    partial void OnReferenceChanged(MelodyReference? value)
    {
        foreach (var n in new[] { nameof(HasReference), nameof(NoReference), nameof(FileName), nameof(DurationText), nameof(Duration),
                     nameof(CanExcerpt), nameof(ShowStart), nameof(MaxStart), nameof(RangeText), nameof(ExcerptHint), nameof(Effective) })
            OnPropertyChanged(n);
    }

    partial void OnStartChanged(double value)
    {
        var snapped = Math.Round(value * 2) / 2; // half-second steps
        if (Math.Abs(snapped - value) > 1e-6) { Start = snapped; return; }
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(Effective));
    }

    partial void OnWholeClipChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(RangeText));
        OnPropertyChanged(nameof(ExcerptHint));
        OnPropertyChanged(nameof(Effective));
    }

    partial void OnIsProbingChanged(bool value) { OnPropertyChanged(nameof(NoReference)); OnPropertyChanged(nameof(CanRecord)); }
    partial void OnIsRecordingChanged(bool value) { OnPropertyChanged(nameof(NoReference)); OnPropertyChanged(nameof(CanRecord)); }
    partial void OnMessageChanged(string? value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnIsPreviewingChanged(bool value) { OnPropertyChanged(nameof(PreviewLabel)); OnPropertyChanged(nameof(PreviewGlyph)); }

    // ---------- choosing a file ----------

    [RelayCommand]
    private async Task PickFile()
    {
        var path = await _s.Dialogs.PickReferenceAudioAsync();
        if (path != null) await UseFileAsync(path);
    }

    /// <summary>Uses a picked or dropped file: measures its decoded length and checks the reference rules.</summary>
    public async Task UseFileAsync(string path, bool recorded = false, double start = 0, bool wholeClip = false)
    {
        if (IsRecording) return;
        if (!AudioDecoder.IsSupportedReference(path)) { Message = "Choose an MP3, WAV, FLAC or M4A file."; return; }
        StopPreview();
        var version = ++_probeVersion;
        IsProbing = true;
        Message = null;
        try
        {
            var probe = await Task.Run(() => AudioDecoder.Probe(path));
            if (version != _probeVersion) return;
            if (ReferenceRules.CheckSource(probe.DurationSeconds) is { } problem) { Message = problem; return; }
            if (probe.Silent) { Message = "This audio is silent. Choose another file or record again."; return; }
            ReplaceReference(new MelodyReference { SourcePath = path, SourceSeconds = probe.DurationSeconds, Recorded = recorded });
            WholeClip = wholeClip;
            Start = ExcerptRules.ClampStart(start, probe.DurationSeconds);
        }
        catch (AudioDecodeException e) { if (version == _probeVersion) Message = e.Message; }
        finally { if (version == _probeVersion) IsProbing = false; }
    }

    private void ReplaceReference(MelodyReference? next)
    {
        var old = Reference;
        Reference = next;
        // Recordings made here are ours to delete when replaced or cleared; picked files are never touched.
        if (old is { Recorded: true } && old.SourcePath != next?.SourcePath && !_inUse(old.SourcePath)
            && RecordingFiles.IsRecording(old.SourcePath, _s.Paths.Recordings))
            TryDelete(old.SourcePath);
    }

    [RelayCommand]
    private void Clear()
    {
        StopPreview();
        _probeVersion++;
        IsProbing = false;
        ReplaceReference(null);
        Message = null;
        Start = 0;
        WholeClip = false;
    }

    // ---------- microphone ----------

    [RelayCommand]
    private void Record()
    {
        if (IsRecording) return;
        StopPreview();
        Message = null;
        var path = RecordingFiles.NewPath(_s.Paths.Recordings, DateTime.Now);
        try { _recorder.Start(path); }
        catch (MicrophoneException e) { Message = e.Message; MicRecorder.OpenMicrophoneSettings(); return; }
        catch (Exception e) { Message = MicRecorder.Describe(e); MicRecorder.OpenMicrophoneSettings(); return; }
        IsRecording = true;
        ElapsedText = "0:00";
        Level = 0;
        _meter.Start();
    }

    [RelayCommand]
    private void StopRecording() => _recorder.Stop();

    private async void OnRecordingStopped(RecordingResult r)
    {
        _meter.Stop();
        IsRecording = false;
        Level = 0;
        if (r.Silent)
        {
            TryDelete(r.Path);
            Message = r.Error ?? "Nothing was recorded (only silence). Check that the microphone is not muted and that Windows lets " +
                "desktop apps use it (Settings → Privacy & security → Microphone).";
            MicRecorder.OpenMicrophoneSettings();
            return;
        }
        if (r.Seconds < ReferenceRules.MinSeconds)
        {
            TryDelete(r.Path);
            Message = $"That recording is only {r.Seconds:0.#} s. Record at least {ReferenceRules.MinSeconds:0} seconds of melody.";
            return;
        }
        await UseFileAsync(r.Path, recorded: true);
        if (r.Error != null) Message = r.Error + " The part recorded so far is used.";
        else if (r.Seconds >= ReferenceRules.MaxSourceSeconds - 0.5) Message = $"Recording stopped at the {ReferenceRules.MaxSourceSeconds / 60:0}-minute limit.";
    }

    // ---------- preview (separate from the main player; pauses it) ----------

    [RelayCommand]
    private void TogglePreview()
    {
        if (IsPreviewing) { StopPreview(); return; }
        if (Effective is not { } r) return;
        try
        {
            if (_s.Playback.IsPlaying) _s.Playback.TogglePlayPause();
            _preview ??= new MediaFoundationPlayer();
            _preview.TrackEnded += OnPreviewEnded;
            _preview.Open(r.SourcePath);
            var w = r.Window;
            _preview.Position = TimeSpan.FromSeconds(w.Start);
            _preview.Play();
            IsPreviewing = true;
            var end = w.Length is { } len ? w.Start + len : double.MaxValue;
            _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, (_, _) =>
            {
                if (_preview == null || !_preview.IsPlaying || _preview.Position.TotalSeconds >= end) StopPreview();
            });
            _previewTimer.Start();
        }
        catch (Exception e)
        {
            StopPreview();
            Message = "Could not play the reference: " + e.Message;
        }
    }

    private void OnPreviewEnded() => Dispatcher.UIThread.Post(StopPreview);

    public void StopPreview()
    {
        _previewTimer?.Stop();
        _previewTimer = null;
        if (_preview != null)
        {
            _preview.TrackEnded -= OnPreviewEnded;
            _preview.Close();
        }
        IsPreviewing = false;
    }

    private static void TryDelete(string p) { try { File.Delete(p); } catch { } }

    public void Dispose()
    {
        _meter.Stop();
        StopPreview();
        _preview?.Dispose();
        _recorder.Dispose();
    }
}
