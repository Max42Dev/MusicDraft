using MusicDraft.Core.Audio;

namespace MusicDraft.Tests;

public class AudioDecoderTests
{
    private readonly string _dir = TestAudio.TempDir();

    [Fact]
    public void Probes_wav_duration_from_samples()
    {
        var p = AudioDecoder.Probe(TestAudio.Wav(_dir, "a.wav", 3.0));
        Assert.InRange(p.DurationSeconds, 2.99, 3.01);
        Assert.False(p.Silent);
    }

    [Fact]
    public void Detects_silence()
    {
        Assert.True(AudioDecoder.Probe(TestAudio.Wav(_dir, "s.wav", 1.0, silent: true)).Silent);
    }

    [Fact]
    public void Probes_mp3_when_encoder_available()
    {
        var mp3 = TestAudio.Encode(TestAudio.Wav(_dir, "m.wav", 2.0), ".mp3");
        if (mp3 == null) return; // no MP3 encoder on this machine (decoder tested via damaged-file case)
        var p = AudioDecoder.Probe(mp3);
        Assert.InRange(p.DurationSeconds, 1.9, 2.2);
    }

    [Fact]
    public void Missing_file_gives_clear_error()
    {
        var e = Assert.Throws<AudioDecodeException>(() => AudioDecoder.Probe(Path.Combine(_dir, "nope.mp3")));
        Assert.Contains("not found", e.Message);
    }

    [Fact]
    public void Unsupported_extension_rejected()
    {
        var p = Path.Combine(_dir, "x.ogg");
        File.WriteAllText(p, "x");
        Assert.Throws<AudioDecodeException>(() => AudioDecoder.Probe(p));
    }

    [Theory]
    [InlineData(".mp3")]
    [InlineData(".flac")]
    [InlineData(".wav")]
    public void Damaged_file_gives_decode_error(string ext)
    {
        var p = Path.Combine(_dir, "bad" + ext);
        var junk = new byte[4096];
        new Random(1).NextBytes(junk);
        File.WriteAllBytes(p, junk);
        Assert.Throws<AudioDecodeException>(() => AudioDecoder.Probe(p));
    }

    [Fact]
    public void Extract_writes_48k_stereo_excerpt_and_leaves_source()
    {
        var src = TestAudio.Wav(_dir, "src.wav", 30, rate: 44100, channels: 1);
        var before = File.ReadAllBytes(src);
        var dst = Path.Combine(_dir, "out", "ex.wav");
        var (secs, silent) = AudioDecoder.ExtractToWav(src, dst, 5, 20);
        Assert.InRange(secs, 19.95, 20.05);
        Assert.False(silent);
        var probe = AudioDecoder.Probe(dst);
        Assert.Equal(48000, probe.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.Equal(before, File.ReadAllBytes(src));
        Assert.False(File.Exists(dst + ".part"));
    }

    [Fact]
    public void Extract_past_end_fails_cleanly()
    {
        var src = TestAudio.Wav(_dir, "short.wav", 2);
        var dst = Path.Combine(_dir, "none.wav");
        Assert.Throws<AudioDecodeException>(() => AudioDecoder.ExtractToWav(src, dst, 10, 20));
        Assert.False(File.Exists(dst));
        Assert.False(File.Exists(dst + ".part"));
    }

    [Fact]
    public void Peaks_are_normalised()
    {
        var peaks = AudioDecoder.ComputePeaks(TestAudio.Wav(_dir, "p.wav", 2), 100);
        Assert.InRange(peaks.Length, 99, 101);
        Assert.Equal(1f, peaks.Max(), 3);
    }
}
