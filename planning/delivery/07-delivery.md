# Build order, tests and shipping gates

## Phase 0: unblock feasibility

- On native Windows, prove **pinned ComfyUI** built-in YuE2 pipeline text->FLAC and SheetSage2 audio->ABC->YuE2->FLAC using the official templates and Comfy-Org checkpoint(s). Create minimal API prompt graphs from templates, test job events/cancel, VAE tiling, instrumental behavior and duration ceiling; keep Comfy UI hidden. If this fails, evaluate the standalone upstream Python runtime as a fallback. Document exact OS/GPU/driver/VRAM/RAM/versions, download bytes, license sources, execution times and error traces. Do not call the project generation-ready before this gate.
- Identify any genuinely compatible lower-memory YuE2 profiles and run a **single short end-to-end smoke test per profile/backend**, not an exhaustive GPU matrix. Use OS-reported VRAM plus conservative per-profile estimates for automatic selection; record failures and do not promise an untested 4 GB/8 GB profile. Decide shell/runtime packaging and download manifest after the spike.

## Phase 1: player slice

Windows app starts without internet/model/GPU. Add files, persist/reorder playlists, volume/seek/next/previous, shuffle and repeat. Tests: empty and missing file behavior, MP3/WAV/FLAC decoding, concurrent user actions and DB reopen. Playback must remain responsive during background downloads.

## Phase 2: generation slice

Add worker protocol, managed model cache/consent, device selection, input validation, text+lyrics flow, stage progress, cancel and FLAC artifacts. One song at a time: no job queue and no Downloads/Jobs screen; progress, cancel, result and errors show inline in Create, and model-download progress shows in the setup dialog and Settings. Tests with worker stub (UI/state, second start rejected while busy), real GPU integration (score through decoded audio), loss of network, hash mismatch, low disk, low free VRAM, restart mid-generation (marked Interrupted), bad prompt and interrupted decode. Confirm displayed target vs actual length and truncated flags.

## Phase 3: local cover slice

Decode local input, 20 s first-third-start clip / whole-song selection, native ComfyUI SheetSage2 audio-encoder nodes, ABC review, melody-conditioned YuE2 generation. Use a separate environment only if the alternative standalone backend requires it. Tests: 10 s/30 s/60 s/180 s input edge cases, damaged MP3, silent excerpt, nonempty ABC, no copyrighted test asset shipped, transcription warning and failed cover cleanup.

## Phase 4: polish and optional lyrics

Add compatible lower-VRAM paths to registry as they become available; advanced model switch/download and cleanup. Add local lyric assistant only with chosen licensed model/backend and offline test. Accessibility, UI responsiveness, upgrade/recovery, signed release binaries if possible.

## Release criteria

- Native Windows install/uninstall and first-run download verified on clean target machines; app has one user-visible launch EXE, documented external runtime prerequisites and realistic download/storage sizes. Never ship a misleading self-contained claim.
- Test launch with no network/GPU, generation with a supported GPU, incomplete installation rollback and cover with both excerpt/whole song; playback of generated results. Do not require a wide VRAM benchmark campaign.
- Ship pinned license notices, full source/model attributions, copyright/rights reminders, a privacy statement describing local processing, and explicit unsupported-hardware messaging. Confirm company-commercial use terms before marketing/distribution.
