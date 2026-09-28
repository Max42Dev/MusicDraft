# Implementation progress

## Phase 0 — done (2026-09-27)

See `engineering/09-spike-results.md`.

## Phase 1 — player slice: implemented (2026-09-27)

Build/test: `dotnet build MusicDraft.sln`, `dotnet test tests\MusicDraft.Tests` (64 tests, all pass on Windows Server 2022 with Media Foundation decoders present).
Run: `src\MusicDraft.App\bin\Debug\net10.0-windows\MusicDraft.exe [files or folders]`. Set `MUSICDRAFT_HOME` to use an isolated data folder.

- **Core**: `PlaybackController` (queue + device + persisted volume/mute/shuffle/repeat/queue position; stale end-event guard; skips missing/undecodable tracks; seek-to-end = natural end; Previous restarts after 3 s), `LibraryImporter` (background, cancellable, files referenced in place), `IAudioPlayer.Close()` releases file handles before deletion. DXGI adapter struct made blittable.
- **App** (Avalonia 12, MVVM Toolkit): sidebar nav, Library (search, add files/folder, drag-drop from Explorer, context menu: play, queue, add to playlist, rename, show in folder, remove reference, delete file with confirmation, missing/changed-on-disk markers), Playlists (create/rename/delete, drag from Library or Explorer, drag/Alt+arrow reorder), queue panel, bottom transport with true stream position, keyboard shortcuts and media keys, dismissable notifications, Settings with DXGI hardware detection and profile recommendation (no downloads). Create / From audio / Jobs pages state plainly they are not available yet.
- **Verified manually**: launches with no network, model or GPU; imports and plays WAVs; the UI renders in the light theme. (The Create, Jobs and Settings pages were replaced in Phase 2; see below.)
- **Tested**: empty queue, missing and damaged files (MP3/WAV/FLAC), repeat off/all/one, shuffle cycle + history, reorder, concurrent DB writers, DB reopen, real device playback/end/stop/close.

### Not yet verified

- Dark theme appearance, screen-reader pass, high DPI.
- "Playback remains responsive during background downloads" — needs the Phase 2 downloader.
- MP3 decode on a machine with an MP3 encoder present (the MP3 happy-path test self-skips without one; damaged-MP3 path is tested).

## Phase 2 — generation slice: core done and GPU-verified, app UI built but not yet run (2026-09-27)

Build: clean (0 warnings). Tests: `dotnet test tests\MusicDraft.Tests` → **104 pass** (the GPU test self-skips).
Real GPU test (opt-in): set `MUSICDRAFT_GPU_TESTS=1` and `MUSICDRAFT_GPU_HOME=%LOCALAPPDATA%\MusicDraft`, then
`dotnet test tests\MusicDraft.Tests --filter FullyQualifiedName~ComfyIntegration`.

### Done

