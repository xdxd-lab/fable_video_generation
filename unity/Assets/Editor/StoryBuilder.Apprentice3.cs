// Apprentice v3: lit and grounded in the painted scenes, with smoother shapes and more motion.
//
// Same contract as v1/v2 (feet at origin, head centre (0,45), arm pivot (7.5,31), Hand() formula unchanged),
// so every shot keeps working. What v3 adds:
//   - Scene lighting: fills are multiplied by an ambient colour taken from the scene's backdrop tint, and a rim
//     light (an offset copy of each silhouette shape drawn under the fills) glows on the side facing the light.
//     Shots can point the rim at the star with Light(); otherwise a soft sky light comes from above.
//   - Curved shapes: quadratic-bezier outlines, ear-clipping triangulation and normal-offset outlines, so concave
//     parts (hat tip, hem trim) outline correctly.
//   - Translucent parts: a sheer star-veil cape, blush, robe shading and fold glazes.
//   - Two-bone arms (the drawn hand still lands on the Hand() point), 3/4 face with two eyes, look-at pupils,
//     eyebrows and a "wonder" mouth, lean and squash while walking, cape and scarf ribbons that trail.
// Walking speed is estimated from consecutive frames; StoryBuilder.Render pre-rolls a second before each still
// so stills match the video.
using System;
using System.Collections.Generic;
using UnityEngine;

public static partial class StoryBuilder
{
    // ---------- geometry for curved, possibly concave shapes ----------
    static List<Vector2> Bz(Vector2 a, Vector2 c, Vector2 b, int n = 8)
    {
        var o = new List<Vector2>();
        for (int i = 0; i <= n; i++) { float u = i / (float)n; o.Add((1 - u) * (1 - u) * a + 2 * (1 - u) * u * c + u * u * b); }
        return o;
    }

