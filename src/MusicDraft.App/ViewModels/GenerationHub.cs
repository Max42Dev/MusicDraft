using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Generation;
using MusicDraft.Core.Hardware;
using MusicDraft.Core.Models;

namespace MusicDraft.App.ViewModels;

public sealed record ProfileOption(ModelProfile Profile, ProfileCompatibility Compat, bool Recommended)
{
    public string Label => $"{Profile.DisplayName}{(Profile.Status == ProfileStatus.Experimental ? " · experimental" : "")}{(Recommended ? " · recommended" : "")}";
    public override string ToString() => Label;
}

/// <summary>
/// App-wide generation state: hardware, selected model profile, readiness, model setup/download progress and the
/// single current generation (there is no job queue: one song at a time).
/// All members are used on the UI thread; background events are marshalled via the dispatcher.
/// </summary>
public sealed partial class GenerationHub : ObservableObject, IAsyncDisposable
{
    private const string ProfileSetting = "generation.profile";
    private static readonly TimeSpan IdleShutdown = TimeSpan.FromMinutes(10);

    private readonly AppServices _s;
    private readonly IMusicBackend _backend;
    private readonly LyricsAssistant _lyrics;
    private readonly DispatcherTimer _idleTimer;
    private CancellationTokenSource? _setupCts;
    private CancellationTokenSource? _textSetupCts;
    private HardwareInfo? _hw;

    public GenerationHub(AppServices s, ModelStore store, GenerationService service, IMusicBackend backend, LyricsAssistant lyrics)
    {
        _s = s;
        Store = store;
        Service = service;
        _backend = backend;
        _lyrics = lyrics;
        Service.Updated += u => Dispatcher.UIThread.Post(() => OnJobUpdate(u));
        _idleTimer = new DispatcherTimer(IdleShutdown, DispatcherPriority.Background, async (_, _) =>
        {
            _idleTimer!.Stop();
            if (!Service.IsBusy) await _backend.StopAsync(); // free GPU and RAM while idle
        });
    }

    public ModelStore Store { get; }
    public GenerationService Service { get; }
    public ModelRegistry Registry => Store.Registry;

    public ObservableCollection<ProfileOption> Profiles { get; } = [];

    /// <summary>The song being created now, or the last one created this session (null before the first).</summary>
    [ObservableProperty] private CurrentGeneration? _current;
    [ObservableProperty] private bool _isGenerating;

    [ObservableProperty] private ProfileOption? _selectedProfile;
    [ObservableProperty] private string _hardwareText = "Detecting hardware…";
    [ObservableProperty] private string _recommendationText = "";
    [ObservableProperty] private bool _detected;
    [ObservableProperty] private GenerationReadiness _readiness = new(false, false, false, "Checking…");

    // setup / download progress
    [ObservableProperty] private bool _isSettingUp;
    [ObservableProperty] private string _setupStep = "";
    [ObservableProperty] private string _setupDetail = "";
    [ObservableProperty] private double _setupFraction;
    [ObservableProperty] private bool _setupIndeterminate;
    [ObservableProperty] private string? _setupError;

    public bool CanGenerate => Readiness.CanGenerate && SelectedProfile != null;
    public string ReadinessText => SelectedProfile == null
        ? (Detected ? "No compatible model profile for this PC. The player still works." : "Checking hardware…")
        : Readiness.CanGenerate ? $"Ready · {SelectedProfile.Profile.DisplayName}" : Readiness.Problem ?? "Not set up.";
    public string SelectedWarning => SelectedProfile switch
    {
        null => "",
        { Compat.Fits: false } o => "This profile may not run on this PC: " + string.Join(" ", o.Compat.Problems),
        { Profile.Status: ProfileStatus.Experimental } o => "Experimental: " + o.Profile.StatusNote,
        _ => "",
    };

    partial void OnSelectedProfileChanged(ProfileOption? value)
    {
        if (value != null) _s.Db.SetSetting(ProfileSetting, value.Profile.Id);
        RefreshReadiness();
        OnPropertyChanged(nameof(SelectedWarning));
    }

    partial void OnReadinessChanged(GenerationReadiness value)
    {
        OnPropertyChanged(nameof(CanGenerate));
        OnPropertyChanged(nameof(ReadinessText));
    }

    partial void OnDetectedChanged(bool value) => OnPropertyChanged(nameof(ReadinessText));

