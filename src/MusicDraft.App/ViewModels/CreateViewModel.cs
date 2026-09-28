using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicDraft.Core.Covers;
using MusicDraft.Core.Generation;

namespace MusicDraft.App.ViewModels;

public sealed partial class CreateViewModel : ObservableObject
{
    private const string DraftKey = "create.draft";
    private readonly AppServices _s;
    private readonly Func<Task> _promptInstall;
    private readonly Func<Task> _setUpTranscription;

    /// <param name="setUpTranscription">Shows the setup dialog for the SheetSage2 transcription model (melody from audio).</param>
    public CreateViewModel(AppServices s, GenerationHub hub, Func<Task> promptInstall, Func<Task> setUpTranscription)
    {
        _s = s;
        Hub = hub;
        _promptInstall = promptInstall;
        _setUpTranscription = setUpTranscription;
        Melody = new MelodyReferenceViewModel(s, p => Hub.IsGenerating && Hub.Current?.Request.Reference?.SourcePath == p);
        Melody.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MelodyReferenceViewModel.Effective) or nameof(MelodyReferenceViewModel.IsProbing)
                or nameof(MelodyReferenceViewModel.IsRecording))
            {
                OnPropertyChanged(nameof(HasMelody));
                OnPropertyChanged(nameof(NeedsTranscriptionSetup));
                Revalidate();
            }
        };
        Hub.PropertyChanged += OnHubChanged;
        LoadDraft();
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Errors) or nameof(ErrorText) or nameof(HasErrors) or nameof(CanCreate) or nameof(LengthText)
                or nameof(ShowLyrics) or nameof(VerseHint) or nameof(ChorusHint) or nameof(HasVerseHint)
                or nameof(HasChorusHint) or nameof(LyricsOk) or nameof(TagsIgnoredHint) or nameof(HasTagsIgnoredHint)
                or nameof(HasMelody) or nameof(NeedsTranscriptionSetup) or nameof(CanDraftLyrics) or nameof(DraftLyricsLabel)
                or nameof(HasLyricsDraftError) or nameof(LyricsAssistantReady)) return;
            Revalidate();
        };
        Revalidate();
    }

    public GenerationHub Hub { get; }

    /// <summary>Optional "Melody from audio" reference (file or microphone).</summary>
    public MelodyReferenceViewModel Melody { get; }
    public bool HasMelody => Melody.Effective != null;
    /// <summary>A reference is set but the SheetSage2 transcription model is not installed/verified yet.</summary>
    public bool NeedsTranscriptionSetup => HasMelody && Hub.CanGenerate && !Hub.Readiness.CoverPresent;

    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _instrumental;
    [ObservableProperty] private string _verse = "";
    [ObservableProperty] private string _chorus = "";
    [ObservableProperty] private double _maxSeconds = 90;

    /// <summary>Short prompt for the local lyrics assistant (defaults to the song description when empty).</summary>
    [ObservableProperty] private string _lyricsPrompt = "";
    [ObservableProperty] private bool _isDraftingLyrics;
    [ObservableProperty] private string? _lyricsDraftError;

    [ObservableProperty] private IReadOnlyList<string> _errors = [];
    [ObservableProperty] private string? _verseHint;
    [ObservableProperty] private string? _chorusHint;
    [ObservableProperty] private string? _tagsIgnoredHint;

    public int MaxSectionChars => LyricsComposer.MaxSectionChars;
    public bool ShowLyrics => !Instrumental;
    public bool HasVerseHint => VerseHint != null;
    public bool HasChorusHint => ChorusHint != null;
    public bool HasTagsIgnoredHint => TagsIgnoredHint != null;
    public bool HasLyricsDraftError => LyricsDraftError != null;
    /// <summary>The local lyrics assistant is installed and ready.</summary>
    public bool LyricsAssistantReady => Hub.LyricsAssistantReady;
    public bool CanDraftLyrics => ShowLyrics && !IsDraftingLyrics && Hub.LyricsAssistantReady;
    public string DraftLyricsLabel => IsDraftingLyrics ? "Writing lyrics…" : "Draft lyrics";
    /// <summary>Both lyric boxes are filled (or the song is instrumental).</summary>
    public bool LyricsOk => Instrumental || (VerseHint == null && ChorusHint == null);
    public bool HasErrors => Errors.Count > 0;
    public string ErrorText => string.Join("\n", Errors.Select(e => "• " + e));
    /// <summary>Only one song at a time: Create is disabled while a generation is running.</summary>
    public bool CanCreate => !HasErrors && LyricsOk && Hub.CanGenerate && !Hub.IsGenerating && !Melody.IsProbing && !Melody.IsRecording;

    /// <summary>The section-tagged lyrics string sent to the model, built in Core from the two boxes.</summary>
    public string ComposedLyrics => LyricsComposer.Compose(Verse, Chorus);
    public string LengthText => $"Up to {ExcerptRules.Format(MaxSeconds)}";
    public string LengthHint => Instrumental
        ? "Instrumentals usually fill the maximum length."
        : "The song may end sooner: its length follows the lyrics and planned score.";

    partial void OnInstrumentalChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowLyrics));
        OnPropertyChanged(nameof(LengthHint));
        OnPropertyChanged(nameof(CanDraftLyrics));
    }

    partial void OnIsDraftingLyricsChanged(bool value)
    {
        OnPropertyChanged(nameof(CanDraftLyrics));
        OnPropertyChanged(nameof(DraftLyricsLabel));
    }

    partial void OnLyricsDraftErrorChanged(string? value) => OnPropertyChanged(nameof(HasLyricsDraftError));

    partial void OnMaxSecondsChanged(double value)
    {
        var snapped = Math.Round(value / 5) * 5;
        if (Math.Abs(snapped - value) > 0.001) { MaxSeconds = snapped; return; }
        OnPropertyChanged(nameof(LengthText));
    }

    partial void OnErrorsChanged(IReadOnlyList<string> value)
    {
        OnPropertyChanged(nameof(HasErrors));
        OnPropertyChanged(nameof(ErrorText));
        OnPropertyChanged(nameof(CanCreate));
    }

    private void OnHubChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GenerationHub.CanGenerate) or nameof(GenerationHub.Readiness) or nameof(GenerationHub.SelectedProfile)
            or nameof(GenerationHub.IsGenerating) or nameof(GenerationHub.LyricsAssistantReady) or nameof(GenerationHub.IsSettingUpLyrics))
        {
            OnPropertyChanged(nameof(CanCreate));
            OnPropertyChanged(nameof(NeedsTranscriptionSetup));
            OnPropertyChanged(nameof(LyricsAssistantReady));
            OnPropertyChanged(nameof(CanDraftLyrics));
        }
    }

    private GenerationRequest BuildRequest() => new()
    {
        Title = string.IsNullOrWhiteSpace(Title) ? null : Title.Trim(),
        Description = Description,
        Lyrics = Instrumental ? "" : ComposedLyrics,
        Instrumental = Instrumental,
        MaxSeconds = MaxSeconds,
        Seed = GenerationRequest.NewSeed(),
        Reference = Melody.Effective,
    };

    private void Revalidate()
    {
        // Lyric problems are shown inline under each box, not in the error list.
        var errs = BuildRequest().Validate(includeLyrics: false).ToList();
        VerseHint = Instrumental ? null : LyricsComposer.SectionProblem("verse", Verse);
        ChorusHint = Instrumental ? null : LyricsComposer.SectionProblem("chorus", Chorus);
        TagsIgnoredHint = !Instrumental && (LyricsComposer.HasTagLines(Verse) || LyricsComposer.HasTagLines(Chorus))
            ? "Lines that are only a tag, like [Chorus], are ignored. Section tags are added for you."
            : null;
        OnPropertyChanged(nameof(HasVerseHint));
        OnPropertyChanged(nameof(HasChorusHint));
        OnPropertyChanged(nameof(HasTagsIgnoredHint));
        OnPropertyChanged(nameof(LyricsOk));
        Errors = errs;
        OnPropertyChanged(nameof(CanCreate));
    }

    [RelayCommand]
    private async Task Create()
    {
        Revalidate();
        if (HasErrors || !LyricsOk || Hub.IsGenerating) return;
        if (!Hub.CanGenerate && !Hub.AnyModelInstalled) { await _promptInstall(); return; }
        if (!Hub.CanGenerate) { _s.Error(Hub.ReadinessText + " Open Settings to set up generation."); return; }
        if (HasMelody && !Hub.Readiness.CoverPresent)
        {
            await _setUpTranscription();
            if (!Hub.Readiness.CoverPresent) return; // declined, still downloading, or failed
        }
        Melody.StopPreview();
        // Every Create uses a fresh random seed by default; ComfyUI caches identical requests.
        if (Hub.Start(BuildRequest()) != null) SaveDraft();
    }

    [RelayCommand]
    private Task SetUpTranscription() => _setUpTranscription();

    /// <summary>Asks the local lyrics model to write a verse and chorus from the short prompt (or the description).</summary>
    [RelayCommand]
    private async Task DraftLyrics()
    {
        if (IsDraftingLyrics) return;
        if (!Hub.LyricsAssistantReady)
        {
            await Hub.EnsureLyricsAssistantAsync();
            if (!Hub.LyricsAssistantReady) { LyricsDraftError = Hub.LyricsSetupError ?? "The lyrics assistant is not ready yet."; return; }
        }
        var prompt = string.IsNullOrWhiteSpace(LyricsPrompt) ? Description : LyricsPrompt;
        if (string.IsNullOrWhiteSpace(prompt)) { LyricsDraftError = "Describe the song first, or type a short prompt for the lyrics."; return; }
        IsDraftingLyrics = true;
        LyricsDraftError = null;
        try
        {
            var (verse, chorus) = await Hub.DraftLyricsAsync(prompt, Title, MaxSeconds, CancellationToken.None);
            if (verse.Length > 0) Verse = verse;
            if (chorus.Length > 0) Chorus = chorus;
            if (verse.Length == 0 && chorus.Length == 0) LyricsDraftError = "The assistant returned no lyrics. Try again or write them yourself.";
        }
        catch (Exception e)
        {
            LyricsDraftError = e.Message;
        }
        finally
        {
            IsDraftingLyrics = false;
        }
    }

    [RelayCommand]
    private async Task GoToSettings()
    {
        NavigateRequested?.Invoke(Page.Settings);
        if (!Hub.AnyModelInstalled) await _promptInstall();
    }

    public event Action<Page>? NavigateRequested;

    // ---------- draft persistence (so a crash or restart never loses typed lyrics) ----------

    /// <summary>Lyrics is the legacy single tagged box (older drafts); Verse/Chorus replace it.</summary>
    private sealed record Draft(string Title, string Description, bool Instrumental, string? Lyrics, double MaxSeconds,
        string? Verse = null, string? Chorus = null, MelodyReference? Melody = null, string? LyricsPrompt = null);

    public void SaveDraft()
    {
        try
        {
            _s.Db.SetSetting(DraftKey, System.Text.Json.JsonSerializer.Serialize(
                new Draft(Title, Description, Instrumental, null, MaxSeconds, Verse, Chorus, Melody.Effective, LyricsPrompt)));
        }
        catch (Exception) { /* non-critical */ }
    }

    private void LoadDraft()
    {
        try
        {
            if (_s.Db.GetSetting(DraftKey) is not { } json) return;
            var d = System.Text.Json.JsonSerializer.Deserialize<Draft>(json);
            if (d == null) return;
            Title = d.Title;
            Description = d.Description;
            Instrumental = d.Instrumental;
            LyricsPrompt = d.LyricsPrompt ?? "";
            if (d.Verse != null || d.Chorus != null)
            {
                Verse = d.Verse ?? "";
                Chorus = d.Chorus ?? "";
            }
            else if (!string.IsNullOrWhiteSpace(d.Lyrics))
            {
                (Verse, Chorus) = LyricsComposer.Split(d.Lyrics);
            }
            MaxSeconds = Math.Clamp(d.MaxSeconds, GenerationRequest.MinSeconds, GenerationRequest.MaxSecondsLimit);
            if (d.Melody is { SourcePath.Length: > 0 } m && File.Exists(m.SourcePath))
                _ = Melody.UseFileAsync(m.SourcePath, m.Recorded, m.Start, m.WholeClip);
        }
        catch (Exception) { }
    }
}
