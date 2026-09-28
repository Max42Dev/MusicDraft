using System.Text.RegularExpressions;

namespace MusicDraft.Core.Generation;

/// <summary>
/// Turns the two plain-text boxes the user fills in (Verse, Chorus) into the section-tagged lyrics YuE2 expects:
/// <c>"[Verse]\n{verse lines}\n\n[Chorus]\n{chorus lines}"</c>. Users never type tags; lines that consist solely of a
/// bracket tag (e.g. "[Chorus]") are dropped, blank lines are removed and each line is trimmed.
/// </summary>
public static partial class LyricsComposer
{
    public const string VerseTag = "[Verse]", ChorusTag = "[Chorus]";
    /// <summary>Per-box limit; two full boxes plus tags stay under <see cref="GenerationRequest.MaxLyricsChars"/>.</summary>
    public const int MaxSectionChars = 2500;

    [GeneratedRegex(@"^\[[^\]\r\n]{1,40}\]$")]
    private static partial Regex TagLine();

    [GeneratedRegex(@"^\s*\[([^\]\r\n]{1,40})\]\s*$")]
    private static partial Regex TagLineCapture();

    private static IEnumerable<string> Lines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Trim());

    /// <summary>Trimmed, non-empty lines with tag-only lines removed, joined by "\n".</summary>
    public static string Clean(string? text) =>
        string.Join("\n", Lines(text).Where(l => l.Length > 0 && !TagLine().IsMatch(l)));

    /// <summary>True if the text contains at least one line that is only a bracket tag (these are ignored).</summary>
    public static bool HasTagLines(string? text) => Lines(text).Any(l => TagLine().IsMatch(l));

    /// <summary>
    /// The exact lyrics string sent to the model. Empty sections are left out, so this returns "" when both are empty;
    /// use <see cref="Validate"/> to require both.
    /// </summary>
    public static string Compose(string? verse, string? chorus)
    {
        var parts = new List<string>(2);
        var v = Clean(verse);
        var c = Clean(chorus);
        if (v.Length > 0) parts.Add(VerseTag + "\n" + v);
        if (c.Length > 0) parts.Add(ChorusTag + "\n" + c);
        return string.Join("\n\n", parts);
    }

    /// <summary>User-facing problems with the two boxes (both are mandatory for a vocal song).</summary>
    public static IReadOnlyList<string> Validate(string? verse, string? chorus)
    {
        var e = new List<string>();
        Check(e, "verse", verse);
        Check(e, "chorus", chorus);
        return e;
    }

    /// <summary>Hint for one box, or null when it is fine. Used inline under each textbox.</summary>
    public static string? SectionProblem(string name, string? text)
    {
        var e = new List<string>();
        Check(e, name, text);
        return e.Count > 0 ? e[0] : null;
    }

    private static void Check(List<string> e, string name, string? text)
    {
        var cleaned = Clean(text);
        if (cleaned.Length == 0)
            e.Add(string.IsNullOrWhiteSpace(text) || !HasTagLines(text)
                ? $"Write the {name} lyrics (required for a vocal song, or switch to Instrumental)."
                : $"The {name} only contains a [tag] line; write the words to sing. Section tags are added for you.");
        else if (cleaned.Length > MaxSectionChars)
            e.Add($"The {name} is too long (max {MaxSectionChars} characters).");
    }

    /// <summary>
    /// Best-effort split of legacy tagged lyrics (older drafts) into verse and chorus text: the first [Verse…] section and
    /// the first [Chorus…] section. Untagged text is treated as the verse.
    /// </summary>
    public static (string Verse, string Chorus) Split(string? tagged)
    {
        string? current = null;
        var sections = new List<(string Tag, List<string> Lines)>();
        foreach (var line in Lines(tagged))
        {
            var m = TagLineCapture().Match(line);
            if (m.Success) { current = m.Groups[1].Value.Trim().ToLowerInvariant(); sections.Add((current, [])); continue; }
            if (line.Length == 0) continue;
            if (current == null) { current = "verse"; sections.Add((current, [])); }
            sections[^1].Lines.Add(line);
        }
        string First(string prefix) =>
            sections.FirstOrDefault(s => s.Tag.StartsWith(prefix, StringComparison.Ordinal) && s.Lines.Count > 0).Lines is { } l
                ? string.Join("\n", l) : "";
        return (First("verse"), First("chorus"));
    }
}
