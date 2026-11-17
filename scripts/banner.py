#!/usr/bin/env python3
"""Render the README / social-preview banner.

    python3 scripts/banner.py

Inputs : stories/star_city/refs_2x/Image_1.png (background)
         output/star_city/final.mp4            (phone screen frame; run the story pipeline first)
Outputs: docs/banner/banner.png          2560x1280 (README)
         docs/banner/social-preview.png  1280x640  (GitHub Settings > Social preview)

All text and layout live in the constants below, so the banner can be restyled
without touching the ffmpeg graph.
"""
import shutil
import subprocess
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
FFMPEG = shutil.which("ffmpeg") or str(Path.home() / ".local/bin/ffmpeg")
W, H = 2560, 1280

BG = ROOT / "stories/star_city/refs_2x/Image_1.png"
SCREEN_VIDEO = ROOT / "output/star_city/final.mp4"
SCREEN_TIME = 22.0                 # star touching Cheng's fingertip

EYEBROW = "FABLE 5.2  ×  UNITY 6.3"
TITLE = ["Fable Video", "Generation"]
TAGLINE_ZH = "AI 寫腳本、Unity 算圖，自動產出雙語短影音"
TAGLINE_EN = "From script and narration to a Reels-ready vertical video"
PILLS = ["1080×1920", "30 fps", "中英雙語字幕", "Reels Ready"]

# phone mockup (2x canvas pixels)
PHONE_H = 1040
PHONE_W = round(PHONE_H * 1080 / 1920)
PHONE_X, PHONE_Y = 1700, 120
SCREEN_R = 46                     # screen corner radius
TOUCH = (500, 800)                # touch point in 1080x1920 frame coords
TOUCH_RGB = "FAC85A"              # ASS colours are BGR: this is #5AC8FA

ZH_FONT, EN_FONT = "PingFang TC", "Helvetica Neue"


def circle(cx, cy, r):
    k = 0.5523 * r
    return (f"m {cx + r} {cy} b {cx + r} {cy + k} {cx + k} {cy + r} {cx} {cy + r} "
            f"{cx - k} {cy + r} {cx - r} {cy + k} {cx - r} {cy} "
            f"{cx - r} {cy - k} {cx - k} {cy - r} {cx} {cy - r} "
            f"{cx + k} {cy - r} {cx + r} {cy - k} {cx + r} {cy}")


def rrect(x, y, w, h, r):
    k = 0.4477 * r                # distance from corner to bezier handle
    x2, y2 = x + w, y + h
    return (f"m {x + r} {y} l {x2 - r} {y} b {x2 - k} {y} {x2} {y + k} {x2} {y + r} "
            f"l {x2} {y2 - r} b {x2} {y2 - k} {x2 - k} {y2} {x2 - r} {y2} "
            f"l {x + r} {y2} b {x + k} {y2} {x} {y2 - k} {x} {y2 - r} "
            f"l {x} {y + r} b {x} {y + k} {x + k} {y} {x + r} {y}")


def text_width(s, size):
    return sum(size * (1.0 if ord(c) > 0x2E80 else 0.56) for c in s)


