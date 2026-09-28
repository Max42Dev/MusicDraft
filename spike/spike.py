"""Phase 0 feasibility spike: drive a headless ComfyUI with API-format YuE2 graphs.

Usage (with the pinned portable ComfyUI's embedded Python or any Python 3.10+):
    python spike.py text  --port 8189 --seconds 30 --out result.json
    python spike.py cover --port 8189 --audio ref.flac --start 0 --length 20 --out result.json
"""
import argparse
import json
import sys
import time
import urllib.request
import uuid

CKPT = "yue2_3b_int8_convrot.safetensors"
ENCODER = "sheetsage2_bf16.safetensors"


def post(port, path, body):
    req = urllib.request.Request(f"http://127.0.0.1:{port}{path}", data=json.dumps(body).encode(),
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req) as r:
        return json.loads(r.read() or b"{}")


def get(port, path):
    with urllib.request.urlopen(f"http://127.0.0.1:{port}{path}") as r:
        return json.loads(r.read())


def music_graph(style, lyrics, abc_link_or_text, seed, max_duration, mode, prefix, tiled):
    g = {
        "ckpt": {"class_type": "CheckpointLoaderSimple", "inputs": {"ckpt_name": CKPT}},
        "music": {"class_type": "YuE2GenerateMusic", "inputs": {
            "clip": ["ckpt", 1], "style": style, "lyrics": lyrics, "abc": abc_link_or_text,
            "seed": seed, "mode": mode, "max_duration": max_duration,
            "temperature": 1.0, "top_p": 0.95, "top_k": 100, "repetition_penalty": 1.2}},
        "neg": {"class_type": "ConditioningZeroOut", "inputs": {"conditioning": ["music", 0]}},
        "latent": {"class_type": "EmptyYuE2LatentAudio", "inputs": {"seconds": ["music", 1], "batch_size": 1}},
        "sampler": {"class_type": "KSampler", "inputs": {
            "model": ["ckpt", 0], "positive": ["music", 0], "negative": ["neg", 0], "latent_image": ["latent", 0],
            "seed": seed, "steps": 32, "cfg": 1.0, "sampler_name": "dpm_2", "scheduler": "sgm_uniform", "denoise": 1.0}},
        "seconds": {"class_type": "PreviewAny", "inputs": {"source": ["music", 1]}},
        "save": {"class_type": "SaveAudioAdvanced", "inputs": {
            "audio": ["decode", 0], "filename_prefix": prefix, "format": "flac"}},
    }
    if tiled:
        g["decode"] = {"class_type": "VAEDecodeAudioTiled", "inputs": {
            "samples": ["sampler", 0], "vae": ["ckpt", 2], "tile_size": 512, "overlap": 64}}
    else:
        g["decode"] = {"class_type": "VAEDecodeAudio", "inputs": {"samples": ["sampler", 0], "vae": ["ckpt", 2]}}
    return g


def run(port, graph):
    client = uuid.uuid4().hex
    t0 = time.time()
    resp = post(port, "/prompt", {"prompt": graph, "client_id": client})
    if "error" in resp:
        print(json.dumps(resp, indent=2))
        sys.exit(1)
    pid = resp["prompt_id"]
    while True:
        h = get(port, f"/history/{pid}")
        if pid in h:
            entry = h[pid]
            entry["elapsed_s"] = time.time() - t0
            return entry
        time.sleep(2)


def main():
    global CKPT
    ap = argparse.ArgumentParser()
    ap.add_argument("mode", choices=["text", "cover", "abc"])
    ap.add_argument("--port", type=int, default=8189)
    ap.add_argument("--seconds", type=float, default=30)
    ap.add_argument("--audio")
    ap.add_argument("--start", type=float, default=0)
    ap.add_argument("--length", type=float, default=0)
    ap.add_argument("--style", default="upbeat indie pop, bright electric guitar, warm female vocal, 110 BPM")
    ap.add_argument("--lyrics", default="[Verse]\nMorning light across the city\nEvery window burning gold\n\n[Chorus]\nWe are running, we are flying\nNever growing old")
    ap.add_argument("--cot", default="full", choices=["full", "melody", "off"])
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--tiled", action="store_true")
    ap.add_argument("--abc-file")
    ap.add_argument("--out", default="result.json")
    ap.add_argument("--ckpt", default=CKPT)
    a = ap.parse_args()
    CKPT = a.ckpt

    if a.mode == "text":
        if a.abc_file:
            with open(a.abc_file, encoding="utf-8") as f:
                abc = f.read()
            g = music_graph(a.style, a.lyrics, abc, a.seed, a.seconds, a.cot, "spike/text", a.tiled)
        elif a.cot == "off":
            abc, mode = "", "full"
            g = music_graph(a.style, a.lyrics, abc, a.seed, a.seconds, mode, "spike/text", a.tiled)
        else:
            g = music_graph(a.style, a.lyrics, ["abcgen", 0], a.seed, a.seconds, a.cot, "spike/text", a.tiled)
            g["abcgen"] = {"class_type": "YuE2GenerateABC", "inputs": {
                "clip": ["ckpt", 1], "style": a.style, "lyrics": a.lyrics, "seed": a.seed, "mode": a.cot,
                "max_abc_tokens": 8192, "temperature": 0.7, "top_p": 0.9, "top_k": 30,
                "repetition_penalty": 1.005, "penalty_window": 100}}
            g["abcview"] = {"class_type": "PreviewAny", "inputs": {"source": ["abcgen", 0]}}
    else:
        audio_src = {"class_type": "LoadAudio", "inputs": {"audio": a.audio}}
        g = {"load": audio_src,
             "enc": {"class_type": "AudioEncoderLoader", "inputs": {"audio_encoder_name": ENCODER}}}
        src = ["load", 0]
        if a.length > 0:
            g["trim"] = {"class_type": "TrimAudioDuration", "inputs": {"audio": src, "start_index": a.start, "duration": a.length}}
            src = ["trim", 0]
        g["sheet"] = {"class_type": "SheetSage2AudioToABC", "inputs": {"audio_encoder": ["enc", 0], "audio": src, "mode": "melody"}}
        g["abcview"] = {"class_type": "PreviewAny", "inputs": {"source": ["sheet", 0]}}
        if a.mode == "cover":
            g.update(music_graph(a.style, a.lyrics, ["sheet", 0], a.seed, a.seconds, "melody", "spike/cover", a.tiled))

    result = run(a.port, g)
    with open(a.out, "w", encoding="utf-8") as f:
        json.dump({"graph": g, "result": result}, f, indent=2)
    status = result.get("status", {})
    print("status:", status.get("status_str"), "elapsed:", round(result["elapsed_s"], 1), "s")
    for node, out in result.get("outputs", {}).items():
        print(node, json.dumps(out)[:600])


if __name__ == "__main__":
    main()
