#!/usr/bin/env python3
"""Narration, timeline, Unity render, bilingual subtitles and final mux for a story under stories/<name>/.

  python3 scripts/story.py <name>                      everything, both formats
  python3 scripts/story.py <name> all -f horizontal    everything, one format
  python3 scripts/story.py <name> timeline             output/<name>/narration.wav + timeline.json
  python3 scripts/story.py <name> mix                  output/<name>/mix.wav (narration + ducked music, if spec.music)
  python3 scripts/story.py <name> render -f vertical   output/<name>/vertical/unity_render.mp4
  python3 scripts/story.py <name> render -f vertical --stills 3,20
  python3 scripts/story.py <name> final -f vertical    output/<name>/vertical/final.mp4

Formats: vertical 1080x1920 (9:16), horizontal 1920x1080 (16:9), both (default).
Cue timing comes from the synthesized audio, so pictures, voice and subtitles share one timeline
across both formats; only the picture framing and subtitle layout differ.
"""
import argparse, json, os, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FORMATS = {"vertical": (1080, 1920), "horizontal": (1920, 1080)}
NAME = SDIR = OUT = S = SPEC = CUES = FPS = DUR = None
LEAD, TAIL = 0.7, 1.2                      # silence before the first cue / after the last
GAP = {"clause": 0.10, "sentence": 0.32, "shot": 0.60}
MAX_TEMPO = 1.15


def run(*cmd):
    subprocess.run([str(c) for c in cmd], check=True)


def dur(path):
    o = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)],
                       check=True, capture_output=True, text=True)
    return float(o.stdout.strip())


def pad_to(src, dst):
    """Pad/trim audio to exactly DUR. Done in its own ffmpeg run: after loudnorm in the same graph,
    apad/atrim still leave the file ~94 ms short."""
    run("ffmpeg", "-v", "error", "-y", "-i", src, "-af", f"apad=whole_dur={DUR},atrim=0:{DUR}", dst)


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
    flt.append(f"{''.join(lab)}amix=inputs={len(cues)}:normalize=0,loudnorm=I=-14:TP=-1.5:LRA=11[out]")
    run("ffmpeg", "-v", "error", "-y", *ins, "-filter_complex", ";".join(flt), "-map", "[out]",
        "-ar", "48000", "-ac", "2", OUT / "narration_raw.wav")
    pad_to(OUT / "narration_raw.wav", OUT / "narration.wav"); (OUT / "narration_raw.wav").unlink()
    print(f"tempo {tempo:.3f}  pause stretch {stretch:.2f}  last cue ends {cues[-1]['end']:.2f}s")
    for s in shots: print(f"  {s['id']}  {s['start']:6.2f} - {s['end']:6.2f}")


def lufs_stats(path, af=""):
    """Integrated loudness etc. from ffmpeg's loudnorm analysis pass."""
    o = subprocess.run(["ffmpeg", "-hide_banner", "-nostats", "-i", str(path), "-af",
                        (af + "," if af else "") + "loudnorm=I=-14:TP=-1.5:LRA=11:print_format=json",
                        "-f", "null", "-"], check=True, capture_output=True, text=True).stderr
    return json.loads(o[o.rindex("{"):o.rindex("}") + 1])


def mix():
    """Narration over a music bed that ducks under the voice, mastered to -14 LUFS / -1.5 dBTP.
    spec.music: {"file", "start" (s into track), "bed" (LUFS of the bed before ducking), "duck_ratio", ...credits}.
    Without spec.music the mix is the narration alone."""
    narr, out = OUT / "narration.wav", OUT / "mix.wav"
    if not narr.exists():
        sys.exit("narration.wav missing; run the timeline step first")
    m = SPEC.get("music")
    if not m:
        pad_to(narr, out); print(out, "(no music)"); return out
    src, start, bed = ROOT / m["file"], m.get("start", 0.0), m.get("bed", -27.0)
    fade_in, fade_out = m.get("fade_in", 1.5), m.get("fade_out", 3.0)
    duck_ratio = m.get("duck_ratio", 2.5)
    seg = f"atrim={start}:{start + DUR},asetpts=PTS-STARTPTS,aresample=48000"
    gain = bed - float(lufs_stats(src, seg)["input_i"])
    music = (f"[1:a]{seg},aformat=channel_layouts=stereo,volume={gain:.2f}dB,"
             f"afade=t=in:d={fade_in},afade=t=out:st={DUR - fade_out}:d={fade_out}[m]")
    # duck under the voice (ratio 2.5 ~ 8.7 dB, 1.6 ~ 5 dB); slow release so the bed swells back between lines
    duck = ("[0:a]asplit[v][sc];"
            f"[m][sc]sidechaincompress=threshold=0.05:ratio={duck_ratio}:attack=40:release=700[md];"
            "[v][md]amix=inputs=2:normalize=0[pre]")
    pre = OUT / "mix_pre.wav"
    run("ffmpeg", "-v", "error", "-y", "-i", narr, "-i", src, "-filter_complex", f"{music};{duck}",
        "-map", "[pre]", "-ar", "48000", "-ac", "2", "-t", DUR, pre)
    st = lufs_stats(pre)   # second loudnorm pass in linear mode keeps the dynamics, just sets the level
    run("ffmpeg", "-v", "error", "-y", "-i", pre, "-af",
        f"loudnorm=I=-14:TP=-1.5:LRA=11:linear=true:measured_I={st['input_i']}:measured_TP={st['input_tp']}"
        f":measured_LRA={st['input_lra']}:measured_thresh={st['input_thresh']}:offset={st['target_offset']}",
        "-ar", "48000", "-ac", "2", pre.with_name("mix_ln.wav"))
    pad_to(pre.with_name("mix_ln.wav"), out)
    pre.unlink(); pre.with_name("mix_ln.wav").unlink()
    print(out, f"music gain {gain:+.1f} dB -> bed {bed} LUFS")
    return out


