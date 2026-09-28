using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MusicDraft.App.ViewModels;

namespace MusicDraft.App.Views;

public partial class LibraryView : UserControl
{
    /// <summary>In-process drag format carrying library track IDs (dropped onto playlists).</summary>
    public static readonly DataFormat<string> TrackIdsFormat = DataFormat.CreateStringApplicationFormat("musicdraft-track-ids");

    /// <summary>In-process drag format carrying a playlist position (reorder within the open playlist).</summary>
    private static readonly DataFormat<string> RowFormat = DataFormat.CreateStringApplicationFormat("musicdraft-playlist-row");

    private PointerPressedEventArgs? _pressArgs;
    private Avalonia.Point _pressPoint;
    private ListBoxItem? _dragTargetItem;

    public LibraryView()
    {
        InitializeComponent();
        DropZone.AddHandler(DragDrop.DragOverEvent, OnListDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnListDrop);
        SourceList.AddHandler(DragDrop.DragOverEvent, OnSourceDragOver);
        SourceList.AddHandler(DragDrop.DropEvent, OnSourceDrop);
        SourceList.AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDragTarget(null));
        AddHandler(PointerMovedEvent, OnPointerMoved, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, (_, _) => _pressArgs = null, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private LibraryViewModel? Vm => DataContext as LibraryViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Vm?.PlayCommand.Execute(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        switch (e.Key)
        {
            case Key.Enter: vm.PlayCommand.Execute(null); e.Handled = true; break;
            case Key.F2: vm.RenameCommand.Execute(null); e.Handled = true; break;
            case Key.Delete when e.KeyModifiers == KeyModifiers.Shift: vm.DeleteFileCommand.Execute(null); e.Handled = true; break;
            case Key.Delete: vm.RemoveCommand.Execute(null); e.Handled = true; break;
            case Key.Up when e.KeyModifiers == KeyModifiers.Alt && vm.IsPlaylist: vm.MoveUpCommand.Execute(null); e.Handled = true; break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt && vm.IsPlaylist: vm.MoveDownCommand.Execute(null); e.Handled = true; break;
        }
    }

    private void OnSourceKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { IsPlaylist: true } vm) return;
        switch (e.Key)
        {
            case Key.F2: vm.RenamePlaylistCommand.Execute(null); e.Handled = true; break;
            case Key.Delete: vm.DeletePlaylistCommand.Execute(null); e.Handled = true; break;
        }
    }

    // ---------- drag rows (to a playlist on the left, or to reorder) ----------

    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressArgs = e;
        _pressPoint = e.GetPosition(this);
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pressArgs is not { } press || Vm is not { Selected: { } entry } vm) return;
        var d = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(d.X) + Math.Abs(d.Y) < 8) return;
        _pressArgs = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(TrackIdsFormat, entry.Item.Id.ToString()));
        var effects = DragDropEffects.Copy;
        if (vm.IsPlaylist)
        {
            data.Add(DataTransferItem.Create(RowFormat, entry.Position.ToString()));
            effects |= DragDropEffects.Move;
        }
        await DragDrop.DoDragDropAsync(press, data, effects);
    }

    // ---------- drops on the track list ----------

    private void OnListDragOver(object? sender, DragEventArgs e)
    {
        var t = e.DataTransfer;
        var playlist = Vm?.IsPlaylist == true;
        e.DragEffects = playlist && t.Contains(RowFormat) ? DragDropEffects.Move
            : t.Contains(DataFormat.File) ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnListDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        e.Handled = true;
        var t = e.DataTransfer;
        if (vm.IsPlaylist && t.TryGetValue(RowFormat) is { } row && int.TryParse(row, out var from))
        {
            vm.MoveTo(from, TargetIndex(e) ?? vm.PlaylistLength - 1);
            return;
        }
        var paths = MainWindow.DroppedPaths(e);
        if (paths.Count > 0) await vm.ImportDroppedAsync(paths, vm.SelectedPlaylist);
    }

    private int? TargetIndex(DragEventArgs e)
    {
        var hit = List.InputHitTest(e.GetPosition(List)) as Avalonia.Visual;
        var item = hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        return item?.DataContext is TrackEntry te ? te.Position : null;
    }

    // ---------- drops on the left pane (onto a specific playlist) ----------

    private LibrarySource? SourceAt(DragEventArgs e)
    {
        var hit = SourceList.InputHitTest(e.GetPosition(SourceList)) as Avalonia.Visual;
        return hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as LibrarySource;
    }

    private void SetDragTarget(ListBoxItem? item)
    {
        if (_dragTargetItem == item) return;
        _dragTargetItem?.Classes.Remove("drag-target");
        _dragTargetItem = item;
        item?.Classes.Add("drag-target");
    }

    private void OnSourceDragOver(object? sender, DragEventArgs e)
    {
        var t = e.DataTransfer;
        var target = SourceAt(e);
        var acceptsTracks = target is { IsAll: false } && t.Contains(TrackIdsFormat);
        var acceptsFiles = target != null && t.Contains(DataFormat.File);
        e.DragEffects = acceptsTracks || acceptsFiles ? DragDropEffects.Copy : DragDropEffects.None;

        // Highlight the hovered playlist so it's obvious where the drag will land.
        var hit = SourceList.InputHitTest(e.GetPosition(SourceList)) as Avalonia.Visual;
        var item = hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        SetDragTarget(acceptsTracks ? item : null);
        e.Handled = true;
    }

    private async void OnSourceDrop(object? sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        e.Handled = true;
        SetDragTarget(null);
        if (SourceAt(e) is not { } target) return;
        var t = e.DataTransfer;
        if (target.Playlist is { } p && t.TryGetValue(TrackIdsFormat) is { } ids)
        {
            vm.DropTracksOnPlaylist(p, ids.Split(',').Select(long.Parse).ToList());
            return;
        }
        var paths = MainWindow.DroppedPaths(e);
        if (paths.Count > 0) await vm.ImportDroppedAsync(paths, target.Playlist);
    }
}
