#!/usr/bin/env python3
"""Render the README / social-preview banner.

    python3 scripts/banner.py

Inputs : stories/star_city/refs_2x/Image_1.png          (background)
         output/star_city/horizontal/final.mp4          (16:9 screen frame)
         output/star_city/vertical/final.mp4            (9:16 phone frame)
         Run `python3 scripts/story.py star_city` first to produce both videos.
Outputs: docs/banner/banner.png          2560x1280 (README)
         docs/banner/social-preview.jpg  1280x640  (GitHub Settings > Social preview)

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
WIDE_VIDEO = ROOT / "output/star_city/horizontal/final.mp4"
WIDE_TIME = 49.8                   # armillary rings glowing after the star returns
SCREEN_VIDEO = ROOT / "output/star_city/vertical/final.mp4"
SCREEN_TIME = 22.0                 # star touching Cheng's fingertip

EYEBROW = "CLAUDE  ×  UNITY"
TITLE = ["Unity Video", "Generation"]
TAGLINE_ZH = "AI 寫腳本、Unity 算圖，自動產出雙語短影音"
TAGLINE_EN = "From script and narration to vertical and horizontal videos"
PILLS = ["橫式 16:9", "直式 9:16", "中英雙語字幕", "30 fps"]

# devices (2x canvas pixels): 16:9 screen behind, 9:16 phone in front
WIDE_W = 880
WIDE_H = round(WIDE_W * 9 / 16)
WIDE_X, WIDE_Y = 1560, 170
WIDE_R = 18
PHONE_H = 840
PHONE_W = round(PHONE_H * 1080 / 1920)
PHONE_X, PHONE_Y = 1300, 330
PHONE_R = 40
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


def ass(events, w=W, h=H):
    head = f"""[Script Info]
ScriptType: v4.00+
PlayResX: {w}
PlayResY: {h}
WrapStyle: 2
ScaledBorderAndShadow: yes

[V4+ Styles]
Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding
Style: D,{EN_FONT},20,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,0,0,7,0,0,0,1