- **Core `Generation/`**
  - `GenerationRequest`: validated; defaults are the upstream node defaults; max length 20–180 s; vocals need `[Section]` tags (since added by `LyricsComposer`, see below); instrumental needs a planned score.
  - `ComfyGraph`: flat API graphs matching the spike (text→FLAC, score-only). `SaveAudioAdvanced.format` is sent as a plain `"flac"` string. Readers for `/history` outputs.
  - `ComfyWorker`: hidden ComfyUI launched with the flags from `09-spike-results.md` on a random loopback port. It is killed with the app (Win32 job object). Progress arrives by websocket, with `/history` polling as a fallback. Cancel removes the prompt from the queue and interrupts it; if it hasn't stopped after 45 s, the engine is restarted. Engine logs go to `logs\comfyui-*.log`.
  - `GenerationService`: one job at a time; every job is saved to the `jobs` table before it starts.
    - Stages: Planning score → Generating music → Synthesizing audio → Decoding audio → Saving.
    - Instrumental recipe: plan a score from section tags → `md_instrumental.py` (run by the embedded Python) → render.
    - The log is scanned for the upstream "token budget" warnings, so `semanticTruncated` and `scoreTruncated` are reported.
    - Decoded length is measured and silence is detected. The file is copied into `library\` under a unique name, then a Generated track is added.
    - Artifacts per job in `jobs\{id}\`: request, graph(s), score.abc, engine.log, manifest.json, error.json.
    - OOM becomes `E_VRAM`, with clear text. Jobs still running at a restart become `Interrupted`. The disk-space check runs before starting.
- **Core `Downloads/`**
  - `Downloader`: resumable Range requests, 3 retries, size and SHA-256 checks, `.part` file then an atomic move, disk space checked first.
  - `ModelStore`: builds the setup plan (extractor, runtime archive, checkpoint) from the registry. It remembers verified hashes by size and timestamp, so large files are only hashed once. Files already on disk are verified instead of downloaded again. The runtime is extracted with the pinned `7zr.exe` 26.03 (Windows tar can't read LZMA) into a short staging path, then renamed into place and version-checked; the archive is deleted afterwards.
  - Registry additions: runtime `version` and `extractor` (7zr.exe, sha256 pinned).
- **Worker scripts**: `worker\**\*.py` are copied to the output under `worker\` (see `WorkerScripts`).
- **App** (code written, compiles, **not yet launched**)
  - `GenerationHub`: profiles, readiness, setup progress, job list, idle engine shutdown after 10 min.
  - Consent dialog `SetupDialog`: every file with its source, size, destination and license, plus a free-disk check and an explicit checkbox.
  - Settings generation card: profile picker, Set up / Verify / Remove model / License.
  - `CreateView`: description, title, Vocals/Instrumental, lyrics editor with a template, maximum length slider, Advanced options (seed and lock, score mode, sampling, steps, tiled decode, reset), payload preview, errors shown as you type, recent-jobs sidebar. Drafts are saved.
  - `JobsView`: progress, cancel/retry/regenerate, warnings, target vs actual length, show file, job details folder.
- **Real GPU run, NVIDIA L4, int8 8 GB profile**: all through `GenerationService` + `ComfyWorker`.
  - 30 s vocal song → FLAC in 54 s.
  - Cancel during music generation → Cancelled, no track added.
  - 40 s instrumental → FLAC in 39 s.
  - Both songs hit their length ceiling, and truncation was flagged correctly.
- **Local runtime moved**: renamed `%LOCALAPPDATA%\MusicDraft\runtime\ComfyUI_windows_portable` → `comfyui-0.37.0` (the path in the registry). The models already in `%LOCALAPPDATA%\MusicDraft\models` are reused.

### Incident: concurrent edits (2026-09-27, about 08:40–08:55)

A second agent (OpenCode) edited the workspace while this work was in progress. Resolved as follows:
- Removed `Models/ModelManager.cs`: it duplicated `ModelStore` and expected a bundled `tools\7zr.exe`.
- Restored `Downloads/Downloader.cs`.
- Repaired garbled blocks in `ModelStore.cs` and `GenerationService.cs`.
- **Kept** its `registry.json` change: the bf16 16 GB profile is now `supported`, backed by the bf16 smoke test in `09-spike-results.md`. The test expectation was updated to match.
- **Before continuing, run only one agent on this repo.**

## UI simplification: Library and Playlists merged (2026-09-27)

- A single **Library** screen replaces the separate Library and Playlists screens. There are now five nav entries: Library, Create, From audio, Downloads & jobs, Settings.
- **Left pane:** "All tracks" plus every playlist. "New playlist" is at the bottom. You can rename or delete a playlist from the header buttons, the context menu, or F2/Delete.
- **Main pane:** shows the tracks of the selected entry, with search, Play, Add files/folder, and the import progress bar.
  - The track context menu has play, queue, add to playlist, rename, show in folder, remove from library, and delete file.
  - When a playlist is open, the menu also has move up/down and remove from playlist.
  - Delete removes the track from the open playlist, or from the library when All tracks is shown.
- **Drag and drop:**
  - Drag tracks onto a playlist in the left pane to add them.
  - Drag rows to reorder them within an open playlist. Alt+Up/Down also reorders.
  - Drop Explorer files on the list or on a playlist to import them, and also add them to that playlist.
- Removed `PlaylistsViewModel.cs`, `Views/PlaylistsView.axaml(.cs)` and `Page.Playlists`. `LibraryViewModel` now uses `TrackEntry` rows, so position-aware playlist edits work. Core is unchanged.
- The build has 0 warnings and 0 errors. 104/104 tests pass. A UI Automation smoke launch found the merged view and no Playlists nav entry.

## UI simplification: one song at a time, no job queue (2026-09-27)

- **Removed** the Downloads & jobs screen (`JobsView`, `Page.Jobs`) and the job queue. The nav now has four entries: Library, Create, From audio, Settings.
- **Core** `GenerationService.Start` runs a single generation and throws `InvalidOperationException` if one is already running; `Cancel()` takes no job ID and cancels the running one. Jobs are still recorded in the `jobs` table with their artifact folder.
- **App** `GenerationHub.Current` (`CurrentGeneration`) is the song being created now, or the last one this session; `IsGenerating` blocks a second start. `CancelGenerationCommand` cancels it.
- **Create** has a "Your song" panel beside the form (replacing the recent-jobs sidebar): title, stage, progress bar and Cancel while running; on success actual vs maximum length, warnings, Play / Open in library / Show file; the error text on failure; Details opens the artifact folder. The Create button is disabled while a song is being created, with a note saying so.
- **Model downloads** show their progress in the setup dialog (`SetupDialog`) and in Settings rather than on a separate screen.
- "Open in library" navigates to Library and reveals the new track (`ShowInLibraryRequested`).

## Lyrics input: separate Verse and Chorus boxes (2026-09-27)

- Create replaces the single tagged lyrics editor (and its "Insert section template" button and tag help text) with two **mandatory** plain-text boxes, **Verse** and **Chorus**. Users no longer type `[Section]` tags.
- **Core** `Generation/LyricsComposer` (pure, unit-tested) builds the lyrics sent to YuE2: `"[Verse]\n{verse lines}\n\n[Chorus]\n{chorus lines}"`. Lines are trimmed, blank lines dropped, and lines that are solely a bracket tag (e.g. `[Chorus]`) are stripped. Limit: 2,500 characters per box (combined stays under `GenerationRequest.MaxLyricsChars` = 6,000).
- Create is disabled and an inline hint shows under each empty box (a specific message if a box held only a tag line); a subtle note appears when tag lines were ignored. The payload preview shows the composed string. `GenerationRequest.Validate(includeLyrics: false)` lets the UI report lyric problems per box; `GenerationService` still validates the composed lyrics.
- Drafts store `Verse`/`Chorus`; older drafts with one tagged `Lyrics` string are split into verse/chorus on load (`LyricsComposer.Split`).
- From audio is still a placeholder (no lyrics input); it will get the same two boxes when it is folded into Create.
- Build: 0 warnings, 0 errors. Tests: 122/122 pass. Smoke launch with an empty `MUSICDRAFT_HOME` ran without error.

## Melody from audio folded into Create (2026-09-27)

- **Removed** the From audio page. The nav now has three entries: Library, Create, Settings (`Page { Library, Create, Settings }`).
- **Create** has an optional "Melody from audio" section (`MelodyReferenceViewModel`), next to the style and Verse/Chorus inputs:
  - **File:** choose or drop an MP3, WAV, FLAC or M4A file. Drops on this section become the reference instead of a Library import.
  - **Microphone:** Record/Stop with a level meter and elapsed time (`Core/Audio/MicRecorder`). Recordings are saved under the app data `Recordings` folder, and are deleted when replaced or cleared unless a running generation still uses them.
  - Sources must be 5 s to 10 min long, and silent audio is rejected. Sources over 20 s use a 20-second excerpt that starts in the first third, or the whole clip if chosen (`ReferenceRules`, `ExcerptRules`). There is a Preview button that pauses the main player.
- **Pipeline** (`GenerationService`): decode the reference to a 48 kHz WAV in the engine input folder → `LoadAudio` → optional `TrimAudioDuration` → `SheetSage2AudioToABC(melody)` → YuE2 `melody` mode, using the same Verse/Chorus lyrics (or instrumental). The score setting is ignored while a melody is set. The engine's working copy is deleted afterwards.
- If the SheetSage2 transcription model is missing, Create shows a setup prompt (about 1.4 GB, consent first).
- Build: 0 warnings, 0 errors. Tests: 151/151 pass.

## Local lyrics assistant: in-process LLamaSharp (2026-09-27)

- **Create** gains a **Draft lyrics for me** box above the Verse/Chorus editors: a short prompt (defaults to the song description) and a button that fills both boxes with generated lyrics you can edit. Errors show inline; the button is disabled while drafting.
- **Core** `Generation/LyricsAssistant` runs the model **in-process** with **LLamaSharp 0.27.0** (`LLamaSharp` + `LLamaSharp.Backend.Cpu` NuGet packages) — no server process, no HTTP, no network. The model is loaded on first use and unloaded after 5 minutes idle to free RAM. `Generation/LyricsPrompt` (pure, unit-tested) builds the instruction and parses the reply into Verse/Chorus.
- **Model**: pinned **Gemma 4 E2B Q4_0** GGUF (`ggml-org/gemma-4-E2B-it-GGUF`, revision `b4243c15…`, 2.84 GB, SHA-256 pinned) in `registry.json` under `textAssistant`. It is **always present**: the app downloads it automatically at startup (no consent prompt — small, pinned, local-only) and Settings has Download/Verify/Remove.
- **Prompt format**: Gemma 4's chat template uses Jinja features LLamaSharp cannot render (`MissingTemplateException`), so the assistant builds the prompt directly with the model's turn tokens (`<|turn>system … <turn|>`). Thinking is not requested, so the reply is just the lyrics. Verified end-to-end against the real GGUF.
- **Tests**: `LyricsPromptTests` + `TextAssistantRegistryTests` (parsing, prompt, registry pinning). Opt-in real-model test: set `MUSICDRAFT_LYRICS_TESTS=1` and `MUSICDRAFT_GPU_HOME`, then `dotnet test --filter FullyQualifiedName~LyricsAssistantIntegration`. Build: 0 warnings, 0 errors. Tests: 162/162 pass.

## Next steps (in order)

1. **Launch the app and fix the new UI end to end** (the generation UI has not been run against a real model yet):
   - Settings shows detection, profile, readiness and the setup dialog.
   - Create (Verse + Chorus) → "Your song" panel progress → the finished track appears in the Library and plays.
   - Test cancel from the panel and creating again afterwards; check dark theme and window resizing.
2. **Fresh-machine setup test**: use an empty `MUSICDRAFT_HOME`.
   - Consent → download 7zr → runtime (1.9 GB) → checkpoint → extraction → Ready.
   - Check that cancelling and resuming works, and that a damaged download is rejected by the hash check.
   - Confirm MAX_PATH warnings from 7-Zip (exit code 1) are tolerated.
   - Record real download and extraction times.
3. **Remaining Phase 2 checks** (07-delivery): lost network during download, low disk, low free VRAM warning, restart mid-generation marks the job Interrupted (and the draft is kept), bad prompt, interrupted decode. Also check that playback stays smooth during a download and during generation.
4. **Generation polish**:
   - Show the planned score (ABC) for review before rendering (optional "review score" step).
   - Persist which profile each track was made with in the Library (or show it in a track details panel).
   - Let the user choose the output folder (Settings; `AppPaths` already supports an override).
   - Add a VRAM check before each generation, using `GetStatusAsync`.
  - Optional extra lyric sections (e.g. a second verse or bridge) if testing shows longer songs need them; keep Verse + Chorus the mandatory minimum.
5. **Phase 3, covers** (`04-covers.md`): the melody-from-audio UI and pipeline are in Create; what remains:
   - Run it against the real engine (file and microphone), and add an optional ABC review step before rendering.
   - Scan the log for "SheetSage2 reached its token limit".
   - Test with 10/30/60/180 s inputs, a damaged MP3 and a silent excerpt, using synthetic audio only.
6. **Phase 4 / release**:
   - Inno Setup per-user installer, with `worker\` included.
   - Third-party notices: ComfyUI GPL-3.0 (separate process), YuE2 CC BY-NC 4.0, 7-Zip, NAudio, Avalonia.
   - Privacy statement; smoke test on a physical 8 GB card (and a 4 GB card, if claimed).
   - Third-party notices: add LLamaSharp (MIT) and the Gemma Terms of Use for the lyrics assistant.
