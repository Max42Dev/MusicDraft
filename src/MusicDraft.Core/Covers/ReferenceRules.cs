using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MusicDraft.Core.Covers;

/// <summary>
/// A local audio file (picked or recorded) whose melody guides the new song. Only its path, measured length and the
/// chosen window are stored; the audio itself never leaves this PC except to the local, loopback-only engine.
/// </summary>
public sealed record MelodyReference
{
    public string SourcePath { get; init; } = "";
    /// <summary>Length measured from decoded samples.</summary>
    public double SourceSeconds { get; init; }
    /// <summary>Requested excerpt start (seconds); ignored when the whole clip is used.</summary>
    public double Start { get; init; }
    /// <summary>Use the whole clip instead of a 20-second excerpt (always the case for clips of 20 s or less).</summary>
    public bool WholeClip { get; init; }
    /// <summary>True when the clip was recorded from the microphone in MusicDraft.</summary>
    public bool Recorded { get; init; }

    [JsonIgnore] public ReferenceWindow Window => ReferenceRules.Window(SourceSeconds, Start, WholeClip);
}

/// <summary>The part of the source that is transcribed. <see cref="Length"/> is null for the whole clip (no trim node).</summary>
public readonly record struct ReferenceWindow(double Start, double? Length)
{
    public bool IsWhole => Length == null;
}

/// <summary>
/// Rules for melody references, on top of <see cref="ExcerptRules"/>: sources must be 5 s to 10 min long. Sources longer
/// than 20 s use a 20-second excerpt whose start lies in the first third (or, if chosen, the whole clip); shorter ones
/// always use the whole clip.
/// </summary>
public static partial class ReferenceRules
{
    public const double MinSeconds = 5;
    public const double MaxSourceSeconds = ExcerptRules.MaxWholeSongSeconds;

    public static string? CheckSource(double seconds)
    {
        if (seconds <= 0) return "The audio contains no sound to use.";
        if (seconds < MinSeconds)
            return $"The clip is only {seconds:0.#} s long. Use at least {MinSeconds:0} seconds of melody.";
        if (seconds > MaxSourceSeconds + 0.5)
            return $"The audio is {ExcerptRules.Format(seconds)} long; melody references are limited to {MaxSourceSeconds / 60:0} minutes.";
        return null;
    }

    /// <summary>True when a 20-second excerpt is used rather than the whole clip.</summary>
    public static bool UsesExcerpt(double duration, bool wholeClip) => !wholeClip && duration > ExcerptRules.ExcerptSeconds;

    public static ReferenceWindow Window(double duration, double start, bool wholeClip) =>
        UsesExcerpt(duration, wholeClip)
            ? new(ExcerptRules.ClampStart(start, duration), ExcerptRules.ExcerptSeconds)
            : new(0, null);

    public static string? Validate(MelodyReference r)
    {
        if (string.IsNullOrWhiteSpace(r.SourcePath)) return "Choose an audio file or record one for the melody.";
        if (CheckSource(r.SourceSeconds) is { } p) return p;
        return UsesExcerpt(r.SourceSeconds, r.WholeClip) ? ExcerptRules.Validate(ReferenceMode.Excerpt, r.SourceSeconds, r.Start) : null;
    }

    /// <summary>Human-readable range, e.g. "0:10–0:30 of 1:30" or "whole clip (0:12)".</summary>
    public static string Describe(MelodyReference r)
    {
        var w = r.Window;
        return w.Length is { } len
            ? $"{ExcerptRules.Format(w.Start)}–{ExcerptRules.Format(w.Start + len)} of {ExcerptRules.Format(r.SourceSeconds)}"
            : $"whole clip ({ExcerptRules.Format(r.SourceSeconds)})";
    }

    [GeneratedRegex(@"^\s*([A-Za-z]:|%)")]
    private static partial Regex HeaderOrComment();

    [GeneratedRegex(@"[A-Ga-g]")]
    private static partial Regex NoteLetter();

    /// <summary>True if an ABC score has at least one note in its body (header fields and comments are ignored).</summary>
    public static bool AbcHasNotes(string? abc)
    {
        if (string.IsNullOrWhiteSpace(abc)) return false;
        foreach (var raw in abc.Split('\n'))
        {
            if (HeaderOrComment().IsMatch(raw)) continue;
            var line = Regex.Replace(raw, "\"[^\"]*\"|\\[[A-Za-z]:[^\\]]*\\]|![^!]*!", ""); // chord symbols, inline fields, decorations
            if (NoteLetter().IsMatch(line)) return true;
        }
        return false;
    }
}
