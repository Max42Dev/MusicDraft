using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using MusicDraft.App.Services;
using MusicDraft.App.ViewModels;
using MusicDraft.Core.Audio;

namespace MusicDraft.App;

public partial class MainWindow : Window, IDialogs
{
    public MainWindow()
    {
        InitializeComponent();
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    // ---------- file drops (whole window: add to library; the Library view's track list and playlist pane handle their own drops first) ----------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || Vm is not { } vm) return;
        var paths = DroppedPaths(e);
        if (paths.Count == 0) return;
        e.Handled = true;
        await vm.Library.ImportAsync(paths);
    }

    public static List<string> DroppedPaths(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? []).Select(f => f.TryGetLocalPath()).OfType<string>().ToList();

    /// <summary>Model setup: consent, then download progress in the same dialog. Completes when the dialog closes.</summary>
    public async Task ShowSetupAsync(SetupViewModel vm)
    {
        var dlg = new Views.SetupDialog { DataContext = vm };
        await dlg.ShowDialog(this);
    }

    // ---------- IDialogs ----------

    public async Task<IReadOnlyList<string>> PickAudioFilesAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add audio files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio (MP3, WAV, FLAC)") { Patterns = AudioDecoder.SupportedExtensions.Select(x => "*" + x).ToArray() },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> PickReferenceAudioAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose audio for the melody",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio (MP3, WAV, FLAC, M4A)") { Patterns = AudioDecoder.ReferenceExtensions.Select(x => "*" + x).ToArray() },
            ],
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().FirstOrDefault();
    }

    public async Task<string?> PickFolderAsync(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText)
    {
        var result = false;
        var dlg = MakeDialog(title);
        var ok = new Button { Content = confirmText, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => { result = true; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = DialogBody(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok, cancel);
        await dlg.ShowDialog(this);
        return result;
    }

    public async Task<string?> PromptAsync(string title, string label, string initial)
    {
        string? result = null;
        var dlg = MakeDialog(title);
        var box = new TextBox { Text = initial, PlaceholderText = label, MinWidth = 320 };
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        var ok = new Button { Content = "OK", IsDefault = true, Classes = { "accent" } };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        cancel.Click += (_, _) => dlg.Close();
        dlg.Content = DialogBody(new StackPanel { Spacing = 6, Children = { new TextBlock { Text = label }, box } }, ok, cancel);
        dlg.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dlg.ShowDialog(this);
        return result;
    }

    private static Window MakeDialog(string title) => new()
    {
        Title = title,
        SizeToContent = SizeToContent.WidthAndHeight,
        MaxWidth = 560,
        CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ShowInTaskbar = false,
    };

    private static Control DialogBody(Control content, Button ok, Button cancel) => new DockPanel
    {
        Margin = new Avalonia.Thickness(24),
        Children =
        {
            new StackPanel
            {
                [DockPanel.DockProperty] = Dock.Bottom,
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Avalonia.Thickness(0, 20, 0, 0),
                Children = { ok, cancel },
            },
            content,
        },
    };
}
