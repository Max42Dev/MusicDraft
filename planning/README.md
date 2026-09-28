# MusicDraft implementation plan

MusicDraft is a **Windows-only, modern desktop music player and local YuE2 music studio**. This directory is the implementation brief for coding agents, not a claim that an inference engine has already been validated.

Read [AGENTS.md](AGENTS.md) first, then follow the numbered documents in order:

1. [Product and scope](product/01-product.md)
2. [Architecture and feasibility gates](engineering/02-architecture.md)
3. [YuE2 generation contract](engineering/03-generation.md)
4. [Reference-audio covers](engineering/04-covers.md)
5. [Models, downloads, and VRAM](engineering/05-models.md)
6. [Interface and player](experience/06-interface.md)
7. [Delivery, milestones, and tests](delivery/07-delivery.md)
8. [Decisions and unresolved blockers](delivery/08-decisions.md)
9. [Phase 0 spike results](engineering/09-spike-results.md) and [implementation progress](delivery/10-progress.md)

## Confirmed by the project owner

- Windows only; modern GUI, ideally one user-facing EXE.
- Local inference and first-start model recommendation/download (after consent), chosen according to OS-reported GPU VRAM; alternative compatible models selectable in Advanced Settings and downloaded on demand.
- Local audio-file import, **not YouTube**. A 20-second clip may start anywhere in the first third and extend beyond it.
- Lyrics entry for vocal music; optional small local lyric-writing LLM is desirable.
- Intended for personal/individual creators, not licensed company-commercial model use.
- Playlist, volume, repeat, shuffle, text description, duration control, Create, advanced settings, and reference-audio generation with 20-second excerpt or entire song.

## Important distinction

The app can be one launcher EXE without containing multi-gigabyte models. **Primary inference candidate: headless native-Windows ComfyUI using its built-in YuE2/SheetSage2 nodes and a custom MusicDraft GUI.** This is not a conventional text-only GGUF/llama.cpp loop: score planning, music tokens, acoustic sampling and audio VAE decoding must all work. Use an OS-reported VRAM heuristic against compatible model profiles, not an exhaustive GPU test matrix; a smaller int8 file is not itself a 4 GB/8 GB VRAM guarantee. No WSL2 requirement or cloud service has been approved.

## Primary references (checked 2026-09-27)

- [YuE2 repository and README](https://github.com/multimodal-art-projection/YuE)
- [Official generation guide](https://github.com/multimodal-art-projection/YuE/blob/main/docs/generation.md) and [cover guide](https://github.com/multimodal-art-projection/YuE/blob/main/docs/covers.md)
- [YuE2-3B model card](https://huggingface.co/m-a-p/YuE2-3B), [YuE2-Vae](https://huggingface.co/m-a-p/YuE2-Vae), [SheetSage2](https://huggingface.co/m-a-p/SheetSage2)
- [Supplied ComfyUI workflow](https://github.com/user-attachments/files/32133499/yue2_full.json)
- [Official ComfyUI text-to-music template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/audio_yue2_text2music.json), [cover template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/audio_yue2_music_cover.json), [repackaged models](https://huggingface.co/Comfy-Org/YuE2)
- [YuE2 model-weight license](https://github.com/multimodal-art-projection/YuE/blob/main/MODEL_LICENSE)

Recheck upstream releases, terms, support and dependencies when implementing; pin tested revisions instead of silently tracking latest.
