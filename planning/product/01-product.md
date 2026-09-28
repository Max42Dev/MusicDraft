# Product contract and scope

## Core journeys

1. **Listen:** add MP3, WAV or FLAC files via picker/drop, build and persist ordered playlists, play/pause/seek, skip/back, volume/mute, shuffle and repeat (`off`, `all`, optionally `one`). Missing or moved files appear as unavailable instead of crashing or silently disappearing. Imported files stay in their original location; generating creates a new local track and adds it to a chosen playlist.
2. **Create:** write a song/style description, choose an approximate desired length, choose Instrumental or Vocals, and click Create. Vocal mode allows typed/edited lyrics; never invent a full lyric without asking or showing what will be sent. Show stages, estimated progress (not fictional percent), cancel, failure/retry and playable result.
3. **Create from audio:** drop/pick a local MP3/WAV/FLAC, preview it, choose either a 20-second window **starting** within the first third of the source (it may extend beyond that third) or the whole recording, select a target style, review/enter lyrics for vocal output, and make a **new interpretation/cover**, not a waveform-to-waveform clone. Expose transcription warnings and allow reviewing the resulting score before rendering where practical.
4. **Set up:** on first start inspect OS-reported GPU VRAM/driver and available RAM/disk, use a simple heuristic to suggest a compatible model profile and prompt before download. Defer if offline/declined, allowing player-only use. Reuse verified cache offline. Advanced Settings lists other compatible profiles, their estimated requirements/licenses/download status, and allows manual override with warnings.

## First-release boundaries

- Local only; no YouTube downloader, hosted generation, accounts, sharing, DRM bypass or automatic source-lyrics extraction.
- One generation at a time. Imported references must be files the user is entitled to use; provide a short rights reminder, not a blanket assertion of permission.
- Preserve original input; save generated FLAC and metadata. MP3 export can follow after FFmpeg licensing/packaging is reviewed.
- Approximate duration control; do **not** imply sample-accurate length. Display actual output duration and truncated warnings.
- Optional tiny local LLM for lyrics is an enhancement behind a separate model download, editable output and explicit approval, not a dependency for listening or instrumental generation.

## Definition of usable

Player works without a GPU or any AI download. Generation is offered only with a real, compatible backend/model combination and clear actionable errors otherwise. No nonexistent/unsupported 4 GB model is auto-downloaded just to satisfy the requested VRAM behavior.
