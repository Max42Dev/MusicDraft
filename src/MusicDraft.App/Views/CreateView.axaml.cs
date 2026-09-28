using Avalonia.Controls;
using Avalonia.Input;
using MusicDraft.App.ViewModels;
using MusicDraft.Core.Audio;

namespace MusicDraft.App.Views;

public partial class CreateView : UserControl
{
    public CreateView()
    {
        InitializeComponent();
        // Files dropped on the melody section become the reference (the window-level drop would import them to the Library).
        MelodyDropZone.AddHandler(DragDrop.DragOverEvent, OnMelodyDragOver);
        MelodyDropZone.AddHandler(DragDrop.DropEvent, OnMelodyDrop);
    }

    private MelodyReferenceViewModel? Melody => (DataContext as CreateViewModel)?.Melody;

    private void OnMelodyDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) && Melody is { IsRecording: false } ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnMelodyDrop(object? sender, DragEventArgs e)
    {
        e.Handled = true;
        if (Melody is not { } m) return;
        var paths = MainWindow.DroppedPaths(e);
        var path = paths.FirstOrDefault(AudioDecoder.IsSupportedReference) ?? paths.FirstOrDefault();
        if (path != null) await m.UseFileAsync(path);
    }
}
