"""Procedural sound effects for the story cue sheet (output/<name>/sfx.json, written by the Unity build).

Every sound is synthesised with ffmpeg (aevalsrc sine/bell partials, anoisesrc filtered noise), so there are
no third-party samples and no licences to track. build_bus() renders each (name, variant) once, places every
cue with adelay, adds a short reverb and returns one bus WAV of exactly `dur` seconds.

Bell partials use inharmonic ratios (1, 2.76, 5.40, 8.93), which read as glass or crystal; the voice band
(1-4 kHz) is kept light by putting most energy either below 1 kHz (body) or above 4 kHz (sparkle).
"""
import json, math, subprocess
from pathlib import Path

SR = 48000
# Mix-time balance per sound (multiplies the cue gain from the Unity build), tuned against the voice:
# low or noisy sounds need more level to read under speech than bright chimes do.
TRIM = {"whoosh": 1.8, "gate_off": 1.3, "dim": 1.4, "home": 1.6, "step": 1.3, "place": 0.85}
BELL = ((1.0, 1.0), (2.76, 0.42), (5.40, 0.20), (8.93, 0.10))


def _bell(f, d, amp=1.0, t0=0.0, ratios=BELL):
    """Struck bell at frequency f, decay d seconds, starting at t0."""
    tt = f"(t-{t0:.4f})"
    parts = [f"{a * amp:.4f}*sin(2*PI*{f * r:.2f}*{tt})*exp(-{tt}/{d / (1 + 0.6 * i):.4f})"
             for i, (r, a) in enumerate(ratios) if f * r < 16000]
    return f"gte(t,{t0:.4f})*(1-exp(-{tt}/0.003))*(" + "+".join(parts) + ")"


def _glide(f0, f1, T, amp, d, t0=0.0):
    """Exponential pitch glide f0 -> f1 over T, decaying with d."""
    r = f1 / f0; lr = math.log(r); tt = f"(t-{t0:.4f})"
    ph = f"(2*PI*({f0 * T / lr:.4f}*(pow({r:.6f},min({tt},{T})/{T})-1)+{f1:.3f}*max({tt}-{T},0)))"
    return f"gte(t,{t0:.4f})*{amp:.4f}*sin({ph})*(1-exp(-{tt}/0.01))*exp(-{tt}/{d:.4f})"


def _noise(d, f, w, env, amp, seed, color="pink"):
    """Band-passed noise with a volume envelope expression in t (eval per frame)."""
    return (f"anoisesrc=d={d}:c={color}:r={SR}:a={amp}:s={seed},bandpass=f={f}:width_type=o:w={w},"
            f"volume='{env}':eval=frame")


def _tone(expr, d):
    return f"aevalsrc='{expr}':s={SR}:d={d}"


