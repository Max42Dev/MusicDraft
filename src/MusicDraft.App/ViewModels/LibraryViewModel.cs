using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicDraft.Core.Catalog;

namespace MusicDraft.App.ViewModels;

/// <summary>An entry in the Library's left pane: "All tracks" (no playlist) or one playlist.</summary>
public sealed record LibrarySource(PlaylistRef? Playlist)
{
    public bool IsAll => Playlist == null;
    public string Name => Playlist?.Name ?? "All tracks";
    public string Glyph => IsAll ? "\uE8F1" : "\uE90B";
    public override string ToString() => Name;
}

/// <summary>
/// The single Library screen: browse all tracks or one playlist, search, play, import,
/// and manage playlists (create/rename/delete, add/remove/reorder tracks) in one place.
/// </summary>
public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppServices _s;
    private CancellationTokenSource? _importCts;
    private bool _refreshing;
    private int _libraryCount;
    private int _playlistCount;

    public LibraryViewModel(AppServices s)
    {
        _s = s;
        _s.CatalogChanged += Refresh;
        Refresh();
    }

    /// <summary>Left pane: "All tracks" followed by every playlist.</summary>
    public ObservableCollection<LibrarySource> Sources { get; } = [];

    /// <summary>Playlists only (for the "Add to playlist" menu).</summary>
    public ObservableCollection<PlaylistRef> Playlists { get; } = [];

    /// <summary>Tracks shown for the selected source, after the search filter.</summary>
    public ObservableCollection<TrackEntry> Tracks { get; } = [];

    [ObservableProperty] private LibrarySource? _selectedSource;
    [ObservableProperty] private string _filter = "";
    [ObservableProperty] private TrackEntry? _selected;
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importStatus = "";

    public PlaylistRef? SelectedPlaylist => SelectedSource?.Playlist;
    public bool IsPlaylist => SelectedPlaylist != null;
    public bool IsAllTracks => !IsPlaylist;
    public string Title => SelectedSource?.Name ?? "All tracks";

    /// <summary>Nothing imported at all: show the big "add music" empty state.</summary>
    public bool IsEmpty => IsAllTracks && _libraryCount == 0 && !IsImporting;
    public bool PlaylistEmpty => IsPlaylist && _playlistCount == 0;
    public bool NoMatches => Tracks.Count == 0 && Filter.Trim().Length > 0 && !IsEmpty && !PlaylistEmpty;
    public bool ShowList => !IsEmpty;

    public string Summary
    {
        get
        {
            var n = IsPlaylist ? _playlistCount : _libraryCount;
            var s = $"{n} track{(n == 1 ? "" : "s")}";
            return Filter.Trim().Length > 0 ? $"{Tracks.Count} of {s}" : IsPlaylist ? $"Playlist · {s}" : s;
        }
    }

    partial void OnFilterChanged(string value) => LoadTracks();
    partial void OnIsImportingChanged(bool value) => RaiseStateChanged();

    partial void OnSelectedSourceChanged(LibrarySource? value)
    {
        if (_refreshing) return;
        Selected = null;
        LoadTracks();
    }

    // ---------- loading ----------

    public void Refresh()
    {
        _refreshing = true;
        var selPlaylist = SelectedSource?.Playlist?.Id;
        Sources.Clear();
        Playlists.Clear();
        Sources.Add(new LibrarySource(null));
        foreach (var p in _s.Db.GetPlaylists())
        {
            var r = new PlaylistRef(p.Id, p.Name);
            Playlists.Add(r);
            Sources.Add(new LibrarySource(r));
        }
        SelectedSource = selPlaylist is { } id ? Sources.FirstOrDefault(x => x.Playlist?.Id == id) ?? Sources[0] : Sources[0];
        _refreshing = false;
        LoadTracks();
    }

    private void LoadTracks()
    {
        var selPos = Selected?.Position;
        var selId = Selected?.Item.Id;
        var f = Filter.Trim();
        Tracks.Clear();

        IEnumerable<(int Position, Track Track)> rows;
        var all = _s.Db.GetTracks();
        _libraryCount = all.Count;
        if (SelectedPlaylist is { } p)
        {
            var ids = _s.Db.GetPlaylistTrackIds(p.Id);
            _playlistCount = ids.Count;
            var list = new List<(int, Track)>();
            for (var i = 0; i < ids.Count; i++)
                if (_s.Db.GetTrack(ids[i]) is { } t) list.Add((i, t));
            rows = list;
        }
        else
        {
            _playlistCount = 0;
            rows = all.Select((t, i) => (i, t));
        }

        foreach (var (pos, t) in rows)
        {
            if (f.Length > 0 && !t.Title.Contains(f, StringComparison.OrdinalIgnoreCase) &&
                !t.Path.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            Tracks.Add(new TrackEntry(pos, new TrackItem(t)));
        }

        Selected = IsPlaylist
            ? Tracks.FirstOrDefault(e => e.Position == selPos && e.Item.Id == selId) ?? Tracks.FirstOrDefault(e => e.Position == selPos)
            : Tracks.FirstOrDefault(e => e.Item.Id == selId);
        MarkCurrent(_s.Playback.CurrentTrack?.Id);
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(SelectedPlaylist));
        OnPropertyChanged(nameof(IsPlaylist));
        OnPropertyChanged(nameof(IsAllTracks));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(PlaylistEmpty));
        OnPropertyChanged(nameof(NoMatches));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Shows "All tracks" with no filter and selects the given track (e.g. a song just created).</summary>
    public void Reveal(long trackId)
    {
        Refresh();
        if (SelectedSource != Sources[0]) SelectedSource = Sources[0];
        if (Filter.Length > 0) Filter = "";
        Selected = Tracks.FirstOrDefault(e => e.Item.Id == trackId);
    }

    public void MarkCurrent(long? id)
    {
        foreach (var e in Tracks) e.IsCurrent = e.Item.IsCurrent = e.Item.Id == id;
    }

    // ---------- import ----------

    [RelayCommand]
    private async Task AddFiles()
    {
        var files = await _s.Dialogs.PickAudioFilesAsync();
        if (files.Count > 0) await ImportAsync(files);
    }

    [RelayCommand]
    private async Task AddFolder()
    {
        var folder = await _s.Dialogs.PickFolderAsync("Add a music folder");
        if (folder != null) await ImportAsync([folder]);
    }

    [RelayCommand]
    private void CancelImport() => _importCts?.Cancel();

    public async Task ImportAsync(IReadOnlyList<string> paths)
    {
        if (IsImporting) { _s.Info("An import is already running."); return; }
        IsImporting = true;
        _importCts = new CancellationTokenSource();
        var progress = new Progress<(int Done, string Current)>(p => ImportStatus = $"Reading {Path.GetFileName(p.Current)} ({p.Done + 1})…");
        try
        {
            var ct = _importCts.Token;
            var r = await Task.Run(() => LibraryImporter.Import(_s.Db, paths, progress, ct), ct);
            _s.RaiseCatalogChanged();
            var msg = $"Added {r.Added.Count} track{(r.Added.Count == 1 ? "" : "s")}.";
            if (r.SkippedUnsupported > 0) msg += $" Skipped {r.SkippedUnsupported} unsupported file(s) (MP3, WAV and FLAC are supported).";
            if (r.Failed.Count > 0)
            {
                msg += $" {r.Failed.Count} could not be read: " + string.Join("; ", r.Failed.Take(3).Select(f => $"{Path.GetFileName(f.Path)} – {f.Error}"));
                _s.Error(msg);
            }
            else _s.Info(msg);
        }
        catch (OperationCanceledException)
        {
            _s.RaiseCatalogChanged();
            _s.Info("Import cancelled. Files read so far were kept.");
        }
        finally
        {
            IsImporting = false;
            ImportStatus = "";
            _importCts.Dispose();
            _importCts = null;
        }
    }

    /// <summary>Files dropped from Explorer: import them, then append them to <paramref name="target"/> if given.</summary>
    public async Task ImportDroppedAsync(IReadOnlyList<string> paths, PlaylistRef? target)
    {
        await ImportAsync(paths);
        if (target == null) return;
        var added = LibraryImporter.Expand(paths).Select(p => _s.Db.GetTrackByPath(p)?.Id).OfType<long>().ToList();
        AddTracksToPlaylist(target, added);
    }

    // ---------- playback ----------

    [RelayCommand]
    private void Play(TrackEntry? item)
    {
        item ??= Selected;
        if (item == null) return;
        PlayFrom(Tracks.IndexOf(item));
    }

    [RelayCommand]
    private void PlayAll() => PlayFrom(0);

    private void PlayFrom(int index)
    {
        if (Tracks.Count == 0)
        {
            _s.Info(IsPlaylist ? "This playlist is empty. Drag tracks from All tracks onto it." : "There is nothing to play yet.");
            return;
        }
        _s.Playback.PlayList(Tracks.Select(t => t.Item.Id).ToList(), Math.Clamp(index, 0, Tracks.Count - 1));
    }

    [RelayCommand]
    private void Enqueue(TrackEntry? item)
    {
        item ??= Selected;
        if (item == null) return;
        _s.Playback.Enqueue([item.Item.Id]);
        _s.Info($"Added \"{item.Item.Title}\" to the queue.");
    }

    // ---------- track actions ----------

    [RelayCommand]
    private void AddToPlaylist(object? parameter)
    {
        if (parameter is not PlaylistRef p || Selected is not { } item) return;
        AddTracksToPlaylist(p, [item.Item.Id]);
        _s.Info($"Added \"{item.Item.Title}\" to {p.Name}.");
    }

    public void AddTracksToPlaylist(PlaylistRef p, IReadOnlyList<long> ids)
    {
        if (ids.Count == 0) return;
        _s.Db.AddToPlaylist(p.Id, ids);
        _s.RaiseCatalogChanged();
    }

    /// <summary>Library tracks dragged onto a playlist in the left pane.</summary>
    public void DropTracksOnPlaylist(PlaylistRef p, IReadOnlyList<long> ids)
    {
        AddTracksToPlaylist(p, ids);
        if (ids.Count > 0) _s.Info($"Added {ids.Count} track{(ids.Count == 1 ? "" : "s")} to {p.Name}.");
    }

    [RelayCommand]
    private async Task Rename(TrackEntry? item)
    {
        item ??= Selected;
        if (item == null) return;
        var name = await _s.Dialogs.PromptAsync("Rename track", "Title", item.Item.Title);
        if (string.IsNullOrWhiteSpace(name)) return;
        _s.Db.RenameTrack(item.Item.Id, name.Trim());
        _s.RaiseCatalogChanged();
    }

    [RelayCommand]
    private void ShowInFolder(TrackEntry? item)
    {
        item ??= Selected;
        if (item == null) return;
        var path = item.Item.Path;
        try
        {
            if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(Path.GetDirectoryName(path))) Process.Start("explorer.exe", $"\"{Path.GetDirectoryName(path)}\"");
            else _s.Error($"The folder no longer exists: {Path.GetDirectoryName(path)}");
        }
        catch (Exception e) { _s.Error(e.Message); }
    }

    [RelayCommand]
    private void RemoveFromLibrary(TrackEntry? item)
    {
        item ??= Selected;
        if (item == null) return;
        _s.Db.RemoveTrack(item.Item.Id);
        _s.Playback.SyncWithCatalog();
        _s.RaiseCatalogChanged();
        _s.Info($"Removed \"{item.Item.Title}\" from the library. The file was not deleted.");
    }

    [RelayCommand]
    private async Task DeleteFile(TrackEntry? item)
    {
        item ??= Selected;
        if (item == null) return;
        var path = item.Item.Path;
        if (!await _s.Dialogs.ConfirmAsync("Delete file?",
                $"This permanently deletes the audio file from disk:\n\n{path}\n\nThis cannot be undone.", "Delete file")) return;
        if (_s.Playback.CurrentTrack?.Id == item.Item.Id) _s.Playback.Stop();
        _s.Db.RemoveTrack(item.Item.Id);
        _s.Playback.SyncWithCatalog();
        try
        {
            if (File.Exists(path)) File.Delete(path);
            _s.Info($"Deleted {Path.GetFileName(path)}.");
        }
        catch (Exception e)
        {
            _s.Error($"Removed from library, but the file could not be deleted: {e.Message}");
        }
        _s.RaiseCatalogChanged();
    }

    /// <summary>Delete key: removes from the open playlist, or from the library when viewing All tracks.</summary>
    [RelayCommand]
    private void Remove(TrackEntry? item)
    {
        if (IsPlaylist) RemoveFromPlaylist(item);
        else RemoveFromLibrary(item);
    }

    // ---------- playlist management ----------

    [RelayCommand]
    private async Task NewPlaylist()
    {
        var name = await _s.Dialogs.PromptAsync("New playlist", "Name", $"Playlist {Playlists.Count + 1}");
        if (string.IsNullOrWhiteSpace(name)) return;
        var p = _s.Db.CreatePlaylist(name.Trim());
        _s.RaiseCatalogChanged();
        SelectedSource = Sources.FirstOrDefault(x => x.Playlist?.Id == p.Id) ?? SelectedSource;
    }

    [RelayCommand]
    private async Task RenamePlaylist(LibrarySource? source)
    {
        if ((source?.Playlist ?? SelectedPlaylist) is not { } p) return;
        var name = await _s.Dialogs.PromptAsync("Rename playlist", "Name", p.Name);
        if (string.IsNullOrWhiteSpace(name)) return;
        _s.Db.RenamePlaylist(p.Id, name.Trim());
        _s.RaiseCatalogChanged();
    }

    [RelayCommand]
    private async Task DeletePlaylist(LibrarySource? source)
    {
        if ((source?.Playlist ?? SelectedPlaylist) is not { } p) return;
        if (!await _s.Dialogs.ConfirmAsync("Delete playlist?", $"Delete the playlist \"{p.Name}\"? Tracks stay in your library.", "Delete playlist")) return;
        _s.Db.DeletePlaylist(p.Id);
        if (SelectedPlaylist?.Id == p.Id) SelectedSource = Sources[0];
        _s.RaiseCatalogChanged();
    }

    [RelayCommand]
    private void MoveUp(TrackEntry? e) => Move(e ?? Selected, -1);

    [RelayCommand]
    private void MoveDown(TrackEntry? e) => Move(e ?? Selected, +1);

    private void Move(TrackEntry? e, int delta)
    {
        if (e != null) MoveTo(e.Position, e.Position + delta);
    }

    /// <summary>Reorder within the open playlist; positions are playlist positions (valid even when filtered).</summary>
    public void MoveTo(int from, int to)
    {
        if (SelectedPlaylist is not { } p) return;
        var ids = _s.Db.GetPlaylistTrackIds(p.Id).ToList();
        if (from == to || from < 0 || to < 0 || from >= ids.Count || to >= ids.Count) return;
        var id = ids[from];
        ids.RemoveAt(from);
        ids.Insert(to, id);
        _s.Db.SetPlaylistOrder(p.Id, ids);
        Selected = null;
        LoadTracks();
        Selected = Tracks.FirstOrDefault(x => x.Position == to);
    }

    public int PlaylistLength => SelectedPlaylist is { } p ? _s.Db.GetPlaylistTrackIds(p.Id).Count : 0;

    [RelayCommand]
    private void RemoveFromPlaylist(TrackEntry? e)
    {
        e ??= Selected;
        if (SelectedPlaylist is not { } p || e == null) return;
        _s.Db.RemoveFromPlaylistAt(p.Id, e.Position);
        _s.RaiseCatalogChanged();
    }

    public void Detach() => _s.CatalogChanged -= Refresh;

    internal static void OnUi(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a(); else Dispatcher.UIThread.Post(a);
    }
}
