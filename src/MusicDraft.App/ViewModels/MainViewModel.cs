using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MusicDraft.App.ViewModels;

public enum Page { Library, Create, Settings }

public sealed record NavItem(Page Page, string Label, string Glyph);

public sealed partial class Notice(string text, bool isError) : ObservableObject
{
    public string Text { get; } = text;
    public bool IsError { get; } = isError;
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    public MainViewModel(AppServices s, GenerationHub hub, Func<SetupViewModel, Task> showSetup)
    {
        Services = s;
        Hub = hub;
        Library = new LibraryViewModel(s);
        Player = new PlayerViewModel(s);
        Settings = new SettingsViewModel(s, hub, showSetup);
        Create = new CreateViewModel(s, hub, Settings.PromptModelInstallAsync, Settings.SetUpTranscriptionAsync);
        Create.NavigateRequested += Navigate;
        hub.ShowInLibraryRequested += id =>
        {
            Navigate(Page.Library);
            if (id is { } trackId) Library.Reveal(trackId);
        };
        Player.CurrentTrackChanged += Library.MarkCurrent;
        s.Playback.Error += s.Error;
        _selectedNav = Nav[0];
    }

    public AppServices Services { get; }
    public GenerationHub Hub { get; }
    /// <summary>Library + playlists in one screen.</summary>
    public LibraryViewModel Library { get; }
    public PlayerViewModel Player { get; }
    public SettingsViewModel Settings { get; }
    /// <summary>Create also holds the optional melody-from-audio reference (file or microphone).</summary>
    public CreateViewModel Create { get; }

    public IReadOnlyList<NavItem> Nav { get; } =
    [
        new(Page.Library, "Library", "\uE8F1"),
        new(Page.Create, "Create", "\uE945"),
        new(Page.Settings, "Settings", "\uE713"),
    ];

    [ObservableProperty] private NavItem _selectedNav;
    [ObservableProperty] private bool _queueOpen;

    public object CurrentPage => SelectedNav.Page switch
    {
        Page.Library => Library,
        Page.Create => Create,
        _ => Settings,
    };

    partial void OnSelectedNavChanged(NavItem value) => OnPropertyChanged(nameof(CurrentPage));

    public void Navigate(Page p) => SelectedNav = Nav.First(n => n.Page == p);

    // ---------- notifications ----------

    public ObservableCollection<Notice> Notices { get; } = [];

    public void Notify(string text, bool isError) => Notify(new Notice(text, isError));

    private void Notify(Notice n)
    {
        // Collapse identical consecutive messages; keep at most a few on screen.
        if (Notices.Count > 0 && Notices[^1].Text == n.Text) return;
        Notices.Add(n);
        while (Notices.Count > 4) Notices.RemoveAt(0);
        if (!n.IsError)
        {
            _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => LibraryViewModel.OnUi(() => Notices.Remove(n)));
        }
    }

    [RelayCommand]
    private void DismissNotice(Notice n) => Notices.Remove(n);

    [RelayCommand]
    private void ToggleQueue() => QueueOpen = !QueueOpen;

    public void Dispose()
    {
        Create.SaveDraft();
        Create.Melody.Dispose();
        Player.Dispose();
        Library.Detach();
    }
}
