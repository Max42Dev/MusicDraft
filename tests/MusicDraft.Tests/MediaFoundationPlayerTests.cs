using MusicDraft.Core.Audio;
using NAudio.Wave;

namespace MusicDraft.Tests;

/// <summary>Real device playback. Returns early (passes) on machines without an audio output device.</summary>
public class MediaFoundationPlayerTests
{
    private static bool HasOutputDevice => WaveOut.DeviceCount > 0;

    [Fact]
    public async Task Plays_seeks_and_signals_end()
    {
        if (!HasOutputDevice) return;
        var dir = TestAudio.TempDir();
        var wav = TestAudio.Wav(dir, "tone.wav", 1.5);
        using var p = new MediaFoundationPlayer { Volume = 0.05f };
        var ended = new TaskCompletionSource();
        p.TrackEnded += () => ended.TrySetResult();
        p.Open(wav);
        Assert.InRange(p.Duration.TotalSeconds, 1.45, 1.55);
        p.Play();
        await Task.Delay(400);
        Assert.True(p.IsPlaying);
        Assert.True(p.Position > TimeSpan.Zero);
        p.Position = TimeSpan.FromSeconds(1.2);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Stop_does_not_raise_track_ended_and_close_releases_file()
    {
        if (!HasOutputDevice) return;
        var dir = TestAudio.TempDir();
        var wav = TestAudio.Wav(dir, "tone.wav", 3);
        using var p = new MediaFoundationPlayer { Volume = 0.05f };
        var endedCount = 0;
        p.TrackEnded += () => Interlocked.Increment(ref endedCount);
        p.Open(wav);
        p.Play();
        await Task.Delay(200);
        p.Stop();
        await Task.Delay(300);
        Assert.Equal(0, endedCount);
        p.Close();
        File.Delete(wav); // would throw if the handle were still open
        Assert.False(p.IsOpen);
    }
}
