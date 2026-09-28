# Phase 0 feasibility spike results (2026-09-27)

Status: **gate passed on one machine** for text→FLAC, instrumental recipe, and SheetSage2 cover (excerpt and whole song) using headless native-Windows ComfyUI. Reproducible drivers are in `spike/` (`spike.py`, `cancel_test.py`, `capped_launch.py`).

## Environment

| Item | Value |
|---|---|
| OS | Windows Server 2022 (10.0.20348), native, no WSL/Docker |
| GPU / driver | NVIDIA L4, 23,034 MiB, WDDM, driver 596.86 (CUDA 13.2) |
| Host RAM / disk | 15.75 GB RAM, ~51 GB free |
| Runtime | ComfyUI **v0.37.0** `ComfyUI_windows_portable_nvidia.7z` (1,925,204,508 bytes, sha256 `7805f634…7d65`), embedded Python 3.13.14, PyTorch 2.13.0+cu130; extracted ≈3.9 GB (57,927 files) |
| Checkpoint | `Comfy-Org/YuE2` `checkpoints/yue2_3b_int8_convrot.safetensors` 3,960,938,800 bytes, sha256 `96fe1993…db6` |
| Cover encoder | `audio_encoders/sheetsage2_bf16.safetensors` 1,386,868,122 bytes, sha256 `5fd960ce…c5`; **no separate MERT download was needed** |

Launch (headless, loopback only, no custom/API nodes):
`python_embeded\python.exe -s ComfyUI\main.py --listen 127.0.0.1 --port <p> --disable-auto-launch --disable-api-nodes --disable-all-custom-nodes --models-directory <models> --input-directory <in> --output-directory <out> --temp-directory <tmp> --user-directory <user> --log-stdout`

## Results

| Test | Requested ceiling | Actual | Wall time | Peak whole-GPU VRAM |
|---|---|---|---|---|
| Text, `full` ABC, plain decode (cold load) | 30 s | 30.0 s | 48.5 s | 5.1 GB |
| Text, `full`, tiled decode | 120 s | **73.56 s** (lyrics ended) | 50 s | 5.5 GB |
| Cover, 20 s excerpt @10 s → melody | 60 s | 36.12 s | 30 s | 6.6 GB |
| Instrumental recipe (Vocal→Ins), tiled | 120 s | 74.92 s (score nominal 75.6 s) | 44 s | – |
| Cancel via `POST /interrupt` during token generation | 300 s | `execution_interrupted`, no outputs | – | – |
| **8 GB class**: hard PyTorch cap 7.5 GB + `--reserve-vram 15` | 60 s | 39.8 s | 46 s | 4.75 GB |
| **4 GB class**: hard cap 3.5 GB + `--reserve-vram 19` | 60 s | 28.84 s | 28 s | 4.14 GB |
| 4 GB class: whole-song cover (73.6 s source) | 180 s | 74.88 s | 68 s | 4.15 GB |
| **bf16** checkpoint (`yue2_3b_bf16`, 7.8 GB, sha256 verified), uncapped | 60 s | FLAC written | 59 s | 5.76 GB |

Host RAM: ComfyUI process peak working set ≈ **8.2 GB** (dynamic VRAM stages weights in RAM). Budget ≥16 GB system RAM.

## Findings that change or sharpen the plans

- **API graph format.** The UI templates use subgraphs; the app must emit its own API-format graph. `SaveAudioAdvanced` uses a DynamicCombo: send `"format": "flac"` (flat string; nested options as `"format.quality"`), not an object.
- **`max_duration` is a ceiling** (confirmed). `YuE2GenerateMusic` output 1 is actual seconds; capture it via a `PreviewAny` node. Duration is also bounded by lyric/score length.
- **Caching.** ComfyUI reuses cached node outputs for identical inputs (a repeated request returned in 2 s). The app always sends an explicit seed; "Regenerate" must change it.
- **SheetSage2** returns a list with one ABC string per batch item (batch=1 → one string). Long audio is windowed internally (300 s windows, 200 s overlap). Token-limit warnings are only logged, so the worker must scan the log. In `melody` mode the ABC had no chord symbols.
- **Instrumental**: the upstream `yue2-music` instrumental helpers (MIT, pinned `72272f90`, stdlib-only) run under the embedded Python and convert a real ComfyUI ABC correctly (90 Vocal notes → Ins, chords/meter preserved). Vocal leakage was **not verified by listening**; the UI labels instrumental output accordingly.
- **Progress**: token generation, SheetSage2 and KSampler use `comfy.utils.ProgressBar`, so websocket `progress` events are available per stage.
- **Low VRAM**: ComfyUI's dynamic VRAM (default in 0.37) made the int8 profile run under a hard 3.5 GB PyTorch cap. The CUDA context and desktop add ~0.6 GB outside that cap, so **8 GB class = supported (simulated), 4 GB class = experimental**. This used a hard cap on a 24 GB card, not a real 4/8 GB card; one real low-end card smoke test is still owed before release.
- **Paths**: a few torch license files in the portable build exceed MAX_PATH under `%LOCALAPPDATA%\MusicDraft\runtime\ComfyUI_windows_portable`. They are not needed at runtime; keep the install root short.
- **Licenses**: ComfyUI is **GPL-3.0**. MusicDraft only launches it as a separate process over loopback HTTP and does not link or modify it. YuE2 weights are CC BY-NC 4.0 (+ individual-creator monetization permission).
