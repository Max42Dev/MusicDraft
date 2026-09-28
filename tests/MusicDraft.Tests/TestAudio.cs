using NAudio.Wave;

namespace MusicDraft.Tests;

/// <summary>Synthesised test signals only; no recorded or copyrighted audio is used or shipped.</summary>
internal static class TestAudio
{
    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "musicdraft-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    public static string Wav(string dir, string name, double seconds, int rate = 44100, int channels = 2, bool silent = false, double freq = 440)
    {
        var path = Path.Combine(dir, name);
        using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        var frames = (int)(seconds * rate);
        var buf = new float[channels * 4096];
        var written = 0;
        while (written < frames)
        {
            var n = Math.Min(4096, frames - written);
            for (var i = 0; i < n; i++)
            {
                var v = silent ? 0f : (float)(0.3 * Math.Sin(2 * Math.PI * freq * (written + i) / rate));
                for (var c = 0; c < channels; c++) buf[i * channels + c] = v;
            }
            w.WriteSamples(buf, 0, n * channels);
            written += n;
        }
        return path;
    }

    /// <summary>A synthetic sung-like melody (harmonic tones with vibrato, C major scale pattern) for transcription tests.</summary>
    public static string Melody(string dir, string name, double seconds, int rate = 44100)
    {
        int[] pattern = [60, 62, 64, 65, 67, 65, 64, 62, 60, 64, 67, 72, 67, 64, 62, 60];
        var path = Path.Combine(dir, name);
        using var w = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        var frames = (int)(seconds * rate);
        var noteFrames = rate / 2; // 120 BPM quarter notes
        var buf = new float[frames];
        double phase = 0;
        for (var i = 0; i < frames; i++)
        {
            var n = i / noteFrames;
            var pos = (double)(i % noteFrames) / noteFrames;
            var f = 440 * Math.Pow(2, (pattern[n % pattern.Length] - 69) / 12.0) * (1 + 0.004 * Math.Sin(2 * Math.PI * 5.5 * i / rate));
            phase += 2 * Math.PI * f / rate;
            var env = Math.Min(1, pos * 20) * Math.Min(1, (1 - pos) * 8);
            buf[i] = (float)(0.25 * env * (Math.Sin(phase) + 0.5 * Math.Sin(2 * phase) + 0.25 * Math.Sin(3 * phase)));
        }
        w.WriteSamples(buf, 0, buf.Length);
        return path;
    }

    /// <summary>Encodes a WAV to another container via Media Foundation; returns null when no encoder is available.</summary>
    public static string? Encode(string wav, string extension)
    {
        var dest = Path.ChangeExtension(wav, extension);
        try
        {
            using var reader = new WaveFileReader(wav);
            if (extension == ".mp3") MediaFoundationEncoder.EncodeToMp3(reader, dest, 128000);
            else return null;
            return dest;
        }
        catch { return null; }
    }
}
