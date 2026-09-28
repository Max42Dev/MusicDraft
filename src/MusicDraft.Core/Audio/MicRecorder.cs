using MusicDraft.Core.Covers;
using NAudio;
using NAudio.Wave;

namespace MusicDraft.Core.Audio;

public sealed class MicrophoneException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Outcome of a recording. <see cref="Error"/> is set if the device failed mid-recording.</summary>
public sealed record RecordingResult(string Path, double Seconds, double Peak, string? Error)
{
    /// <summary>Digital silence: typical when Windows privacy settings block the microphone, or it is muted.</summary>
    public bool Silent => Peak < 1e-4;
}

/// <summary>File naming for microphone recordings (kept locally under the app data folder).</summary>
public static class RecordingFiles
{
    public const string Prefix = "recording-";

    /// <summary>A new, unused path like <c>recording-20260927-143012.wav</c> (with " (2)" etc. if taken).</summary>
    public static string NewPath(string dir, DateTime now)
    {
        var name = Prefix + now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var p = Path.Combine(dir, name + ".wav");
        for (var i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i}).wav");
        return p;
    }

    /// <summary>True for a file MusicDraft recorded into <paramref name="dir"/>.</summary>
    public static bool IsRecording(string path, string dir)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase)
               && Path.GetFileName(full).StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
               && Path.GetExtension(full).Equals(".wav", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Records the default microphone (WinMM wave mapper via NAudio) to a 44.1 kHz mono 16-bit WAV. Stops by itself at
/// <see cref="ReferenceRules.MaxSourceSeconds"/>. <see cref="Stopped"/> may be raised on a background thread.
/// </summary>
public sealed class MicRecorder : IDisposable
{
    public static readonly WaveFormat Format = new(44100, 16, 1);
    private static readonly long MaxBytes = (long)(ReferenceRules.MaxSourceSeconds * Format.AverageBytesPerSecond);

    private readonly object _gate = new();
    private WaveInEvent? _in;
    private WaveFileWriter? _writer;
    private string? _path;
    private long _bytes;
    private double _peak;
    private float _level;

    public event Action<RecordingResult>? Stopped;

    public static bool HasMicrophone
    {
        get
        {
            try { return WaveInEvent.DeviceCount > 0; }
            catch (Exception) { return false; }
        }
    }

    public bool IsRecording { get { lock (_gate) return _in != null; } }

    public TimeSpan Elapsed { get { lock (_gate) return TimeSpan.FromSeconds((double)_bytes / Format.AverageBytesPerSecond); } }

    /// <summary>Peak level (0..1) since the previous call, for a level meter.</summary>
    public float ReadLevel()
    {
        lock (_gate)
        {
            var l = _level;
            _level = 0;
            return l;
        }
    }

    public void Start(string path)
    {
        lock (_gate)
        {
            if (_in != null) throw new InvalidOperationException("Already recording.");
            if (!HasMicrophone) throw new MicrophoneException("No microphone was found. Connect one, or choose an audio file instead.");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var wi = new WaveInEvent { DeviceNumber = -1, WaveFormat = Format, BufferMilliseconds = 50 }; // -1 = default device
            WaveFileWriter? writer = null;
            try
            {
                writer = new WaveFileWriter(path, Format);
                wi.DataAvailable += OnData;
                wi.RecordingStopped += OnStopped;
                _in = wi;
                _writer = writer;
                _path = path;
                _bytes = 0;
                _peak = 0;
                _level = 0;
                wi.StartRecording();
            }
            catch (Exception e)
            {
                _in = null;
                _writer = null;
                wi.DataAvailable -= OnData;
                wi.RecordingStopped -= OnStopped;
                try { wi.Dispose(); } catch { }
                writer?.Dispose();
                try { File.Delete(path); } catch { }
                throw new MicrophoneException(Describe(e), e);
            }
        }
    }

    public static string Describe(Exception e)
    {
        var why = e is MmException m ? m.Result.ToString() : e.Message.Trim();
        return $"Could not open the microphone ({why}). Check that it is connected and not in use by another app, and that " +
               "Windows lets desktop apps use the microphone (Settings → Privacy & security → Microphone).";
    }

    /// <summary>Opens Windows Settings to the microphone privacy page, so the user can allow desktop apps to use the mic.</summary>
    public static void OpenMicrophoneSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-microphone")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception)
        {
            // Settings can't always be launched (e.g. sandboxed); the on-screen message still points the user there.
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        var stop = false;
        lock (_gate)
        {
            if (_writer == null || !ReferenceEquals(sender, _in)) return;
            _writer.Write(e.Buffer, 0, e.BytesRecorded);
            _bytes += e.BytesRecorded;
            float peak = 0;
            for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
            {
                var a = Math.Abs(BitConverter.ToInt16(e.Buffer, i) / 32768f);
                if (a > peak) peak = a;
            }
            if (peak > _level) _level = peak;
            if (peak > _peak) _peak = peak;
            stop = _bytes >= MaxBytes;
        }
        if (stop) Stop();
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        RecordingResult r;
        lock (_gate)
        {
            if (_in == null || !ReferenceEquals(sender, _in)) return;
            try { _writer?.Dispose(); } catch (Exception) { }
            r = new RecordingResult(_path!, (double)_bytes / Format.AverageBytesPerSecond, _peak,
                e.Exception == null ? null : "The microphone stopped unexpectedly: " + e.Exception.Message.Trim());
            _writer = null;
            _in.DataAvailable -= OnData;
            _in.RecordingStopped -= OnStopped;
            _in.Dispose();
            _in = null;
        }
        Stopped?.Invoke(r);
    }

    /// <summary>Requests the recording to stop; <see cref="Stopped"/> follows once the file is closed.</summary>
    public void Stop()
    {
        WaveInEvent? wi;
        lock (_gate) wi = _in;
        try { wi?.StopRecording(); } catch (Exception) { /* already stopping */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_in == null) return;
            _in.DataAvailable -= OnData;
            _in.RecordingStopped -= OnStopped;
            try { _in.StopRecording(); } catch { }
            try { _in.Dispose(); } catch { }
            _in = null;
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