# name -> (duration, list of source chains); variant shifts pitch or seed
def recipe(name, v):
    pent = [1.0, 9 / 8, 5 / 4, 3 / 2, 5 / 3]
    if name == "ping":                                             # crystal being polished
        f = 2093 * pent[v % 5]
        return 1.6, [_tone("+".join([_bell(f, 0.55, 0.30), _bell(f * 2, 0.25, 0.10, 0.03)]), 1.6)]
    if name == "step":                                             # soft footstep on crystal rock
        f = 4700 + 600 * v
        return 0.5, [_noise(0.5, 2400 + 400 * v, 1.2, "exp(-t/0.035)*min(t/0.002,1)", 0.7, 71 + v),
                     _tone(f"0.5*sin(2*PI*{140 + 15 * v}*t)*exp(-t/0.045)*(1-exp(-t/0.002))+" + _bell(f, 0.09, 0.12), 0.5)]
    if name == "glint":                                            # title sparkle
        return 1.2, [_tone("+".join(_bell(f, 0.35, 0.16, i * 0.07) for i, f in enumerate((4186, 5274, 6272))), 1.2)]
    if name == "riser":                                            # opening swell
        return 2.6, [_noise(2.6, 3000, 2.0, "0.9*pow(min(t/2.0,1),2)*(1-max(t-2.1,0)/0.5)", 0.6, 7),
                     _tone(_glide(220, 880, 2.2, 0.10, 3.0) + "*min(t/1.5,1)*(1-max(t-2.1,0)/0.5)", 2.6)]
    if name == "shimmer":                                          # star wakes up
        notes = [3136, 3951, 4699, 5274, 6272, 4186, 3520]
        return 2.0, [_tone("+".join(_bell(f, 0.5, 0.07, 0.11 * i) for i, f in enumerate(notes)), 2.0)]
    if name == "whoosh":                                           # star flies
        return 1.2, [_noise(1.2, 700, 1.4, "1.1*exp(-pow((t-0.32)/0.16,2))", 0.6, 11 + v),
                     _noise(1.2, 1800, 1.2, "0.8*exp(-pow((t-0.48)/0.15,2))", 0.6, 12 + v),
                     _noise(1.2, 5200, 1.0, "0.7*exp(-pow((t-0.62)/0.15,2))", 0.6, 13 + v)]
    if name == "swish":                                            # cut transition
        return 0.8, [_noise(0.8, 1200, 2.0, "0.6*exp(-pow((t-0.22)/0.12,2))", 0.5, 21 + v)]
    if name == "touch":                                            # fingertip meets star
        return 3.0, [_tone("+".join([_bell(1046.5, 1.6, 0.30), _bell(1568, 1.2, 0.18, 0.02), _bell(2637, 0.7, 0.08, 0.05),
                                     "0.10*sin(2*PI*523.25*t)*exp(-t/1.2)*(1-exp(-t/0.01))"]), 3.0)]
    if name == "gate_off":                                         # a star gate goes dark
        f = 330 * (1 - 0.06 * v)
        return 1.6, [_tone(_glide(f, f / 2, 0.6, 0.22, 0.6) + "+" + _glide(4200 - 150 * v, 2100, 0.5, 0.05, 0.35), 1.6),
                     _noise(1.6, 400, 1.5, "0.7*exp(-t/0.35)*min(t/0.02,1)", 0.6, 31 + v)]
    if name == "shrink":
        return 1.6, [_tone(_glide(1400, 620, 0.9, 0.16, 0.9) + "+" + _glide(2100, 930, 0.9, 0.06, 0.7), 1.6)]
    if name == "dim":                                              # the palace darkens
        env = "min(t/0.8,1)*exp(-max(t-0.8,0)/0.9)"
        return 3.0, [_tone(f"({env})*(0.20*sin(2*PI*110*t)+0.12*sin(2*PI*164.8*t)+0.06*sin(2*PI*220*t))", 3.0)]
    if name == "rise":                                             # star lifts off
        return 1.8, [_tone(_glide(600, 1800, 1.1, 0.12, 1.2) + "+" + "+".join(_bell(f, 0.4, 0.05, 0.18 * i) for i, f in enumerate((2637, 3136, 3951, 4699))), 1.8),
                     _noise(1.8, 2500, 1.5, "0.5*sin(PI*min(t/1.2,1))", 0.5, 41)]
    if name == "chart":                                            # star map lights up: arpeggio
        notes = [1046.5, 1318.5, 1568, 2093, 2637, 3136]
        return 2.6, [_tone("+".join(_bell(f, 0.9, 0.13, 0.12 * i) for i, f in enumerate(notes)), 2.6)]
    if name == "place":                                            # star returns to the armillary
        boom = "0.55*sin(2*PI*(55*t+40*(1-exp(-t/0.12))*0.12))*exp(-t/0.7)*(1-exp(-t/0.004))"
        chord = "+".join(_bell(f, 2.4, 0.16, 0.01 * i) for i, f in enumerate((523.25, 659.25, 784, 1046.5)))
        sparkle = "+".join(_bell(f, 0.6, 0.05, 0.06 + 0.09 * i) for i, f in enumerate((3136, 3951, 4699, 5274, 6272, 4186)))
        return 4.0, [_tone(f"{boom}+{chord}+{sparkle}", 4.0),
                     _noise(4.0, 900, 2.5, "0.9*exp(-t/0.25)*min(t/0.005,1)", 0.6, 51)]
    if name == "whirr":                                            # rings start turning
        env = "min(t/0.6,1)*exp(-max(t-0.8,0)/1.2)"
        return 3.4, [_tone(f"({env})*(0.5+0.5*sin(2*PI*(6+4*t)*t))*({_glide(1760, 2640, 2.5, 0.07, 9)}+{_glide(2640, 3960, 2.5, 0.04, 9)})", 3.4)]
    if name == "weave":                                            # the river lights up again
        notes = [523.25 * p * 2 ** (k // 5) for k, p in enumerate(pent * 3)][:13]
        return 3.6, [_tone("+".join(_bell(f, 0.6, 0.08, 0.19 * i) for i, f in enumerate(notes)), 3.6),
                     _noise(3.6, 3500, 1.5, "0.35*sin(PI*min(t/2.8,1))", 0.5, 61)]
    if name == "wink":
        return 1.5, [_tone("+".join(_bell(f, 0.4, 0.15, 0.08 * i) for i, f in enumerate((2637, 3136, 3951))), 1.5)]
    if name == "home":                                             # soft closing chord
        env = "min(t/1.0,1)*exp(-max(t-1.0,0)/1.0)"
        return 3.0, [_tone(f"({env})*(0.10*sin(2*PI*261.6*t)+0.08*sin(2*PI*329.6*t)+0.07*sin(2*PI*392*t)+0.04*sin(2*PI*493.9*t))", 3.0)]
    raise KeyError(name)


def render(name, v, out, peak=-6.0):
    """Render one sound, peak-normalised to `peak` dBFS so cue gains alone set the balance."""
    d, chains = recipe(name, v)
    graph = ";".join(f"{c}[s{i}]" for i, c in enumerate(chains))
    graph += f";{''.join(f'[s{i}]' for i in range(len(chains)))}amix=inputs={len(chains)}:normalize=0,afade=t=out:st={d - 0.15}:d=0.15[out]"
    raw = Path(out).with_suffix(".raw.wav")
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-filter_complex", graph, "-map", "[out]", "-ar", str(SR), "-ac", "1",
                    "-c:a", "pcm_f32le", str(raw)], check=True)
    o = subprocess.run(["ffmpeg", "-nostats", "-i", str(raw), "-af", "volumedetect", "-f", "null", "-"],
                       check=True, capture_output=True, text=True).stderr
    mx = float(next(l for l in o.splitlines() if "max_volume" in l).split(":")[1].split()[0])
    subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(raw), "-af", f"volume={peak - mx:.2f}dB", str(out)], check=True)
    raw.unlink()