def ts(t):
    return f"{int(t // 3600)}:{int(t % 3600 // 60):02d}:{t % 60:05.2f}"


def subtitles(cues, fmt):
    """Chinese is bottom-anchored on a baseline; English hangs below it (top-anchored), so wrapping never collides.
    Vertical: baseline y=1465 in the panel under the picture. Horizontal: baseline y=960 over the picture."""
    W, H = FORMATS[fmt]
    if fmt == "vertical":
        zh, en = "56,&H00FFFFFF,&H00FFFFFF,&H00201008,&H96000000,-1,0,0,0,100,100,1,0,1,4,2,2,60,60,455,1", \
                 "34,&H00E8DCD2,&H00FFFFFF,&H00201008,&H96000000,0,-1,0,0,100,100,0,0,1,3,1,8,90,90,1486,1"
    else:
        zh, en = "58,&H00FFFFFF,&H00FFFFFF,&H00201008,&H96000000,-1,0,0,0,100,100,1,0,1,4,2,2,160,160,120,1", \
                 "36,&H00E8DCD2,&H00FFFFFF,&H00201008,&H96000000,0,-1,0,0,100,100,0,0,1,3,1,8,200,200,976,1"
    head = f"""[Script Info]
ScriptType: v4.00+
PlayResX: {W}
PlayResY: {H}
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: ZH,PingFang TC,{zh}
Style: EN,Helvetica Neue,{en}

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    ev = []
    for i, (c, src) in enumerate(zip(cues, CUES)):
        nxt = cues[i + 1]["start"] if i + 1 < len(cues) else DUR
        end = min(c["end"] + 0.35, nxt - 0.10)   # never overlap the next cue, or libass shifts lines apart
        for style, key in (("ZH", "zh"), ("EN", "en")):
            ev.append(f"Dialogue: 1,{ts(c['start'] - 0.03)},{ts(end)},{style},,0,0,0,,{{\\fad(120,120)}}{src[key]}")
    out = OUT / fmt / "subtitles.ass"; out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(head + "\n".join(ev) + "\n", encoding="utf-8")
    return out


def render(fmt, stills=None):
    if not (OUT / "timeline.json").exists():
        sys.exit("timeline.json missing; run the timeline step first")
    cmd = [ROOT / "scripts/story_render.sh", NAME, fmt] + ([stills] if stills else [])
    run(*cmd)


def final(fmt):
    video = OUT / fmt / "unity_render.mp4"
    if not video.exists():
        sys.exit(f"{video} missing; run the render step first")
    cues = json.loads((OUT / "timeline.json").read_text(encoding="utf-8"))["cues"]
    ass = subtitles(cues, fmt).relative_to(ROOT)
    audio = OUT / "mix.wav"
    if not audio.exists():
        audio = mix()
    out = OUT / fmt / "final.mp4"
    run("ffmpeg", "-v", "error", "-y", "-i", video, "-i", audio,
        "-filter_complex", f"[0:v]subtitles={ass}[v]", "-map", "[v]", "-map", "1:a", "-t", DUR, "-r", FPS,
        "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", out)
    print(out, f"{dur(out):.2f}s")


def main():
    global NAME, SDIR, OUT, S, SPEC, CUES, FPS, DUR
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("name")
    ap.add_argument("step", nargs="?", default="all", choices=["all", "timeline", "mix", "render", "final"])
    ap.add_argument("-f", "--format", default="both", choices=[*FORMATS, "both"])
    ap.add_argument("--stills", help="comma-separated seconds; render step only, writes PNGs instead of video")
    ap.add_argument("--character", default="v1", choices=["v1", "v2", "v3"], help="apprentice design (v2 refined, v3 lit + polished; under review)")
    a = ap.parse_args()
    os.chdir(ROOT)
    os.environ["STORY_CHARACTER"] = a.character
    NAME = a.name
    SDIR, OUT = ROOT / "stories" / NAME, ROOT / "output" / NAME
    S = json.loads((SDIR / "story.json").read_text(encoding="utf-8"))
    SPEC, CUES = S["spec"], S["cues"]
    FPS, DUR = SPEC["fps"], SPEC["duration"]
    fmts = list(FORMATS) if a.format == "both" else [a.format]
    if a.step in ("all", "timeline"):
        timeline()
    if a.step in ("all", "mix") and not a.stills:
        mix()
    for f in fmts:
        if a.step in ("all", "render"):
            render(f, a.stills)
        if a.step in ("all", "final") and not a.stills:
            final(f)


if __name__ == "__main__":
    main()