def ass(events):
    head = f"""[Script Info]
ScriptType: v4.00+
PlayResX: {W}
PlayResY: {H}
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: D,{EN_FONT},20,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    return head + "".join(f"Dialogue: {l},0:00:00.00,0:00:01.00,D,,0,0,0,,{t}\n" for l, t in events)


def back_layer():
    """Drawn under the phone: glow and bezel."""
    cx, cy = PHONE_X + PHONE_W / 2, PHONE_Y + PHONE_H / 2
    pad = 16
    return [
        (0, r"{\an7\pos(0,0)\bord0\blur90\1c&HF2A04A&\1a&H70&\p1}" + circle(cx, cy, 520)),
        (1, r"{\an7\pos(0,0)\bord0\blur60\1c&HE0589A&\1a&H90&\p1}" + circle(cx - 260, cy + 280, 300)),
        (2, r"{\an7\pos(0,0)\bord3\blur1\1c&H1A0E0B&\3c&H6E5048&\3a&H30&\p1}"
            + rrect(PHONE_X - pad, PHONE_Y - pad, PHONE_W + 2 * pad, PHONE_H + 2 * pad, SCREEN_R + pad)),
    ]


def front_layer():
    """Drawn over everything: copy, pills, touch rings."""
    x = 170
    ev = [
        (5, rf"{{\an7\pos({x},300)\fn{EN_FONT}\fs40\b1\fsp9\1c&HFFD38F&}}{EYEBROW}"),
        (5, rf"{{\an7\pos({x - 8},360)\fn{EN_FONT}\fs168\b1\fsp-3\1c&HFFFFFF&\shad0\bord0}}{TITLE[0]}"),
        (5, rf"{{\an7\pos({x - 8},540)\fn{EN_FONT}\fs168\b1\fsp-3\1c&HFFFFFF&\shad0\bord0}}{TITLE[1]}"),
        (5, rf"{{\an7\pos({x},770)\fn{ZH_FONT}\fs60\b1\1c&HF5E6E8&}}{TAGLINE_ZH}"),
        (5, rf"{{\an7\pos({x},858)\fn{EN_FONT}\fs38\i1\1c&HC8B4B0&}}{TAGLINE_EN}"),
        # accent rule
        (5, rf"{{\an7\pos(0,0)\bord0\1c&HFFD38F&\p1}}" + rrect(x, 262, 120, 8, 4)),
    ]
    px, py, size, padx, h = x, 990, 36, 30, 72
    for label in PILLS:
        w = text_width(label, size) + 2 * padx
        ev.append((5, rf"{{\an7\pos(0,0)\bord2\1c&H2A1A14&\1a&H40&\3c&HFFD38F&\3a&H70&\p1}}" + rrect(px, py, w, h, h / 2)))
        font = ZH_FONT if any(ord(c) > 0x2E80 for c in label) else EN_FONT
        ev.append((6, rf"{{\an5\pos({px + w / 2},{py + h / 2})\fn{font}\fs{size}\b1\1c&HFFFFFF&}}{label}"))
        px += w + 22
    # mtk-viewer style touch: dot plus expanding rings
    s = PHONE_H / 1920
    tx, ty = PHONE_X + TOUCH[0] * s, PHONE_Y + TOUCH[1] * s
    for r, a in ((70, "40"), (120, "88"), (170, "C0")):
        ev.append((7, rf"{{\an7\pos(0,0)\bord5\1a&HFF&\3c&H{TOUCH_RGB}&\3a&H{a}&\p1}}" + circle(tx, ty, r)))
    ev.append((7, rf"{{\an7\pos(0,0)\bord0\blur2\1c&H{TOUCH_RGB}&\1a&H30&\p1}}" + circle(tx, ty, 26)))
    ev.append((8, rf"{{\an5\pos({tx},{ty})\fn{EN_FONT}\fs30\b1\1c&HFFFFFF&}}1"))
    return ev


def main():
    out = ROOT / "docs/banner"
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        back, front = tmp / "back.ass", tmp / "front.ass"
        back.write_text(ass(back_layer()), encoding="utf-8")
        front.write_text(ass(front_layer()), encoding="utf-8")
        r = SCREEN_R
        round_alpha = (f"if(lte(pow(max(max({r}-X\\,X-(W-1-{r}))\\,0)\\,2)"
                       f"+pow(max(max({r}-Y\\,Y-(H-1-{r}))\\,0)\\,2)\\,{r * r})\\,255\\,0)")
        # left-to-right shade: dark under the copy, light over the phone
        shade = "255*(0.42+0.50*(1-clip((X/W-0.28)/0.34\\,0\\,1)))+40*pow(Y/H\\,3)"
        graph = (
            f"[0:v]scale={W}:-1,crop={W}:{H}:0:(ih-{H})*0.45,format=rgba[bg];"
            f"color=c=0x070A1A:s={W}x{H},format=rgba,geq=r=7:g=10:b=26:a='clip({shade}\\,0\\,255)'[sh];"
            f"[bg][sh]overlay[b1];[b1]subtitles={back}[b2];"
            f"[1:v]scale={PHONE_W}:{PHONE_H}:flags=lanczos,format=rgba,"
            f"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='{round_alpha}'[ph];"
            f"[b2][ph]overlay={PHONE_X}:{PHONE_Y}[b3];[b3]subtitles={front},format=rgb24[o];"
            f"[o]split[a][b];[b]scale=1280:640:flags=lanczos[s]"
        )
        subprocess.run([FFMPEG, "-v", "error", "-y", "-i", str(BG),
                        "-ss", str(SCREEN_TIME), "-i", str(SCREEN_VIDEO),
                        "-filter_complex", graph,
                        "-map", "[a]", "-frames:v", "1", str(out / "banner.png"),
                        "-map", "[s]", "-frames:v", "1", str(out / "social-preview.png")], check=True)
    print(out / "banner.png")


if __name__ == "__main__":
    main()
