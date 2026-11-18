// Atmosphere and event effects, mostly translucent, plus the sound-effect cue sheet.
//
// Screen-space layers (all sort below 900, so the vertical subtitle panel stays clean):
//   light shafts (additive, per-shot direction and colour), drifting fog bands, large out-of-focus bokeh,
//   floating dust motes, lens-flare ghosts for the big flashes, and a vignette.
// Event helpers used by the shots: Ripple (mtk-viewer style expanding rings), Burst (stateless particles:
// a pure function of time since the event, so stills match the video), and the flowing river ribbon.
// Every audible beat registers Sfx(name, t); Build writes them to STORY_SFX_OUT for scripts/story.py.
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public static partial class StoryBuilder
{
    static Texture2D shaftTex, fogTex, vignetteTex, ringTex, sparkTex, bokehTex, stripeTex;
    static string ShotNow = "A";
    static Scene SceneNow;

    // ---------- sound cue sheet ----------
    [Serializable] class SfxCue { public float t; public string name; public float gain; public int variant; }
    [Serializable] class SfxSheet { public List<SfxCue> cues = new List<SfxCue>(); }
    static readonly SfxSheet sfx = new SfxSheet();
    static void Sfx(string name, float t, float gain = 1f, int variant = 0)
    {
        if (t >= 0f && t < DUR) sfx.cues.Add(new SfxCue { t = (float)Math.Round(t, 3), name = name, gain = gain, variant = variant });
    }
    static void WriteSfx()
    {
        string p = Environment.GetEnvironmentVariable("STORY_SFX_OUT");
        if (string.IsNullOrEmpty(p)) return;
        sfx.cues.Sort((a, b) => a.t.CompareTo(b.t));
        File.WriteAllText(p, JsonUtility.ToJson(sfx, true));
        Debug.Log($"[Story] sfx cues {sfx.cues.Count} -> {p}");
    }

    // ---------- procedural textures ----------
    static Texture2D MakeTex(int w, int h, Func<float, float, float> alpha, TextureWrapMode wrap = TextureWrapMode.Clamp)
    {
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, true) { wrapMode = wrap, filterMode = FilterMode.Trilinear };
        var px = new Color[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) px[y * w + x] = new Color(1, 1, 1, Mathf.Clamp01(alpha((x + 0.5f) / w, (y + 0.5f) / h)));
        tex.SetPixels(px); tex.Apply(); return tex;
    }
    static float Gauss(float x, float s) => Mathf.Exp(-(x * x) / (s * s));

    static void InitFxTextures()
    {
        sfx.cues.Clear();
        shaftTex = MakeTex(64, 256, (u, v) => Gauss(u - 0.5f, 0.22f) * Mathf.Pow(v, 1.4f) * Mathf.SmoothStep(0f, 1f, (1f - v) / 0.06f));
        fogTex = MakeTex(256, 96, (u, v) =>
        {
            float a = u * Mathf.PI * 2f;     // sampled on a loop, so it tiles horizontally
            float n = 0.65f * Mathf.PerlinNoise(4f + 1.4f * Mathf.Cos(a) + v * 1.6f, 4f + 1.4f * Mathf.Sin(a) + v * 0.9f)
                    + 0.35f * Mathf.PerlinNoise(9f + 3.1f * Mathf.Cos(a) + v * 3.5f, 2f + 3.1f * Mathf.Sin(a) + v * 2.2f);
            return Mathf.SmoothStep(0f, 1f, (n - 0.32f) / 0.4f) * Mathf.Pow(Mathf.Sin(v * Mathf.PI), 1.6f);
        }, TextureWrapMode.Repeat);
        vignetteTex = MakeTex(128, 128, (u, v) => { float d = new Vector2(u - 0.5f, v - 0.5f).magnitude / 0.7071f; return Mathf.Pow(Mathf.SmoothStep(0f, 1f, (d - 0.5f) / 0.55f), 1.3f); });
        ringTex = MakeTex(256, 256, (u, v) => { float r = new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f; return (Gauss(r - 0.86f, 0.045f) + 0.18f * Gauss(r - 0.82f, 0.2f)) * Mathf.SmoothStep(0f, 1f, (1f - r) / 0.1f); });
        sparkTex = MakeTex(64, 64, (u, v) =>
        {
            float x = Mathf.Abs(u * 2f - 1f), y = Mathf.Abs(v * 2f - 1f);
            return Mathf.Exp(-y * 26f) * Mathf.Exp(-x * 3.2f) + Mathf.Exp(-x * 26f) * Mathf.Exp(-y * 3.2f) + Mathf.Exp(-(x * x + y * y) * 22f);
        });
        bokehTex = MakeTex(128, 128, (u, v) => { float r = new Vector2(u - 0.5f, v - 0.5f).magnitude * 2f; return Mathf.SmoothStep(0f, 1f, (1f - r) / 0.07f) * (0.55f + 0.45f * Mathf.SmoothStep(0f, 1f, (r - 0.55f) / 0.4f)); });
        stripeTex = MakeTex(128, 32, (u, v) => Mathf.Pow(0.5f + 0.5f * Mathf.Sin(u * Mathf.PI * 2f), 3f) * Gauss(v - 0.5f, 0.32f), TextureWrapMode.Repeat);
    }

    // ---------- event helpers ----------
    // mtk-viewer style touch: a dot plus rings that expand and fade one after another.
    class Ripple
    {
        readonly Glow[] rings; readonly Glow dot;
        public Ripple(Transform parent, int order, int n = 3)
        {
            rings = new Glow[n]; for (int i = 0; i < n; i++) rings[i] = new Glow(parent, Vector3.zero, 1, 1, order, true, ringTex);
            dot = new Glow(parent, Vector3.zero, 1, 1, order + 1);
        }
        // t0: start; r0..r1: radius in parent units; dur: one ring's life; gap: delay between rings
        public void Update(float t, float t0, Vector3 at, float r0, float r1, Color c, float k = 1f, float dur = 0.9f, float gap = 0.13f)
        {
            for (int i = 0; i < rings.Length; i++)
            {
                float u = (t - t0 - i * gap) / dur;
                if (u < 0f || u > 1f) { rings[i].Set(c, 0f); continue; }
                float r = Mathf.Lerp(r0, r1, OutCubic(u)), s = 2f * r / 0.86f;
                rings[i].At(at); rings[i].Size(s, s); rings[i].Set(c, k * 1.6f * (1f - u) * (1f - u) * Mathf.Clamp01(u * 12f));
            }
            float du = (t - t0) / (dur * 0.8f);
            dot.At(at); dot.Size(r0 * 3.2f, r0 * 3.2f); dot.Set(Color.Lerp(c, White, 0.5f), du < 0f || du > 1f ? 0f : k * 1.8f * (1f - du));
        }
    }

    // Particles that are a pure function of time since the burst.
    class Burst
    {
        readonly Glow[] p; readonly Vector2[] dir; readonly float[] spd, life, size, spin;
        readonly float grow, gravity; readonly bool additive;
        public Burst(Transform parent, int n, int seed, int order, Texture tex, float speed, float lifeMax, float sizeMax,
                     float grow = 0f, float gravity = 0f, bool additive = true, float spread = 360f, float aim = 90f)
        {
            this.grow = grow; this.gravity = gravity; this.additive = additive;
            var rnd = new System.Random(seed);
            p = new Glow[n]; dir = new Vector2[n]; spd = new float[n]; life = new float[n]; size = new float[n]; spin = new float[n];
            for (int i = 0; i < n; i++)
            {
                float a = (aim + ((float)rnd.NextDouble() - 0.5f) * spread) * Mathf.Deg2Rad;
                dir[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                spd[i] = speed * (0.35f + 0.65f * (float)rnd.NextDouble());
                life[i] = lifeMax * (0.55f + 0.45f * (float)rnd.NextDouble());
                size[i] = sizeMax * (0.45f + 0.55f * (float)rnd.NextDouble());
                spin[i] = ((float)rnd.NextDouble() - 0.5f) * 240f;
                p[i] = new Glow(parent, Vector3.zero, 1, 1, order, additive, tex);
            }
        }
        public void Update(float t, float t0, Vector3 at, Color c, float k = 1f)
        {
            float tau = t - t0;
            for (int i = 0; i < p.Length; i++)
            {
                if (tau < 0f || tau > life[i]) { p[i].Set(c, 0f); continue; }
                float u = tau / life[i];
                var d = dir[i] * spd[i] * (tau - 0.45f * tau * tau / life[i]) + new Vector2(0, gravity * tau * tau);
                p[i].At(at + (Vector3)d); float s = size[i] * (1f + grow * u) * (additive ? 1f - 0.4f * u : 1f);
                p[i].Size(s, s); p[i].t.localRotation = Quaternion.Euler(0, 0, spin[i] * tau);
                p[i].Set(c, k * Mathf.Pow(1f - u, 1.5f) * Mathf.Clamp01(tau * 14f) * (additive ? 1.3f : 0.55f));
            }
        }
    }

    // A strip along a polyline whose stripes flow forward; alpha is revealed from the start up to `reveal`.
    class Flow
    {
        readonly Mesh m = new Mesh(); readonly Material mat; readonly List<Vector3> pts = new List<Vector3>(); readonly float width;
        readonly List<Vector2> uv = new List<Vector2>(); readonly List<Color> col = new List<Color>(); float[] along;
        public Flow(Transform parent, Vector2[] path, float width, int order)
        {
            this.width = width;
            for (int i = 0; i <= 120; i++) pts.Add(Along(path, i / 120f));
            var v = new List<Vector3>(); var tri = new List<int>(); along = new float[pts.Count]; float acc = 0f;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0) acc += (pts[i] - pts[i - 1]).magnitude; along[i] = acc;
                var d = (pts[Mathf.Min(i + 1, pts.Count - 1)] - pts[Mathf.Max(i - 1, 0)]).normalized; var nn = new Vector3(-d.y, d.x) * width * 0.5f;
                v.Add(pts[i] + nn); v.Add(pts[i] - nn);
                if (i > 0) { int a = 2 * (i - 1); tri.Add(a); tri.Add(a + 2); tri.Add(a + 1); tri.Add(a + 1); tri.Add(a + 2); tri.Add(a + 3); }
            }
            m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateBounds();
            var r = Part(parent, m, Vector3.zero, White, order, add, stripeTex); mat = r.sharedMaterial;
        }
        public void Update(float t, float reveal, Color c, float k)
        {
            uv.Clear(); col.Clear(); float total = along[along.Length - 1];
            for (int i = 0; i < pts.Count; i++)
            {
                float s = along[i] / total, a = Mathf.Clamp01((reveal - s) / 0.08f) * Mathf.Sin(Mathf.Clamp01(s) * Mathf.PI * 0.98f + 0.03f);
                float u = along[i] / (width * 2.2f) - t * 1.3f;
                uv.Add(new Vector2(u, 0)); uv.Add(new Vector2(u, 1)); var cc = new Color(a, a, a, 1f); col.Add(cc); col.Add(cc);
            }
            m.SetUVs(0, uv); m.SetColors(col);
            mat.SetColor("_TintColor", new Color(c.r * k * 0.5f, c.g * k * 0.5f, c.b * k * 0.5f, 0.5f));
        }
    }

    // ---------- screen-space atmosphere ----------
    class Look { public Vector2 src; public float spread, shafts, haze; public Color beam, bokehA, bokehB, fog; }
    static Look LookFor(string id)
    {
        switch (id)
        {
            case "A": case "I": return new Look { src = new Vector2(0.08f, -0.25f), spread = 30f, shafts = 1.0f, haze = 1f, beam = Hex("FFE2B0"), bokehA = Hex("8FB8FF"), bokehB = Hex("FFD9A0"), fog = Hex("B9B2FF") };
            case "B": return new Look { src = new Vector2(0.12f, -0.3f), spread = 24f, shafts = 0.9f, haze = 1f, beam = Hex("E2C8FF"), bokehA = Hex("C79BFF"), bokehB = Hex("8FD0FF"), fog = Hex("B7A6FF") };
            case "C": case "D": return new Look { src = new Vector2(0.9f, -0.3f), spread = 26f, shafts = 0.8f, haze = 1f, beam = Hex("BFD8FF"), bokehA = Hex("7FB4FF"), bokehB = Hex("D2B0FF"), fog = Hex("A9B8FF") };
            case "E": return new Look { src = new Vector2(0.45f, -0.35f), spread = 34f, shafts = 0.7f, haze = 0.9f, beam = Hex("CFE6FF"), bokehA = Hex("8FC8FF"), bokehB = Hex("C3B2FF"), fog = Hex("AFC2FF") };
            case "F": return new Look { src = new Vector2(0.6f, -0.3f), spread = 22f, shafts = 0.6f, haze = 0.8f, beam = Hex("C9BCFF"), bokehA = Hex("8A7CFF"), bokehB = Hex("5AB8FF"), fog = Hex("8E88D8") };
            default: return new Look { src = new Vector2(0.92f, -0.25f), spread = 28f, shafts = 0.9f, haze = 1f, beam = Hex("FFE6B8"), bokehA = Hex("FFD27A"), bokehB = Hex("8FC0FF"), fog = Hex("B4B4FF") };  // G, H
        }
    }

    static Vector3 flareAt; static float flareK; static Color flareC;
    static void Flare(Vector3 world, float k, Color c) { if (k > flareK) { flareAt = world; flareK = k; flareC = c; } }

    static void BuildFx()
    {
        const int SHAFT = 860, FOG = 850, BOKEH = 870, DUST = 865, FLARE = 875, VIG = 880;
        float ph = VH;                                           // picture height (vertical keeps a panel below)
        var looks = new Dictionary<string, Look>(); foreach (var s in TL.shots) looks[s.id] = LookFor(s.id);

        var shafts = new Glow[4];
        for (int i = 0; i < shafts.Length; i++) shafts[i] = new Glow(null, Vector3.zero, 1, 1, SHAFT, true, shaftTex);
        var fog = new MeshRenderer[2];
        for (int i = 0; i < 2; i++)
        {
            var m = Quad(2f * W, 1f); m.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(2, 0), new Vector2(2, 1), new Vector2(0, 1) });
            fog[i] = Part(null, m, Vector3.zero, White, FOG + i, null, fogTex);
        }
        var rnd = new System.Random(5);
        int nb = 11, nd = 46;
        var bokeh = new Glow[nb]; var bHome = new Vector2[nb]; var bSize = new float[nb]; var bPh = new float[nb];
        for (int i = 0; i < nb; i++) { bokeh[i] = new Glow(null, Vector3.zero, 1, 1, BOKEH, true, bokehTex); bHome[i] = new Vector2((float)rnd.NextDouble(), (float)rnd.NextDouble()); bSize[i] = 70f + 150f * (float)rnd.NextDouble(); bPh[i] = (float)rnd.NextDouble() * 20f; }
        var dust = new Glow[nd]; var dHome = new Vector2[nd]; var dSize = new float[nd]; var dPh = new float[nd]; var dSpd = new float[nd];
        for (int i = 0; i < nd; i++)
        {
            bool sp = i % 6 == 0;
            dust[i] = new Glow(null, Vector3.zero, 1, 1, DUST, true, sp ? sparkTex : glowTex);
            dHome[i] = new Vector2((float)rnd.NextDouble(), (float)rnd.NextDouble()); dSize[i] = sp ? 22f + 16f * (float)rnd.NextDouble() : 5f + 7f * (float)rnd.NextDouble();
            dPh[i] = (float)rnd.NextDouble() * 30f; dSpd[i] = 8f + 18f * (float)rnd.NextDouble();
        }
        var flare = new Glow[6]; float[] fPos = { -0.35f, 0.25f, 0.55f, 0.85f, 1.2f, 1.55f }, fSize = { 60f, 110f, 46f, 160f, 80f, 210f };
        for (int i = 0; i < flare.Length; i++) flare[i] = new Glow(null, Vector3.zero, 1, 1, FLARE, true, i % 2 == 0 ? glowTex : bokehTex);
        var flareStreak = new Glow(null, Vector3.zero, 1, 1, FLARE, true, glowTex);
        Part(null, Quad(W, ph), Px(W / 2f, ph / 2f), new Color(0.02f, 0.02f, 0.08f, 0.62f), VIG, null, vignetteTex);

        anims.Add(t =>
        {
            var L = looks.TryGetValue(ShotNow, out var l) ? l : LookFor("G");
            float lum = SceneNow != null ? SceneNow.bg.color.g : 1f;                 // shafts and haze follow the scene's darkening
            Vector3 src = Px(L.src.x * W, L.src.y * ph);
            for (int i = 0; i < shafts.Length; i++)
            {
                float a = (-L.spread / 2f + L.spread * i / (shafts.Length - 1) + 2.2f * Mathf.Sin(t * 0.21f + i * 1.9f));
                Vector3 toward = Px(W / 2f, ph * 0.65f) - src; float baseAng = Mathf.Atan2(toward.x, -toward.y) * Mathf.Rad2Deg;   // 0 = straight down
                float ang = (baseAng + a) * Mathf.Deg2Rad; var dir = new Vector3(Mathf.Sin(ang), -Mathf.Cos(ang));
                float len = 1.5f * Mathf.Max(W, ph), wid = (130f + 90f * Mathf.Sin(i * 2.3f + 1f)) * (W / 1080f) * (1f + 0.15f * Mathf.Sin(t * 0.37f + i));
                shafts[i].At(src + dir * len * 0.5f); shafts[i].Size(wid, len);
                shafts[i].t.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(dir.x, -dir.y) * Mathf.Rad2Deg);
                shafts[i].Set(L.beam, L.shafts * lum * 0.11f * (0.55f + 0.45f * Mathf.Sin(t * 0.45f + i * 1.7f)));
            }
            for (int i = 0; i < 2; i++)
            {
                float speed = i == 0 ? 14f : -9f, x = Mathf.Repeat(t * speed + i * 300f, W);
                fog[i].transform.position = Px(W / 2f + x - W / 2f, ph - (i == 0 ? 120f : 260f) * (ph / 1080f)) ;
                fog[i].transform.localScale = new Vector3(1f, (i == 0 ? 260f : 200f) * (ph / 1080f), 1f);
                var c = L.fog; c.a = L.haze * (i == 0 ? 0.16f : 0.10f) * Mathf.Lerp(0.5f, 1f, lum); fog[i].sharedMaterial.color = c;
            }
            for (int i = 0; i < nb; i++)
            {
                float x = bHome[i].x * W + 60f * Mathf.Sin(t * 0.06f + bPh[i]), y = bHome[i].y * ph + 40f * Mathf.Cos(t * 0.05f + bPh[i]) - t * 3f % ph;
                y = Mathf.Repeat(y, ph + 200f) - 100f;
                bokeh[i].At(Px(x, y)); float s = bSize[i] * (W / 1080f); bokeh[i].Size(s, s);
                bokeh[i].Set(i % 2 == 0 ? L.bokehA : L.bokehB, L.haze * lum * (0.12f + 0.07f * Mathf.Sin(t * 0.4f + bPh[i])));
            }
            for (int i = 0; i < nd; i++)
            {
                float y = Mathf.Repeat(dHome[i].y * (ph + 60f) - t * dSpd[i] - dPh[i] * 10f, ph + 60f) - 30f;
                float x = dHome[i].x * W + 18f * Mathf.Sin(t * 0.7f + dPh[i]);
                dust[i].At(Px(x, y)); dust[i].Size(dSize[i], dSize[i]); dust[i].t.localRotation = Quaternion.Euler(0, 0, t * 20f + dPh[i] * 30f);
                float tw = 0.35f + 0.65f * Mathf.Pow(0.5f + 0.5f * Mathf.Sin(t * (1.3f + dPh[i] % 1.7f) + dPh[i]), 3f);
                dust[i].Set(Color.Lerp(White, L.bokehA, 0.35f), L.haze * Mathf.Lerp(0.45f, 1f, lum) * 0.75f * tw);
            }
            // lens flare ghosts along the line from the flash through the picture centre
            var centre = Px(W / 2f, ph / 2f);
            for (int i = 0; i < flare.Length; i++)
            {
                var pos = flareAt + (centre - flareAt) * fPos[i] * 1.6f; float s = fSize[i] * (W / 1080f);
                flare[i].At(pos); flare[i].Size(s, s); flare[i].Set(Color.Lerp(flareC, i % 3 == 1 ? Hex("FFB0E0") : Hex("A0FFE8"), 0.35f), flareK * 0.22f);
            }
            flareStreak.At(flareAt); flareStreak.Size(W * 1.1f, 14f * (W / 1080f)); flareStreak.Set(Color.Lerp(flareC, White, 0.3f), flareK * 0.9f);
        });
    }
}
