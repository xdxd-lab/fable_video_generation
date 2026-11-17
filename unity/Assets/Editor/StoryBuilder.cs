// "Star City" story: painted reference images as backdrops (pan/zoom), with a procedural character,
// star, glows and rings on top. Timing comes from timeline.json (built from the narration audio).
//
//   Unity -batchmode -projectPath unity -executeMethod StoryBuilder.Render
// Environment: STORY_REFS (image dir), STORY_TIMELINE (json), REEL_OUT, REEL_STILLS (optional), REEL_FFMPEG,
//              STORY_FORMAT = vertical (1080x1920, picture on top + subtitle panel) | horizontal (1920x1080, full frame)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class StoryBuilder
{
    const int FPS = 30, UI = 5;
    const float IW = 1024f, IH = 576f;
    static bool Wide;                   // horizontal 16:9 output
    static int W = 1080, H = 1920;
    static float VH = 1340f;            // picture area height; vertical keeps a subtitle panel below it
    static float S0 = VH / IH;          // screen px per image px at zoom 1
    static float ZK = 1f;               // extra zoom so horizontal shots still have room to pan
    static float TY = 250f;             // title line y

    static void SetFormat(string f)
    {
        Wide = f == "horizontal";
        if (!Wide && !string.IsNullOrEmpty(f) && f != "vertical") throw new Exception("unknown STORY_FORMAT " + f);
        W = Wide ? 1920 : 1080; H = Wide ? 1080 : 1920; VH = Wide ? 1080f : 1340f;
        S0 = VH / IH; ZK = Wide ? 1.12f : 1f; TY = Wide ? 150f : 250f;
    }

    [Serializable] class Cue { public string shot; public float start, end; }
    [Serializable] class Shot { public string id; public float start, end; }
    [Serializable] class Timeline { public float duration; public Cue[] cues; public Shot[] shots; }

    static Timeline TL;
    static float DUR;
    static Camera cam;
    static Font zh;
    static Shader flat, add;
    static Texture2D glowTex;
    static Material curtain;
    static readonly List<Action<float>> anims = new List<Action<float>>();
    static readonly Dictionary<string, Scene> sceneOf = new Dictionary<string, Scene>();
    static readonly List<Scene> scenes = new List<Scene>();

    static Color Hex(string h, float a = 1f) { ColorUtility.TryParseHtmlString("#" + h, out var c); c.a = a; return c; }
    static readonly Color Blue = Hex("5AB8FF"), Gold = Hex("FFD27A"), White = Color.white, Night = Hex("07081A");

    // ---------- timing ----------
    static float C(int i) => TL.cues[i].start;
    static float CE(int i) => TL.cues[i].end;
    static Shot Sh(string id) { foreach (var s in TL.shots) if (s.id == id) return s; throw new Exception("no shot " + id); }
    static float U(float t, float start, float dur) => Mathf.Clamp01((t - start) / Mathf.Max(1e-4f, dur));
    static float Smooth(float x) => x * x * (3f - 2f * x);
    static float OutCubic(float x) => 1f - Mathf.Pow(1f - x, 3f);
    static float OutBack(float x) { const float c1 = 1.70158f, c3 = c1 + 1f; return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f); }
    static float Pulse(float t, float start, float rise, float fall) => t < start ? 0f : t < start + rise ? (t - start) / rise : Mathf.Clamp01(1f - (t - start - rise) / fall);
    static float Noise(float x) => Mathf.PerlinNoise(x, 0.37f);

    // ---------- meshes ----------
    static Vector3 Px(float x, float y) => new Vector3(x - W / 2f, H / 2f - y, 0f);
    static Vector3 Img(float ix, float iy) => new Vector3(ix - IW / 2f, IH / 2f - iy, 0f);
    static Vector3 Img(Vector2 p) => Img(p.x, p.y);

    static Mesh Quad(float w, float h, Color? top = null, Color? bottom = null)
    {
        var m = new Mesh();
        m.SetVertices(new List<Vector3> { new Vector3(-w / 2, -h / 2), new Vector3(w / 2, -h / 2), new Vector3(w / 2, h / 2), new Vector3(-w / 2, h / 2) });
        m.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
        if (top != null) { var b = bottom ?? top.Value; m.SetColors(new List<Color> { b, b, top.Value, top.Value }); }
        m.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0); m.RecalculateBounds(); return m;
    }

    static Mesh Poly(params float[] xy)
    {
        var v = new List<Vector3>(); for (int i = 0; i < xy.Length; i += 2) v.Add(new Vector3(xy[i], xy[i + 1]));
        var tri = new List<int>(); for (int i = 1; i < v.Count - 1; i++) { tri.Add(0); tri.Add(i); tri.Add(i + 1); }
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateBounds(); return m;
    }

    static Mesh Disc(float rx, float ry)
    {
        var xy = new float[64]; for (int i = 0; i < 32; i++) { float a = i * Mathf.PI / 16f; xy[2 * i] = Mathf.Cos(a) * rx; xy[2 * i + 1] = Mathf.Sin(a) * ry; }
        return Poly(xy);
    }

    static void EllipseRing(Mesh m, float rx, float ry, float width)
    {
        const int n = 96; var v = new List<Vector3>(); var tri = new List<int>();
        for (int i = 0; i < n; i++)
        {
            float a = i * Mathf.PI * 2f / n; var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a));
            v.Add(new Vector3(d.x * (rx + width / 2), d.y * (ry + width / 2))); v.Add(new Vector3(d.x * (rx - width / 2), d.y * (ry - width / 2)));
        }
        for (int i = 0; i < n; i++) { int a = 2 * i, b = 2 * ((i + 1) % n); tri.Add(a); tri.Add(b); tri.Add(a + 1); tri.Add(b); tri.Add(b + 1); tri.Add(a + 1); }
        m.Clear(); m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateBounds();
    }

    static MeshRenderer Part(Transform parent, Mesh mesh, Vector3 local, Color c, int order, Shader sh = null, Texture tex = null)
    {
        var go = new GameObject("part") { layer = UI };
        go.transform.SetParent(parent, false); go.transform.localPosition = local;
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = new Material(sh ?? flat) { color = c };
        if (tex != null) r.sharedMaterial.mainTexture = tex;
        r.sortingOrder = order; return r;
    }

    // ---------- glows ----------
    class Glow
    {
        public Transform t; Material m; bool additive;
        public Glow(Transform parent, Vector3 local, float w, float h, int order, bool additive = true)
        {
            this.additive = additive;
            var r = Part(parent, Quad(1, 1), local, White, order, additive ? add : flat, glowTex);
            t = r.transform; m = r.sharedMaterial; t.localScale = new Vector3(w, h, 1); Set(White, 0f);
        }
        public void Set(Color c, float a)
        {
            a = Mathf.Max(0f, a);
            if (additive && m.HasProperty("_TintColor")) m.SetColor("_TintColor", new Color(c.r * a * 0.5f, c.g * a * 0.5f, c.b * a * 0.5f, 0.5f));
            else { c.a = Mathf.Clamp01(a); m.color = c; }
        }
        public void At(Vector3 local) => t.localPosition = local;
        public void Size(float w, float h) => t.localScale = new Vector3(w, h, 1);
    }

    // A little star: soft halo, white core and two thin spikes.
    class Star
    {
        Glow aura, halo, core, sh, sv; Transform root;
        public Star(Transform parent, int order = 40)
        {
            root = new GameObject("star") { layer = UI }.transform; root.SetParent(parent, false);
            aura = new Glow(root, Vector3.zero, 150, 150, order - 1); halo = new Glow(root, Vector3.zero, 70, 70, order); core = new Glow(root, Vector3.zero, 26, 26, order + 2);
            sh = new Glow(root, Vector3.zero, 120, 9, order + 1); sv = new Glow(root, Vector3.zero, 9, 120, order + 1);
        }
        public void Set(Vector3 local, float size, float bright, float t, Color? tint = null)
        {
            var c = tint ?? Blue;
            root.localPosition = local; root.localScale = Vector3.one * Mathf.Max(0.001f, size);
            root.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(t * 0.9f) * 12f);
            aura.Set(c, 0.5f * bright); halo.Set(c, 1.5f * bright); core.Set(Color.Lerp(c, White, 0.8f), 3.2f * bright);
            float sp = 0.75f + 0.25f * Mathf.Sin(t * 6f);
            sh.Set(Color.Lerp(c, White, 0.4f), 2.0f * bright * sp); sv.Set(Color.Lerp(c, White, 0.4f), 2.0f * bright * sp);
        }
        public void Hide() => root.localScale = Vector3.zero;
    }

    // ---------- picture scenes ----------
    class Scene
    {
        public Transform root; public Material bg;
        public void Cam(float cx, float cy, float zoom)
        {
            float s = S0 * zoom * ZK;
            cx = Mathf.Clamp(cx, W / 2f / s, IW - W / 2f / s); cy = Mathf.Clamp(cy, VH / 2f / s, IH - VH / 2f / s);
            root.localScale = new Vector3(s, s, 1f);
            root.position = Px(W / 2f, VH / 2f) - s * Img(cx, cy);
        }
        public void Tint(float v) => bg.color = new Color(v * (0.86f + 0.14f * v), v * (0.88f + 0.12f * v), Mathf.Lerp(v, 1f, 0.35f), 1f);
    }

    static Scene MakeScene(string key, int image, int twinkles, int seed)
    {
        var s = new Scene { root = new GameObject("Scene" + key) { layer = UI }.transform };
        var tex = new Texture2D(2, 2, TextureFormat.RGB24, true);
        tex.LoadImage(File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable("STORY_REFS"), "Image_" + image + ".png")));
        tex.wrapMode = TextureWrapMode.Clamp; tex.filterMode = FilterMode.Trilinear; tex.anisoLevel = 4;
        s.bg = Part(s.root, Quad(IW, IH), Vector3.zero, White, -100, null, tex).sharedMaterial;
        var rnd = new System.Random(seed);
        var cols = new[] { White, Hex("BFE8FF"), Hex("E6C8FF"), Hex("FFE6B0") };
        for (int i = 0; i < twinkles; i++)
        {
            var home = new Vector2((float)rnd.NextDouble() * IW, (float)rnd.NextDouble() * IH);
            float per = 2.2f + (float)rnd.NextDouble() * 3.5f, ph = (float)rnd.NextDouble() * 10f, sz = 7f + (float)rnd.NextDouble() * 9f;
            var g = new Glow(s.root, Img(home), sz, sz, 25); var col = cols[i % cols.Length];
            anims.Add(t =>
            {
                float k = Mathf.Pow(Mathf.Max(0f, Mathf.Sin((t + ph) * Mathf.PI * 2f / per)), 6f);
                g.Set(col, 1.1f * k); g.At(Img(home.x + Mathf.Sin(t * 0.4f + ph) * 3f, home.y - Mathf.Repeat(t * 2.5f + ph * 7f, 30f) * 0.3f));
            });
        }
        scenes.Add(s); return s;
    }

    // ---------- the apprentice ----------
    class Kid
    {
        Transform root, upper, arm, legL, legR; Glow shadow; float armDeg, sitNow, scaleNow; int face = 1; Vector3 feet;
        public Kid(Transform parent)
        {
            shadow = new Glow(parent, Vector3.zero, 46, 11, 8, false);
            root = new GameObject("kid") { layer = UI }.transform; root.SetParent(parent, false);
            Color robe = Hex("5B46B0"), robeDark = Hex("47358F"), gold = Hex("E9C46A"), skin = Hex("F6D7BD"), dark = Hex("2A2140"), hat = Hex("7C5CE0");
            legL = Pivot(root, -3.8f, 12f); legR = Pivot(root, 3.8f, 12f);
            Part(legL, Poly(-2.6f, 0, 2.6f, 0, 2.6f, -12, -2.6f, -12), Vector3.zero, dark, 10);
            Part(legR, Poly(-2.6f, 0, 2.6f, 0, 2.6f, -12, -2.6f, -12), Vector3.zero, dark, 10);
            upper = Pivot(root, 0, 0);
            Part(upper, Poly(-10, 35, 10, 35, 16, 9, -16, 9), Vector3.zero, robe, 11);
            Part(upper, Poly(-16, 9, 16, 9, 16.6f, 5.5f, -16.6f, 5.5f), Vector3.zero, gold, 12);
            Part(upper, Poly(-2, 35, 2, 35, 3.2f, 9, -3.2f, 9), Vector3.zero, robeDark, 12);
            Part(upper, Disc(9.6f, 9.6f), new Vector3(0, 45), skin, 13);
            Part(upper, Disc(11f, 3.6f), new Vector3(0, 36), gold, 14);                       // scarf
            Part(upper, Poly(-12, 50, 12, 50, 3, 62, -6, 74), Vector3.zero, hat, 15);        // floppy pointed hat
            Part(upper, Disc(14f, 3.2f), new Vector3(0, 50.5f), Hex("6A4BD0"), 16);          // brim
            Part(upper, Disc(2.6f, 2.6f), new Vector3(-6, 74), gold, 17);
            Part(upper, Disc(1.4f, 1.8f), new Vector3(5.2f, 45.5f), dark, 17);                // eye
            Part(upper, Disc(2.2f, 1.5f), new Vector3(6.2f, 41.5f), Hex("F29C9C", 0.7f), 17); // cheek
            arm = Pivot(upper, 7.5f, 31f);
            Part(arm, Poly(-2.6f, 1, 2.6f, 1, 2.6f, -15, -2.6f, -15), Vector3.zero, robeDark, 18);
            Part(arm, Disc(3.1f, 3.1f), new Vector3(0, -16.5f), skin, 19);
        }
        static Transform Pivot(Transform p, float x, float y) { var t = new GameObject("pivot") { layer = UI }.transform; t.SetParent(p, false); t.localPosition = new Vector3(x, y); return t; }

        // feet: image-space position; walk: step phase; armDeg: 0 hangs down, 90 points forward; sit and tip in 0..1
        public void Pose(Vector3 feetLocal, float scale, int facing, float walk = 0f, float armDeg = 0f, float sit = 0f, float tip = 0f)
        {
            feet = feetLocal; face = facing; this.armDeg = armDeg; sitNow = sit; scaleNow = scale;
            root.gameObject.SetActive(scale > 0f); if (scale <= 0f) { shadow.Set(Color.black, 0f); return; }
            float bob = Mathf.Abs(Mathf.Sin(walk * Mathf.PI)) * 1.6f, swing = Mathf.Sin(walk * Mathf.PI) * 24f;
            root.localPosition = feetLocal + new Vector3(0, tip * 3.5f * scale); root.localScale = new Vector3(scale * facing, scale, 1f);
            upper.localPosition = new Vector3(0, bob - 9f * sit);
            legL.localPosition = new Vector3(-3.8f, 12f - 9f * sit); legR.localPosition = new Vector3(3.8f, 12f - 9f * sit);
            legL.localRotation = Quaternion.Euler(0, 0, swing * (1f - sit) + 82f * sit); legR.localRotation = Quaternion.Euler(0, 0, -swing * (1f - sit) + 74f * sit);
            arm.localRotation = Quaternion.Euler(0, 0, armDeg);
            shadow.At(feetLocal + new Vector3(0, -1f * scale)); shadow.Size(46 * scale, 11 * scale); shadow.Set(Color.black, 0.45f);
        }
        public Vector3 Hand(float reach = 0f)
        {
            float a = armDeg * Mathf.Deg2Rad, len = 16.5f + reach;
            return feet + scaleNow * new Vector3(face * (7.5f + Mathf.Sin(a) * len), 31f - Mathf.Cos(a) * len - 9f * sitNow);
        }
        public void Hide() => Pose(Vector3.zero, 0f, 1);
    }

    static Vector3 Along(Vector2[] p, float u)
    {
        u = Mathf.Clamp01(u) * (p.Length - 1); int i = Mathf.Min((int)u, p.Length - 2);
        return Img(Vector2.Lerp(p[i], p[i + 1], u - i));
    }
    static Vector3 Arc(Vector3 a, Vector3 b, float u, float lift) => Vector3.Lerp(a, b, u) + new Vector3(0, Mathf.Sin(u * Mathf.PI) * lift);

    // ---------- shots ----------
    static void ShotA()
    {
        var sc = sceneOf["A"] = MakeScene("A", 1, 42, 11); var sh = Sh("A");
        var spire = new Glow(sc.root, Img(778, 128), 120, 120, 30);
        var shade = new Glow(null, Px(W / 2f, TY + 40), W + 20, 420, 940, false);
        var title = Text("星辰城的小星星", W / 2f, TY, 92, true); var sub = Text("阿澄與藍色小星星的回家路", W / 2f, TY + 100, 40, false);
        anims.Add(t =>
        {
            float u = Smooth(U(t, sh.start, sh.end - sh.start));
            sc.Cam(Mathf.Lerp(470, 640, u), 288, Mathf.Lerp(1.0f, 1.07f, u)); sc.Tint(1f);
            spire.Set(Hex("C9A0FF"), 0.5f + 0.3f * Mathf.Sin(t * 2.2f));
            float a = Mathf.Min(U(t, 0.4f, 0.9f), 1f - U(t, sh.end - 1.3f, 0.8f));
            shade.Set(Hex("05061A"), 0.6f * a);
            title.color = new Color(1, 1, 1, a); sub.color = new Color(0.86f, 0.9f, 1f, a * 0.95f);
            title.transform.localPosition = Px(W / 2f, TY - 14f * OutCubic(U(t, 0.4f, 1.2f)));
        });
    }

    static void ShotB()
    {
        var sc = sceneOf["B"] = MakeScene("B", 5, 36, 23); var sh = Sh("B");
        var kid = new Kid(sc.root);
        var spots = new[] { new Vector2(96, 452), new Vector2(62, 486), new Vector2(118, 498), new Vector2(40, 440) };
        var sparks = new Star[spots.Length]; for (int i = 0; i < spots.Length; i++) sparks[i] = new Star(sc.root, 34);
        anims.Add(t =>
        {
            float u = Smooth(U(t, sh.start, sh.end - sh.start));
            sc.Cam(Mathf.Lerp(206, 236, u), 330, 1.16f); sc.Tint(1f);
            float rub = Mathf.Sin(t * 10f);
            kid.Pose(Img(170, 506), 1.0f, -1, 0.5f + 0.08f * rub, 62f + 26f * rub);
            for (int i = 0; i < spots.Length; i++)
            {
                float k = Pulse(Mathf.Repeat(t - sh.start - i * 0.9f, 3.6f), 0.5f, 0.18f, 0.7f);
                sparks[i].Set(Img(spots[i]), 0.35f + 0.75f * k, k, t + i, Hex("E9D4FF"));
            }
        });
    }

    static void ShotCD()
    {
        var sc = sceneOf["C"] = sceneOf["D"] = MakeScene("CD", 2, 36, 37); var c = Sh("C"); var d = Sh("D");
        var gates = new[] { new Vector2(606, 200), new Vector2(776, 236), new Vector2(848, 222), new Vector2(935, 240) };
        var warm = new Glow[gates.Length]; var dim = new Glow[gates.Length];
        for (int i = 0; i < gates.Length; i++) { warm[i] = new Glow(sc.root, Img(gates[i]), 70, 70, 30); dim[i] = new Glow(sc.root, Img(gates[i]), 96, 96, -50, false); }
        var kid = new Kid(sc.root); var star = new Star(sc.root); var touch = new Glow(sc.root, Vector3.zero, 60, 60, 45);
        Vector3 ground = Img(292, 484), hover = Img(276, 458);
        anims.Add(t =>
        {
            bool inD = t >= d.start;
            float flick = 0.45f + 0.55f * Mathf.SmoothStep(0.15f, 0.8f, Noise(t * 5.5f)) * (Mathf.Repeat(t, 1.7f) < 0.12f ? 0.25f : 1f);
            if (!inD)
            {
                float pan = Smooth(U(t, C(7) - 0.4f, 2.2f));
                sc.Cam(Mathf.Lerp(292, 730, pan), Mathf.Lerp(346, 288, pan), Mathf.Lerp(1.3f, 1.0f, pan));
                sc.Tint(Mathf.Lerp(1f, 0.86f, pan));
                float walk = U(t, c.start, C(5) - c.start - 0.2f);
                float reach = Smooth(U(t, C(5), 0.6f));
                kid.Pose(Img(Mathf.Lerp(150, 236, Smooth(walk)), Mathf.Lerp(470, 494, Smooth(walk))), 1.0f, 1, walk < 1f ? walk * 5f : 0f, 18f + 40f * reach);
                star.Set(Vector3.Lerp(ground, hover, Smooth(U(t, C(5) + 0.3f, 1.0f))) + new Vector3(0, Mathf.Sin(t * 2.4f) * 1.5f), 0.9f, flick, t);
                touch.Set(Blue, 0f);
            }
            else
            {
                float z = OutCubic(U(t, d.start, 1.2f));
                sc.Cam(Mathf.Lerp(262, 268, z), 446, Mathf.Lerp(1.75f, 1.95f, z)); sc.Tint(0.86f);
                float raise = Smooth(U(t, C(8), 0.7f));
                kid.Pose(Img(236, 494), 1.0f, 1, 0f, 58f + 40f * raise);
                var tip = kid.Hand(2.5f) + new Vector3(4.5f, 1.5f);
                float go = Smooth(U(t, C(9), 0.9f)), bump = Pulse(t, C(9) + 0.9f, 0.1f, 0.5f);
                star.Set(Vector3.Lerp(hover + new Vector3(8, 8), tip, go) + new Vector3(Mathf.Sin(t * 3f) * 0.8f - bump * 2.5f, Mathf.Sin(t * 2.4f) * 1.2f), 0.75f + 0.25f * bump, flick + bump, t);
                touch.At(tip); touch.Set(Hex("BFE8FF"), bump * 1.3f); touch.Size(40 + 50 * bump, 40 + 50 * bump);
            }
            for (int i = 0; i < gates.Length; i++)
            {
                float off = Smooth(U(t, C(7) + 0.25f + i * 0.5f, 0.45f));
                warm[i].Set(Gold, (1f - off) * (0.55f + 0.25f * Mathf.Sin(t * 3f + i)) + Pulse(t, C(7) + 0.2f + i * 0.5f, 0.06f, 0.2f));
                dim[i].Set(Hex("060818"), 0.78f * off);
            }
        });
    }

    static void ShotE()
    {
        var sc = sceneOf["E"] = MakeScene("E", 4, 36, 53); var sh = Sh("E");
        var gates = new[] { new Vector2(592, 238), new Vector2(460, 206), new Vector2(514, 184), new Vector2(775, 196), new Vector2(226, 186) };
        var warm = new Glow(sc.root, Img(gates[0]), 90, 90, 30); var dim = new Glow[gates.Length];
        for (int i = 0; i < gates.Length; i++) dim[i] = new Glow(sc.root, Img(gates[i]), i == 0 || i == 4 ? 120 : 84, i == 0 || i == 4 ? 120 : 84, -50, false);
        var kid = new Kid(sc.root); var star = new Star(sc.root);
        anims.Add(t =>
        {
            float u = U(t, sh.start, sh.end - sh.start), su = Smooth(u);
            sc.Cam(Mathf.Lerp(500, 640, su), 312, 1.14f); sc.Tint(0.84f);
            float stop = Smooth(U(t, sh.start, C(12) + 0.5f - sh.start));
            kid.Pose(Img(Mathf.Lerp(585, 716, stop), Mathf.Lerp(396, 372, stop)), 0.92f, 1, stop < 1f ? (t - sh.start) * 2.3f : 0f, 78f);
            float shrink = Smooth(U(t, C(13), 0.7f));
            float flick = 0.5f + 0.5f * Mathf.SmoothStep(0.15f, 0.8f, Noise(t * 5.5f));
            star.Set(kid.Hand() + new Vector3(3f, Mathf.Lerp(13f, 3f, shrink) + Mathf.Sin(t * 2.6f) * 1.2f), Mathf.Lerp(0.85f, 0.38f, shrink), flick * Mathf.Lerp(1f, 0.55f, shrink), t);
            float off = Smooth(U(t, C(12) + 0.5f, 0.6f));
            warm.Set(Gold, (1f - off) * (0.6f + 0.3f * Mathf.Sin(t * 3.2f)) + Pulse(t, C(12) + 0.45f, 0.06f, 0.25f));
            dim[0].Set(Hex("060818"), 0.8f * off); dim[3].Set(Hex("060818"), 0.75f * Smooth(U(t, C(13) + 0.3f, 0.6f)));
            dim[1].Set(Hex("060818"), 0.72f); dim[2].Set(Hex("060818"), 0.72f); dim[4].Set(Hex("060818"), 0.7f);
        });
    }

    static void ShotF()
    {
        var sc = sceneOf["F"] = MakeScene("F", 3, 30, 71); var sh = Sh("F");
        var kid = new Kid(sc.root); var star = new Star(sc.root);
        anims.Add(t =>
        {
            float u = Smooth(U(t, sh.start, sh.end - sh.start));
            sc.Cam(Mathf.Lerp(600, 700, u), 322, 1.14f);
            sc.Tint(Mathf.Lerp(0.82f, 0.36f, Smooth(U(t, C(14), 2.2f))));
            float walk = U(t, sh.start, C(15) - sh.start - 0.3f), sit = Smooth(U(t, C(15) - 0.2f, 0.7f));
            kid.Pose(Img(Mathf.Lerp(690, 742, Smooth(walk)), 452), 1.0f, 1, walk < 1f ? (t - sh.start) * 1.8f : 0f, Mathf.Lerp(70f, 52f, sit), sit);
            star.Set(kid.Hand() + new Vector3(2.5f, 4f + Mathf.Sin(t * 2.2f)), 0.42f, 0.5f + 0.2f * Noise(t * 4f), t);
        });
    }

    static void ShotGH()
    {
        var sc = sceneOf["G"] = sceneOf["H"] = MakeScene("GH", 6, 30, 89); var g = Sh("G"); var h = Sh("H");
        Vector2 chart = new Vector2(432, 40), core = new Vector2(775, 182);
        var chartGlow = new Glow(sc.root, Img(chart), 330, 120, 28); var chartStars = new Glow[14];
        for (int i = 0; i < chartStars.Length; i++) chartStars[i] = new Glow(sc.root, Vector3.zero, 12, 12, 29);
        var hole = new Glow(sc.root, Img(core), 74, 74, -50, false);
        var slotMesh = new Mesh(); var slot = Part(sc.root, slotMesh, Img(core), Blue, 31).sharedMaterial;
        var burst = new Glow(sc.root, Img(core), 200, 200, 38); var rayH = new Glow(sc.root, Img(core), 300, 10, 38); var rayV = new Glow(sc.root, Img(core), 10, 300, 38);
        var ringMesh = new Mesh[3]; var ringMat = new Material[3]; var ringT = new Transform[3];
        float[] rr = { 66f, 52f, 38f }, tiltAt = { 20f, -35f, 70f };
        for (int i = 0; i < 3; i++) { ringMesh[i] = new Mesh(); var r = Part(sc.root, ringMesh[i], Img(core), Gold, 33); ringMat[i] = r.sharedMaterial; ringT[i] = r.transform; }
        var trail = new Glow[10]; for (int i = 0; i < trail.Length; i++) trail[i] = new Glow(sc.root, Vector3.zero, 16, 16, 36);
        var kid = new Kid(sc.root); var kid2 = new Kid(sc.root); var star = new Star(sc.root);
        Vector3 seat = Img(350, 470), stairs0 = Img(716, 262), stairs1 = Img(748, 247);
        float tPlace = C(22) + 0.95f;
        anims.Add(t =>
        {
            bool inH = t >= h.start;
            float lit = Smooth(U(t, C(18) - 0.2f, 0.9f)), placed = Smooth(U(t, tPlace, 1.3f));
            sc.Tint(Mathf.Lerp(Mathf.Lerp(0.38f, 0.62f, lit), 1f, placed));
            float pan = Smooth(U(t, C(19) - 0.3f, 1.8f)), zoom = Smooth(U(t, h.start, 1.3f));
            sc.Cam(Mathf.Lerp(Mathf.Lerp(396, 690, pan), 770, zoom), Mathf.Lerp(288, 196, zoom), Mathf.Lerp(1.0f, 1.8f, zoom));

            chartGlow.Set(Gold, lit * (0.75f + 0.15f * Mathf.Sin(t * 2f)));
            for (int i = 0; i < chartStars.Length; i++)
            {
                float a = i * Mathf.PI * 2f / chartStars.Length + t * 0.35f * (i % 2 == 0 ? 1 : -0.7f), rad = i % 2 == 0 ? 104f : 62f;
                chartStars[i].At(Img(chart.x + Mathf.Cos(a) * rad, chart.y + Mathf.Sin(a) * rad * 0.30f));
                chartStars[i].Set(Hex("FFE9B8"), lit * (0.6f + 0.6f * Mathf.Abs(Mathf.Sin(t * 3f + i))));
            }
            hole.Set(Hex("070A1C"), 0.86f * (1f - placed));
            EllipseRing(slotMesh, 7f, 7f, 1.2f);
            var sc2 = Blue; sc2.a = U(t, C(19), 0.6f) * (1f - U(t, tPlace, 0.2f)) * (0.45f + 0.45f * Mathf.Sin(t * 5f)); slot.color = sc2;

            // the star: palm -> star map -> armillary side -> carried up -> centre
            float rise = Smooth(U(t, C(17), 1.3f)), cross = Smooth(U(t, C(19), 1.9f));
            Vector3 palm = seat + new Vector3(16, 22), mapPos = Img(chart.x, chart.y + 30), wait = Img(706, 246);
            Vector3 p = Arc(palm, mapPos, rise, 14f);
            if (cross > 0f) p = Arc(mapPos, wait, cross, 26f);
            float size = Mathf.Lerp(0.45f, 1.05f, rise), bright = Mathf.Lerp(0.6f, 1.25f, rise);
            if (inH)
            {
                float climb = Smooth(U(t, h.start + 0.5f, C(22) - h.start - 0.6f)), tip = Smooth(U(t, C(21) + 1.0f, 0.5f));
                kid2.Pose(Vector3.Lerp(stairs0, stairs1, climb), 0.5f, 1, climb < 1f ? (t - h.start) * 2.6f : 0f, Mathf.Lerp(80f, 158f, tip), 0f, tip);
                float carry = Smooth(U(t, h.start + 0.2f, 0.7f)), put = Smooth(U(t, C(22), 0.95f));
                p = Vector3.Lerp(Vector3.Lerp(wait, kid2.Hand() + new Vector3(1, 5), carry), Img(core), put);
                size = Mathf.Lerp(0.6f, 0.8f, put); bright = 1.2f + placed;
            }
            else kid2.Hide();
            star.Set(p + new Vector3(0, Mathf.Sin(t * 2.5f) * 1.2f * (1f - placed)), size, bright, t);
            for (int i = 0; i < trail.Length; i++)
            {
                float lag = (i + 1) * 0.07f, r2 = Smooth(U(t - lag, C(17), 1.3f)), c2 = Smooth(U(t - lag, C(19), 1.9f));
                Vector3 q = c2 > 0f ? Arc(mapPos, wait, c2, 26f) : Arc(palm, mapPos, r2, 14f);
                bool moving = (r2 > 0f && r2 < 1f) || (c2 > 0f && c2 < 1f);
                trail[i].At(q); trail[i].Set(Hex("BFE8FF"), moving && !inH ? 0.8f * (1f - i / (float)trail.Length) : 0f);
            }

            float standUp = Smooth(U(t, C(19), 0.6f));
            if (inH && zoom > 0.6f) kid.Hide(); else kid.Pose(seat, 1.0f, 1, 0f, Mathf.Lerp(58f, 118f, rise) * (1f - 0.5f * standUp), 1f - standUp);

            float flash = Pulse(t, tPlace, 0.12f, 1.1f);
            burst.Set(Blue, flash * 1.5f + placed * (0.55f + 0.15f * Mathf.Sin(t * 4f))); burst.Size(120 + 240 * flash, 120 + 240 * flash);
            rayH.Set(Hex("BFE8FF"), flash * 1.3f + placed * 0.35f); rayV.Set(Hex("BFE8FF"), flash * 1.3f + placed * 0.35f);
            // Rings ease into rotation; each is a circle turning about an in-plane axis, so it shows as a breathing ellipse.
            float spin = Mathf.Max(0f, t - tPlace); float ang = spin * spin * 0.5f / (1f + spin * 0.35f);
            for (int i = 0; i < 3; i++)
            {
                float th = tiltAt[i] * Mathf.Deg2Rad + ang * (1.6f + i * 0.7f) * (i % 2 == 0 ? 1 : -1);
                EllipseRing(ringMesh[i], rr[i], Mathf.Max(1.5f, rr[i] * Mathf.Abs(Mathf.Cos(th))), 2.4f);
                ringT[i].localRotation = Quaternion.Euler(0, 0, 25f + i * 55f + ang * 14f);
                var rc = Color.Lerp(Gold, White, 0.25f + 0.5f * flash); rc.a = Smooth(U(t, tPlace - 0.1f, 0.5f)) * 0.95f; ringMat[i].color = rc;
            }
        });
    }

    static void ShotI()
    {
        var sc = sceneOf["I"] = MakeScene("I", 1, 40, 97); var sh = Sh("I");
        var river = new[] { new Vector2(778, 150), new Vector2(742, 250), new Vector2(640, 296), new Vector2(520, 290), new Vector2(452, 304), new Vector2(470, 336),
                            new Vector2(560, 372), new Vector2(660, 418), new Vector2(730, 462), new Vector2(790, 520) };
        var flow = new Glow[22]; for (int i = 0; i < flow.Length; i++) flow[i] = new Glow(sc.root, Vector3.zero, 44, 44, 30);
        var wash = new Glow(sc.root, Img(600, 380), 520, 300, 27);
        var wink = new Star(sc.root, 44); var kid = new Kid(sc.root);
        anims.Add(t =>
        {
            float u = Smooth(U(t, sh.start, sh.end - sh.start));
            sc.Cam(Mathf.Lerp(630, 600, u), 288, Mathf.Lerp(1.0f, 1.05f, u));
            float weave = U(t, C(24) - 0.2f, 2.6f), home = Smooth(U(t, C(27), 1.2f));
            sc.Tint(Mathf.Lerp(0.72f, 1f, Smooth(weave)));
            for (int i = 0; i < flow.Length; i++)
            {
                float pos = Mathf.Repeat(i / (float)flow.Length + (t - sh.start) * 0.11f, 1f);
                float a = pos < weave ? 0.55f + 0.35f * Mathf.Sin(t * 4f + i) : 0f;
                flow[i].At(Along(river, pos)); flow[i].Set(Blue, a * (0.8f + 0.9f * home));
                float sz = 36f + 30f * pos + 14f * home; flow[i].Size(sz, sz);
            }
            wash.Set(Blue, 0.16f * Smooth(weave) + 0.22f * home);
            float k = Pulse(t, C(26) + 0.55f, 0.14f, 0.9f);
            wink.Set(Img(778, 128), 0.45f + 0.8f * k, 0.22f + 0.75f * k, t, Hex("BFE8FF"));
            float wave = Pulse(t, C(26) + 0.9f, 0.3f, 1.6f);
            kid.Pose(Img(480, 477), 0.9f, 1, 0f, 20f + wave * (125f + 18f * Mathf.Sin(t * 11f)));
        });
    }

    static TextMesh Text(string s, float x, float y, int size, bool bold)
    {
        var go = new GameObject("text") { layer = UI }; go.transform.position = Px(x, y);
        var r = go.AddComponent<MeshRenderer>(); var tm = go.AddComponent<TextMesh>();
        tm.font = zh; tm.fontSize = size; tm.characterSize = 10f; tm.text = s; tm.anchor = TextAnchor.MiddleCenter; tm.alignment = TextAlignment.Center;
        tm.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal; tm.color = new Color(1, 1, 1, 0);
        zh.RequestCharactersInTexture(s, size, tm.fontStyle);
        r.sharedMaterial = zh.material; r.sortingOrder = 950; return tm;
    }

    // ---------- build + render ----------
    static void Build()
    {
        SetFormat(Environment.GetEnvironmentVariable("STORY_FORMAT"));
        Debug.Log($"[Story] format {(Wide ? "horizontal" : "vertical")} {W}x{H}");
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        anims.Clear(); scenes.Clear(); sceneOf.Clear();
        ShaderUtil.allowAsyncCompilation = false;
        TL = JsonUtility.FromJson<Timeline>(File.ReadAllText(Environment.GetEnvironmentVariable("STORY_TIMELINE"))); DUR = TL.duration;
        flat = Shader.Find("Sprites/Default");
        add = Shader.Find("Legacy Shaders/Particles/Additive") ?? Shader.Find("Mobile/Particles/Additive") ?? flat;
        Debug.Log("[Story] additive shader: " + add.name);
        zh = Font.CreateDynamicFontFromOSFont(new[] { "PingFang TC", "Heiti TC", "Arial Unicode MS" }, 64);

        glowTex = new Texture2D(128, 128, TextureFormat.RGBA32, true) { wrapMode = TextureWrapMode.Clamp };
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
        {
            float d = Mathf.Clamp01(Vector2.Distance(new Vector2(x, y), new Vector2(63.5f, 63.5f)) / 63.5f), a = Mathf.Pow(1f - d, 1.7f);
            glowTex.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        glowTex.Apply();

        cam = new GameObject("Cam").AddComponent<Camera>();
        cam.orthographic = true; cam.orthographicSize = H / 2f; cam.clearFlags = CameraClearFlags.SolidColor; cam.backgroundColor = Night;
        cam.cullingMask = 1 << UI; cam.transform.position = new Vector3(0, 0, -500f); cam.nearClipPlane = 1f; cam.farClipPlane = 1000f; cam.enabled = false;

        ShotA(); ShotB(); ShotCD(); ShotE(); ShotF(); ShotGH(); ShotI();

        // Vertical: storybook panel under the picture with a soft edge. Horizontal: subtitles sit on the
        // picture, so only darken the bottom for legibility. Both get a slight darkening at the very top.
        var clear = Night; clear.a = 0f;
        if (Wide) { var shadeBottom = Night; shadeBottom.a = 0.72f; Part(null, Quad(W, 300, clear, shadeBottom), Px(W / 2f, H - 150f), White, 900); }
        else
        {
            Part(null, Quad(W, H - VH + 2), Px(W / 2f, (VH + H) / 2f), Night, 900);
            Part(null, Quad(W, 190, clear, Night), Px(W / 2f, VH - 95f), White, 900);
        }
        Part(null, Quad(W, 200, new Color(0, 0, 0, 0.45f), new Color(0, 0, 0, 0f)), Px(W / 2f, 100f), White, 900);
        curtain = Part(null, Quad(W, H), Px(W / 2f, H / 2f), Night, 1000).sharedMaterial;
    }

    static void Apply(float t)
    {
        Scene current = null; string id = null;
        foreach (var s in TL.shots) if (t >= s.start) { current = sceneOf[s.id]; id = s.id; }
        foreach (var s in scenes) s.root.gameObject.SetActive(s == current);
        foreach (var a in anims) a(t);
        float dip = Mathf.Max(1f - t / 0.5f, 1f - (DUR - t) / 0.7f);
        foreach (var s in TL.shots)
            if (s.start > 0f && s.id != "H") dip = Mathf.Max(dip, 1f - Mathf.Abs(t - s.start) / 0.22f);
        var c = Night; c.a = Mathf.Clamp01(dip); curtain.color = c;
    }

    static void Frame(float t, RenderTexture rt, Texture2D tex)
    {
        Apply(t);
        cam.targetTexture = rt; cam.Render();
        RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false); RenderTexture.active = null;
    }

    public static void Render()
    {
        try
        {
            string outDir = Environment.GetEnvironmentVariable("REEL_OUT");
            if (string.IsNullOrEmpty(outDir)) throw new Exception("REEL_OUT is not set");
            Directory.CreateDirectory(outDir);
            Build();
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            Frame(0.8f, rt, tex);

            string stills = Environment.GetEnvironmentVariable("REEL_STILLS");
            if (!string.IsNullOrEmpty(stills))
            {
                foreach (var s in stills.Split(','))
                {
                    Frame(float.Parse(s, System.Globalization.CultureInfo.InvariantCulture), rt, tex); tex.Apply();
                    File.WriteAllBytes(Path.Combine(outDir, "still_" + s + ".png"), tex.EncodeToPNG());
                }
                Debug.Log("[Story] stills done"); EditorApplication.Exit(0); return;
            }
            string outFile = Path.Combine(outDir, "unity_render.mp4");
            var psi = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("REEL_FFMPEG") ?? "ffmpeg",
                Arguments = $"-v error -y -f rawvideo -pix_fmt rgb24 -s {W}x{H} -r {FPS} -i - -vf vflip -c:v libx264 -preset medium -crf 14 -pix_fmt yuv420p \"{outFile}\"",
                UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true
            };
            int total = Mathf.RoundToInt(DUR * FPS);
            using (var ff = Process.Start(psi))
            {
                var pipe = ff.StandardInput.BaseStream;
                for (int f = 0; f < total; f++)
                {
                    Frame(f / (float)FPS, rt, tex);
                    var raw = tex.GetRawTextureData(); pipe.Write(raw, 0, raw.Length);
                }
                pipe.Flush(); pipe.Close(); ff.WaitForExit();
                if (ff.ExitCode != 0) throw new Exception("ffmpeg exit " + ff.ExitCode);
            }
            Debug.Log("[Story] wrote " + outFile); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogError("[Story] FAILED " + e); EditorApplication.Exit(1); }
    }
}