[Events]
Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text
"""
    return head + "".join(f"Dialogue: {l},0:00:00.00,0:00:01.00,D,,0,0,0,,{t}\n" for l, t in events)


def bezel(x, y, w, h, r, pad):
    return (r"{\an7\pos(0,0)\bord3\blur1\1c&H1A0E0B&\3c&H6E5048&\3a&H30&\p1}"
            + rrect(x - pad, y - pad, w + 2 * pad, h + 2 * pad, r + pad))


def back_layer():
    """Drawn under the landscape screen: its glow and bezel."""
    cx, cy = WIDE_X + WIDE_W / 2, WIDE_Y + WIDE_H / 2
    return [
        (0, r"{\an7\pos(0,0)\bord0\blur90\1c&HF2A04A&\1a&H70&\p1}" + circle(cx, cy, 460)),
        (1, bezel(WIDE_X, WIDE_Y, WIDE_W, WIDE_H, WIDE_R, 14)),
    ]


def mid_layer():
    """Drawn over the landscape screen, under the phone: phone glow and bezel."""
    cx, cy = PHONE_X + PHONE_W / 2, PHONE_Y + PHONE_H / 2
    return [
        (0, r"{\an7\pos(0,0)\bord0\blur70\1c&HE0589A&\1a&H90&\p1}" + circle(cx, cy + 120, 330)),
        (0, r"{\an7\pos(0,0)\bord0\blur30\1c&H000000&\1a&H60&\p1}"
            + rrect(PHONE_X + 30, PHONE_Y + 40, PHONE_W, PHONE_H, PHONE_R)),
        (1, bezel(PHONE_X, PHONE_Y, PHONE_W, PHONE_H, PHONE_R, 14)),
    ]


def chip(x, y, label, anchor_right=False):
    size, padx, h = 28, 18, 46
    w = text_width(label, size) + 2 * padx
    if anchor_right:
        x -= w
    return [
        (5, rf"{{\an7\pos(0,0)\bord0\1c&HFFD38F&\1a&H10&\p1}}" + rrect(x, y, w, h, 10)),
        (6, rf"{{\an5\pos({x + w / 2},{y + h / 2})\fn{EN_FONT}\fs{size}\b1\1c&H1A0E0B&}}{label}"),
    ]


def front_layer():
    """Drawn over everything: copy, pills, format chips, touch rings."""
    x = 170
    ev = [
        (5, rf"{{\an7\pos(0,0)\bord0\1c&HFFD38F&\p1}}" + rrect(x, 262, 120, 8, 4)),
        (5, rf"{{\an7\pos({x},300)\fn{EN_FONT}\fs40\b1\fsp9\1c&HFFD38F&}}{EYEBROW}"),
        (5, rf"{{\an7\pos({x - 8},360)\fn{EN_FONT}\fs168\b1\fsp-3\1c&HFFFFFF&}}{TITLE[0]}"),
        (5, rf"{{\an7\pos({x - 8},540)\fn{EN_FONT}\fs168\b1\fsp-3\1c&HFFFFFF&}}{TITLE[1]}"),
        (5, rf"{{\an7\pos({x},770)\fn{ZH_FONT}\fs60\b1\1c&HF5E6E8&}}{TAGLINE_ZH}"),
        (5, rf"{{\an7\pos({x},858)\fn{EN_FONT}\fs38\i1\1c&HC8B4B0&}}{TAGLINE_EN}"),
    ]
    px, py, size, padx, h = x, 990, 36, 30, 72
    for label in PILLS:
        w = text_width(label, size) + 2 * padx
        ev.append((5, rf"{{\an7\pos(0,0)\bord2\1c&H2A1A14&\1a&H40&\3c&HFFD38F&\3a&H70&\p1}}" + rrect(px, py, w, h, h / 2)))
        font = ZH_FONT if any(ord(c) > 0x2E80 for c in label) else EN_FONT
        ev.append((6, rf"{{\an5\pos({px + w / 2},{py + h / 2})\fn{font}\fs{size}\b1\1c&HFFFFFF&}}{label}"))
        px += w + 22
    ev += chip(WIDE_X + WIDE_W, WIDE_Y + WIDE_H + 34, "16:9", anchor_right=True)
    ev += chip(PHONE_X + PHONE_W + 34, PHONE_Y + PHONE_H - 46, "9:16")
    # mtk-viewer style touch: dot plus expanding rings
    s = PHONE_H / 1920
    tx, ty = PHONE_X + TOUCH[0] * s, PHONE_Y + TOUCH[1] * s
    for r, a in ((60, "40"), (105, "88"), (150, "C0")):
        ev.append((7, rf"{{\an7\pos(0,0)\bord5\1a&HFF&\3c&H{TOUCH_RGB}&\3a&H{a}&\p1}}" + circle(tx, ty, r)))
    ev.append((7, rf"{{\an7\pos(0,0)\bord0\blur2\1c&H{TOUCH_RGB}&\1a&H30&\p1}}" + circle(tx, ty, 24)))
    ev.append((8, rf"{{\an5\pos({tx},{ty})\fn{EN_FONT}\fs28\b1\1c&HFFFFFF&}}1"))
    return ev


def round_alpha(r):
    return (f"if(lte(pow(max(max({r}-X\\,X-(W-1-{r}))\\,0)\\,2)"
            f"+pow(max(max({r}-Y\\,Y-(H-1-{r}))\\,0)\\,2)\\,{r * r})\\,255\\,0)")


def rounded(label_in, w, h, r, label_out):
    return (f"[{label_in}]scale={w}:{h}:flags=lanczos,format=rgba,"
            f"geq=r='r(X,Y)':g='g(X,Y)':b='b(X,Y)':a='{round_alpha(r)}'[{label_out}];")


def main():
    out = ROOT / "docs/banner"
    out.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        files = {}
        for name, ev, size in (("back", back_layer(), (W, H)),
                               ("mid", mid_layer(), (W, H)), ("front", front_layer(), (W, H))):
            files[name] = tmp / f"{name}.ass"
            files[name].write_text(ass(ev, *size), encoding="utf-8")
        # left-to-right shade: dark under the copy, lighter behind the devices
        shade = "255*(0.42+0.50*(1-clip((X/W-0.28)/0.34\\,0\\,1)))+40*pow(Y/H\\,3)"
        graph = (
            f"[0:v]scale={W}:-1,crop={W}:{H}:0:(ih-{H})*0.45,format=rgba[bg];"
            f"color=c=0x070A1A:s={W}x{H},format=rgba,geq=r=7:g=10:b=26:a='clip({shade}\\,0\\,255)'[sh];"
            f"[bg][sh]overlay,subtitles={files['back']}[b1];"
            + rounded("1:v", WIDE_W, WIDE_H, WIDE_R, "wide")
            + f"[b1][wide]overlay={WIDE_X}:{WIDE_Y},subtitles={files['mid']}[b2];"
            + rounded("2:v", PHONE_W, PHONE_H, PHONE_R, "ph")
            + f"[b2][ph]overlay={PHONE_X}:{PHONE_Y},subtitles={files['front']},format=rgb24,split[a][b];"
            f"[b]scale=1280:640:flags=lanczos[s]"
        )
        subprocess.run([FFMPEG, "-v", "error", "-y", "-i", str(BG), "-ss", str(WIDE_TIME), "-i", str(WIDE_VIDEO),
                        "-ss", str(SCREEN_TIME), "-i", str(SCREEN_VIDEO),
                        "-filter_complex", graph,
                        "-map", "[a]", "-frames:v", "1", str(out / "banner.png"),
                        "-map", "[s]", "-frames:v", "1", "-q:v", "2", str(out / "social-preview.jpg")], check=True)
    print(out / "banner.png")


if __name__ == "__main__":
    main()
