// Refined apprentice (KidV2) and the character sheet renderer.
//
// KidV2 keeps KidV1's contract and local units (feet at origin, head centre (0,45), arm pivot (7.5,31),
// arm length 16.5), so every shot's choreography and Hand() positions stay valid when switching versions.
// Look: silhouette outline, gradient robe with belt and star-dotted hem, hair, nose, eye highlight, smile,
// back arm, scarf tail and a hat with a drooping tip. Idle motion (blink, breathing, hat/scarf sway) is a
// pure function of NowT, so renders stay reproducible.
//
// Colours come from stories/<name>/characters/<id>.json (STORY_CHARACTER_SPEC); missing fields keep defaults.
//
//   Unity -batchmode -projectPath unity -executeMethod StoryBuilder.RenderSheet
// Environment: SHEET_OUT (png path), STORY_CHARACTER_SPEC (optional json)
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public static partial class StoryBuilder
{
    [Serializable]
    class CharacterSpec
    {
        public string robe = "5B46B0", robeLight = "7B63D9", robeDark = "433288", trim = "E9C46A", trimDark = "B8923E";
        public string skin = "F6D7BD", skinShade = "E8BFA3", hair = "4A2E2A", hat = "7C5CE0", hatDark = "553CB0";
        public string scarf = "5AC8FA", boots = "2A2140", outline = "1A1230", eye = "221A38", cheek = "F29C9C", mouth = "8A3B4A";
        public float outlineWidth = 1.1f;
    }
    static CharacterSpec Spec = new CharacterSpec();

    static void LoadCharacterSpec()
    {
        Spec = new CharacterSpec();
        string p = Environment.GetEnvironmentVariable("STORY_CHARACTER_SPEC");
        if (!string.IsNullOrEmpty(p) && File.Exists(p)) { JsonUtility.FromJsonOverwrite(File.ReadAllText(p), Spec); Debug.Log("[Story] character spec " + p); }
    }

    // Convex polygon pushed outwards from its centroid; good enough for the small convex parts used here.
    static float[] Inflate(float[] xy, float d)
    {
        float cx = 0, cy = 0; int n = xy.Length / 2;
        for (int i = 0; i < n; i++) { cx += xy[2 * i]; cy += xy[2 * i + 1]; }
        cx /= n; cy /= n; var o = new float[xy.Length];
        for (int i = 0; i < n; i++)
        {
            var v = new Vector2(xy[2 * i] - cx, xy[2 * i + 1] - cy); var e = v.normalized * d;
            o[2 * i] = xy[2 * i] + e.x; o[2 * i + 1] = xy[2 * i + 1] + e.y;
        }
        return o;
    }

    static float[] Ellipse(float cx, float cy, float rx, float ry, int n = 28)
    {
        var xy = new float[2 * n];
        for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; xy[2 * i] = cx + Mathf.Cos(a) * rx; xy[2 * i + 1] = cy + Mathf.Sin(a) * ry; }
        return xy;
    }

    // Vertical gradient by vertex colour (top colour at max y).
    static Mesh PolyGrad(float[] xy, Color top, Color bottom)
    {
        var m = Poly(xy); float lo = float.MaxValue, hi = float.MinValue;
        for (int i = 1; i < xy.Length; i += 2) { lo = Mathf.Min(lo, xy[i]); hi = Mathf.Max(hi, xy[i]); }
        var cols = new List<Color>();
        for (int i = 1; i < xy.Length; i += 2) cols.Add(Color.Lerp(bottom, top, Mathf.InverseLerp(lo, hi, xy[i])));
        m.SetColors(cols); return m;
    }

    class KidV2 : Kid
    {
        Transform root, upper, head, eye, arm, back, legL, legR, hatTip, scarfTail; Glow shadow;
        float armDeg, sitNow, scaleNow; int face = 1; Vector3 feet;

        // Fill plus an outline copy one sorting step below; outlineOrder < 0 means "just under the fill".
        static void Shape(Transform p, float[] xy, Color fill, int order, int outlineOrder = -1, Color? bottom = null)
        {
            float w = Spec.outlineWidth;
            Part(p, Poly(Inflate(xy, w)), Vector3.zero, Hex(Spec.outline), outlineOrder < 0 ? order - 1 : outlineOrder);
            if (bottom == null) Part(p, Poly(xy), Vector3.zero, fill, order);
            else Part(p, PolyGrad(xy, fill, bottom.Value), Vector3.zero, White, order);
        }
        static void Fill(Transform p, float[] xy, Color c, int order) => Part(p, Poly(xy), Vector3.zero, c, order);

        public KidV2(Transform parent)
        {
            var S = Spec;
            shadow = new Glow(parent, Vector3.zero, 46, 11, 8, false);
            root = new GameObject("kid2") { layer = UI }.transform; root.SetParent(parent, false);
            const int SIL = 9;                                   // silhouette outline layer, under every fill

            legL = Pivot(root, -3.8f, 12f); legR = Pivot(root, 3.8f, 12f);
            foreach (var leg in new[] { legL, legR })
            {
                Shape(leg, new[] { -2.4f, 0, 2.4f, 0, 2.4f, -11, -2.4f, -11 }, Hex(S.boots), 10, SIL);
                Shape(leg, Ellipse(0.9f, -11.6f, 3.4f, 2.0f, 16), Hex(S.boots), 10, SIL);
            }

            upper = Pivot(root, 0, 0);
            back = Pivot(upper, -6.5f, 31f);                     // far arm, behind the body
            Shape(back, new[] { -2.4f, 1, 2.4f, 1, 3.0f, -13, -3.0f, -13 }, Hex(S.robeDark), 10, SIL);
            Fill(back, Ellipse(0, -15.2f, 2.8f, 2.8f, 16), Hex(S.skinShade), 10);

            Shape(upper, new[] { -9.5f, 35, 9.5f, 35, 14, 20, 17, 8, -17, 8, -14, 20 }, Hex(S.robeLight), 11, SIL, Hex(S.robe));
            Fill(upper, new[] { -17, 8, 17, 8, 17.4f, 4.6f, -17.4f, 4.6f }, Hex(S.trim), 12);            // hem trim
            Part(upper, Poly(Inflate(new[] { -17, 8, 17, 8, 17.4f, 4.6f, -17.4f, 4.6f }, S.outlineWidth)), Vector3.zero, Hex(S.outline), SIL);
            foreach (var x in new[] { -10f, 0f, 10f }) Fill(upper, Ellipse(x, 11.6f, 1.1f, 1.1f, 10), Hex(S.trim), 12);
            Fill(upper, new[] { -2, 35, 2, 35, 3.2f, 8, -3.2f, 8 }, Hex(S.robeDark), 12);                 // placket
            Fill(upper, new[] { -13.4f, 22.4f, 13.4f, 22.4f, 13.9f, 18.8f, -13.9f, 18.8f }, Hex(S.trimDark), 13); // belt
            Fill(upper, Ellipse(0, 20.6f, 2.3f, 2.3f, 14), Hex(S.trim), 14);

            head = Pivot(upper, 0, 0);
            Fill(head, Ellipse(-2.2f, 47.5f, 10.4f, 7.2f), Hex(S.hair), 12);                              // hair behind the face
            Shape(head, Ellipse(0, 45, 9.6f, 9.6f), Hex(S.skin), 13, SIL);
            Shape(head, Ellipse(9.0f, 43.6f, 1.7f, 1.5f, 12), Hex(S.skin), 13, SIL);                       // nose
            Fill(head, new[] { 2.5f, 51.5f, 9.4f, 49.4f, 8.6f, 46.8f, 4.0f, 48.2f }, Hex(S.hair), 15);    // fringe
            Fill(head, Ellipse(6.4f, 41.2f, 2.3f, 1.5f, 12), Hex(S.cheek, 0.65f), 16);
            eye = Pivot(head, 5.0f, 45.6f);
            Fill(eye, Ellipse(0, 0, 1.5f, 2.0f, 14), Hex(S.eye), 17);
            Fill(eye, Ellipse(0.45f, 0.75f, 0.55f, 0.55f, 8), White, 18);
            Fill(head, new[] { 3.9f, 40.9f, 6.9f, 41.1f, 5.6f, 39.6f }, Hex(S.mouth), 17);                  // smile

            Shape(upper, Ellipse(0, 35.2f, 10.6f, 3.0f), Hex(S.scarf), 16);                                   // scarf wrap
            scarfTail = Pivot(upper, -7f, 35.2f);
            Shape(scarfTail, new[] { 0, 1.2f, 0.4f, -1.4f, -7.4f, -6.0f, -9.2f, -3.0f }, Hex(S.scarf), 11, SIL);

            var hat = Pivot(head, 0, 50f);
            Shape(hat, new[] { -12, 0, 12, 0, 3.6f, 12.4f, -3.6f, 13.2f }, Hex(S.hat), 18, -1, Hex(S.hatDark));
            Fill(hat, new[] { -10.8f, 1.6f, 10.8f, 1.6f, 9.6f, 4.6f, -9.6f, 4.6f }, Hex(S.trim), 19);    // hat band
            Fill(hat, new[] { 1.6f, 9.4f, 2.3f, 7.0f, 1.6f, 4.6f, 0.9f, 7.0f }, Hex(S.trim), 19);          // emblem, vertical
            Fill(hat, new[] { -0.8f, 7.0f, 1.6f, 7.7f, 4.0f, 7.0f, 1.6f, 6.3f }, Hex(S.trim), 19);         // emblem, horizontal
            hatTip = Pivot(hat, 0, 12.6f);
            Shape(hatTip, new[] { -4.4f, -2.4f, 4.4f, -2.4f, -2.4f, 8.6f, -6.6f, 11.4f }, Hex(S.hat), 18, -1, Hex(S.hat));
            Shape(hatTip, Ellipse(-6.6f, 11.6f, 2.5f, 2.5f, 14), Hex(S.trim), 20);
            Shape(head, Ellipse(0, 50.5f, 14.6f, 3.4f), Hex(S.hatDark), 20);                              // brim

            arm = Pivot(upper, 7.5f, 31f);
            Shape(arm, new[] { -2.8f, 1, 2.8f, 1, 3.6f, -13.8f, -3.6f, -13.8f }, Hex(S.robeLight), 22, -1, Hex(S.robe));
            Fill(arm, new[] { -3.7f, -12.2f, 3.7f, -12.2f, 3.7f, -14.4f, -3.7f, -14.4f }, Hex(S.trim), 23);
            Shape(arm, Ellipse(0, -16.5f, 3.1f, 3.1f, 16), Hex(S.skin), 23, 21);
        }

        static Transform Pivot(Transform p, float x, float y) { var t = new GameObject("pivot") { layer = UI }.transform; t.SetParent(p, false); t.localPosition = new Vector3(x, y); return t; }

        public override void Pose(Vector3 feetLocal, float scale, int facing, float walk = 0f, float armDeg = 0f, float sit = 0f, float tip = 0f)
        {
            feet = feetLocal; face = facing; this.armDeg = armDeg; sitNow = sit; scaleNow = scale;
            root.gameObject.SetActive(scale > 0f); if (scale <= 0f) { shadow.Set(Color.black, 0f); return; }
            float t = NowT, step = Mathf.Sin(walk * Mathf.PI);
            float bob = Mathf.Abs(step) * 1.6f, swing = step * 24f, breathe = 1f + 0.012f * Mathf.Sin(t * 2.4f);
            root.localPosition = feetLocal + new Vector3(0, tip * 3.5f * scale); root.localScale = new Vector3(scale * facing, scale, 1f);
            upper.localPosition = new Vector3(0, bob - 9f * sit); upper.localScale = new Vector3(1f, breathe, 1f);
            legL.localPosition = new Vector3(-3.8f, 12f - 9f * sit); legR.localPosition = new Vector3(3.8f, 12f - 9f * sit);
            legL.localRotation = Quaternion.Euler(0, 0, swing * (1f - sit) + 82f * sit); legR.localRotation = Quaternion.Euler(0, 0, -swing * (1f - sit) + 74f * sit);
            arm.localRotation = Quaternion.Euler(0, 0, armDeg);
            back.localRotation = Quaternion.Euler(0, 0, -swing * 0.6f * (1f - sit) + 30f * sit + 10f * tip);
            head.localRotation = Quaternion.Euler(0, 0, -4f * tip + 1.2f * Mathf.Sin(t * 0.9f));
            // Secondary motion: the hat tip and scarf trail behind steps and drift a little at rest.
            hatTip.localRotation = Quaternion.Euler(0, 0, 16f + 5f * Mathf.Sin(t * 1.6f) + 9f * Mathf.Sin(walk * Mathf.PI * 2f - 0.6f));
            scarfTail.localRotation = Quaternion.Euler(0, 0, -6f + 7f * Mathf.Sin(t * 2.1f) + 10f * Mathf.Sin(walk * Mathf.PI * 2f - 1.1f));
            // Blink: 0.13 s every 3.7 s, plus a double blink every third cycle.
            float c = Mathf.Repeat(t, 3.7f), k = (c < 0.13f || (Mathf.Repeat(t, 11.1f) > 7.4f && c > 0.3f && c < 0.43f)) ? 0.12f : 1f;
            eye.localScale = new Vector3(1f, k, 1f);
            shadow.At(feetLocal + new Vector3(0, -1f * scale)); shadow.Size(46 * scale, 11 * scale); shadow.Set(Color.black, 0.45f);
        }

        public override Vector3 Hand(float reach = 0f)
        {
            float a = armDeg * Mathf.Deg2Rad, len = 16.5f + reach;
            return feet + scaleNow * new Vector3(face * (7.5f + Mathf.Sin(a) * len), 31f - Mathf.Cos(a) * len - 9f * sitNow);
        }
    }

    // ---------- character sheet: both versions side by side in the poses the story uses ----------
    public static void RenderSheet()
    {
        try
        {
            string outPath = Environment.GetEnvironmentVariable("SHEET_OUT");
            if (string.IsNullOrEmpty(outPath)) throw new Exception("SHEET_OUT is not set");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            W = 2560; H = 1900; Wide = true;
            InitCommon();
            var poses = new (string label, float walk, float arm, float sit, float tip, float t)[]
            {
                ("待機", 0f, 0f, 0f, 0f, 1f), ("走路 A", 0.5f, 0f, 0f, 0f, 1f), ("走路 B", 1.5f, 0f, 0f, 0f, 1f),
                ("伸手", 0f, 90f, 0f, 0f, 1f), ("踮腳舉高", 0f, 158f, 0f, 1f, 1f), ("坐下", 0f, 52f, 1f, 0f, 1f), ("眨眼", 0f, 20f, 0f, 0f, 0.05f),
            };
            float x0 = 360f, dx = 316f, scale = 4.6f;
            var rows = new (string label, float y, Func<Transform, Kid> make)[]
            {
                ("v1", 600f, p => new KidV1(p)), ("v2", 1170f, p => new KidV2(p)), ("v3 打磨", 1740f, p => new KidV3(p)),
            };
            var bg = Part(null, Quad(W, H, Hex("1B1F44"), Night), Px(W / 2f, H / 2f), White, -200);
            foreach (var row in rows)
            {
                Part(null, Quad(W - 120, 6), Px(W / 2f, row.y + 6), Hex("2E3466"), -150);
                Text(row.label, 150, row.y - 160, 46, true).color = Gold;
                for (int i = 0; i < poses.Length; i++)
                {
                    var p = poses[i]; float x = x0 + dx * i;
                    var holder = new GameObject("cell") { layer = UI }.transform;
                    var kid = row.make(holder);
                    NowT = p.t;
                    kid.Pose(Px(x, row.y), scale, 1, p.walk, p.arm, p.sit, p.tip);
                    if (row.y < 700f) Text(p.label, x, 110, 38, false).color = White;
                }
            }
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32) { antiAliasing = 8 };
            var tex = new Texture2D(W, H, TextureFormat.RGB24, false);
            cam.targetTexture = rt; cam.Render();
            RenderTexture.active = rt; tex.ReadPixels(new Rect(0, 0, W, H), 0, 0, false); tex.Apply(); RenderTexture.active = null;
            File.WriteAllBytes(outPath, tex.EncodeToPNG());
            Debug.Log("[Story] sheet " + outPath); EditorApplication.Exit(0);
        }
        catch (Exception e) { Debug.LogError("[Story] FAILED " + e); EditorApplication.Exit(1); }
    }
}
