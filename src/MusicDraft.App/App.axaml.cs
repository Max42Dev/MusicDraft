using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using MusicDraft.App.ViewModels;
using MusicDraft.Core;
using MusicDraft.Core.Audio;
using MusicDraft.Core.Catalog;
using MusicDraft.Core.Downloads;
using MusicDraft.Core.Generation;
using MusicDraft.Core.Models;
using MusicDraft.Core.Playback;

namespace MusicDraft.App;

public partial class App : Application
{
    private CatalogDb? _db;
    private IAudioPlayer? _player;
    private PlaybackController? _playback;
    private MainViewModel? _vm;
    private GenerationHub? _hub;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Player-only startup: no network, model or GPU is touched here. The engine starts on the first Create.
            var paths = new AppPaths(AppPaths.DefaultRoot);
            paths.EnsureCreated();
            _db = new CatalogDb(paths.Database);
            _player = new MediaFoundationPlayer();
            _playback = new PlaybackController(_db, _player, a => Dispatcher.UIThread.Post(a));

            var registry = ModelRegistry.LoadEmbedded();
            var store = new ModelStore(paths, registry, new Downloader(Downloader.CreateClient()));
            var runtime = registry.Runtimes[0];
            var worker = new ComfyWorker(new ComfyWorkerOptions(store.PythonExe(runtime), store.MainPy(runtime), paths.Models,
                paths.WorkerState, paths.Logs, WorkerScripts.InstrumentalScript));
            var generation = new GenerationService(_db, paths, registry, store.Readiness, worker);
            var lyrics = new LyricsAssistant(new LyricsAssistantOptions(store.TextModelPath));

            var window = new MainWindow();
            MainViewModel? vm = null;
            var services = new AppServices(paths, _db, _playback, window,
                (msg, err) => Dispatcher.UIThread.Post(() => vm?.Notify(msg, err)));
            _hub = new GenerationHub(services, store, generation, worker, lyrics);
            vm = _vm = new MainViewModel(services, _hub, window.ShowSetupAsync);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => Shutdown();

            window.Opened += async (_, _) =>
            {
                await _hub.DetectAsync();
                // The lyrics assistant is small, pinned and local-only: fetch it automatically so "Draft lyrics" always works.
                _ = _hub.EnsureLyricsAssistantAsync();
                // No model installed: ask the user to choose and install one. Nothing downloads without consent;
                // declining keeps player-only use.
                if (!_hub.AnyModelInstalled)
                    await vm.Settings.PromptModelInstallAsync();
                else if (_db.GetSetting("setup.offered") == null && _hub.SelectedProfile != null && !_hub.Readiness.CanGenerate)
                {
                    _db.SetSetting("setup.offered", DateTimeOffset.UtcNow.ToString("O"));
                    vm.Notify($"Generation with {_hub.SelectedProfile.Profile.DisplayName} is not fully set up. Open Settings → Set up generation.", false);
                }
            };

            // "Open with MusicDraft" / command-line files: add to library and play.
            var files = (desktop.Args ?? []).Where(a => File.Exists(a) || Directory.Exists(a)).ToList();
            if (files.Count > 0)
                window.Opened += async (_, _) =>
                {
                    await vm.Library.ImportAsync(files);
                    var ids = LibraryImporter.Expand(files).Select(f => _db.GetTrackByPath(f)?.Id).OfType<long>().ToList();
                    if (ids.Count > 0) _playback.PlayList(ids);
                };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Shutdown()
    {
        _vm?.Dispose();
        // Stops the engine process; a song still being created is marked Interrupted on next start.
        try { _hub?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(15)); } catch { }
        _playback?.Dispose();
        _player?.Dispose();
        _db?.Dispose();
    }
}
