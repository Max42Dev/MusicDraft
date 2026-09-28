using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Models;

namespace MusicDraft.App.ViewModels;

public sealed record SetupRow(SetupItem Item)
{
    public string Name => Item.DisplayName;
    public string Size => Downloader.Gb(Item.DownloadBytes);
    public string Source => Item.SourcePage;
    public string Destination => Item.Destination;
    public string License => Item.License;
    public string LicenseUrl => Item.LicenseUrl;
    public string State => Item.AlreadyDownloaded ? "Already downloaded and verified"
        : Item.PresentUnverified ? "Already on disk – will be checked, not downloaded"
        : "Will be downloaded";
}

/// <summary>
/// Model setup dialog content. First the consent screen: exactly what will be fetched, from where, how large, where it goes
/// and on what terms. After the user agrees, the same dialog shows download/verify/extract progress (there is no separate
/// downloads screen). When created with profile options, the user first picks which model to install.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    private readonly Func<ModelProfile, SetupPlan>? _planner;
    private readonly Func<SetupViewModel, Task> _install;

    /// <summary>Fixed plan for an already selected profile.</summary>
    public SetupViewModel(SetupPlan plan, GenerationHub hub, Func<SetupViewModel, Task> install)
    {
        _plan = plan;
        Hub = hub;
        _install = install;
        Options = [];
    }

    /// <summary>Lets the user choose the model profile to install; the plan is rebuilt for each choice.</summary>
    public SetupViewModel(IReadOnlyList<ProfileOption> options, ProfileOption initial, Func<ModelProfile, SetupPlan> planner,
        GenerationHub hub, Func<SetupViewModel, Task> install)
    {
        _planner = planner;
        Hub = hub;
        _install = install;
        Options = options;
        _selectedOption = initial;
        _plan = planner(initial.Profile);
    }

    /// <summary>Progress (step, bytes, speed, errors) is bound straight from the hub.</summary>
    public GenerationHub Hub { get; }

    /// <summary>True once the user agreed and installation began; the dialog then switches to progress.</summary>
    [ObservableProperty] private bool _started;
    public bool ShowConsent => !Started;
    public bool Installing => Started && Hub.IsSettingUp;
    public bool Finished => Started && !Hub.IsSettingUp;
    public bool Succeeded => Finished && Hub.SetupError == null;
    public bool FailedOrCancelled => Finished && Hub.SetupError != null;

    partial void OnStartedChanged(bool value) => RaiseProgressState();

    private void RaiseProgressState()
    {
        foreach (var n in new[] { nameof(ShowConsent), nameof(Installing), nameof(Finished), nameof(Succeeded), nameof(FailedOrCancelled) })
            OnPropertyChanged(n);
    }

    private void OnHubChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GenerationHub.IsSettingUp) or nameof(GenerationHub.SetupError)) RaiseProgressState();
    }

    [RelayCommand]
    private async Task Install()
    {
        if (!CanStart || Started) return;
        Hub.PropertyChanged += OnHubChanged;
        Started = true;
        try { await _install(this); }
        finally
        {
            Hub.PropertyChanged -= OnHubChanged;
            RaiseProgressState();
        }
    }

    /// <summary>After a failure or cancel: go back to the consent screen to try again (completed files are kept).</summary>
    [RelayCommand]
    private void Retry()
    {
        if (!Finished) return;
        Plan = _planner != null && SelectedOption != null ? _planner(SelectedOption.Profile) : Hub.Store.Plan(Plan.Profile, includeCover: false);
        Started = false;
    }

    public IReadOnlyList<ProfileOption> Options { get; }
    public bool HasChoices => Options.Count > 0;

    [ObservableProperty] private SetupPlan _plan;
    [ObservableProperty] private ProfileOption? _selectedOption;

    public IReadOnlyList<SetupRow> Rows => Plan.Items.Select(i => new SetupRow(i)).ToList();

    public string OptionWarning => SelectedOption switch
    {
        { Compat.Fits: false } o => "This model may not run on this PC: " + string.Join(" ", o.Compat.Problems),
        _ => "",
    };

    partial void OnSelectedOptionChanged(ProfileOption? value)
    {
        if (value == null || _planner == null) return;
        Plan = _planner(value.Profile);
        Accepted = false; // terms and sizes changed: consent must be given again
        OnPropertyChanged(nameof(OptionWarning));
    }

    partial void OnPlanChanged(SetupPlan value)
    {
        foreach (var n in new[] { nameof(Rows), nameof(Heading), nameof(Summary), nameof(DiskText), nameof(DiskProblem),
                     nameof(ProfileNote), nameof(Terms), nameof(CanStart) })
            OnPropertyChanged(n);
    }

    public string Heading => HasChoices ? "Choose a music model to install"
        : Plan.NothingToDo ? "Generation is already set up" : $"Set up {Plan.Profile.DisplayName}";
    public string Summary =>
        (Plan.DownloadBytes > 0 ? $"Download {Downloader.Gb(Plan.DownloadBytes)}" : "Nothing to download") +
        (Plan.VerifyBytes > 0 ? $", verify {Downloader.Gb(Plan.VerifyBytes)} already on disk" : "") +
        (Plan.NeedsExtraction ? $", then install the runtime (≈{Downloader.Gb(Plan.ExtractedBytes)} on disk; the archive is deleted afterwards)" : "") + ".";
    public string DiskText => $"Needs about {Downloader.Gb(Plan.RequiredFreeBytes)} free; {Downloader.Gb(Plan.FreeBytes)} available.";
    public bool DiskProblem => !Plan.EnoughDisk;
    public string ProfileNote => Plan.Profile.StatusNote;
    public string Terms =>
        $"The YuE2 model weights are licensed {Plan.Profile.License}. " +
        "The runtime (ComfyUI) is GPL-3.0 and runs as a separate local process that only listens on this PC. " +
        "Music is generated entirely on this computer; your prompts, lyrics and audio are never uploaded.";

    [ObservableProperty] private bool _accepted;

    public bool CanStart => Accepted && Plan.EnoughDisk;
    partial void OnAcceptedChanged(bool value) => OnPropertyChanged(nameof(CanStart));

    [RelayCommand]
    private static void OpenLink(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return;
        try { Process.Start(new ProcessStartInfo(u.ToString()) { UseShellExecute = true }); } catch { }
    }
}
