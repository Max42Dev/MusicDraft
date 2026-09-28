using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MusicDraft.App.Views;

/// <summary>Model setup: consent screen, then download progress. Closing it while downloading only hides it; setup continues.</summary>
public partial class SetupDialog : Window
{
    public SetupDialog() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
