using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using MusicDraft.Core.Hardware;

namespace MusicDraft.Core.Downloads;

public sealed record DownloadItem(string DisplayName, Uri Url, string Destination, long SizeBytes, string Sha256);

public readonly record struct DownloadProgress(string Item, long Done, long Total, double BytesPerSecond, string Phase);

public enum DownloadErrorCode { Network, HashMismatch, SizeMismatch, DiskFull, Http, Cancelled }

public sealed class DownloadException(DownloadErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public DownloadErrorCode Code { get; } = code;
}

/// <summary>
/// Resumable HTTP download into <c>{destination}.part</c> with size and SHA-256 verification before an atomic move.
/// Only URLs from the shipped registry are ever passed here.
/// </summary>
public sealed class Downloader(HttpClient http)
{
    public const long DiskReserveBytes = 512L * 1024 * 1024;

    public static HttpClient CreateClient()
    {
        var h = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            AutomaticDecompression = DecompressionMethods.None,
        }) { Timeout = Timeout.InfiniteTimeSpan };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("MusicDraft/0.1 (+local desktop app)");
        return h;
    }

    /// <summary>True if the destination exists with the expected size (hash is checked separately by the model store).</summary>
    public static bool IsPresent(DownloadItem item) => new FileInfo(item.Destination) is { Exists: true } f && f.Length == item.SizeBytes;

    public async Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(item.Destination))!);
        var part = item.Destination + ".part";
        var have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > item.SizeBytes) { File.Delete(part); have = 0; }

        var needed = item.SizeBytes - have;
        var free = HardwareProbe.FreeDiskBytes(item.Destination);
        if (free < needed + DiskReserveBytes)
            throw new DownloadException(DownloadErrorCode.DiskFull,
                $"Not enough disk space for {item.DisplayName}: needs {Gb(needed + DiskReserveBytes)} free, {Gb(free)} available on {Path.GetPathRoot(Path.GetFullPath(item.Destination))}.");

        if (have < item.SizeBytes)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    have = await FetchAsync(item, part, have, progress, ct);
                    break;
                }
                catch (DownloadException e) when (e.Code == DownloadErrorCode.Network && attempt < 4 && !ct.IsCancellationRequested)
                {
                    progress?.Report(new(item.DisplayName, have, item.SizeBytes, 0, $"Connection lost, retrying ({attempt}/3)…"));
                    await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
                    have = File.Exists(part) ? new FileInfo(part).Length : 0;
                }
            }
        }

        if (have != item.SizeBytes)
            throw new DownloadException(DownloadErrorCode.SizeMismatch, $"{item.DisplayName}: expected {item.SizeBytes:N0} bytes, received {have:N0}.");

        progress?.Report(new(item.DisplayName, 0, item.SizeBytes, 0, "Verifying"));
        var hash = await HashFileAsync(part, p => progress?.Report(new(item.DisplayName, p, item.SizeBytes, 0, "Verifying")), ct);
        if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(part); // never keep or install a corrupt file
            throw new DownloadException(DownloadErrorCode.HashMismatch,
                $"{item.DisplayName} failed its integrity check (SHA-256 mismatch). The partial file was removed; try again.");
        }
        File.Move(part, item.Destination, overwrite: true);
    }

    private async Task<long> FetchAsync(DownloadItem item, string part, long have, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException e) { throw new DownloadException(DownloadErrorCode.Network, $"Could not reach {item.Url.Host}: {e.Message}", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new DownloadException(DownloadErrorCode.Network, $"Timed out contacting {item.Url.Host}.", e); }

        using (resp)
        {
            if (have > 0 && resp.StatusCode == HttpStatusCode.OK) have = 0; // server ignored Range: restart
            else if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) { File.Delete(part); have = 0; return await FetchAsync(item, part, 0, progress, ct); }
            else if (!resp.IsSuccessStatusCode)
                throw new DownloadException(DownloadErrorCode.Http, $"{item.DisplayName}: server returned {(int)resp.StatusCode} {resp.ReasonPhrase}.");

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
            var buf = new byte[1 << 20];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long windowStart = have, lastReport = 0;
            double rate = 0;
            try
            {
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    if (have + n > item.SizeBytes)
                        throw new DownloadException(DownloadErrorCode.SizeMismatch, $"{item.DisplayName}: server sent more data than expected.");
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    have += n;
                    var ms = sw.ElapsedMilliseconds;
                    if (ms - lastReport >= 250)
                    {
                        rate = (have - windowStart) / Math.Max(0.001, ms / 1000.0);
                        lastReport = ms;
                        progress?.Report(new(item.DisplayName, have, item.SizeBytes, rate, "Downloading"));
                    }
                }
            }
            catch (IOException e) when (IsDiskFull(e))
            {
                throw new DownloadException(DownloadErrorCode.DiskFull, "The disk became full during the download. Free some space and retry; the download will resume.", e);
            }
            catch (IOException e) when (!ct.IsCancellationRequested)
            {
                throw new DownloadException(DownloadErrorCode.Network, $"Connection interrupted: {e.Message}", e);
            }
            catch (HttpRequestException e)
            {
                throw new DownloadException(DownloadErrorCode.Network, $"Connection interrupted: {e.Message}", e);
            }
            progress?.Report(new(item.DisplayName, have, item.SizeBytes, rate, "Downloading"));
            return have;
        }
    }

    private static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 112 or 39; // ERROR_DISK_FULL / ERROR_HANDLE_DISK_FULL

    public static async Task<string> HashFileAsync(string path, Action<long>? progress, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var buf = new byte[1 << 20];
        long done = 0, last = 0;
        int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0)
        {
            sha.AppendData(buf, 0, n);
            done += n;
            if (done - last >= 64L << 20) { progress?.Invoke(done); last = done; }
        }
        progress?.Invoke(done);
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    public static string Gb(long bytes) => bytes >= 1L << 30 ? $"{bytes / 1e9:0.0} GB" : $"{bytes / 1e6:0} MB";
}
