// Builds the whole 30 s reel procedurally and renders it frame by frame (no play mode, no Recorder).
// Every animation is a pure function of time, so output is deterministic.
//
//   Unity -batchmode -projectPath unity -executeMethod ReelBuilder.Render -quit
// Environment:
//   REEL_OUT     output directory (required)
//   REEL_STILLS  comma-separated times in seconds -> PNG stills only (optional)
//   REEL_FFMPEG  ffmpeg path, used when REEL_STILLS is not set
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static class ReelBuilder
{
    const int W = 1080, H = 1920, FPS = 30, UI = 5;
    const float DUR = 30f;
    static readonly float[] Cuts = { 0f, 3.4f, 9f, 17f, 23f, 28f, 30f };
    static readonly string[] Bg = { "0B1020", "101828", "14213D", "1B263B", "22223B", "0B1020" };

    static Camera cam3D, cam2D;
    static Font zh, mono;
    static Shader flat;
    static readonly List<Action<float>> anims = new List<Action<float>>();
    static readonly GameObject[] shots = new GameObject[6];
    static GameObject floaters, stage;
    static Material curtain;
    static RenderTexture rt3D;

    static Color Hex(string h, float a = 1f) { ColorUtility.TryParseHtmlString("#" + h, out var c); c.a = a; return c; }
    static readonly Color Accent = Hex("64C8FF"), White = Color.white, Dim = Hex("94A3B8"), Green = Hex("7EE787"), Orange = Hex("FFA94D");
    static readonly string[] Palette = { "FFA94D", "4DD0E1", "F06292", "AED581", "9575CD" };

    // ---------- easing ----------
    static float U(float t, float start, float dur) => Mathf.Clamp01((t - start) / dur);
    static float OutCubic(float x) => 1f - Mathf.Pow(1f - x, 3f);
    static float OutBack(float x) { const float c1 = 1.70158f, c3 = c1 + 1f; return 1f + c3 * Mathf.Pow(x - 1f, 3f) + c1 * Mathf.Pow(x - 1f, 2f); }

    // ---------- 2D helpers (pixel coordinates, origin top-left) ----------
    static Vector3 Px(float x, float y) => new Vector3(x - W / 2f, H / 2f - y, 0f);

    static List<Vector3> Perimeter(float w, float h, float r, int seg = 12)
    {
        r = Mathf.Max(0f, Mathf.Min(r, Mathf.Min(w, h) / 2f));
        var pts = new List<Vector3>();
        var centers = new[] { new Vector2(w / 2 - r, h / 2 - r), new Vector2(-w / 2 + r, h / 2 - r), new Vector2(-w / 2 + r, -h / 2 + r), new Vector2(w / 2 - r, -h / 2 + r) };
        for (int c = 0; c < 4; c++)
            for (int i = 0; i <= seg; i++)
            {
                float a = (c * 90f + i * 90f / seg) * Mathf.Deg2Rad;
                pts.Add(new Vector3(centers[c].x + Mathf.Cos(a) * r, centers[c].y + Mathf.Sin(a) * r, 0f));
            }
        return pts;
    }

    static Mesh RoundedRect(float w, float h, float r)
    {
        var p = Perimeter(w, h, r);
        var v = new List<Vector3> { Vector3.zero }; v.AddRange(p);
        var tri = new List<int>();
        for (int i = 0; i < p.Count; i++) { tri.Add(0); tri.Add(1 + i); tri.Add(1 + (i + 1) % p.Count); }
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateBounds(); return m;
    }

    static Mesh Strip(List<Vector3> outer, List<Vector3> inner, Mesh m = null)
    {
        int n = outer.Count;
        var v = new List<Vector3>(outer); v.AddRange(inner);
        var tri = new List<int>();
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            tri.Add(i); tri.Add(j); tri.Add(n + i);
            tri.Add(j); tri.Add(n + j); tri.Add(n + i);
        }
        if (m == null) m = new Mesh(); else m.Clear();
        m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateBounds(); return m;
    }

    static Mesh Outline(float w, float h, float r, float thick) =>
        Strip(Perimeter(w, h, r), Perimeter(w - 2 * thick, h - 2 * thick, Mathf.Max(0f, r - thick)));

    static void Ring(Mesh m, float radius, float width)
    {
        var o = new List<Vector3>(); var inn = new List<Vector3>();
        for (int i = 0; i < 72; i++)
        {
            float a = i * Mathf.PI * 2f / 72f; var d = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
            o.Add(d * (radius + width / 2f)); inn.Add(d * (radius - width / 2f));
        }
        Strip(o, inn, m);
    }

    static GameObject Node(string name, Transform parent, float x = W / 2f, float y = H / 2f)
    {
        var go = new GameObject(name) { layer = UI };
        go.transform.SetParent(parent, false);
        go.transform.localPosition = parent == null ? Px(x, y) : Px(x, y) - LocalOrigin(parent);
        return go;
    }
    // Children are positioned in absolute pixels regardless of the parent's own offset.
    static Vector3 LocalOrigin(Transform parent) => parent.position;

    static MeshRenderer Shape(Transform parent, Mesh mesh, float x, float y, Color c, int order)
    {
        var go = Node("shape", parent, x, y);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var r = go.AddComponent<MeshRenderer>();
        r.sharedMaterial = new Material(flat) { color = c };
        r.sortingOrder = order;
        return r;
    }
    static MeshRenderer Box(Transform parent, float x, float y, float w, float h, float r, Color c, int order) =>
        Shape(parent, RoundedRect(w, h, r), x, y, c, order);

    static TextMesh Txt(Transform parent, string s, float x, float y, int size, Color c, Font f,
                        TextAnchor anchor = TextAnchor.MiddleCenter, bool bold = false, int order = 20)
    {
        var go = Node("text", parent, x, y);
        var r = go.AddComponent<MeshRenderer>();
        var tm = go.AddComponent<TextMesh>();
        tm.font = f; tm.fontSize = size; tm.characterSize = 10f; tm.text = s; tm.color = c;
        tm.anchor = anchor; tm.alignment = TextAlignment.Center; tm.richText = true;
        tm.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        r.sharedMaterial = f.material; r.sortingOrder = order;
        return tm;
    }

    static string Typed(string s, float progress) => s.Substring(0, Mathf.Clamp(Mathf.RoundToInt(s.Length * progress), 0, s.Length));

    // ---------- 3D helpers ----------
    static Material Lit(Color c, float gloss = 0.35f)
    {
        var m = new Material(Shader.Find("Standard")) { color = c };
        m.SetFloat("_Glossiness", gloss); return m;
    }

    static void Look(Vector3 pos, Vector3 target) { cam3D.transform.position = pos; cam3D.transform.LookAt(target); }

    // ---------- shots ----------
    static void BuildFloaters()
    {
        floaters = new GameObject("Floaters");
        // Kept in the top and bottom bands so they never sit behind the title or the subtitles.
        var spots = new[] { new Vector3(-1.4f, 3.05f, 2f), new Vector3(1.35f, 3.2f, 3f), new Vector3(0.1f, 3.5f, 4f), new Vector3(-1.5f, -2.55f, 2f),
                            new Vector3(1.45f, -2.4f, 3f), new Vector3(0.05f, -3.0f, 2f), new Vector3(-0.75f, -3.4f, 4f) };
        for (int i = 0; i < spots.Length; i++)
        {
            var c = GameObject.CreatePrimitive(i % 3 == 1 ? PrimitiveType.Sphere : PrimitiveType.Cube);
            c.transform.SetParent(floaters.transform, false);
            c.GetComponent<Renderer>().sharedMaterial = Lit(Hex(Palette[i % Palette.Length]));
            float size = 0.45f + 0.12f * (i % 3); int k = i; var home = spots[i];
            anims.Add(t =>
            {
                c.transform.localPosition = home + new Vector3(0f, 0.18f * Mathf.Sin(t * 1.3f + k * 1.7f), 0f);
                c.transform.localRotation = Quaternion.Euler(t * 35f + k * 40f, t * 50f + k * 25f, k * 15f);
                c.transform.localScale = Vector3.one * size;
            });
        }
    }

    static void BuildS1()
    {
        var g = shots[0] = Node("S1", null); var p = g.transform;
        var l1 = Txt(p, "一句話", 540, 520, 170, White, zh, bold: true);
        var l2 = Txt(p, "做出短影音", 540, 720, 150, Accent, zh, bold: true);
        var bar = Box(p, 540, 850, 420, 8, 4, Accent, 10);
        var chip = Node("chip", p, 540, 960);
        Box(chip.transform, 540, 960, 640, 96, 48, Hex("1E293B"), 10);
        Txt(chip.transform, "Fable 5.2  ×  Unity", 540, 960, 46, White, mono);
        anims.Add(t =>
        {
            l1.transform.localScale = Vector3.one * OutBack(U(t, 0.10f, 0.50f));
            l2.transform.localScale = Vector3.one * OutBack(U(t, 0.35f, 0.50f));
            bar.transform.localScale = new Vector3(OutCubic(U(t, 0.70f, 0.40f)), 1f, 1f);
            float c = OutCubic(U(t, 0.95f, 0.45f));
            chip.transform.localScale = Vector3.one * c;
        });
    }

    static void BuildS2()
    {
        var g = shots[1] = Node("S2", null); var p = g.transform;
        Box(p, 540, 660, 948, 868, 36, Hex("30363D"), 5);
        Box(p, 540, 660, 940, 860, 32, Hex("0D1117"), 6);
        Box(p, 540, 278, 940, 96, 32, Hex("161B22"), 7);
        Box(p, 540, 302, 940, 48, 0, Hex("161B22"), 7);
        string[] dots = { "FF5F56", "FFBD2E", "27C93F" };
        for (int i = 0; i < 3; i++) Box(p, 124 + i * 40, 278, 24, 24, 12, Hex(dots[i]), 8);
        Txt(p, "Terminal", 540, 278, 30, Dim, mono);

        const string prompt = "幫我做一支 30 秒的短影音";
        var l0 = Txt(p, "", 110, 390, 38, White, mono, TextAnchor.MiddleLeft);
        var busy = Txt(p, "<color=#FFA94D>●</color> Fable 5.2 規劃腳本與分鏡", 110, 470, 36, White, mono, TextAnchor.MiddleLeft);
        string[,] rows = { { "S1", "標題字卡", "0–3s" }, { "S2", "規劃腳本", "3–9s" }, { "S3", "Unity 場景", "9–17s" }, { "S4", "輸出與字幕", "17–23s" }, { "S5", "上傳 Reels", "23–28s" } };
        var rowNodes = new GameObject[5];
        for (int i = 0; i < 5; i++)
        {
            float y = 560 + i * 68;
            var row = rowNodes[i] = Node("row", p, 540, y);
            Txt(row.transform, rows[i, 0], 130, y, 36, Accent, mono, TextAnchor.MiddleLeft, true);
            Txt(row.transform, rows[i, 1], 230, y, 36, White, mono, TextAnchor.MiddleLeft);
            Txt(row.transform, rows[i, 2], 960, y, 34, Dim, mono, TextAnchor.MiddleRight);
        }
        var done = Txt(p, "✓ 分鏡完成", 110, 950, 38, Green, mono, TextAnchor.MiddleLeft, true);
        anims.Add(t =>
        {
            float k = U(t, 3.75f, 1.2f);
            bool cursor = k < 1f || Mathf.Repeat(t, 0.8f) < 0.4f;
            l0.text = "<color=#7EE787>></color> " + Typed(prompt, k) + (cursor && t < 5.3f ? "<color=#64C8FF>▌</color>" : "");
            busy.gameObject.SetActive(t >= 5.3f);
            for (int i = 0; i < 5; i++)
            {
                float a = U(t, 5.8f + i * 0.38f, 0.25f);
                rowNodes[i].SetActive(a > 0f);
                rowNodes[i].transform.localPosition = new Vector3((1f - OutCubic(a)) * 60f, rowNodes[i].transform.localPosition.y, 0f);
            }
            done.gameObject.SetActive(t >= 7.95f);
            done.transform.localScale = Vector3.one * OutBack(U(t, 7.95f, 0.3f));
        });
    }

    static void BuildS3()
    {
        stage = new GameObject("Stage");
        var grid = new Texture2D(128, 128, TextureFormat.RGB24, true) { wrapMode = TextureWrapMode.Repeat, anisoLevel = 8 };
        for (int y = 0; y < 128; y++) for (int x = 0; x < 128; x++)
            grid.SetPixel(x, y, (x < 3 || y < 3) ? Hex("2F4A80") : Hex("131E38"));
        grid.Apply();
        var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
        ground.transform.SetParent(stage.transform, false); ground.transform.localScale = new Vector3(6f, 1f, 6f);
        var gm = Lit(White, 0.1f); gm.mainTexture = grid; gm.mainTextureScale = new Vector2(60f, 60f);
        ground.GetComponent<Renderer>().sharedMaterial = gm;

        var kinds = new[] { PrimitiveType.Cube, PrimitiveType.Sphere, PrimitiveType.Cylinder, PrimitiveType.Capsule };
        var spots = new[] { new Vector3(-1.15f, 0f, -0.8f), new Vector3(1.15f, 0f, -0.7f), new Vector3(-0.95f, 0f, 1.0f), new Vector3(1.0f, 0f, 1.1f) };
        var lift = new[] { 0.5f, 0.5f, 1f, 1f };
        var pops = new[] { 9.9f, 11.2f, 12.5f, 13.8f };
        var holders = new Transform[4];
        for (int i = 0; i < 4; i++)
        {
            var h = new GameObject("holder").transform; h.SetParent(stage.transform, false); h.localPosition = spots[i]; holders[i] = h;
            var o = GameObject.CreatePrimitive(kinds[i]); o.transform.SetParent(h, false);
            o.transform.localPosition = new Vector3(0f, lift[i], 0f);
            o.GetComponent<Renderer>().sharedMaterial = Lit(Hex(Palette[i]));
        }

        var g = shots[2] = Node("S3", null); var p = g.transform;
        Box(p, 540, 250, 948, 108, 28, Hex("30363D"), 5);
        Box(p, 540, 250, 940, 100, 24, Hex("0D1117", 0.92f), 6);
        var code = Txt(p, "", 100, 250, 31, White, mono, TextAnchor.MiddleLeft);
        var count = Txt(p, "", 540, 1130, 36, White, mono, bold: true);
        anims.Add(t =>
        {
            float ang = Mathf.Lerp(-60f, 55f, U(t, 9f, 8f)) * Mathf.Deg2Rad;
            if (t >= 9f && t < 17f) Look(new Vector3(Mathf.Sin(ang) * 12f, 7f, -Mathf.Cos(ang) * 12f), new Vector3(0f, -0.9f, 0f));
            int made = 0; string line = "";
            for (int i = 0; i < 4; i++)
            {
                float a = U(t, pops[i], 0.45f);
                if (a > 0f) made++;
                float hop = t > 15.2f ? Mathf.Abs(Mathf.Sin((t - 15.2f) * 4.2f - i * 0.6f)) * 0.9f : 0f;
                holders[i].localScale = Vector3.one * OutBack(a);
                holders[i].localPosition = spots[i] + new Vector3(0f, hop, 0f);
                holders[i].localRotation = Quaternion.Euler(0f, Mathf.Max(0f, t - pops[i]) * 40f, 0f);
                if (t >= pops[i] - 0.6f) line = Typed("CreatePrimitive(PrimitiveType." + kinds[i] + ");", U(t, pops[i] - 0.6f, 0.5f));
            }
            if (t >= 14.7f) line = Typed("timeline.Play();  // 排動畫", U(t, 14.7f, 0.45f));
            code.text = "<color=#7EE787>></color> " + line;
            count.text = "GameObjects: " + made + " / 4";
        });
    }

    static void BuildS4()
    {
        var g = shots[3] = Node("S4", null); var p = g.transform;
        const float cx = 540, cy = 520, fw = 330, fh = 586;
        Box(p, cx, cy, fw, fh, 26, Hex("0B1020"), 5);
        Box(p, cx, cy - 120, 120, 120, 16, Hex(Palette[0]), 6);
        Box(p, cx + 70, cy - 20, 110, 110, 55, Hex(Palette[1]), 7);
        Box(p, cx - 60, cy + 40, 90, 90, 12, Hex(Palette[2]), 7);
        Shape(p, Outline(fw + 12, fh + 12, 32, 6), cx, cy, Accent, 9);
        var sub1 = Box(p, cx, cy + 190, 230, 24, 12, White, 8);
        var sub2 = Box(p, cx, cy + 226, 170, 16, 8, Dim, 8);
        Txt(p, "1080 × 1920 · 30 fps", 540, 868, 34, Dim, mono);
        Box(p, 540, 940, 640, 16, 8, Hex("334155"), 5);
        var fill = Box(p, 220, 940, 640, 16, 8, Accent, 6);
        fill.GetComponent<MeshFilter>().sharedMesh = RoundedRect(640, 16, 8);
        var pct = Txt(p, "", 540, 990, 32, White, mono);
        var bars = new Transform[28];
        for (int i = 0; i < 28; i++) bars[i] = Box(p, 216 + i * 24, 1100, 12, 100, 6, Hex(Palette[1]), 6).transform;
        anims.Add(t =>
        {
            float k = OutCubic(U(t, 17.4f, 1.7f));
            // Scale the bar from its left edge.
            fill.transform.localScale = new Vector3(Mathf.Max(0.001f, k), 1f, 1f);
            fill.transform.localPosition = new Vector3(Px(220, 940).x + 320f * k, fill.transform.localPosition.y, 0f);
            pct.text = k < 1f ? "Render " + Mathf.RoundToInt(k * 100f) + "%" : "<color=#7EE787>✓ Render 100%</color>";
            float wv = OutCubic(U(t, 19.2f, 0.5f));
            for (int i = 0; i < 28; i++)
            {
                float amp = 0.25f + 0.75f * Mathf.Abs(Mathf.Sin(t * 5.1f + i * 0.9f) * Mathf.Cos(t * 2.3f + i * 0.37f));
                bars[i].localScale = new Vector3(1f, wv * amp, 1f);
            }
            float s = OutBack(U(t, 20.4f, 0.4f));
            sub1.transform.localScale = new Vector3(s, s, 1f);
            sub2.transform.localScale = new Vector3(OutBack(U(t, 20.7f, 0.4f)), s > 0f ? 1f : 0f, 1f);
        });
    }

    static void BuildS5()
    {
        var g = shots[4] = Node("S5", null); var p = g.transform;
        const float py = 660;
        Box(p, 540, py, 496, 916, 70, Hex("475569"), 5);
        Box(p, 540, py, 480, 900, 62, Hex("0F172A"), 6);
        Box(p, 540, 246, 130, 26, 13, Hex("475569"), 7);
        Txt(p, "Reels", 540, 340, 60, White, zh, bold: true);
        string[] labels = { "上傳  Upload", "音樂  Music", "發布  Share" };
        float[] ys = { 490, 660, 830 }, taps = { 24.0f, 25.2f, 26.4f }, hues = { 0.55f, 0.08f, 0.33f };
        var btn = new Material[3]; var checks = new TextMesh[3]; var dots = new GameObject[3]; var rings = new Mesh[3]; var ringMat = new Material[3];
        for (int i = 0; i < 3; i++)
        {
            btn[i] = Box(p, 540, ys[i], 390, 130, 30, Hex("1E293B"), 7).sharedMaterial;
            Txt(p, labels[i], 372, ys[i], 40, White, zh, TextAnchor.MiddleLeft);
            checks[i] = Txt(p, "✓", 690, ys[i], 54, White, mono, bold: true);
            // mtk-viewer touch style: filled dot with a bold white id, plus an expanding ring on touch-down.
            float tx = 672, ty = ys[i] + 14;
            dots[i] = Node("touch", p, tx, ty);
            Box(dots[i].transform, tx, ty, 68, 68, 34, Color.HSVToRGB(hues[i], 0.7f, 0.95f) * new Color(1, 1, 1, 0.85f), 30);
            Txt(dots[i].transform, (i + 1).ToString(), tx, ty, 34, White, mono, bold: true, order: 31);
            rings[i] = new Mesh();
            var rr = Shape(p, rings[i], tx, ty, White, 29); ringMat[i] = rr.sharedMaterial;
        }
        var hud = Txt(p, "", 96, 168, 30, Hex("D9D9D9"), mono, TextAnchor.MiddleLeft);
        var done = Txt(p, "已發布  Published", 540, 1010, 46, Green, zh, bold: true);
        anims.Add(t =>
        {
            int n = 0;
            for (int i = 0; i < 3; i++)
            {
                float since = t - taps[i];
                if (since >= 0f) n++;
                btn[i].color = Color.Lerp(Hex("1E293B"), Color.HSVToRGB(hues[i], 0.65f, 0.55f), OutCubic(U(t, taps[i], 0.25f)));
                checks[i].transform.localScale = Vector3.one * OutBack(U(t, taps[i] + 0.5f, 0.3f));
                dots[i].SetActive(since >= 0f && since < 0.55f);
                float pr = Mathf.Clamp01(since / 0.35f);
                bool live = since >= 0f && since < 0.35f;
                Ring(rings[i], live ? 40f + pr * 80f : 0.01f, 4f);
                var c = Color.HSVToRGB(hues[i], 0.8f, 1f); c.a = live ? 1f - pr : 0f; ringMat[i].color = c;
            }
            hud.text = "touch  " + n + " / 3";
            done.transform.localScale = Vector3.one * OutBack(U(t, 26.9f, 0.35f));
        });
    }

    static void BuildS6()
    {
        var g = shots[5] = Node("S6", null); var p = g.transform;
        var badge = Node("badge", p, 540, 560);
        Box(badge.transform, 540, 560, 260, 260, 130, Accent, 6);
        var tri = new Mesh(); tri.SetVertices(new List<Vector3> { new Vector3(-42, 62, 0), new Vector3(-42, -62, 0), new Vector3(70, 0, 0) });
        tri.SetTriangles(new[] { 0, 1, 2 }, 0);
        Shape(badge.transform, tri, 540, 560, Hex("0B1020"), 7);
        var title = Txt(p, "Fable 5.2  ×  Unity", 540, 830, 72, White, mono, bold: true);
        var sub = Txt(p, "30 秒短影音流程", 540, 950, 52, Dim, zh);
        anims.Add(t =>
        {
            badge.transform.localScale = Vector3.one * OutBack(U(t, 28.1f, 0.45f));
            title.transform.localScale = Vector3.one * OutCubic(U(t, 28.35f, 0.35f));
            sub.transform.localScale = Vector3.one * OutCubic(U(t, 28.55f, 0.35f));
        });
    }

    // ---------- scene + timeline ----------
    static void Build()
    {
        EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        anims.Clear();
        ShaderUtil.allowAsyncCompilation = false; // otherwise lit objects are skipped while variants compile
        flat = Shader.Find("Sprites/Default");
        zh = Font.CreateDynamicFontFromOSFont(new[] { "PingFang TC", "Heiti TC", "Arial Unicode MS" }, 64);
        mono = Font.CreateDynamicFontFromOSFont(new[] { "Menlo", "PingFang TC", "Heiti TC", "Arial Unicode MS" }, 64);

        QualitySettings.shadows = ShadowQuality.All; QualitySettings.shadowDistance = 60f;
        QualitySettings.shadowResolution = ShadowResolution.VeryHigh; QualitySettings.anisotropicFiltering = AnisotropicFiltering.ForceEnable;
        RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat; RenderSettings.ambientLight = Hex("5A6788");
        RenderSettings.fog = false; // linear fog blanked the whole 3D pass in batch rendering; the high camera hides the horizon instead

        var sun = new GameObject("Sun").AddComponent<Light>(); sun.type = LightType.Directional; sun.intensity = 1.15f;
        sun.shadows = LightShadows.Soft; sun.shadowStrength = 0.7f; sun.transform.rotation = Quaternion.Euler(48f, -35f, 0f);

        cam3D = new GameObject("Cam3D").AddComponent<Camera>();
        cam3D.fieldOfView = 40f; cam3D.clearFlags = CameraClearFlags.SolidColor; cam3D.cullingMask = ~(1 << UI); cam3D.farClipPlane = 200f;
        cam3D.enabled = false;
        cam2D = new GameObject("Cam2D").AddComponent<Camera>();
        cam2D.orthographic = true; cam2D.orthographicSize = H / 2f; cam2D.clearFlags = CameraClearFlags.SolidColor; cam2D.backgroundColor = Color.black; cam2D.cullingMask = 1 << UI;
        cam2D.transform.position = new Vector3(0f, 0f, -500f); cam2D.nearClipPlane = 1f; cam2D.farClipPlane = 1000f;
        cam2D.enabled = false;

        // Composite: the 3D pass goes to rt3D, which the 2D camera draws as a full-screen backdrop.
        // (Rendering both cameras into one MSAA target loses the first pass on Metal.)
        rt3D = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
        var quad = new Mesh();
        quad.SetVertices(new List<Vector3> { new Vector3(-W / 2f, -H / 2f, 0), new Vector3(W / 2f, -H / 2f, 0), new Vector3(W / 2f, H / 2f, 0), new Vector3(-W / 2f, H / 2f, 0) });
        quad.SetUVs(0, new List<Vector2> { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) });
        quad.SetTriangles(new[] { 0, 2, 1, 0, 3, 2 }, 0);
        var back = Shape(null, quad, W / 2f, H / 2f, White, -100);
        back.sharedMaterial = new Material(Shader.Find("Unlit/Texture")) { mainTexture = rt3D };
        back.transform.position += new Vector3(0, 0, 100f);

        BuildFloaters(); BuildS1(); BuildS2(); BuildS3(); BuildS4(); BuildS5(); BuildS6();
        var cur = Box(null, W / 2f, H / 2f, W, H, 0, Color.black, 1000); curtain = cur.sharedMaterial;
    }

    static void Apply(float t)
    {
        int s = 0; while (s < 5 && t >= Cuts[s + 1]) s++;
        for (int i = 0; i < 6; i++) shots[i].SetActive(i == s);
        floaters.SetActive(s == 0 || s == 5); stage.SetActive(s == 2);
        cam3D.backgroundColor = Hex(Bg[s]);
        Look(new Vector3(0f, 0f, -10f), Vector3.zero);
        foreach (var a in anims) a(t);
        // Dip to the background colour around every cut, plus fade in/out at the ends.
        float dip = 0f;
        for (int i = 1; i < 6; i++) dip = Mathf.Max(dip, 1f - Mathf.Abs(t - Cuts[i]) / 0.18f);
        dip = Mathf.Max(dip, 1f - t / 0.25f, 1f - (DUR - t) / 0.2f);
        var c = Hex(Bg[s]); c.a = Mathf.Clamp01(dip); curtain.color = c;
    }

    static void Frame(float t, RenderTexture rt, Texture2D tex)
    {
        Apply(t);
        cam3D.targetTexture = rt3D; cam3D.Render();
        cam2D.targetTexture = rt; cam2D.Render();
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false);
        RenderTexture.active = null;
    }

    public static void Render()
    {
        try
        {
            string outDir = Environment.GetEnvironmentVariable("REEL_OUT");
            if (string.IsNullOrEmpty(outDir)) throw new Exception("REEL_OUT is not set");
            Directory.CreateDirectory(outDir);
            Build();
            Debug.Log("[Reel] fonts: zh=" + string.Join(",", zh.fontNames) + " mono=" + string.Join(",", mono.fontNames));
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            Frame(0f, rt, tex); // warm-up: lets dynamic fonts populate their atlas

            string stills = Environment.GetEnvironmentVariable("REEL_STILLS");
            if (!string.IsNullOrEmpty(stills))
            {
                foreach (var s in stills.Split(','))
                {
                    float t = float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
                    Frame(t, rt, tex); tex.Apply();
                    File.WriteAllBytes(Path.Combine(outDir, "still_" + s + ".png"), tex.EncodeToPNG());
                }
                Debug.Log("[Reel] stills done"); EditorApplication.Exit(0); return;
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
                    var raw = tex.GetRawTextureData();
                    pipe.Write(raw, 0, raw.Length);
                    if (f % 150 == 0) Debug.Log("[Reel] frame " + f + "/" + total);
                }
                pipe.Flush(); pipe.Close(); ff.WaitForExit();
                if (ff.ExitCode != 0) throw new Exception("ffmpeg exit " + ff.ExitCode);
            }
            Debug.Log("[Reel] wrote " + outFile); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogError("[Reel] FAILED " + e); EditorApplication.Exit(1); }
    }
}