    public async Task DetectAsync()
    {
        _hw = await Task.Run(HardwareProbe.Probe);
        HardwareText = _hw.Describe();
        var rec = ProfileSelector.Recommend(Registry, _hw);
        RecommendationText = rec.Profile is { } rp ? $"Recommended: {rp.DisplayName}. {rec.Reason}" : rec.Reason;
        Profiles.Clear();
        foreach (var p in Registry.Profiles.OrderBy(p => p.VramClass))
            Profiles.Add(new ProfileOption(p, ProfileSelector.Check(p, _hw, Registry), p.Id == rec.Profile?.Id));
        var saved = _s.Db.GetSetting(ProfileSetting);
        SelectedProfile = Profiles.FirstOrDefault(o => o.Profile.Id == saved) ?? Profiles.FirstOrDefault(o => o.Recommended);
        Detected = true;
        RefreshReadiness();
    }

    /// <summary>True when the generator weights of at least one profile are on disk (verified or awaiting verification).</summary>
    public bool AnyModelInstalled => Registry.Profiles.Any(p => Store.IsModelPresent(Registry.File(p.GeneratorFile)));

    public void RefreshReadiness()
    {
        Readiness = SelectedProfile is { } o ? Store.Readiness(o.Profile) : new(false, false, false, "No compatible profile.");
    }

    /// <summary>True when present-but-unverified files exist that only need hashing (e.g. after an update or manual copy).</summary>
    public SetupPlan? PlanSetup(bool includeCover = false) => SelectedProfile is { } o ? Store.Plan(o.Profile, includeCover) : null;

    /// <summary>Runs downloads/verification/extraction for an approved plan. Playback keeps running; this is all off the UI thread.</summary>
    public async Task RunSetupAsync(SetupPlan plan)
    {
        if (IsSettingUp) return;
        IsSettingUp = true;
        SetupError = null;
        _setupCts = new CancellationTokenSource();
        var progress = new Progress<SetupProgress>(p =>
        {
            SetupStep = $"Step {p.StepIndex} of {p.StepCount}: {p.Step}";
            SetupIndeterminate = p.Fraction == null;
            SetupFraction = (p.Fraction ?? 0) * 100;
            SetupDetail = p.Phase switch
            {
                "Downloading" => $"{Downloader.Gb(p.Done)} of {Downloader.Gb(p.Total)}" + (p.BytesPerSecond > 0 ? $" · {p.BytesPerSecond / 1e6:0.0} MB/s" + Eta(p) : ""),
                "Verifying" => $"Verifying checksum… {Downloader.Gb(p.Done)} of {Downloader.Gb(p.Total)}",
                "Extracting" => $"Extracting… {p.Done}%",
                _ => p.Phase,
            };
        });
        try
        {
            var ct = _setupCts.Token;
            await Task.Run(() => Store.InstallAsync(plan, progress, ct), ct);
            _s.Db.SetSetting("setup.completed", DateTimeOffset.UtcNow.ToString("O"));
            _s.Info("Generation is set up. Open Create to make a song.");
        }
        catch (OperationCanceledException)
        {
            SetupError = "Setup was cancelled. Completed files are kept and partial downloads resume next time.";
        }
        catch (DownloadException e)
        {
            SetupError = e.Code == DownloadErrorCode.Network ? e.Message + " Check your internet connection and retry; the download will resume." : e.Message;
        }
        catch (Exception e)
        {
            SetupError = "Setup failed: " + e.Message;
        }
        finally
        {
            IsSettingUp = false;
            _setupCts.Dispose();
            _setupCts = null;
            if (SetupError != null) _s.Error(SetupError);
            RefreshReadiness();
        }
    }

    private static string Eta(SetupProgress p)
    {
        if (p.BytesPerSecond <= 0 || p.Total <= p.Done) return "";
        var t = TimeSpan.FromSeconds((p.Total - p.Done) / p.BytesPerSecond);
        return t.TotalHours >= 1 ? $" · {t:h\\:mm\\:ss} left" : $" · {t:m\\:ss} left";
    }

    [RelayCommand]
    private void CancelSetup() => _setupCts?.Cancel();

    // ---------- local lyrics assistant (always present; downloaded automatically) ----------

    /// <summary>True when the llama.cpp runtime and the small GGUF are installed and verified.</summary>
    public bool LyricsAssistantReady => Store.TextReadiness().Ready;
    public string LyricsAssistantName => Store.TextAssistant?.DisplayName ?? "Lyrics assistant";

    [ObservableProperty] private bool _isSettingUpLyrics;
    [ObservableProperty] private string _lyricsSetupStep = "";
    [ObservableProperty] private string _lyricsSetupDetail = "";
    [ObservableProperty] private double _lyricsSetupFraction;
    [ObservableProperty] private bool _lyricsSetupIndeterminate;
    [ObservableProperty] private string? _lyricsSetupError;

