# Instructions for coding agents

This repository currently has only an MIT license and planning material. Do not mistake the plans for implemented features.

1. Read `planning/README.md` and all linked documents before selecting packages or writing application code. Confirm any open owner decisions in `planning/delivery/08-decisions.md` before irreversible architecture or distribution choices.
2. First implement the **native Windows ComfyUI YuE2 feasibility spike** described in `planning/engineering/02-architecture.md`. Use ComfyUI's built-in YuE2 nodes/checkpoint and audio VAE decode, with SheetSage2 for covers; keep the GUI/playback independent of the inference backend. Do not promise a low-memory profile without at least one end-to-end generation check, including decode and audio playback; no exhaustive GPU-tier benchmarking is requested.
3. Prefer pinned, existing ComfyUI components over reimplementing its model/sampler/decoder. The official YuE2 Python pipeline is an alternative if integration fails. Do not replace the YuE2 music pipeline with `llama.cpp` or Ollama solely because the model is described as an LLM. These may be useful for the **separate optional lyrics assistant**; they are not known drop-in YuE2 backends.
4. Do not silently download/execute unpinned third-party code, assume arbitrary community quantizations work, bypass license restrictions, bundle copyrighted input recordings, or download model weights without showing source, size, destination and terms.
5. Keep source audio and artifacts local. Separate UI, playback, catalog, downloads, generation and transcription behind typed contracts. Run long jobs off the UI thread and retain resumable/error-visible job state.
6. Build in small vertical slices and test each acceptance criterion in `planning/delivery/07-delivery.md`. Update the plans when a spike changes a stated assumption; record versions, reproducible commands, GPU/driver/RAM/VRAM observations and failures.
7. A single EXE refers to the **user-facing app entry point**, not a promise to embed Python, CUDA, FFmpeg, weights and multiple environments in a tiny standalone binary. Report actual prerequisites and disk footprint plainly.
