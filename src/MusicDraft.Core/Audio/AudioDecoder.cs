using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace MusicDraft.Core.Audio;

public sealed record AudioProbe(double DurationSeconds, int SampleRate, int Channels, bool Silent, double PeakLevel);

public sealed class AudioDecodeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Decoding helpers. Durations come from decoded sample counts, not container metadata.</summary>
public static class AudioDecoder
{
    public static readonly string[] SupportedExtensions = [".mp3", ".wav", ".flac"];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Melody references also accept M4A/AAC (decoded by Windows Media Foundation).</summary>
    public static readonly string[] ReferenceExtensions = [.. SupportedExtensions, ".m4a"];

    public static bool IsSupportedReference(string path) =>
        ReferenceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static MediaFoundationReader OpenReader(string path)
    {
        if (!File.Exists(path)) throw new AudioDecodeException($"File not found: {path}");
        if (!IsSupportedReference(path)) throw new AudioDecodeException("Only MP3, WAV, FLAC and M4A files are supported.");
        try
        {
            return new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings { RequestFloatOutput = true });
        }
        catch (Exception e)
        {
            throw new AudioDecodeException($"Windows could not decode this file ({e.Message.Trim()}). It may be damaged or use an unsupported codec.", e);
        }
    }

    /// <summary>Fully decodes the file to measure its true length and level.</summary>
    public static AudioProbe Probe(string path, CancellationToken ct = default)
    {
        using var reader = OpenReader(path);
        var sp = reader.ToSampleProvider();
        var buf = new float[sp.WaveFormat.SampleRate * sp.WaveFormat.Channels];
        long samples = 0;
        double peak = 0;
        try
        {
            int n;
            while ((n = sp.Read(buf, 0, buf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                samples += n;
                for (var i = 0; i < n; i++)
                {
                    var a = Math.Abs(buf[i]);
                    if (a > peak) peak = a;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e)
        {
            throw new AudioDecodeException($"Decoding failed part-way through the file ({e.Message.Trim()}). The file may be damaged.", e);
        }
        var frames = samples / sp.WaveFormat.Channels;
        if (frames == 0) throw new AudioDecodeException("The file contains no decodable audio.");
        return new AudioProbe((double)frames / sp.WaveFormat.SampleRate, sp.WaveFormat.SampleRate, sp.WaveFormat.Channels,
            peak < 1e-4, peak);
    }

    /// <summary>
    /// Writes a 48 kHz stereo 16-bit WAV working copy of [start, start+length) (or the whole file when length is null).
    /// The original file is only read. Returns (seconds written, silent?).
    /// </summary>
    public static (double Seconds, bool Silent) ExtractToWav(string source, string destination, double start, double? length, CancellationToken ct = default)
    {
        using var reader = OpenReader(source);
        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2) throw new AudioDecodeException("Only mono or stereo sources are supported.");
        if (sp.WaveFormat.SampleRate != 48000) sp = new WdlResamplingSampleProvider(sp, 48000);

        var skip = new OffsetSampleProvider(sp) { SkipOver = TimeSpan.FromSeconds(Math.Max(0, start)) };
        if (length is { } l) skip.Take = TimeSpan.FromSeconds(l);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var tmp = destination + ".part";
        long frames = 0;
        double peak = 0;
        try
        {
            using (var writer = new WaveFileWriter(tmp, new WaveFormat(48000, 16, 2)))
            {
                var buf = new float[48000 * 2];
                int n;
                while ((n = skip.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    for (var i = 0; i < n; i++)
                    {
                        var a = Math.Abs(buf[i]);
                        if (a > peak) peak = a;
                    }
                    writer.WriteSamples(buf, 0, n);
                    frames += n / 2;
                }
            }
            if (frames == 0) throw new AudioDecodeException("The selected range contains no audio.");
            File.Move(tmp, destination, overwrite: true);
        }
        catch (Exception e) when (e is not AudioDecodeException and not OperationCanceledException)
        {
            throw new AudioDecodeException($"Could not prepare the working copy ({e.Message.Trim()}).", e);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
        return (frames / 48000.0, peak < 1e-4);
    }

    /// <summary>Peak envelope for a waveform display, normalised 0..1.</summary>
    public static float[] ComputePeaks(string path, int buckets, CancellationToken ct = default)
    {
        using var reader = OpenReader(path);
        var sp = reader.ToSampleProvider();
        var ch = sp.WaveFormat.Channels;
        var totalFrames = (long)(reader.TotalTime.TotalSeconds * sp.WaveFormat.SampleRate);
        if (totalFrames <= 0) return new float[buckets];
        var framesPerBucket = Math.Max(1, totalFrames / buckets);
        var peaks = new List<float>(buckets);
        var buf = new float[4096 * ch];
        float current = 0;
        long inBucket = 0;
        int n;
        while ((n = sp.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < n; i += ch)
            {
                var a = Math.Abs(buf[i]);
                if (a > current) current = a;
                if (++inBucket >= framesPerBucket)
                {
                    peaks.Add(current);
                    current = 0;
                    inBucket = 0;
                }
            }
        }
        if (inBucket > 0) peaks.Add(current);
        var max = peaks.Count == 0 ? 0 : peaks.Max();
        return max <= 0 ? peaks.ToArray() : peaks.Select(p => p / max).ToArray();
    }
}