    /// <summary>
    /// Downloads the lyrics assistant if it is missing. Called automatically at startup (no consent prompt: it is a
    /// small, pinned, local-only model) and from Settings. Safe to call repeatedly.
    /// </summary>
    public async Task EnsureLyricsAssistantAsync()
    {
        if (IsSettingUpLyrics || LyricsAssistantReady) return;
        var plan = Store.PlanTextAssistant();
        if (plan.NothingToDo) { OnPropertyChanged(nameof(LyricsAssistantReady)); return; }
        IsSettingUpLyrics = true;
        LyricsSetupError = null;
        _textSetupCts = new CancellationTokenSource();
        var progress = new Progress<SetupProgress>(p =>
        {
            LyricsSetupStep = $"Step {p.StepIndex} of {p.StepCount}: {p.Step}";
            LyricsSetupIndeterminate = p.Fraction == null;
            LyricsSetupFraction = (p.Fraction ?? 0) * 100;
            LyricsSetupDetail = p.Phase switch
            {
                "Downloading" => $"{Downloader.Gb(p.Done)} of {Downloader.Gb(p.Total)}" + (p.BytesPerSecond > 0 ? $" · {p.BytesPerSecond / 1e6:0.0} MB/s" : ""),
                "Verifying" => $"Verifying checksum… {Downloader.Gb(p.Done)} of {Downloader.Gb(p.Total)}",
                "Extracting" => $"Extracting… {p.Done}%",
                _ => p.Phase,
            };
        });
        try
        {
            var ct = _textSetupCts.Token;
            await Task.Run(() => Store.InstallTextAssistantAsync(plan, progress, ct), ct);
            _s.Db.SetSetting("lyrics.setup.completed", DateTimeOffset.UtcNow.ToString("O"));
            _s.Info("The lyrics assistant is ready. Use “Draft lyrics” in Create.");
        }
        catch (OperationCanceledException)
        {
            LyricsSetupError = "The lyrics assistant download was cancelled. It will resume next time.";
        }
        catch (DownloadException e)
        {
            LyricsSetupError = e.Message;
        }
        catch (Exception e)
        {
            LyricsSetupError = "The lyrics assistant could not be set up: " + e.Message;
        }
        finally
        {
            IsSettingUpLyrics = false;
            _textSetupCts.Dispose();
            _textSetupCts = null;
            OnPropertyChanged(nameof(LyricsAssistantReady));
        }
    }

    [RelayCommand]
    private void CancelLyricsSetup() => _textSetupCts?.Cancel();

    /// <summary>Asks the local model for a verse and chorus matching the description. Throws on failure.</summary>
    /// <param name="maxSeconds">Selected song length; the verse grows with it (about one line per 15 s).</param>
    public async Task<(string Verse, string Chorus)> DraftLyricsAsync(string description, string? title, double maxSeconds, CancellationToken ct)
    {
        if (!LyricsAssistantReady)
            throw new BackendException("E_LYRICS_MISSING", "The lyrics assistant is still downloading. Try again in a moment.");
        var user = LyricsPrompt.BuildUserPrompt(description, title,
            LyricsPrompt.VerseLines(maxSeconds), LyricsPrompt.ChorusLines);
        var reply = await _lyrics.GenerateAsync(LyricsPrompt.System, user, ct);
        return LyricsPrompt.Parse(reply);
    }

    /// <summary>Stops the lyrics assistant process (frees RAM). Called on shutdown.</summary>
    public Task StopLyricsAssistantAsync() => _lyrics.StopAsync();

    // ---------- the current generation (one at a time, no queue) ----------

    /// <summary>
    /// Starts one song. Returns null (after reporting why) if generation isn't possible or a song is already being created.
    /// </summary>
    public CurrentGeneration? Start(GenerationRequest r)
    {
        if (IsGenerating) { _s.Error("A song is already being created. Wait for it to finish or cancel it."); return null; }
        if (SelectedProfile is not { } o) { _s.Error("No model profile selected."); return null; }
        _idleTimer.Stop();
        try
        {
            var job = Service.Start(r, o.Profile);
            Current = new CurrentGeneration(job.Id, r, o.Profile.DisplayName, this);
            IsGenerating = true;
            return Current;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            _s.Error(e.Message);
            return null;
        }
    }

    [RelayCommand]
    private void CancelGeneration() => Service.Cancel();

    private void OnJobUpdate(JobUpdate u)
    {
        if (Current?.Id != u.JobId) return;
        Current.Apply(u);
        if (!u.IsFinal) return;
        if (u.Status == JobStatus.Succeeded)
        {
            if (_s.Db.GetJob(u.JobId) is { } rec) Current.OutputTrackId = rec.OutputTrackId;
            _s.RaiseCatalogChanged();
            _s.Info($"\"{Current.Title}\" is ready and was added to your library.");
        }
        else if (u.Status == JobStatus.Failed) _s.Error($"\"{Current.Title}\" failed. Details are shown in Create.");
        IsGenerating = false;
        _idleTimer.Start();
    }