def build_bus(sheet, workdir, dur):
    """Render the cue sheet to workdir/sfx_bus.wav (stereo, `dur` seconds). Returns the path."""
    cues = json.loads(Path(sheet).read_text(encoding="utf-8"))["cues"]
    wd = Path(workdir) / "sfx"; wd.mkdir(parents=True, exist_ok=True)
    files = {}
    for c in cues:
        key = (c["name"], c.get("variant", 0))
        if key not in files:
            files[key] = wd / f"{key[0]}_{key[1]}.wav"; render(*key, files[key])
    ins, flt = [], []
    for i, c in enumerate(cues):
        ins += ["-i", str(files[(c["name"], c.get("variant", 0))])]
        ms = int(round(c["t"] * 1000))
        flt.append(f"[{i}:a]volume={c.get('gain', 1.0) * TRIM.get(c['name'], 1.0):.3f},adelay={ms}:all=1[c{i}]")
    flt.append(f"{''.join(f'[c{i}]' for i in range(len(cues)))}amix=inputs={len(cues)}:normalize=0,"
               "aformat=channel_layouts=stereo,aecho=0.8:0.55:41|67|103|151:0.28|0.22|0.16|0.11,"
               f"apad=whole_dur={dur},atrim=0:{dur}[out]")
    out = Path(workdir) / "sfx_bus.wav"
    subprocess.run(["ffmpeg", "-v", "error", "-y", *ins, "-filter_complex", ";".join(flt), "-map", "[out]",
                    "-ar", str(SR), str(out)], check=True)
    return out