    // Concatenate point runs, dropping duplicates where runs meet.
    static List<Vector2> Pts(params IEnumerable<Vector2>[] runs)
    {
        var o = new List<Vector2>();
        foreach (var r in runs) foreach (var p in r) if (o.Count == 0 || (o[o.Count - 1] - p).sqrMagnitude > 1e-4f) o.Add(p);
        if (o.Count > 2 && (o[0] - o[o.Count - 1]).sqrMagnitude < 1e-4f) o.RemoveAt(o.Count - 1);
        return o;
    }
    static List<Vector2> V(params float[] xy) { var o = new List<Vector2>(); for (int i = 0; i < xy.Length; i += 2) o.Add(new Vector2(xy[i], xy[i + 1])); return o; }
    static List<Vector2> Oval(float cx, float cy, float rx, float ry, int n = 28)
    {
        var o = new List<Vector2>(); for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; o.Add(new Vector2(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry)); }
        return o;
    }

    static float Area(List<Vector2> p) { float s = 0; for (int i = 0; i < p.Count; i++) { var a = p[i]; var b = p[(i + 1) % p.Count]; s += a.x * b.y - b.x * a.y; } return s * 0.5f; }
    static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;
    static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
        return !((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0));
    }

    static List<int> EarClip(List<Vector2> p)
    {
        var idx = new List<int>(); for (int i = 0; i < p.Count; i++) idx.Add(i);
        if (Area(p) < 0) idx.Reverse();
        var tri = new List<int>(); int guard = 0;
        while (idx.Count > 3 && guard++ < 10000)
        {
            bool clipped = false;
            for (int i = 0; i < idx.Count && !clipped; i++)
            {
                int i0 = idx[(i + idx.Count - 1) % idx.Count], i1 = idx[i], i2 = idx[(i + 1) % idx.Count];
                Vector2 a = p[i0], b = p[i1], c = p[i2];
                if (Cross(b - a, c - b) <= 1e-7f) continue;                 // reflex corner
                bool inside = false;
                foreach (int j in idx) if (j != i0 && j != i1 && j != i2 && InTri(p[j], a, b, c)) { inside = true; break; }
                if (inside) continue;
                tri.Add(i0); tri.Add(i1); tri.Add(i2); idx.RemoveAt(i); clipped = true;
            }
            if (!clipped) break;                                            // degenerate input: fan the rest
        }
        for (int i = 1; i + 1 < idx.Count; i++) { tri.Add(idx[0]); tri.Add(idx[i]); tri.Add(idx[i + 1]); }
        return tri;
    }

    // Offset along vertex normals (outwards for d > 0), mitre-limited; works for concave outlines too.
    static List<Vector2> Grow(List<Vector2> p, float d)
    {
        int n = p.Count; float sgn = Area(p) > 0 ? 1f : -1f; var o = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
        {
            Vector2 a = p[(i + n - 1) % n], b = p[i], c = p[(i + 1) % n];
            Vector2 e1 = (b - a).normalized, e2 = (c - b).normalized;
            Vector2 n1 = new Vector2(e1.y, -e1.x) * sgn, n2 = new Vector2(e2.y, -e2.x) * sgn, m = n1 + n2;
            m = m.sqrMagnitude < 1e-6f ? n1 : m.normalized;
            o.Add(b + m * (d / Mathf.Max(0.4f, Vector2.Dot(m, n1))));
        }
        return o;
    }

    static Mesh Shp(List<Vector2> p, Color? top = null, Color? bottom = null)
    {
        var m = new Mesh(); var v = new List<Vector3>(); foreach (var q in p) v.Add(q);
        m.SetVertices(v); m.SetTriangles(EarClip(p), 0);
        if (top != null)
        {
            float lo = float.MaxValue, hi = float.MinValue; foreach (var q in p) { lo = Mathf.Min(lo, q.y); hi = Mathf.Max(hi, q.y); }
            var cols = new List<Color>(); foreach (var q in p) cols.Add(Color.Lerp(bottom ?? top.Value, top.Value, Mathf.InverseLerp(lo, hi, q.y)));
            m.SetColors(cols);
        }
        m.RecalculateBounds(); return m;
    }

    // n-pointed star fanned from its centre.
    static Mesh StarMesh(float r1, float r2, int n = 4)
    {
        var v = new List<Vector3> { Vector3.zero }; var tri = new List<int>();
        for (int i = 0; i < 2 * n; i++) { float a = Mathf.PI / 2f + i * Mathf.PI / n, r = i % 2 == 0 ? r1 : r2; v.Add(new Vector3(Mathf.Cos(a) * r, Mathf.Sin(a) * r)); }
        for (int i = 0; i < 2 * n; i++) { tri.Add(0); tri.Add(1 + i); tri.Add(1 + (i + 1) % (2 * n)); }
        var m = new Mesh(); m.SetVertices(v); m.SetTriangles(tri, 0); m.RecalculateBounds(); return m;
    }

    // Ribbon along a centre line, width tapering from w0 to w1, colour from c0 to c1 (alpha included).
    static void Ribbon(Mesh m, List<Vector2> line, float w0, float w1, Color c0, Color c1)
    {
        var v = new List<Vector3>(); var col = new List<Color>(); var tri = new List<int>(); int n = line.Count;
        for (int i = 0; i < n; i++)
        {
            Vector2 d = (line[Mathf.Min(i + 1, n - 1)] - line[Mathf.Max(i - 1, 0)]).normalized, nn = new Vector2(-d.y, d.x);
            float u = i / (float)(n - 1), w = Mathf.Lerp(w0, w1, u) * 0.5f;
            v.Add(line[i] + nn * w); v.Add(line[i] - nn * w); var c = Color.Lerp(c0, c1, u); col.Add(c); col.Add(c);
            if (i > 0) { int a = 2 * (i - 1); tri.Add(a); tri.Add(a + 2); tri.Add(a + 1); tri.Add(a + 1); tri.Add(a + 2); tri.Add(a + 3); }
        }
        m.Clear(); m.SetVertices(v); m.SetColors(col); m.SetTriangles(tri, 0); m.RecalculateBounds();
    }

    class KidV3 : Kid
    {
        // sorting layers, back to front
        const int CAPE = 10, SIL = 11, RIM = 12, TAIL = 13, BACK = 15, ROBE = 16, HEAD = 20, SCARF = 27, HAT = 29, ARM = 34;

        Transform root, upper, head, headArt, arm, armU, armF, back, backU, backF, legL, legR, hatTip, mouthSmile, mouthOpen, browL, browR;
        readonly Transform[] eyes = new Transform[2], irises = new Transform[2];
        readonly Glow shadowSoft, shadowCore, emblem;
        readonly Mesh cape = new Mesh(), tailA = new Mesh(), tailAEdge = new Mesh(), tailB = new Mesh(), tailBEdge = new Mesh();
        readonly List<(Material m, Color c)> tinted = new List<(Material, Color)>();
        readonly List<(Transform t, Material m)> rims = new List<(Transform, Material)>();
        readonly Material bgRef;
        float armDeg, sitNow, scaleNow, lastT = -99f, lastWalk, speed; int face = 1; Vector3 feet;
        Color scarf, scarfDark, outline;

        public KidV3(Transform parent)
        {
            var S = Spec;
            foreach (var s in scenes) if (s.root == parent) bgRef = s.bg;
            shadowSoft = new Glow(parent, Vector3.zero, 64, 15, 8, false); shadowCore = new Glow(parent, Vector3.zero, 34, 7, 9, false);
            root = new GameObject("kid3") { layer = UI }.transform; root.SetParent(parent, false);
            scarf = Hex(S.scarf); scarfDark = Color.Lerp(scarf, Hex(S.outline), 0.45f); outline = Hex(S.outline);

            // translucent star-veil cape, behind everything
            upper = Pivot(root, 0, 0);
            Tinted(Part(Pivot(upper, -4.5f, 33.5f), cape, Vector3.zero, White, CAPE));

            legL = Pivot(root, -3.8f, 12f); legR = Pivot(root, 3.8f, 12f);
            foreach (var leg in new[] { legL, legR })
                Shape(leg, Pts(V(-2.3f, 0, 2.3f, 0, 2.5f, -9.5f), Bz(new Vector2(2.5f, -9.5f), new Vector2(6.2f, -10f), new Vector2(4.4f, -12.4f), 4),
                                V(-2.6f, -12.4f), Bz(new Vector2(-2.6f, -12.4f), new Vector2(-3.4f, -11f), new Vector2(-2.6f, -9f), 3)), Hex(S.boots), BACK, rim: true);

            // far arm (behind the body), two segments
            back = Pivot(upper, -6.5f, 31f); backU = Pivot(back, 0, 0); backF = Pivot(back, 0, -8f);
            Shape(backU, Sleeve(4.6f, 4.8f, 8.6f), Hex(S.robeDark), BACK, rim: true);
            Shape(backF, Sleeve(4.6f, 6.0f, 7.4f), Hex(S.robeDark), BACK, rim: true);
            Shape(backF, Oval(0, -9.4f, 2.6f, 2.7f, 16), Hex(S.skinShade), BACK);

            // scarf tails flutter behind the shoulders
            var tails = Pivot(upper, -6.8f, 35.4f);
            Tinted(Part(tails, tailAEdge, Vector3.zero, White, TAIL)); Tinted(Part(tails, tailA, Vector3.zero, White, TAIL + 1));
            Tinted(Part(tails, tailBEdge, Vector3.zero, White, TAIL)); Tinted(Part(tails, tailB, Vector3.zero, White, TAIL + 1));

            // robe: A-line with bowed sides and a curved hem
            var hem = Bz(new Vector2(-17.6f, 6.5f), new Vector2(0, 2.6f), new Vector2(17.6f, 6.5f), 12);
            var robe = Pts(Bz(new Vector2(9.2f, 35.4f), new Vector2(0, 37.6f), new Vector2(-9.2f, 35.4f), 6),
                            Bz(new Vector2(-9.2f, 35.4f), new Vector2(-14.2f, 22f), new Vector2(-17.6f, 6.5f), 8), hem,
                            Bz(new Vector2(17.6f, 6.5f), new Vector2(14.2f, 22f), new Vector2(9.2f, 35.4f), 8));
            Shape(upper, robe, Hex(S.robeLight), ROBE, rim: true, bottom: Hex(S.robe));
            // shade side and fold glazes (translucent, so the gradient shows through)
            Glaze(upper, Pts(Bz(new Vector2(-9.0f, 35f), new Vector2(-14f, 22f), new Vector2(-17.2f, 6.8f), 8),
                              Bz(new Vector2(-17.2f, 6.8f), new Vector2(-12f, 4.6f), new Vector2(-8.5f, 4.1f), 4),
                              Bz(new Vector2(-8.5f, 4.1f), new Vector2(-6f, 20f), new Vector2(-4.2f, 35.6f), 6)), Hex(S.outline), 0.30f, ROBE + 1);
            Glaze(upper, Pts(Bz(new Vector2(2.0f, 22f), new Vector2(3.4f, 12f), new Vector2(5.6f, 3.9f), 6), Bz(new Vector2(7.6f, 4.0f), new Vector2(4.6f, 12f), new Vector2(2.6f, 22f), 6)), Hex(S.outline), 0.22f, ROBE + 1);
            Glaze(upper, Pts(Bz(new Vector2(10.2f, 30f), new Vector2(13.4f, 18f), new Vector2(15.4f, 8f), 6), Bz(new Vector2(13.6f, 7.4f), new Vector2(11.6f, 18f), new Vector2(9.0f, 30f), 6)), White, 0.16f, ROBE + 1);
            // hem trim follows the curve; tiny embroidered stars
            var trimTop = new List<Vector2>(); foreach (var q in hem) trimTop.Add(q + new Vector2(0, 3.0f));
            trimTop.Reverse(); var trim = Pts(hem, trimTop);
            Tinted(Part(upper, Shp(trim, Hex(S.trim), Hex(S.trimDark)), Vector3.zero, White, ROBE + 2));
            foreach (var x in new[] { -12f, -4f, 4f, 12f })
            {
                float y = 4.6f + 0.0123f * x * x;                            // on the hem curve
                Tinted(Part(upper, StarMesh(1.9f, 0.6f), new Vector3(x, y + 6.2f), Hex(S.trim), ROBE + 2));
            }
            // sash with a star buckle
            Tinted(Part(upper, Shp(Pts(Bz(new Vector2(-13.4f, 22.2f), new Vector2(0, 20.6f), new Vector2(13.4f, 22.2f), 8),
                                        Bz(new Vector2(13.8f, 18.8f), new Vector2(0, 17.2f), new Vector2(-13.8f, 18.8f), 8)), Hex(S.trim), Hex(S.trimDark)), Vector3.zero, White, ROBE + 2));
            Tinted(Part(upper, StarMesh(2.9f, 1.2f, 5), new Vector3(0, 19.6f), Hex(S.trim), ROBE + 3));

            // head pivots at the neck so tilts look natural
            head = Pivot(upper, 0, 37f); headArt = Pivot(head, 0, -37f);
            Fill(headArt, Pts(Oval(-2.4f, 47.2f, 11.6f, 8.8f)), Hex(S.hair), BACK);                  // back hair
            Shape(headArt, Oval(0, 45, 10.4f, 10.0f, 36), Hex(S.skin), HEAD, rim: true);
            Shape(headArt, Oval(10.0f, 43.4f, 1.5f, 1.3f, 12), Hex(S.skin), HEAD);                    // nose
            Glaze(headArt, Oval(-3.4f, 43.6f, 5.8f, 7.6f, 24), Hex(S.skinShade), 0.55f, HEAD + 1);    // far-cheek shade
            Glaze(headArt, Oval(6.8f, 40.8f, 2.6f, 1.6f, 16), Hex(S.cheek), 0.45f, HEAD + 2);       // blush
            Glaze(headArt, Oval(-3.6f, 40.8f, 1.9f, 1.3f, 16), Hex(S.cheek), 0.35f, HEAD + 2);
            // fringe: three soft locks over the forehead
            Fill(headArt, Pts(Bz(new Vector2(-9.8f, 50.4f), new Vector2(-2f, 57f), new Vector2(9.6f, 50.6f), 8),
                               Bz(new Vector2(9.6f, 50.6f), new Vector2(8.6f, 46.8f), new Vector2(6.2f, 48.4f), 4),
                               Bz(new Vector2(6.2f, 48.4f), new Vector2(4.6f, 46.4f), new Vector2(1.8f, 48.8f), 4),
                               Bz(new Vector2(1.8f, 48.8f), new Vector2(-1.2f, 46.6f), new Vector2(-3.8f, 49.4f), 4),
                               Bz(new Vector2(-3.8f, 49.4f), new Vector2(-7.6f, 47f), new Vector2(-9.8f, 50.4f), 4)), Hex(S.hair), HEAD + 2);
            Glaze(headArt, Pts(Bz(new Vector2(-5f, 53.4f), new Vector2(0, 55.2f), new Vector2(5.4f, 53.0f), 6), Bz(new Vector2(5.0f, 52.2f), new Vector2(0, 53.8f), new Vector2(-4.6f, 52.6f), 6)), White, 0.22f, HEAD + 3);
            // eyes: near (right) and far (left, narrower); iris moves for look-at
            for (int e = 0; e < 2; e++)
            {
                float ex = e == 0 ? 5.0f : -2.2f, sx = e == 0 ? 1f : 0.8f;
                eyes[e] = Pivot(headArt, ex, 44.6f); eyes[e].localScale = new Vector3(sx, 1, 1);
                Fill(eyes[e], Oval(0, 0, 2.1f, 2.7f, 20), Hex(S.eye), HEAD + 3);
                irises[e] = Pivot(eyes[e], 0, 0);
                Tinted(Part(irises[e], Shp(Oval(0.1f, -0.45f, 1.5f, 1.85f, 18), Hex("6E8BFF"), Hex("2A2C7A")), Vector3.zero, White, HEAD + 4));
                Fill(irises[e], Oval(0.1f, -0.3f, 0.8f, 1.1f, 12), Hex(S.eye), HEAD + 5);
                Part(irises[e], Disc(0.85f, 0.85f), new Vector3(-0.6f, 1.0f), White, HEAD + 6);
                Part(irises[e], Disc(0.4f, 0.4f), new Vector3(0.8f, -1.2f), new Color(1, 1, 1, 0.8f), HEAD + 6);
            }
            browL = Pivot(headArt, -2.2f, 48.3f); browR = Pivot(headArt, 5.2f, 48.5f);
            Fill(browR, Pts(Bz(new Vector2(-1.8f, -0.2f), new Vector2(0, 0.9f), new Vector2(1.9f, 0f), 5), Bz(new Vector2(1.9f, -0.6f), new Vector2(0, 0.2f), new Vector2(-1.8f, -0.8f), 5)), Hex(S.hair), HEAD + 3);
            Fill(browL, Pts(Bz(new Vector2(-1.4f, -0.2f), new Vector2(0, 0.8f), new Vector2(1.4f, 0f), 5), Bz(new Vector2(1.4f, -0.6f), new Vector2(0, 0.2f), new Vector2(-1.4f, -0.8f), 5)), Hex(S.hair), HEAD + 3);
            mouthSmile = Pivot(headArt, 4.6f, 40.4f);
            Fill(mouthSmile, Pts(Bz(new Vector2(-1.7f, 0.5f), new Vector2(0, -1.3f), new Vector2(1.7f, 0.6f), 6), Bz(new Vector2(1.7f, 0.6f), new Vector2(0, -0.6f), new Vector2(-1.7f, 0.5f), 6)), Hex(S.mouth), HEAD + 3);
            mouthOpen = Pivot(headArt, 4.4f, 40.0f);
            Fill(mouthOpen, Oval(0, 0, 1.4f, 1.7f, 16), Hex(S.mouth), HEAD + 3);
            Fill(mouthOpen, Oval(0, -0.8f, 0.9f, 0.6f, 12), Hex("E07A86"), HEAD + 4);

            // scarf wrap
            Shape(upper, Pts(Bz(new Vector2(-10.8f, 36.2f), new Vector2(0, 40.6f), new Vector2(10.8f, 36.2f), 8),
                              Bz(new Vector2(10.8f, 36.2f), new Vector2(11.6f, 34.2f), new Vector2(10.2f, 32.6f), 3),
                              Bz(new Vector2(10.2f, 32.6f), new Vector2(0, 30.0f), new Vector2(-10.2f, 32.6f), 8),
                              Bz(new Vector2(-10.2f, 32.6f), new Vector2(-11.6f, 34.2f), new Vector2(-10.8f, 36.2f), 3)), scarf, SCARF, rim: true, bottom: scarfDark);
            Glaze(upper, Pts(Bz(new Vector2(-8f, 37.6f), new Vector2(0, 40f), new Vector2(8.6f, 37.4f), 6), Bz(new Vector2(8.2f, 36.6f), new Vector2(0, 38.6f), new Vector2(-7.6f, 36.8f), 6)), White, 0.35f, SCARF + 1);

            // hat: bowed cone, band, star emblem (with a soft glow), brim, drooping tip with a pompom
            var hat = Pivot(headArt, 0, 0);
            Shape(hat, Pts(Bz(new Vector2(-10.6f, 51.2f), new Vector2(-9.0f, 60f), new Vector2(-5.4f, 63.8f), 6), V(3.4f, 64.2f),
                            Bz(new Vector2(3.4f, 64.2f), new Vector2(7.2f, 58f), new Vector2(10.6f, 51.2f), 6)), Hex(S.hat), HAT, rim: true, bottom: Hex(S.hatDark));
            Glaze(hat, Pts(Bz(new Vector2(4.2f, 53f), new Vector2(6.4f, 57.6f), new Vector2(2.6f, 63.4f), 5), Bz(new Vector2(0.8f, 63.4f), new Vector2(4f, 58f), new Vector2(2.0f, 53f), 5)), White, 0.18f, HAT + 1);
            Tinted(Part(hat, Shp(Pts(Bz(new Vector2(-10.4f, 52.4f), new Vector2(0, 54.2f), new Vector2(10.4f, 52.4f), 8),
                                      Bz(new Vector2(9.6f, 55.4f), new Vector2(0, 57.0f), new Vector2(-9.6f, 55.4f), 8)), Hex(S.trim), Hex(S.trimDark)), Vector3.zero, White, HAT + 1));
            emblem = new Glow(hat, new Vector3(1.6f, 59.4f), 9, 9, HAT + 2);
            Tinted(Part(hat, StarMesh(2.6f, 0.8f), new Vector3(1.6f, 59.4f), Hex("FFE9A8"), HAT + 3));
            hatTip = Pivot(hat, -1.0f, 63.8f);
            Shape(hatTip, Pts(V(-4.6f, -0.6f, 4.6f, -0.6f), Bz(new Vector2(4.6f, -0.6f), new Vector2(1.6f, 6.4f), new Vector2(-7.2f, 10.2f), 7),
                               Bz(new Vector2(-7.2f, 10.2f), new Vector2(-5.4f, 5.4f), new Vector2(-4.6f, -0.6f), 6)), Hex(S.hat), HAT, rim: true, bottom: Hex(S.hat));
            Shape(hatTip, Oval(-7.4f, 10.6f, 2.4f, 2.4f, 16), Hex(S.trim), HAT + 2, rim: true);
            Shape(headArt, Pts(Bz(new Vector2(-15.4f, 50.8f), new Vector2(0, 47.0f), new Vector2(15.4f, 50.8f), 10),
                                Bz(new Vector2(15.4f, 50.8f), new Vector2(0, 55.4f), new Vector2(-15.4f, 50.8f), 10)), Hex(S.hat), HAT + 2, rim: true, bottom: Hex(S.hatDark));

            // near arm: shoulder at (7.5,31); hand lands at (0,-16.5) in arm space, as in v1/v2
            arm = Pivot(upper, 7.5f, 31f); armU = Pivot(arm, 0, 0); armF = Pivot(arm, 0, -8.25f);
            Shape(armU, Sleeve(5.0f, 5.4f, 8.8f), Hex(S.robeLight), ARM, outlineOrder: ARM - 1, rim: true, bottom: Hex(S.robe));
            Shape(armF, Sleeve(5.2f, 7.0f, 6.9f), Hex(S.robeLight), ARM, outlineOrder: ARM - 1, rim: true, bottom: Hex(S.robe));
            Shape(armF, Oval(0, 0, 2.7f, 2.7f, 18), Hex(S.robeLight), ARM, outlineOrder: ARM - 1);
            Tinted(Part(armF, Shp(V(-3.6f, -5.4f, 3.6f, -5.4f, 3.7f, -7.1f, -3.7f, -7.1f), Hex(S.trim), Hex(S.trimDark)), Vector3.zero, White, ARM + 1));
            Shape(armF, Oval(0, -8.25f, 3.0f, 3.1f, 18), Hex(S.skin), ARM + 1, outlineOrder: ARM - 1);
            Shape(armF, Oval(1.9f, -7.2f, 1.2f, 1.6f, 12), Hex(S.skin), ARM + 1, outlineOrder: ARM - 1);   // thumb
        }

        static Transform Pivot(Transform p, float x, float y) { var t = new GameObject("pivot") { layer = UI }.transform; t.SetParent(p, false); t.localPosition = new Vector3(x, y); return t; }
        static List<Vector2> Sleeve(float wTop, float wEnd, float len) =>
            Pts(V(-wTop / 2, 0.6f, wTop / 2, 0.6f), Bz(new Vector2(wTop / 2, 0.6f), new Vector2(wTop / 2 + 0.4f, -len / 2), new Vector2(wEnd / 2, -len), 4),
                 Bz(new Vector2(-wEnd / 2, -len), new Vector2(-wTop / 2 - 0.4f, -len / 2), new Vector2(-wTop / 2, 0.6f), 4));

        MeshRenderer Tinted(MeshRenderer r) { tinted.Add((r.sharedMaterial, r.sharedMaterial.color)); return r; }
        void Fill(Transform p, List<Vector2> pts, Color c, int order) => Tinted(Part(p, Shp(pts), Vector3.zero, c, order));
        void Glaze(Transform p, List<Vector2> pts, Color c, float a, int order) { c.a = a; Tinted(Part(p, Shp(pts), Vector3.zero, c, order)); }

        // Fill + dark outline (+ rim copy that slides towards the light, under every fill).
        void Shape(Transform p, List<Vector2> pts, Color fill, int order, int outlineOrder = SIL, bool rim = false, Color? bottom = null)
        {
            float w = Spec.outlineWidth;
            Tinted(Part(p, Shp(Grow(pts, w)), Vector3.zero, outline, outlineOrder));
            if (rim)
            {
                var r = Part(p, Shp(Grow(pts, w * 0.35f)), Vector3.zero, new Color(1, 1, 1, 0), RIM);
                rims.Add((r.transform, r.sharedMaterial));
            }
            if (bottom == null) Tinted(Part(p, Shp(pts), Vector3.zero, fill, order));
            else Tinted(Part(p, Shp(pts, fill, bottom.Value), Vector3.zero, White, order));
        }

        public override void Pose(Vector3 feetLocal, float scale, int facing, float walk = 0f, float armDeg = 0f, float sit = 0f, float tip = 0f)
        {
            feet = feetLocal; face = facing; this.armDeg = armDeg; sitNow = sit; scaleNow = scale;
            root.gameObject.SetActive(scale > 0f);
            if (scale <= 0f) { shadowSoft.Set(Color.black, 0f); shadowCore.Set(Color.black, 0f); emblem.Set(Gold, 0f); return; }
            float t = NowT, dt = t - lastT;
            if (dt > 1e-4f && dt < 0.12f) speed = Mathf.Lerp(speed, Mathf.Abs(walk - lastWalk) / dt, 0.35f); else if (dt <= 0f || dt >= 0.12f) speed = 0f;
            lastT = t; lastWalk = walk;
            float moving = Mathf.Clamp01(speed / 1.5f) * (1f - sit);

            float step = Mathf.Sin(walk * Mathf.PI), bob = Mathf.Abs(step) * 1.6f, swing = step * 26f;
            float breathe = 0.014f * Mathf.Sin(t * 2.4f), squash = 0.035f * (1f - Mathf.Abs(step)) * moving;
            root.localPosition = feetLocal + new Vector3(0, tip * 3.5f * scale); root.localScale = new Vector3(scale * facing, scale, 1f);
            upper.localPosition = new Vector3(0, bob - 9f * sit);
            upper.localScale = new Vector3(1f + squash, 1f + breathe - squash, 1f);
            upper.localRotation = Quaternion.Euler(0, 0, -2.5f * moving + 0.7f * Mathf.Sin(t * 1.1f) * (1f - moving));
            legL.localPosition = new Vector3(-3.8f, 12f - 9f * sit); legR.localPosition = new Vector3(3.8f, 12f - 9f * sit);
            legL.localRotation = Quaternion.Euler(0, 0, swing * (1f - sit) + 82f * sit); legR.localRotation = Quaternion.Euler(0, 0, -swing * (1f - sit) + 74f * sit);

            // near arm: elbow bends back more when the arm hangs, less when reaching
            arm.localRotation = Quaternion.Euler(0, 0, armDeg);
            Bend(armU, armF, -Mathf.Lerp(2.6f, 0.9f, Mathf.Abs(Mathf.Sin(armDeg * Mathf.Deg2Rad))), 8.25f);
            back.localRotation = Quaternion.Euler(0, 0, -swing * 0.7f * (1f - sit) + 30f * sit + 12f * tip + 3f * Mathf.Sin(t * 1.3f));
            Bend(backU, backF, -2.2f, 8f);

            head.localRotation = Quaternion.Euler(0, 0, -4f * tip + 1.4f * Mathf.Sin(t * 0.9f) + 1.5f * moving * Mathf.Sin(walk * Mathf.PI * 2f));
            hatTip.localRotation = Quaternion.Euler(0, 0, 14f + 5f * Mathf.Sin(t * 1.6f) + 10f * moving * Mathf.Sin(walk * Mathf.PI * 2f - 0.6f) + 8f * moving);

            // blink: 0.13 s every 3.7 s, double blink every third cycle
            float c = Mathf.Repeat(t, 3.7f), k = (c < 0.13f || (Mathf.Repeat(t, 11.1f) > 7.4f && c > 0.3f && c < 0.43f)) ? 0.1f : 1f;
            eyes[0].localScale = new Vector3(1f, k, 1f); eyes[1].localScale = new Vector3(0.8f, k, 1f);
            irises[0].localPosition = irises[1].localPosition = Vector3.zero;
            Emote(0f);

            // ribbons: cape and scarf tails trail behind when walking and drift at rest
            CapeAndTails(t, walk, moving);

            float sh = 1f - 0.35f * tip;
            shadowSoft.At(feetLocal + new Vector3(0, -0.6f * scale)); shadowSoft.Size(64 * scale * sh, 15 * scale * sh); shadowSoft.Set(Hex("05061A"), 0.30f);
            shadowCore.At(feetLocal + new Vector3(0, -0.8f * scale)); shadowCore.Size(36 * scale * sh, 7 * scale * sh); shadowCore.Set(Hex("05061A"), 0.42f * sh);
            emblem.Set(Gold, 0.45f + 0.25f * Mathf.Sin(t * 3.1f));

            // scene ambient on every fill, then the default sky rim (shots may override with Light())
            Color amb = White;
            if (bgRef != null) { var b = bgRef.color; amb = new Color(Mathf.Lerp(1f, b.r, 0.8f) * 0.97f, Mathf.Lerp(1f, b.g, 0.8f) * 0.97f, Mathf.Lerp(1f, b.b, 0.8f), 1f); }
            foreach (var (m, bc) in tinted) m.color = new Color(bc.r * amb.r, bc.g * amb.g, bc.b * amb.b, bc.a);
            Light(feetLocal + new Vector3(-30f, 120f) * scale, Hex("D9CCFF"), 0.5f);
        }

        static void Bend(Transform up, Transform fore, float b, float half)
        {
            var e = new Vector2(b, -half); var h = new Vector2(0, -2f * half);
            float len = e.magnitude;
            up.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(e.x, -e.y) * Mathf.Rad2Deg);
            fore.localPosition = e; var d = h - e;
            fore.localRotation = Quaternion.Euler(0, 0, Mathf.Atan2(d.x, -d.y) * Mathf.Rad2Deg);
            up.localScale = fore.localScale = new Vector3(1f, len / half, 1f);
        }

        void CapeAndTails(float t, float walk, float moving)
        {
            var line = new List<Vector2>(); Vector2 p = Vector2.zero; const int n = 10; const float L = 29f;
            for (int i = 0; i <= n; i++)
            {
                float s = i / (float)n, th = (18f + 26f * moving + 5f * Mathf.Sin(t * 1.7f - s * 2.4f) + 6f * moving * Mathf.Sin(walk * Mathf.PI * 2f - s * 3f)) * Mathf.Deg2Rad * s;
                line.Add(p); p += new Vector2(-Mathf.Sin(th), -Mathf.Cos(th)) * (L / n);
            }
            Ribbon(cape, line, 9f, 21f, new Color(0.86f, 0.80f, 1f, 0.55f), new Color(0.70f, 0.78f, 1f, 0.10f));
            for (int k = 0; k < 2; k++)
            {
                var tl = new List<Vector2>(); Vector2 q = new Vector2(0, k * -1.2f); float len = k == 0 ? 11f : 8.5f;
                for (int i = 0; i <= 8; i++)
                {
                    float s = i / 8f, th = (-58f - 10f * k - 20f * moving + (9f + 8f * moving) * Mathf.Sin(t * (3.1f + k) - s * 3.4f + k)) * Mathf.Deg2Rad;
                    tl.Add(q); q += new Vector2(Mathf.Sin(th), -Mathf.Cos(th)) * (len / 8f);
                }
                Ribbon(k == 0 ? tailAEdge : tailBEdge, tl, 3.8f, 2.9f, outline, outline);
                Ribbon(k == 0 ? tailA : tailB, tl, 2.8f, 1.9f, scarf, scarfDark);
            }
        }

        public override void Light(Vector3 src, Color c, float k)
        {
            if (scaleNow <= 0f) return;
            Vector3 centre = root.TransformPoint(new Vector3(0, 30f, 0)), dir = (root.parent.TransformPoint(src) - centre).normalized;
            c.a = Mathf.Clamp01(k);
            foreach (var (tr, m) in rims)
            {
                var l = tr.parent.InverseTransformVector(dir); l.z = 0f;
                tr.localPosition = l.sqrMagnitude > 1e-8f ? l.normalized * 1.25f : Vector3.zero; m.color = c;
            }
        }

        public override void Look(Vector3 target, float w = 1f)
        {
            if (scaleNow <= 0f) return;
            var d = headArt.InverseTransformVector(root.parent.TransformPoint(target) - headArt.TransformPoint(new Vector3(0, 45f, 0))); d.z = 0f;
            if (d.sqrMagnitude < 1e-6f) return; d.Normalize();
            irises[0].localPosition = irises[1].localPosition = new Vector3(d.x * 0.55f, d.y * 0.55f) * w;
            head.localRotation *= Quaternion.Euler(0, 0, Mathf.Clamp(d.y, -0.6f, 1f) * 7f * w);
        }

        public override void Emote(float wonder)
        {
            wonder = Mathf.Clamp01(wonder);
            mouthSmile.localScale = Vector3.one * (1f - Smooth(wonder)); mouthOpen.localScale = Vector3.one * Smooth(wonder);
            browL.localPosition = new Vector3(-2.2f, 48.3f + 0.7f * wonder); browR.localPosition = new Vector3(5.2f, 48.5f + 0.7f * wonder);
        }

        public override Vector3 Hand(float reach = 0f)
        {
            float a = armDeg * Mathf.Deg2Rad, len = 16.5f + reach;
            return feet + scaleNow * new Vector3(face * (7.5f + Mathf.Sin(a) * len), 31f - Mathf.Cos(a) * len - 9f * sitNow);
        }
    }
}