    internal void PlayOutput(CurrentGeneration g)
    {
        if (g.OutputTrackId is not { } id) return;
        if (_s.Db.GetTrack(id) is not { } t) { _s.Error("The generated track was removed from the library."); return; }
        _s.Playback.PlayList([t.Id]);
    }

    internal void ShowInLibrary(CurrentGeneration g) => ShowInLibraryRequested?.Invoke(g.OutputTrackId);

    /// <summary>Raised when the user asks to see the finished song in the Library.</summary>
    public event Action<long?>? ShowInLibraryRequested;

    public void OpenFolder(string path)
    {
        try
        {
            if (File.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (Directory.Exists(path)) System.Diagnostics.Process.Start("explorer.exe", $"\"{path}\"");
            else _s.Error($"Not found: {path}");
        }
        catch (Exception e) { _s.Error(e.Message); }
    }

    public string JobDir(string id) => Service.JobDir(id);

    public async ValueTask DisposeAsync()
    {
        _idleTimer.Stop();
        _setupCts?.Cancel();
        _textSetupCts?.Cancel();
        await Service.DisposeAsync();
        await _lyrics.DisposeAsync();
    }
}

/// <summary>The one song being created (or the last one created) in this session, shown inline in Create.</summary>
public sealed partial class CurrentGeneration : ObservableObject
{
    private readonly GenerationHub _hub;

    public CurrentGeneration(string id, GenerationRequest request, string profileName, GenerationHub hub)
    {
        _hub = hub;
        Id = id;
        Request = request;
        ProfileName = profileName;
    }

    public string Id { get; }
    public GenerationRequest Request { get; }
    public string ProfileName { get; }
    public string Title => Request.EffectiveTitle;

    [ObservableProperty] private JobStatus _status = JobStatus.Running;
    [ObservableProperty] private string _stage = Stages.Starting;
    [ObservableProperty] private double _fraction;
    [ObservableProperty] private bool _indeterminate = true;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private GenerationResult? _result;
    [ObservableProperty] private long? _outputTrackId;

    public bool IsRunning => Status == JobStatus.Running;
    public bool IsSucceeded => Status == JobStatus.Succeeded;
    public bool IsFailed => Status == JobStatus.Failed;
    public bool IsCancelled => Status == JobStatus.Cancelled;
    public bool HasWarnings => Result?.Warnings.Count > 0;
    public string WarningsText => Result == null ? "" : string.Join("\n", Result.Warnings.Select(w => "• " + w));
    public string StatusText => Status switch
    {
        JobStatus.Running => Stage,
        JobStatus.Succeeded => "Done — added to your library",
        JobStatus.Failed => "Failed",
        JobStatus.Cancelled => "Cancelled",
        _ => Status.ToString(),
    };
    public string ResultText => Result is { } r
        ? $"Length {Core.Covers.ExcerptRules.Format(r.DecodedSeconds)} (maximum {Core.Covers.ExcerptRules.Format(r.TargetSeconds)})" +
          (r.SemanticTruncated ? " · cut off at the limit" : "") + $" · seed {r.Seed} · took {Core.Covers.ExcerptRules.Format(r.ElapsedSeconds)}"
        : "";
    public string? OutputPath => Result?.OutputPath;

    partial void OnStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(StatusText));
    }

    partial void OnStageChanged(string value) => OnPropertyChanged(nameof(StatusText));

    partial void OnResultChanged(GenerationResult? value)
    {
        OnPropertyChanged(nameof(HasWarnings));
        OnPropertyChanged(nameof(WarningsText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(OutputPath));
    }

    public void Apply(JobUpdate u)
    {
        Status = u.Status;
        Stage = u.Stage;
        if (u.Message != null) Message = u.Message;
        Indeterminate = u.StageFraction == null && IsRunning;
        Fraction = IsSucceeded ? 100 : (u.StageFraction ?? 0) * 100;
        if (u.Result != null) Result = u.Result;
    }

    [RelayCommand] private void Play() => _hub.PlayOutput(this);
    [RelayCommand] private void ShowInLibrary() => _hub.ShowInLibrary(this);
    [RelayCommand] private void ShowFile() { if (OutputPath is { } p) _hub.OpenFolder(p); }
    [RelayCommand] private void ShowDetails() => _hub.OpenFolder(_hub.JobDir(Id));
}
