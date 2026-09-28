using MusicDraft.App.Services;
using MusicDraft.Core;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Playback;

namespace MusicDraft.App.ViewModels;

/// <summary>Shared services handed to every page view-model.</summary>
public sealed class AppServices(AppPaths paths, CatalogDb db, PlaybackController playback, IDialogs dialogs, Action<string, bool> notify)
{
    public AppPaths Paths { get; } = paths;
    public CatalogDb Db { get; } = db;
    public PlaybackController Playback { get; } = playback;
    public IDialogs Dialogs { get; } = dialogs;

    public void Info(string message) => notify(message, false);
    public void Error(string message) => notify(message, true);

    /// <summary>Raised when tracks/playlists change so all pages refresh from the catalog.</summary>
    public event Action? CatalogChanged;
    public void RaiseCatalogChanged() => CatalogChanged?.Invoke();
}
