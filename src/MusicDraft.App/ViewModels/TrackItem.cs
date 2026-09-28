using CommunityToolkit.Mvvm.ComponentModel;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Covers;

namespace MusicDraft.App.ViewModels;

public sealed partial class TrackItem : ObservableObject
{
    public TrackItem(Track t)
    {
        Track = t;
        Missing = !t.Exists;
        Changed = !Missing && t.ChangedExternally;
    }

    public Track Track { get; }
    public long Id => Track.Id;
    public string Title => Track.Title;
    public string Path => Track.Path;
    public string DurationText => ExcerptRules.Format(Track.DurationSeconds);
    public bool IsGenerated => Track.Origin == TrackOrigin.Generated;
    public string OriginText => IsGenerated ? "Generated" : "Imported";
    public bool Missing { get; }
    public bool Changed { get; }

    public string StatusText => Missing ? "File missing" : Changed ? "Changed on disk" : "";
    public bool HasStatus => Missing || Changed;

    [ObservableProperty] private bool _isCurrent;

    public string AccessibleName =>
        $"{Title}, {DurationText}, {OriginText}{(HasStatus ? ", " + StatusText : "")}{(IsCurrent ? ", now playing" : "")}";
}

/// <summary>A row in a playlist or the queue: position matters because a track may appear more than once.</summary>
public sealed partial class TrackEntry(int position, TrackItem item) : ObservableObject
{
    public int Position { get; } = position;
    public TrackItem Item { get; } = item;
    public string Number => (Position + 1).ToString();
    [ObservableProperty] private bool _isCurrent;
}

public sealed record PlaylistRef(long Id, string Name)
{
    public override string ToString() => Name;
}
