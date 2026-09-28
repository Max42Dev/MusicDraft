namespace MusicDraft.App.Services;

/// <summary>Window-level interactions the view-models need, implemented by the main window.</summary>
public interface IDialogs
{
    Task<IReadOnlyList<string>> PickAudioFilesAsync();
    /// <summary>Picks one local audio file (MP3/WAV/FLAC/M4A) to use as a melody reference.</summary>
    Task<string?> PickReferenceAudioAsync();
    Task<string?> PickFolderAsync(string title);
    Task<bool> ConfirmAsync(string title, string message, string confirmText);
    Task<string?> PromptAsync(string title, string label, string initial);
}
