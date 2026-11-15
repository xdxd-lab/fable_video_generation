#!/usr/bin/env python3
"""Narration, timeline, bilingual subtitles and final mux for a story under stories/<name>/.

  python3 scripts/story.py <name> timeline          -> narration.wav, timeline.json, subtitles.ass
  python3 scripts/story.py <name> final VIDEO.mp4   -> output/<name>/final.mp4
Cue timing comes from the synthesized audio, so pictures, voice and subtitles share one timeline.
"""
import json, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
NAME = sys.argv[1]
SDIR, OUT = ROOT / "stories" / NAME, ROOT / "output" / NAME
S = json.loads((SDIR / "story.json").read_text(encoding="utf-8"))
SPEC, CUES = S["spec"], S["cues"]
W, H, FPS, DUR = SPEC["width"], SPEC["height"], SPEC["fps"], SPEC["duration"]
LEAD, TAIL = 0.7, 1.2                      # silence before the first cue / after the last
GAP = {"clause": 0.10, "sentence": 0.32, "shot": 0.60}
MAX_TEMPO = 1.15


def run(*cmd):
    subprocess.run([str(c) for c in cmd], check=True)


def dur(path):
    o = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
                       check=True, capture_output=True, text=True)
    return float(o.stdout.strip())


def timeline():
    adir = OUT / "audio"; adir.mkdir(parents=True, exist_ok=True)
    raw = []
    for i, c in enumerate(CUES):
        f, g = adir / f"cue{i:02d}.aiff", adir / f"cue{i:02d}.wav"
        run("say", "-v", SPEC["voice"], "-o", f, c["zh"])
        # `say` pads every clip with silence; trim both ends so pauses are controlled by GAP alone.
        trim = "silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.02"
        run("ffmpeg", "-v", "error", "-y", "-i", f, "-af", f"{trim},areverse,{trim},areverse", g)
        raw.append(dur(g))
    kinds = []
    for i, c in enumerate(CUES[1:], 1):
        prev = CUES[i - 1]
        kinds.append("shot" if c["shot"] != prev["shot"] else
                     "sentence" if prev["zh"].rstrip("」")[-1] in "。？！…" else "clause")
    gaps = sum(GAP[k] for k in kinds)
    room = DUR - LEAD - TAIL - gaps
    tempo = max(1.0, sum(raw) / room)
    print(f"raw speech {sum(raw):.1f}s, pauses {gaps:.1f}s, room {room:.1f}s")
    if tempo > MAX_TEMPO:
        sys.exit(f"narration needs tempo {tempo:.2f} > {MAX_TEMPO}; shorten the script")
    speech = sum(raw) / tempo
    stretch = min(2.0, (DUR - LEAD - TAIL - speech) / gaps)   # spread spare time over the pauses
    t, cues = LEAD, []
    for i, c in enumerate(CUES):
        if i: t += GAP[kinds[i - 1]] * stretch
        d = raw[i] / tempo
        cues.append({"shot": c["shot"], "start": round(t, 3), "end": round(t + d, 3)})
        t += d
    shots, order = [], []
    for c in cues:
        if c["shot"] not in order: order.append(c["shot"])
    for n, sid in enumerate(order):
        first = next(c for c in cues if c["shot"] == sid)
        start = 0.0 if n == 0 else round(first["start"] - 0.3, 3)
        shots.append({"id": sid, "start": start, "end": DUR})
    for a, b in zip(shots, shots[1:]): a["end"] = b["start"]
    tl = {"duration": DUR, "tempo": round(tempo, 4), "cues": cues, "shots": shots}
    (OUT / "timeline.json").write_text(json.dumps(tl, ensure_ascii=False, indent=1), encoding="utf-8")

    ins, flt, lab = [], [], []
    for i, c in enumerate(cues):
        ins += ["-i", adir / f"cue{i:02d}.wav"]
        flt.append(f"[{i}:a]aresample=48000,atempo={tempo:.5f},adelay={int(c['start'] * 1000)}:all=1[a{i}]")
        lab.append(f"[a{i}]")
    flt.append(f"{''.join(lab)}amix=inputs={len(cues)}:normalize=0,loudnorm=I=-14:TP=-1.5:LRA=11,apad,atrim=0:{DUR}[out]")
    run("ffmpeg", "-v", "error", "-y", *ins, "-filter_complex", ";".join(flt), "-map", "[out]",
        "-ar", "48000", "-ac", "2", OUT / "narration.wav")
    subtitles(cues)
    print(f"tempo {tempo:.3f}  pause stretch {stretch:.2f}  last cue ends {cues[-1]['end']:.2f}s")
    for s in shots: print(f"  {s['id']}  {s['start']:6.2f} - {s['end']:6.2f}")


def ts(t):
    return f"{int(t // 3600)}:{int(t % 3600 // 60):02d}:{t % 60:05.2f}"


def subtitles(cues):
    """Chinese sits just above y=1465 (bottom-anchored); English hangs below it (top-anchored), so wrapping never collides."""
    head = f"""[Script Info]
ScriptType: v4.00+
PlayResX: {W}
PlayResY: {H}
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: ZH,PingFang TC,56,&H00FFFFFF,&H00FFFFFF,&H00201008,&H96000000,-1,0,0,0,100,100,1,0,1,4,2,2,60,60,455,1
Style: EN,Helvetica Neue,34,&H00E8DCD2,&H00FFFFFF,&H00201008,&H96000000,0,-1,0,0,100,100,0,0,1,3,1,8,90,90,1486,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    ev = []
    for i, (c, src) in enumerate(zip(cues, CUES)):
        nxt = cues[i + 1]["start"] if i + 1 < len(cues) else DUR
        end = min(c["end"] + 0.35, nxt - 0.10)   # never overlap the next cue, or libass shifts lines apart
        for style, key in (("ZH", "zh"), ("EN", "en")):
            ev.append(f"Dialogue: 1,{ts(c['start'] - 0.03)},{ts(end)},{style},,0,0,0,,{{\\fad(120,120)}}{src[key]}")
    (OUT / "subtitles.ass").write_text(head + "\n".join(ev) + "\n", encoding="utf-8")


def final(video):
    ass = (OUT / "subtitles.ass").relative_to(ROOT)
    out = OUT / "final.mp4"
    run("ffmpeg", "-v", "error", "-y", "-i", video, "-i", OUT / "narration.wav",
        "-filter_complex", f"[0:v]subtitles={ass}[v]", "-map", "[v]", "-map", "1:a", "-t", DUR, "-r", FPS,
        "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", out)
    print(out, f"{dur(out):.2f}s")


if __name__ == "__main__":
    import os
    os.chdir(ROOT)
    mode = sys.argv[2] if len(sys.argv) > 2 else "timeline"
    if mode == "timeline": timeline()
    elif mode == "final": final(sys.argv[3])
    else: sys.exit(__doc__)
