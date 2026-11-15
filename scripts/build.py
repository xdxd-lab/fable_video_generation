#!/usr/bin/env python3
"""Build narration, bilingual subtitles and (optionally) an animatic from script.json.

Usage: python3 scripts/build.py animatic          -> output/animatic.mp4 (placeholder cards)
       python3 scripts/build.py final VIDEO.mp4   -> output/final.mp4   (Unity render + audio + subs)
Stdlib only; needs ffmpeg/ffprobe and macOS `say` on PATH.
"""
import json, subprocess, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
S = json.loads((ROOT / "script.json").read_text(encoding="utf-8"))
SPEC, SHOTS = S["spec"], S["shots"]
W, H, FPS, DUR = SPEC["width"], SPEC["height"], SPEC["fps"], SPEC["duration"]
LEAD = 0.25  # seconds of silence before each narration line


def run(*cmd):
    subprocess.run([str(c) for c in cmd], check=True)


def dur(path):
    out = subprocess.run(["ffprobe", "-v", "error", "-show_entries", "format=duration",
                          "-of", "csv=p=0", str(path)], check=True, capture_output=True, text=True)
    return float(out.stdout.strip())


def narration():
    """One aiff per shot, then a single 48 kHz track with each line placed at its shot start."""
    adir = ROOT / "audio"
    inputs, filters, labels, lines = [], [], [], []
    for i, sh in enumerate(SHOTS):
        f = adir / f"{sh['id']}.aiff"
        run("say", "-v", SPEC["voice"], "-o", f, sh["zh"])
        d, slot = dur(f), sh["end"] - sh["start"]
        if d + LEAD > slot:
            sys.exit(f"{sh['id']}: narration {d:.2f}s does not fit slot {slot:.2f}s")
        lines.append((sh, d))
        inputs += ["-i", f]
        ms = int((sh["start"] + LEAD) * 1000)
        filters.append(f"[{i}:a]aresample=48000,adelay={ms}:all=1[a{i}]")
        labels.append(f"[a{i}]")
    filters.append(f"{''.join(labels)}amix=inputs={len(SHOTS)}:normalize=0,"
                   f"loudnorm=I=-14:TP=-1.5:LRA=11,apad,atrim=0:{DUR}[out]")
    out = adir / "narration.wav"
    run("ffmpeg", "-v", "error", "-y", *inputs, "-filter_complex", ";".join(filters),
        "-map", "[out]", "-ar", "48000", "-ac", "2", out)
    return out, lines


def ts(t):
    h, m = int(t // 3600), int(t % 3600 // 60)
    return f"{h}:{m:02d}:{t % 60:05.2f}"


def subtitles(lines, cards):
    """Bilingual ASS. Subtitles sit in the middle safe zone (clear of the Reels bottom UI)."""
    head = f"""[Script Info]
ScriptType: v4.00+
PlayResX: {W}
PlayResY: {H}
WrapStyle: 0
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: ZH,PingFang TC,62,&H00FFFFFF,&H00FFFFFF,&H00000000,&H96000000,-1,0,0,0,100,100,0,0,1,5,2,2,90,90,600,1
Style: EN,Helvetica Neue,38,&H00E6E6E6,&H00FFFFFF,&H00000000,&H96000000,0,0,0,0,100,100,0,0,1,4,2,2,90,90,520,1
Style: CardId,Menlo,120,&H00FFC864,&H00FFFFFF,&H00000000,&H00000000,-1,0,0,0,100,100,0,0,1,0,0,8,90,90,420,1
Style: Card,PingFang TC,46,&H00C8C8C8,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,8,120,120,600,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    ev = []
    for sh, d in lines:
        a = sh["start"] + LEAD
        b = min(sh["end"] - 0.1, a + d + 0.6)
        ev.append(f"Dialogue: 1,{ts(a)},{ts(b)},ZH,,0,0,0,,{sh['zh']}")
        ev.append(f"Dialogue: 1,{ts(a)},{ts(b)},EN,,0,0,0,,{sh['en']}")
        if cards:
            ev.append(f"Dialogue: 0,{ts(sh['start'])},{ts(sh['end'])},CardId,,0,0,0,,{sh['id']}")
            ev.append(f"Dialogue: 0,{ts(sh['start'])},{ts(sh['end'])},Card,,0,0,0,,（分鏡示意）\\N{sh['visual']}")
    out = ROOT / "subtitles" / ("animatic.ass" if cards else "bilingual.ass")
    out.write_text(head + "\n".join(ev) + "\n", encoding="utf-8")
    return out


def encode(video_args, vf, audio, out):
    run("ffmpeg", "-v", "error", "-y", *video_args, "-i", audio, "-filter_complex", vf,
        "-map", "[v]", "-map", f"{video_args.count('-i')}:a", "-t", DUR, "-r", FPS,
        "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p",
        "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", out)


def main():
    mode = sys.argv[1] if len(sys.argv) > 1 else "animatic"
    audio, lines = narration()
    (ROOT / "output").mkdir(exist_ok=True)
    if mode == "animatic":
        ass = subtitles(lines, cards=True)
        args, parts = [], []
        for i, sh in enumerate(SHOTS):
            args += ["-f", "lavfi", "-i",
                     f"color=c=0x{sh['color']}:s={W}x{H}:r={FPS}:d={sh['end'] - sh['start']}"]
            parts.append(f"[{i}:v]")
        vf = f"{''.join(parts)}concat=n={len(SHOTS)}:v=1:a=0,subtitles={ass.relative_to(ROOT)}[v]"
        out = ROOT / "output" / "animatic.mp4"
    elif mode == "final":
        ass = subtitles(lines, cards=False)
        args = ["-i", sys.argv[2]]
        vf = (f"[0:v]scale={W}:{H}:force_original_aspect_ratio=decrease,"
              f"pad={W}:{H}:(ow-iw)/2:(oh-ih)/2,subtitles={ass.relative_to(ROOT)}[v]")
        out = ROOT / "output" / "final.mp4"
    else:
        sys.exit(__doc__)
    encode(args, vf, audio, out)
    print(out, f"{dur(out):.2f}s")


if __name__ == "__main__":
    import os
    os.chdir(ROOT)
    main()
