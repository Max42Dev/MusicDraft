namespace MusicDraft.Core.Covers;

public enum ReferenceMode { Excerpt, WholeSong }

/// <summary>
/// 20-second excerpt rule: the window START must lie in the first third of the recording
/// (0 &lt;= t &lt;= min(duration/3, duration-20)); the window may extend beyond the first third.
/// Sources shorter than 20 s allow whole-song mode only.
/// </summary>
public static class ExcerptRules
{
    public const double ExcerptSeconds = 20.0;

    /// <summary>Longest source accepted for whole-song transcription (tested up to this in the spike: SheetSage2 windows at 300 s).</summary>
    public const double MaxWholeSongSeconds = 10 * 60;

    public static bool ExcerptAllowed(double duration) => duration >= ExcerptSeconds;

    public static double MaxStart(double duration) =>
        ExcerptAllowed(duration) ? Math.Max(0, Math.Min(duration / 3.0, duration - ExcerptSeconds)) : 0;

    public static double ClampStart(double start, double duration) => Math.Clamp(start, 0, MaxStart(duration));

    public static string? Validate(ReferenceMode mode, double duration, double start)
    {
        if (duration <= 0) return "The reference has no audio.";
        if (mode == ReferenceMode.Excerpt)
        {
            if (!ExcerptAllowed(duration)) return "This recording is shorter than 20 seconds; use Whole song.";
            if (start < 0 || start > MaxStart(duration) + 1e-6)
                return $"The excerpt must start within the first third (0:00–{Format(MaxStart(duration))}).";
        }
        else if (duration > MaxWholeSongSeconds)
        {
            return $"Whole-song transcription is limited to {MaxWholeSongSeconds / 60:0} minutes. Use a 20-second excerpt instead.";
        }
        return null;
    }

    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }
}
