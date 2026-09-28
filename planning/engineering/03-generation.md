# YuE2 text-to-music implementation contract

## Primary backend: ComfyUI

The [official ComfyUI template](https://github.com/Comfy-Org/workflow_templates/blob/main/templates/audio_yue2_text2music.json) wires `CheckpointLoaderSimple` (combined YuE2 model/text encoder/VAE) -> optional `YuE2GenerateABC` -> `YuE2GenerateMusic` -> `EmptyYuE2LatentAudio` -> `KSampler` -> `VAEDecodeAudio` -> `SaveAudioAdvanced` (FLAC). For low decode memory, test `VAEDecodeAudioTiled` as in the user-supplied workflow. `YuE2GenerateMusic` emits *actual generated seconds* into the latent node; its `max_duration` is a **ceiling** and the song may end sooner. For off mode, pass empty ABC; the native node automatically treats that as `off`. For planned `full`/`melody` generation, pass the produced/provided ABC. Keep the VAE connected to the chosen decode node.

Drive a pinned local ComfyUI worker with an app-generated API prompt graph, not an editable UI graph passed blindly to `/prompt`. Build a tiny end-to-end graph first, then wire advanced controls and covers; retain the executed graph, model revision, settings, ABC and output file as job artifacts. Monitor node execution/progress and expose cancellation. A packaged checkpoint and its VAE are loaded together in ComfyUI; no separate official YuE2-Vae download is implied for that profile. The official standalone pipeline described below is a **secondary backend/reference**, not the required production runtime.

YuE2 is a staged **AR/NAR mixture-of-transformers plus VAE**, not just plain text completion. The separate official Python API `YuE2Pipeline.from_pretrained("m-a-p/YuE2-3B", device="cuda")` downloads the generator and default `m-a-p/YuE2-Vae`. It exposes `plan()` -> `generate_semantic()` -> `synthesize()` -> `decode()` or a single `pipe(...)` call. If this secondary backend is used, save `song.save_artifacts(job_dir)` for `audio.flac`, `score.abc`, settings, manifests and truncation status. Pin Hub revisions and use cached local paths.

## Request mapping

- Description becomes `style` (genre, instrumentation, tempo, vocal character); `lyrics` is a **separate** field in the official API. `cot="full"` gives a score with melody/chords; `cot="melody"` gives a melody plan, recommended for covers; `cot="off"` bypasses score planning. Default to full for a new composition.
- Vocal mode: require user-provided or user-reviewed generated lyrics; preview the complete payload before starting. The user fills two plain-text boxes (Verse, Chorus, both mandatory) and Core's `LyricsComposer` builds the tagged string YuE2 needs: `"[Verse]\n{verse lines}\n\n[Chorus]\n{chorus lines}"` (lines trimmed, blank and tag-only lines dropped, 2,500 characters per box). Instrumental mode: run a probe for the upstream instrumental recipe (YuE2 score + move Vocal notes to Ins + render, as described by the upstream `yue2-music` skill). A style phrase such as “no vocals” alone is not a validated instrumental guarantee; listen for vocal leakage and block the promise until tested.
- Slider labels and bounds: prototype 20 s to 3 min. ComfyUI's `max_duration` directly sets a maximum music-token budget; the generated seconds output sizes the latent, so label slider **Maximum length** and show actual duration. Lyrics/score can make the result shorter. The secondary official Python `SongRequest` has no direct `seconds` argument; do not copy ComfyUI's behavior onto it. Never cut audio silently; warn if truncated.
- Advanced controls (tested-only): seed, `cot` mode, sampling temperature/top-p/top-k/repetition penalty, text guidance, synthesis steps, VAE tiling and compatible model profile. Use upstream defaults until measured. Validate bounds on both sides of worker boundary; display quality/VRAM implications and a Reset to Defaults action.

## Minimal worker experiment (illustrative, not proven on Windows)

```python
from yue2 import YuE2Pipeline

with YuE2Pipeline.from_pretrained(
    "m-a-p/YuE2-3B", device="cuda", revision=PINNED_REVISION,
    vae_revision=PINNED_VAE_REVISION, cache_dir=MODEL_CACHE,
) as pipe:
    result = pipe(style=style, lyrics=reviewed_lyrics, cot="full", seed=seed)
    result.save_artifacts(unique_output_dir)
    if any(result.truncated.values()):
        report_warning("The model stopped at a token limit; inspect the audio.")
```

Keep the real worker API implementation-specific and integrate cancellation via the pipeline's `cancelled` callback after it has been tested. Report planning, tokens, synthesis and decode as stages. Never label a song complete based solely on semantic tokens or a generated ABC score.

## Supplied ComfyUI workflow interpretation

The user JSON uses `CheckpointLoaderSimple(yue2_convrot_int8.safetensors)` -> `YuE2GenerateABC` -> `YuE2GenerateMusic` -> `EmptyYuE2LatentAudio` -> `KSampler` -> tiled VAE decode -> FLAC save. It shows seed/sampling controls and a muted `SheetSage2AudioToABC` branch. Its regular VAE decode node is missing a VAE connection; use the connected tiled path or official templates as the reference. The user checkpoint **filename differs** from the published Comfy-Org `yue2_3b_int8_convrot.safetensors`; use the actual published filename after checking the precise revision/hash, not a guessed alias. Do not assume the official Python pipeline accepts ComfyUI's combined checkpoint or that a smaller file guarantees a 4/8 GB GPU.
