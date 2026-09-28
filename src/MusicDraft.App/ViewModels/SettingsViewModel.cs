using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicDraft.Core.Downloads;

namespace MusicDraft.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Func<SetupViewModel, Task> _showSetup;

    /// <param name="showSetup">Shows the setup dialog (consent, then progress) and completes when the user closes it.</param>
    public SettingsViewModel(AppServices s, GenerationHub hub, Func<SetupViewModel, Task> showSetup)
    {
        _s = s;
        Hub = hub;
        _showSetup = showSetup;
        DataFolder = s.Paths.Root;
        LibraryFolder = s.Paths.Output;
    }

    public GenerationHub Hub { get; }
    public string DataFolder { get; }
    public string LibraryFolder { get; }

    [ObservableProperty] private bool _busy;

    /// <summary>Shows the consent screen for the selected profile; if accepted, the same dialog runs setup and shows progress.</summary>
    [RelayCommand]
    private async Task SetUp()
    {
        if (Hub.IsSettingUp) return;
        if (!Hub.AnyModelInstalled) { await PromptModelInstallAsync(); return; }
        var plan = Hub.PlanSetup(includeCover: false);
        if (plan == null) { _s.Error("There is no compatible model profile to set up on this PC."); return; }
        if (plan.NothingToDo) { Hub.RefreshReadiness(); _s.Info("Generation is already set up."); return; }
        await _showSetup(new SetupViewModel(plan, Hub, InstallAsync));
    }

    /// <summary>
    /// When no model is installed, asks the user to choose one and shows its download/consent screen.
    /// Nothing is downloaded unless the user accepts; declining keeps the app usable as a player.
    /// </summary>
    public async Task PromptModelInstallAsync()
    {
        if (_prompting || Hub.IsSettingUp || Hub.AnyModelInstalled || Hub.Profiles.Count == 0) return;
        _prompting = true;
        try
        {
            var initial = Hub.SelectedProfile ?? Hub.Profiles.FirstOrDefault(o => o.Recommended) ?? Hub.Profiles[0];
            await _showSetup(new SetupViewModel(Hub.Profiles.ToList(), initial, p => Hub.Store.Plan(p, includeCover: false), Hub, InstallAsync));
        }
        finally { _prompting = false; }
    }

    private bool _prompting;

    /// <summary>
    /// Melody from audio needs the SheetSage2 transcription encoder: shows the consent/progress dialog for it (plus anything
    /// else the selected profile still lacks). Files already on disk are only verified.
    /// </summary>
    public async Task SetUpTranscriptionAsync()
    {
        if (Hub.IsSettingUp) { _s.Info("A model download is already running; see Settings for progress."); return; }
        var plan = Hub.PlanSetup(includeCover: true);
        if (plan == null) { _s.Error("There is no compatible model profile to set up on this PC."); return; }
        if (plan.NothingToDo) { Hub.RefreshReadiness(); return; }
        await _showSetup(new SetupViewModel(plan, Hub, InstallAsync));
    }

    /// <summary>Runs after the user accepted in the setup dialog. Keeps running if the dialog is closed; Settings shows progress too.</summary>
    private async Task InstallAsync(SetupViewModel vm)
    {
        if (vm.SelectedOption is { } chosen) Hub.SelectedProfile = chosen;
        _s.Db.SetSetting("setup.consent." + vm.Plan.Profile.Id, DateTimeOffset.UtcNow.ToString("O"));
        await Hub.RunSetupAsync(vm.Plan);
    }

    [RelayCommand]
    private async Task Verify()
    {
        if (Hub.SelectedProfile is not { } o) return;
        Busy = true;
        try
        {
            // Also verify the transcription encoder when it is on disk (melody from audio).
            var cover = Hub.Store.IsModelPresent(Hub.Registry.File(o.Profile.CoverFile));
            var problems = await Task.Run(() => Hub.Store.VerifyAsync(o.Profile, includeCover: cover, null, default));
            if (problems.Count == 0) _s.Info("Model files verified.");
            else _s.Error(string.Join(" ", problems));
        }
        catch (Exception e) { _s.Error("Verification failed: " + e.Message); }
        finally { Busy = false; Hub.RefreshReadiness(); }
    }

    [RelayCommand]
    private async Task RemoveModel()
    {
        if (Hub.SelectedProfile is not { } o) return;
        if (Hub.IsGenerating) { _s.Error("A song is being created. Wait for it to finish or cancel it, then remove the model."); return; }
        var f = Hub.Registry.File(o.Profile.GeneratorFile);
        if (!await _s.Dialogs.ConfirmAsync("Remove model?",
                $"Delete {f.DisplayName} ({Downloader.Gb(f.SizeBytes)}) from\n{Hub.Store.ModelPath(f)}?\n\nYou can download it again later. Generated songs are kept.",
                "Remove model")) return;
        Hub.Store.RemoveModel(f);
        Hub.RefreshReadiness();
        _s.Info("Model removed.");
        await PromptModelInstallAsync();
    }

    [RelayCommand]
    private Task Detect() => Hub.DetectAsync();

    // ---------- local lyrics assistant ----------

    public string LyricsAssistantName => Hub.LyricsAssistantName;
    public bool LyricsAssistantReady => Hub.LyricsAssistantReady;
    public string LyricsAssistantStatus => Hub.LyricsAssistantReady
        ? "Installed and ready. Draft lyrics from the Create screen."
        : Hub.IsSettingUpLyrics ? "Downloading…" : "Not installed yet. It downloads automatically; you can also start it here.";

    [RelayCommand]
    private async Task SetUpLyricsAssistant()
    {
        await Hub.EnsureLyricsAssistantAsync();
        OnPropertyChanged(nameof(LyricsAssistantReady));
        OnPropertyChanged(nameof(LyricsAssistantStatus));
    }

    [RelayCommand]
    private async Task VerifyLyricsAssistant()
    {
        Busy = true;
        try
        {
            var problems = await Task.Run(() => Hub.Store.VerifyTextAssistantAsync(null, default));
            if (problems.Count == 0) _s.Info("Lyrics assistant files verified.");
            else _s.Error(string.Join(" ", problems));
        }
        catch (Exception e) { _s.Error("Verification failed: " + e.Message); }
        finally { Busy = false; OnPropertyChanged(nameof(LyricsAssistantReady)); OnPropertyChanged(nameof(LyricsAssistantStatus)); }
    }

    [RelayCommand]
    private async Task RemoveLyricsAssistant()
    {
        if (!await _s.Dialogs.ConfirmAsync("Remove lyrics assistant?",
                $"Delete the local lyrics model from\n{Hub.Store.TextModelPath}?\n\nIt will be downloaded again automatically the next time you start MusicDraft.",
                "Remove")) return;
        Hub.Store.RemoveTextAssistant();
        OnPropertyChanged(nameof(LyricsAssistantReady));
        OnPropertyChanged(nameof(LyricsAssistantStatus));
        _s.Info("Lyrics assistant removed.");
    }

    [RelayCommand]
    private void OpenDataFolder() => Open(DataFolder);

    [RelayCommand]
    private void OpenLibraryFolder() { Directory.CreateDirectory(LibraryFolder); Open(LibraryFolder); }

    [RelayCommand]
    private void OpenLicense()
    {
        if (Hub.SelectedProfile?.Profile.LicenseUrl is { } u && u.StartsWith("https://"))
            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
    }

    private void Open(string path)
    {
        try { Process.Start("explorer.exe", $"\"{path}\""); } catch (Exception e) { _s.Error(e.Message); }
    }
}
