using System.Text.RegularExpressions;

namespace MusicDraft.Core.Generation;

/// <summary>
/// Builds the instruction sent to the local lyrics assistant and parses its reply into the two sections the Create
/// screen needs (Verse and Chorus). Kept pure so it can be unit-tested without a model.
/// </summary>
public static partial class LyricsPrompt
{
    public const string System =
        "You are a songwriter. Write short, singable, original lyrics in English. " +
        "Never explain, apologise or add notes. Reply with exactly two sections and nothing else: " +
        "first a line that says Verse:, then the verse lines; then a blank line; " +
        "then a line that says Chorus:, then the chorus lines. " +
        "Do not use section tags like [Verse] or [Chorus], do not number the lines and do not add a title.";

    /// <summary>Seconds of song per lyric line; the verse grows with the selected length.</summary>
    public const double SecondsPerLine = 7.5;

    /// <summary>Verse lines for a song of <paramref name="maxSeconds"/> seconds (about one line per 7.5 s).</summary>
    public static int VerseLines(double maxSeconds) =>
        Math.Clamp((int)Math.Round(maxSeconds / SecondsPerLine), 4, 48);

    /// <summary>Chorus lines: always short and repeatable, regardless of song length.</summary>
    public const int ChorusLines = 4;

    /// <summary>The user message: the song description (and optional title) the lyrics should match.</summary>
    public static string BuildUserPrompt(string description, string? title, int verseLines = 6, int chorusLines = 3)
    {
        var d = (description ?? "").Trim();
        var t = (title ?? "").Trim();
        var sb = new System.Text.StringBuilder();
        if (t.Length > 0) sb.Append("Song title: ").Append(t).Append('\n');
        sb.Append("Song description: ").Append(d.Length > 0 ? d : "an upbeat pop song");
        sb.Append($"\n\nWrite the Verse and Chorus for this song. Use about {verseLines} short lines for the Verse " +
                  $"and about {chorusLines} for the Chorus.");
        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*(?:\[|\*{0,2})?\s*(verse|chorus|lyrics|hook|refrain)\s*(?:\]|\*{0,2})?\s*:?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex Heading();

    /// <summary>
    /// Splits the model's reply into verse and chorus. Headings such as "Verse:", "[Chorus]" or "**Lyrics**" are
    /// recognised; "lyrics"/"hook"/"refrain" are treated as the chorus. Text before any heading is the verse.
    /// </summary>
    public static (string Verse, string Chorus) Parse(string? reply)
    {
        var verse = new List<string>();
        var chorus = new List<string>();
        List<string>? current = verse;
        foreach (var raw in (reply ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim();
            var m = Heading().Match(line);
            if (m.Success)
            {
                var name = m.Groups[1].Value.ToLowerInvariant();
                current = name is "chorus" or "lyrics" or "hook" or "refrain" ? chorus : verse;
                continue;
            }
            if (line.Length == 0) continue;
            current.Add(line);
        }
        // If the model ignored the headings, keep everything as the verse so nothing is lost.
        if (chorus.Count == 0 && verse.Count == 0) return ("", "");
        return (string.Join("\n", verse), string.Join("\n", chorus));
    }
}
