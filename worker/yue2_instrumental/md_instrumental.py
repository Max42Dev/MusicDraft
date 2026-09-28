"""MusicDraft wrapper around the pinned upstream yue2-music instrumental helpers (MIT).

Reads a JSON request on stdin: {"abc": "...", "style": "..."}
Writes JSON on stdout: {"ok": true, "abc": ..., "style": ..., "lyrics": ..., "mode": ..., "check": {...}}
or {"ok": false, "error": "..."}. Standard library only; runs on CPU.
"""
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from abc_tools import parse_abc  # noqa: E402
from instrumentalize import convert_score  # noqa: E402

PLANNING_LYRICS = "[Intro]\n\n[Verse]\n\n[Chorus]\n\n[Outro]\n"


def lyric_tags(text):
    labels = [line[2:] for line in text.splitlines() if line.startswith("% ")]
    return "\n\n".join("[" + x.title() + "]" for x in labels) + ("\n" if labels else "")


def render_style(style):
    style = (style or "").strip().rstrip(".,") or "Expressive instrumental music"
    if not re.match(r"^instrumental\b", style, re.I):
        style = "Instrumental, " + style
    for condition in ("no vocals", "no singing", "no choir", "no spoken words"):
        if condition not in style.lower():
            style += ", " + condition
    return style + "."


def main():
    try:
        req = json.loads(sys.stdin.read())
        if req.get("command") == "planning_lyrics":
            print(json.dumps({"ok": True, "lyrics": PLANNING_LYRICS}))
            return 0
        converted, check = convert_score(req["abc"], overlap="vocal", keep_chords=True)
        score = parse_abc(converted)
        mode = "full" if score.voices["Vocal"].chords else "melody"
        print(json.dumps({"ok": True, "abc": converted, "style": render_style(req.get("style", "")),
                          "lyrics": lyric_tags(converted), "mode": mode, "check": check}))
        return 0
    except Exception as exc:  # report every failure as structured output
        print(json.dumps({"ok": False, "error": f"{type(exc).__name__}: {exc}"}))
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
