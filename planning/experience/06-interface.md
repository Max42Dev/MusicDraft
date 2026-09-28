# Interface and playlist behavior

## Window layout

- Left navigation: **Library**, **Create**, **From audio**, **Settings**. There is no Downloads/Jobs screen and no job queue (removed 2026-09-27): the user creates one song at a time.
- **Library** is a single screen that includes playlists (merged 2026-09-27).
  - Its inner left pane lists **All tracks** plus every playlist, with New, Rename and Delete.
  - The main pane shows the selected entry's tracks, with search, play, import, add to playlist and remove.
  - When a playlist is open, you can also remove tracks from it and reorder them with drag or Alt+Up/Down.
  - Drag tracks onto a playlist in the pane to add them.
- Spacious modern typography, dark/light themes, restrained accent, responsive resizing, keyboard navigation and Windows accessibility labels; no permanently blocking setup modal on player-only launch.
- Persistent bottom transport: current art/filename, play/pause, previous/next, seek timeline/time, volume/mute, shuffle indicator, repeat cycle and queue indicator. Use true playback time and media metadata, not a render-time estimate.
- Create: large description field; Instrumental/Vocals toggle; approximate length slider with minutes:seconds label; when Vocals, two mandatory plain-text boxes, **Verse** and **Chorus** (no section tags to type; the app adds them, and Create stays disabled with an inline hint while either is empty), plus optional **Draft lyrics locally** (later, downloaded on demand), edit/approve before Create; Advanced accordion for seed, model, sampling and decode settings. Model readiness/download state adjacent to button; no misleading ready state.
  - **One song at a time.** The Create button is disabled while a song is being created. A "Your song" panel beside the form shows the current song inline: stage, progress bar and **Cancel** while it runs; on success the actual vs maximum length, truncation/other warnings and **Play**, **Open in library** and **Show file**; on failure the error text. **Details** opens the song's artifact folder (score, settings, graph, engine log).
- From audio: large drop zone + Browse, local file details, timeline with 20-second draggable window **starting** within the first third (window may extend past the third) and audible preview, Whole song radio, description for target sound, lyrics/Instrumental selection, transcription score preview and Generate action.
- Model download: not a separate screen. The first-run setup dialog (also opened from Settings → Set up generation) shows each file's source, size, destination and license with an explicit consent checkbox, then switches to download/verify/extract progress in the same dialog with Cancel and Hide. Settings shows the same progress while it runs, so the dialog can be hidden. Failures show inline with **Try again**; completed files are kept and partial downloads resume.
- From audio (later) will follow the same single-generation pattern: progress, cancel, result and errors inline on its own page.

## Playback state machine

Persist tracks, playlists, membership/order, queue and basic settings in SQLite with migrations. Playback operates on track IDs; only move to next playable track. `repeat=off` stops at queue end; `repeat=all` wraps; optional `repeat=one` repeats current track. Shuffle should produce a non-repeating cycle of playable tracks and preserve navigation history when Previous is clicked. Volume changes immediately and persists (do not conflate output device master volume with app volume). Seek and Next near track end, file removal and empty playlists must remain deterministic. Drop on playlist adds tracks; drop on From audio sets reference, not automatic generation.

Keep generated and imported tracks identifiable; generated outputs have immutable job IDs, source metadata and local file paths. Allow user to choose export location, delete a library reference without deleting its file, and deliberately request file deletion with confirmation. Show errors for missing tracks and changed external files.
