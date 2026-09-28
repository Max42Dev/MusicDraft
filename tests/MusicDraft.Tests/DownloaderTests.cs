using System.Net;
using System.Security.Cryptography;
using MusicDraft.Core.Downloads;

namespace MusicDraft.Tests;

/// <summary>Minimal loopback HTTP server with Range support and fault injection.</summary>
internal sealed class TestServer : IDisposable
{
    private readonly HttpListener _l = new();
    private readonly CancellationTokenSource _cts = new();
    public byte[] Body;
    public int DropAfterBytes = -1;   // abort the connection after N bytes, once
    public bool IgnoreRange;
    public int Requests;
    public List<string?> Ranges = [];

    public TestServer(byte[] body)
    {
        Body = body;
        for (var port = 18300; ; port++)
        {
            try
            {
                _l.Prefixes.Clear();
                _l.Prefixes.Add($"http://127.0.0.1:{port}/");
                _l.Start();
                Url = new Uri($"http://127.0.0.1:{port}/file.bin");
                break;
            }
            catch (HttpListenerException) when (port < 18400) { }
        }
        _ = Task.Run(LoopAsync);
    }

    public Uri Url { get; } = null!;

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { return; }
            Interlocked.Increment(ref Requests);
            try
            {
                var range = ctx.Request.Headers["Range"];
                lock (Ranges) Ranges.Add(range);
                var start = 0;
                if (range != null && !IgnoreRange && range.StartsWith("bytes="))
                {
                    start = int.Parse(range[6..].Split('-')[0]);
                    ctx.Response.StatusCode = 206;
                    ctx.Response.AddHeader("Content-Range", $"bytes {start}-{Body.Length - 1}/{Body.Length}");
                }
                ctx.Response.ContentLength64 = Body.Length - start;
                var os = ctx.Response.OutputStream;
                var toSend = Body.Length - start;
                if (DropAfterBytes >= 0 && DropAfterBytes < toSend)
                {
                    await os.WriteAsync(Body.AsMemory(start, DropAfterBytes));
                    await os.FlushAsync();
                    DropAfterBytes = -1;
                    ctx.Response.Abort();
                    continue;
                }
                await os.WriteAsync(Body.AsMemory(start, toSend));
                ctx.Response.Close();
            }
            catch { try { ctx.Response.Abort(); } catch { } }
        }
    }

    public void Dispose() { _cts.Cancel(); _l.Close(); }
}

public class DownloaderTests
{
    private static byte[] Data(int n) { var b = new byte[n]; new Random(3).NextBytes(b); return b; }
    private static string Sha(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static (Downloader, DownloadItem, string) Setup(TestServer s, byte[] body, string? sha = null)
    {
        var dir = TestAudio.TempDir();
        var dest = Path.Combine(dir, "models", "m.bin");
        return (new Downloader(Downloader.CreateClient()), new DownloadItem("Test model", s.Url, dest, body.Length, sha ?? Sha(body)), dest);
    }

    [Fact]
    public async Task Downloads_and_verifies()
    {
        var body = Data(3_000_000);
        using var s = new TestServer(body);
        var (d, item, dest) = Setup(s, body);
        await d.DownloadAsync(item, null, default);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Resumes_partial_file_with_range_request()
    {
        var body = Data(2_000_000);
        using var s = new TestServer(body);
        var (d, item, dest) = Setup(s, body);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllBytesAsync(dest + ".part", body[..700_000]);
        await d.DownloadAsync(item, null, default);
        Assert.Equal("bytes=700000-", s.Ranges.Single());
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Recovers_from_dropped_connection()
    {
        var body = Data(2_500_000);
        using var s = new TestServer(body) { DropAfterBytes = 1_000_000 };
        var (d, item, dest) = Setup(s, body);
        await d.DownloadAsync(item, null, default);
        Assert.True(s.Requests >= 2);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Server_ignoring_range_restarts_cleanly()
    {
        var body = Data(1_000_000);
        using var s = new TestServer(body) { IgnoreRange = true };
        var (d, item, dest) = Setup(s, body);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        await File.WriteAllBytesAsync(dest + ".part", body[..400_000]);
        await d.DownloadAsync(item, null, default);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected_and_nothing_installed()
    {
        var body = Data(500_000);
        using var s = new TestServer(body);
        var (d, item, dest) = Setup(s, body, sha: new string('0', 64));
        var e = await Assert.ThrowsAsync<DownloadException>(() => d.DownloadAsync(item, null, default));
        Assert.Equal(DownloadErrorCode.HashMismatch, e.Code);
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Size_mismatch_is_rejected()
    {
        var body = Data(500_000);
        using var s = new TestServer(body);
        var (d, item, dest) = Setup(s, body);
        var wrong = item with { SizeBytes = 400_000 };
        var e = await Assert.ThrowsAsync<DownloadException>(() => d.DownloadAsync(wrong, null, default));
        Assert.Equal(DownloadErrorCode.SizeMismatch, e.Code);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task Unreachable_host_reports_network_error()
    {
        var d = new Downloader(Downloader.CreateClient());
        var dest = Path.Combine(TestAudio.TempDir(), "x.bin");
        var item = new DownloadItem("Test", new Uri("http://127.0.0.1:1/none"), dest, 10, new string('0', 64));
        var e = await Assert.ThrowsAsync<DownloadException>(() => d.DownloadAsync(item, null, default));
        Assert.Equal(DownloadErrorCode.Network, e.Code);
    }

    [Fact]
    public async Task Insufficient_disk_is_refused_before_download()
    {
        var d = new Downloader(Downloader.CreateClient());
        var dest = Path.Combine(TestAudio.TempDir(), "huge.bin");
        var item = new DownloadItem("Huge", new Uri("http://127.0.0.1:1/none"), dest, long.MaxValue / 4, new string('0', 64));
        var e = await Assert.ThrowsAsync<DownloadException>(() => d.DownloadAsync(item, null, default));
        Assert.Equal(DownloadErrorCode.DiskFull, e.Code);
    }

    [Fact]
    public async Task Cancellation_keeps_partial_for_resume()
    {
        var body = Data(20_000_000);
        using var s = new TestServer(body);
        var (d, item, dest) = Setup(s, body);
        using var cts = new CancellationTokenSource();
        var p = new Progress<DownloadProgress>(x => { if (x.Done > 0) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.DownloadAsync(item, p, cts.Token));
        Assert.False(File.Exists(dest));
        await d.DownloadAsync(item, null, default);
        Assert.Equal(body, await File.ReadAllBytesAsync(dest));
    }
}
