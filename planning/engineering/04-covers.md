# Local reference audio and cover workflow

## Input and selection

Support drag/drop and file-picker MP3/WAV/FLAC only. No URL field or YouTube download. Decode to a safe temporary working copy using a pinned, licensed decoder; report codec errors. Keep original untouched, preview with playback adapter, calculate actual length from decoded samples (not file metadata alone).

- **Excerpt:** select a 20-second window with start time `t` in `0 <= t <= min(duration/3, duration - 20s)`. Only the **start** must be in the first third; the clip may extend beyond it, including on recordings shorter than 60 s. For sources under 20 s, disable excerpt and offer whole-song mode. Show waveform/timeline, exact selected range and preview.
- **Entire song:** send the full decoded recording to transcription; warn about higher time/RAM use and long-song transcription windowing. Cap maximum accepted source length only after testing and make the limit visible.

## Cover pipeline

1. Get rights reminder/consent. Decode and crop if needed; normalize sample rate/channels using the transcription adapter; retain sample start/end and original-path reference in job metadata.
2. With the **primary ComfyUI backend**, load [Comfy-Org's `sheetsage2_bf16.safetensors`](https://huggingface.co/Comfy-Org/YuE2) via `AudioEncoderLoader`, then `SheetSage2AudioToABC(mode="melody")` to get ABC without chord symbols. Its repackaged audio encoder may include what it needs for MERT internally; inspect the pinned checkpoint/runtime and **do not claim an extra MERT download without checking**. Do **not** use YuE2 alone for audio ingestion.
3. Display/verify transcription warnings, nonempty ABC and optionally a score preview. Let user edit/retry if notes are poor. No promise of exact source timbre, mix, lyrics or duration; a short excerpt produces only a short score, **not** a faithful whole-song cover.
4. Feed nonempty ABC to `YuE2GenerateMusic(mode="melody", style=target_style, lyrics=reviewed_lyrics, ...)`, then audio sampling/VAE decode and FLAC save as in the [official ComfyUI cover template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/audio_yue2_music_cover.json). For source lyrics, require user-supplied words for vocal covers; offer Instrumental when none are provided. Do not treat optional lyric generation as an automatic transcription of source lyrics. Save ABC/source-range/model identities with output.

The **standalone official Python cover path** uses separate YuE2/SheetSage2 environments because their dependencies differ; that is not automatically necessary with ComfyUI's native SheetSage2 nodes and combined runtime. If ComfyUI transcription fails, only then evaluate the standalone path. Review/pin all downloaded encoder code/weights and show download terms. If Windows transcription fails, show covers as unsupported until a tested alternative is approved.
