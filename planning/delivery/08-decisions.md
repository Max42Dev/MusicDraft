# Decision log and questions for the owner

## Answered

- **Windows-only modern GUI**: yes.
- **YouTube links**: no; local audio files only.
- **GPU matching**: use OS-reported dedicated VRAM and a simple heuristic to recommend/download a compatible 4 GB, 8 GB or larger class profile on first app start (with download consent); Advanced Settings allows alternatives. No extensive VRAM testing, but a real checkpoint and one end-to-end smoke test are necessary per claimed profile. Not every tier is guaranteed to exist.
- **Lyrics**: user-editable vocal lyrics; optional small local text model to draft lyrics from a description.
- **Use case**: personal/individual creators; still honor model/third-party terms and do not assume company-commercial distribution rights.
- **20 s excerpt**: its **start** is within the first third; the excerpt may run past that boundary. For <20 s source, whole-song mode only.
- **Local YuE2 inference**: requested; do not impose WSL2/cloud or pretend a llama text runner implements the full music pipeline.
- **ComfyUI implementation**: owner favors reproducing its existing audio-decoder pipeline; primary candidate is a hidden local ComfyUI worker with a MusicDraft GUI. Comfy-Org publishes an int8 and a bf16 combined checkpoint; neither comes with a proven 4/8 GB VRAM claim.

- **2026-09-27, owner:** use option A — headless ComfyUI as the inference backend (not the standalone Python pipeline, not a rewrite). Provide an **installer** that installs the app and its runtime/model prerequisites. Phase 0 spike passed; see `engineering/09-spike-results.md`.
- **2026-09-27, implementation choice:** .NET 10 + Avalonia 12 desktop shell, NAudio/Media Foundation playback, SQLite catalog, Inno Setup installer (per-user, no admin). Runtime pinned to ComfyUI v0.37.0 portable NVIDIA build.

## Awaiting a decision / feasibility result

1. **Native Windows blocker:** if neither pinned Windows ComfyUI nor the standalone official Python pipeline works, which compromise is acceptable: change inference backend/model, accept a local Linux/WSL2 helper, or release player-only initially? Ask the owner after the spike, not before it.
2. **Supported VRAM floor:** no known proven 4 GB or 8 GB YuE2 full-pipeline Windows configuration. Seek real compatible models/backends; if those classes cannot generate usable music, ask the owner whether player-only mode on such GPUs is acceptable rather than fabricating a model choice.
3. **Instrumentals:** choose whether an instrumental-first default is preferred once upstream instrumental workflow is tested. Vocal mode always offers lyrics editor; optional lyric model must not auto-publish unreviewed words.
4. **Distribution scope:** owner specified personal/individual creators. If later distributed or operated as a company-commercial offering, revisit weight licensing first; this repo's MIT license does not grant model-weight rights.
5. **One EXE:** current definition is one visible Windows launcher with external cached runtime/model files. If fully portable/offline on initial launch is required, sizing and installation strategy need renegotiation.

Document owner answers here with date and adjust the affected spec before building them.
