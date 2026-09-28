using Avalonia.Controls;
using Avalonia.Input;
using MusicDraft.App.ViewModels;

namespace MusicDraft.App.Views;

public partial class QueueView : UserControl
{
    public QueueView() => InitializeComponent();

    private PlayerViewModel? Vm => DataContext as PlayerViewModel;

    private void OnDoubleTapped(object? sender, TappedEventArgs e) => Vm?.PlayQueueEntryCommand.Execute(null);

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { SelectedQueueEntry: { } sel } vm) return;
        switch (e.Key)
        {
            case Key.Enter: vm.PlayQueueEntryCommand.Execute(null); e.Handled = true; break;
            case Key.Delete: vm.RemoveQueueEntryCommand.Execute(null); e.Handled = true; break;
            case Key.Up when e.KeyModifiers == KeyModifiers.Alt:
                vm.MoveQueue(sel.Position, sel.Position - 1); Reselect(sel.Position - 1); e.Handled = true; break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt:
                vm.MoveQueue(sel.Position, sel.Position + 1); Reselect(sel.Position + 1); e.Handled = true; break;
        }
    }

    private void Reselect(int pos)
    {
        if (Vm is { } vm) vm.SelectedQueueEntry = vm.QueueEntries.FirstOrDefault(x => x.Position == pos);
    }
}
