using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace MotionProbeTool
{
    /// <summary>Simple CPU canvas (top-left origin) with antialiased lines, discs, a 5x7 bitmap font and PNG output.</summary>
    internal class ProbeCanvas
    {
        public readonly int W, H;
        public readonly Color32[] Px;

        public ProbeCanvas(int w, int h, Color32 bg)
        {
            W = w; H = h;
            Px = new Color32[w * h];
            for (int i = 0; i < Px.Length; i++) Px[i] = bg;
        }

        int clipX0, clipY0, clipX1 = int.MaxValue, clipY1 = int.MaxValue;

        public void SetClip(int x, int y, int w, int h) { clipX0 = x; clipY0 = y; clipX1 = x + w; clipY1 = y + h; }
        public void ResetClip() { clipX0 = 0; clipY0 = 0; clipX1 = int.MaxValue; clipY1 = int.MaxValue; }

        public void Blend(int x, int y, Color32 c, float a)
        {
            if ((uint)x >= (uint)W || (uint)y >= (uint)H || a <= 0f) return;
            if (x < clipX0 || y < clipY0 || x >= clipX1 || y >= clipY1) return;
            int i = y * W + x;
            if (a >= 1f) { Px[i] = new Color32(c.r, c.g, c.b, 255); return; }
            var d = Px[i];
            Px[i] = new Color32((byte)(d.r + (c.r - d.r) * a), (byte)(d.g + (c.g - d.g) * a), (byte)(d.b + (c.b - d.b) * a), 255);
        }

        public void Rect(int x, int y, int w, int h, Color32 c, float a = 1f)
        {
            int x0 = Mathf.Max(0, x), y0 = Mathf.Max(0, y), x1 = Mathf.Min(W, x + w), y1 = Mathf.Min(H, y + h);
            for (int yy = y0; yy < y1; yy++)
                for (int xx = x0; xx < x1; xx++) Blend(xx, yy, c, a);
        }

        public void Outline(int x, int y, int w, int h, Color32 c, int t = 1)
        {
            Rect(x, y, w, t, c); Rect(x, y + h - t, w, t, c); Rect(x, y, t, h, c); Rect(x + w - t, y, t, h, c);
        }

        public void Disc(float cx, float cy, float r, Color32 c, float a = 1f)
        {
            int x0 = Mathf.FloorToInt(cx - r - 1), x1 = Mathf.CeilToInt(cx + r + 1);
            int y0 = Mathf.FloorToInt(cy - r - 1), y1 = Mathf.CeilToInt(cy + r + 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float cov = Mathf.Clamp01(r + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                    if (cov > 0f) Blend(x, y, c, a * cov);
                }
        }

        public void Ring(float cx, float cy, float r, Color32 c, float width = 1.5f)
        {
            int x0 = Mathf.FloorToInt(cx - r - width), x1 = Mathf.CeilToInt(cx + r + width);
            int y0 = Mathf.FloorToInt(cy - r - width), y1 = Mathf.CeilToInt(cy + r + width);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
                    float cov = Mathf.Clamp01(width * 0.5f + 0.5f - Mathf.Abs(Mathf.Sqrt(dx * dx + dy * dy) - r));
                    if (cov > 0f) Blend(x, y, c, cov);
                }
        }

        public void Line(float x0, float y0, float x1, float y1, Color32 c, float width = 1.5f, float a = 1f)
        {
            float r = width * 0.5f;
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(x0, x1) - r - 1)), maxX = Mathf.Min(W - 1, Mathf.CeilToInt(Mathf.Max(x0, x1) + r + 1));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(y0, y1) - r - 1)), maxY = Mathf.Min(H - 1, Mathf.CeilToInt(Mathf.Max(y0, y1) + r + 1));
            if (minX > maxX || minY > maxY) return;
            float dx = x1 - x0, dy = y1 - y0, l2 = dx * dx + dy * dy;
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float t = l2 > 1e-8f ? Mathf.Clamp01(((px - x0) * dx + (py - y0) * dy) / l2) : 0f;
                    float qx = x0 + t * dx - px, qy = y0 + t * dy - py;
                    float cov = Mathf.Clamp01(r + 0.5f - Mathf.Sqrt(qx * qx + qy * qy));
                    if (cov > 0f) Blend(x, y, c, a * cov);
                }
        }

        public void Dashed(float x0, float y0, float x1, float y1, Color32 c, float width = 1f, float dash = 6f, float a = 1f)
        {
            float len = Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            if (len < 1e-3f) return;
            for (float s = 0f; s < len; s += dash * 2f)
            {
                float e = Mathf.Min(len, s + dash);
                Line(x0 + (x1 - x0) * s / len, y0 + (y1 - y0) * s / len, x0 + (x1 - x0) * e / len, y0 + (y1 - y0) * e / len, c, width, a);
            }
        }

        public static int TextW(string s, int scale) => s.Length * 6 * scale;

        public void Text(int x, int y, string s, Color32 c, int scale = 2, bool shadow = true)
        {
            if (string.IsNullOrEmpty(s)) return;
            if (shadow) RawText(x + 1, y + 1, s, new Color32(0, 0, 0, 255), scale);
            RawText(x, y, s, c, scale);
        }

        void RawText(int x, int y, string s, Color32 c, int scale)
        {
            int cx = x;
            foreach (char ch in s)
            {
                var g = ProbeFont.Get(ch);
                for (int row = 0; row < 7; row++)
                    for (int col = 0; col < 5; col++)
                        if (g[row][col] == '#') Rect(cx + col * scale, y + row * scale, scale, scale, c);
                cx += 6 * scale;
            }
        }

        /// <summary>Draws a bottom-up pixel array (Texture2D layout) into the rect, bilinear resampled, with optional dim/desaturate.</summary>
        public void Image(Color32[] src, int sw, int sh, int dx, int dy, int dw, int dh, float bright = 1f, float sat = 1f)
        {
            ImageCrop(src, sw, sh, 0f, 0f, sw, sh, dx, dy, dw, dh, bright, sat, new Color32(0, 0, 0, 255));
        }

        /// <summary>Like Image, but samples only the source sub-rect (cx0, cy0, cw, ch) in top-down source pixels; outside the source = fill.</summary>
        public void ImageCrop(Color32[] src, int sw, int sh, float cx0, float cy0, float cw, float ch, int dx, int dy, int dw, int dh, float bright, float sat, Color32 fill)
        {
            for (int y = 0; y < dh; y++)
            {
                int ty = dy + y;
                if ((uint)ty >= (uint)H || ty < clipY0 || ty >= clipY1) continue;
                float sy = cy0 + (y + 0.5f) * ch / dh - 0.5f;
                bool rowOut = sy < -0.5f || sy > sh - 0.5f;
                int ya = Mathf.Clamp(Mathf.FloorToInt(sy), 0, sh - 1), yb = Mathf.Min(sh - 1, ya + 1);
                float fy = Mathf.Clamp01(sy - ya);
                int ra = (sh - 1 - ya) * sw, rb = (sh - 1 - yb) * sw;
                for (int x = 0; x < dw; x++)
                {
                    int tx = dx + x;
                    if ((uint)tx >= (uint)W || tx < clipX0 || tx >= clipX1) continue;
                    float sx = cx0 + (x + 0.5f) * cw / dw - 0.5f;
                    if (rowOut || sx < -0.5f || sx > sw - 0.5f) { Px[ty * W + tx] = fill; continue; }
                    int xa = Mathf.Clamp(Mathf.FloorToInt(sx), 0, sw - 1), xb = Mathf.Min(sw - 1, xa + 1);
                    float fx = Mathf.Clamp01(sx - xa);
                    Color32 p00 = src[ra + xa], p10 = src[ra + xb], p01 = src[rb + xa], p11 = src[rb + xb];
                    float r = Mathf.Lerp(Mathf.Lerp(p00.r, p10.r, fx), Mathf.Lerp(p01.r, p11.r, fx), fy);
                    float g = Mathf.Lerp(Mathf.Lerp(p00.g, p10.g, fx), Mathf.Lerp(p01.g, p11.g, fx), fy);
                    float b = Mathf.Lerp(Mathf.Lerp(p00.b, p10.b, fx), Mathf.Lerp(p01.b, p11.b, fx), fy);
                    if (sat != 1f)
                    {
                        float l = 0.299f * r + 0.587f * g + 0.114f * b;
                        r = l + (r - l) * sat; g = l + (g - l) * sat; b = l + (b - l) * sat;
                    }
                    Px[ty * W + tx] = new Color32((byte)Mathf.Clamp(r * bright, 0, 255), (byte)Mathf.Clamp(g * bright, 0, 255), (byte)Mathf.Clamp(b * bright, 0, 255), 255);
                }
            }
        }

        public void SavePng(string path)
        {
            var flipped = new Color32[W * H];
            for (int y = 0; y < H; y++) Array.Copy(Px, y * W, flipped, (H - 1 - y) * W, W);
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.SetPixels32(flipped);
            tex.Apply(false);
            File.WriteAllBytes(path, tex.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(tex);
        }
    }

    internal static class ProbeFont
    {
        static Dictionary<char, string[]> glyphs;

        public static string[] Get(char c)
        {
            if (glyphs == null) Build();
            c = char.ToUpperInvariant(c);
            return glyphs.TryGetValue(c, out var g) ? g : glyphs['?'];
        }

        static void A(char c, params string[] rows) => glyphs[c] = rows;

        static void Build()
        {
            glyphs = new Dictionary<char, string[]>();
            A(' ', ".....", ".....", ".....", ".....", ".....", ".....", ".....");
            A('A', ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
            A('B', "####.", "#...#", "#...#", "####.", "#...#", "#...#", "####.");
            A('C', ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###.");
            A('D', "####.", "#...#", "#...#", "#...#", "#...#", "#...#", "####.");
            A('E', "#####", "#....", "#....", "####.", "#....", "#....", "#####");
            A('F', "#####", "#....", "#....", "####.", "#....", "#....", "#....");
            A('G', ".###.", "#...#", "#....", "#.###", "#...#", "#...#", ".####");
            A('H', "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#");
            A('I', ".###.", "..#..", "..#..", "..#..", "..#..", "..#..", ".###.");
            A('J', "..###", "...#.", "...#.", "...#.", "...#.", "#..#.", ".##..");
            A('K', "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#");
            A('L', "#....", "#....", "#....", "#....", "#....", "#....", "#####");
            A('M', "#...#", "##.##", "#.#.#", "#.#.#", "#...#", "#...#", "#...#");
            A('N', "#...#", "#...#", "##..#", "#.#.#", "#..##", "#...#", "#...#");
            A('O', ".###.", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
            A('P', "####.", "#...#", "#...#", "####.", "#....", "#....", "#....");
            A('Q', ".###.", "#...#", "#...#", "#...#", "#.#.#", "#..#.", ".##.#");
            A('R', "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#");
            A('S', ".####", "#....", "#....", ".###.", "....#", "....#", "####.");
            A('T', "#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#..");
            A('U', "#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###.");
            A('V', "#...#", "#...#", "#...#", "#...#", "#...#", ".#.#.", "..#..");
            A('W', "#...#", "#...#", "#...#", "#.#.#", "#.#.#", "#.#.#", ".#.#.");
            A('X', "#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#");
            A('Y', "#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#..");
            A('Z', "#####", "....#", "...#.", "..#..", ".#...", "#....", "#####");
            A('0', ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###.");
            A('1', "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###.");
            A('2', ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####");
            A('3', "#####", "...#.", "..#..", "...#.", "....#", "#...#", ".###.");
            A('4', "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#.");
            A('5', "#####", "#....", "####.", "....#", "....#", "#...#", ".###.");
            A('6', "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###.");
            A('7', "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#...");
            A('8', ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###.");
            A('9', ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##..");
            A('.', ".....", ".....", ".....", ".....", ".....", ".##..", ".##..");
            A(',', ".....", ".....", ".....", ".....", ".##..", "..#..", ".#...");
            A(':', ".....", ".##..", ".##..", ".....", ".##..", ".##..", ".....");
            A('=', ".....", ".....", "#####", ".....", "#####", ".....", ".....");
            A('-', ".....", ".....", ".....", "#####", ".....", ".....", ".....");
            A('+', ".....", "..#..", "..#..", "#####", "..#..", "..#..", ".....");
            A('(', "...#.", "..#..", ".#...", ".#...", ".#...", "..#..", "...#.");
            A(')', ".#...", "..#..", "...#.", "...#.", "...#.", "..#..", ".#...");
            A('/', ".....", "....#", "...#.", "..#..", ".#...", "#....", ".....");
            A('%', "##...", "##..#", "...#.", "..#..", ".#...", "#..##", "...##");
            A('_', ".....", ".....", ".....", ".....", ".....", ".....", "#####");
            A('[', ".###.", ".#...", ".#...", ".#...", ".#...", ".#...", ".###.");
            A(']', ".###.", "...#.", "...#.", "...#.", "...#.", "...#.", ".###.");
            A('<', "...#.", "..#..", ".#...", "#....", ".#...", "..#..", "...#.");
            A('>', ".#...", "..#..", "...#.", "....#", "...#.", "..#..", ".#...");
            A('!', "..#..", "..#..", "..#..", "..#..", "..#..", ".....", "..#..");
            A('?', ".###.", "#...#", "....#", "...#.", "..#..", ".....", "..#..");
            A('#', ".#.#.", ".#.#.", "#####", ".#.#.", "#####", ".#.#.", ".#.#.");
            A('*', ".....", "..#..", "#.#.#", ".###.", "#.#.#", "..#..", ".....");
            A('\'', "..#..", "..#..", ".#...", ".....", ".....", ".....", ".....");
            A('|', "..#..", "..#..", "..#..", "..#..", "..#..", "..#..", "..#..");
            A('~', ".....", ".....", ".#...", "#.#.#", "...#.", ".....", ".....");
        }
    }

    /// <summary>Contact sheets, motion trails and charts built from a run's cached frames and transform data.</summary>
    internal static class ProbeImaging
    {
        static readonly Color32 BgDark = new Color32(22, 24, 28, 255);
        static readonly Color32 TextCol = new Color32(235, 235, 235, 255);
        static readonly Color32 Accent = new Color32(255, 170, 60, 255);
        static readonly Color32 Dim = new Color32(150, 150, 150, 255);
        static readonly Color32[] Palette =
        {
            new Color32(255, 99, 99, 255), new Color32(80, 200, 120, 255), new Color32(80, 160, 255, 255), new Color32(255, 170, 60, 255),
            new Color32(200, 120, 255, 255), new Color32(70, 230, 230, 255), new Color32(255, 110, 210, 255), new Color32(210, 240, 80, 255),
        };

        static bool LoadFrame(ProbeData d, ProbeViewRec v, int f, out Color32[] px, out int w, out int h)
        {
            px = null; w = h = 0;
            string p = d.FramePath(v, f);
            if (!File.Exists(p)) return false;
            var tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(p))) return false;
                px = tex.GetPixels32(); w = tex.width; h = tex.height;
                return true;
            }
            finally { UnityEngine.Object.DestroyImmediate(tex); }
        }

        /// <summary>Projects a world point into a view's image at frame f (pixels, top-left origin, scaled by sx/sy).</summary>
        static Vector2 Project(ProbeData d, ProbeViewRec v, int f, Vector3 world, float sx, float sy)
        {
            Vector3 rp = d.rootPos[f];
            var rigPos = new Vector3(rp.x, d.meta.refRootY, rp.z);
            var rig = Quaternion.Euler(0f, d.rootRot[f].eulerAngles.y, 0f);
            Vector3 camPos = rigPos + rig * v.camLocalPos;
            Quaternion camRot = rig * v.camLocalRot;
            Vector3 pc = Quaternion.Inverse(camRot) * (world - camPos);
            float halfH = v.ortho, halfW = v.ortho * v.w / v.h;
            return new Vector2((pc.x / halfW * 0.5f + 0.5f) * v.w * sx, (0.5f - pc.y / halfH * 0.5f) * v.h * sy);
        }

        static List<ProbeViewRec> SelectViews(ProbeData d, string group, string viewsCsv)
        {
            var all = d.meta.views.Where(v => v.group == group).ToList();
            if (string.IsNullOrWhiteSpace(viewsCsv)) return all;
            var want = viewsCsv.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim().ToLowerInvariant()).Select(s => s == "right" ? "side" : s).ToList();
            return want.Select(w => all.FirstOrDefault(v => v.name == w)).Where(v => v != null).ToList();
        }

        public static string Sheet(ProbeData d, string group, List<float> times, List<string> labels, string viewsCsv, string outPath, string title)
        {
            var views = SelectViews(d, group, viewsCsv);
            if (views.Count == 0) throw new Exception("no '" + group + "' views recorded" + (string.IsNullOrEmpty(viewsCsv) ? "" : " matching '" + viewsCsv + "'") + "; recorded: " + string.Join(",", d.meta.views.Select(v => v.id)));
            if (times == null || times.Count == 0) throw new Exception("no times");
            int cols = times.Count, rows = views.Count;
            int vw = views[0].w, vh = views[0].h;
            const int labelW = 64, headerH = 26, colHeadH = 34, gap = 3, maxW = 1560, maxH = 1500;
            float scale = Mathf.Min(1f, Mathf.Min((maxW - labelW - gap * (cols + 1)) / (float)(cols * vw), (maxH - headerH - colHeadH - gap * (rows + 1)) / (float)(rows * vh)));
            int cw = Mathf.Max(32, Mathf.RoundToInt(vw * scale)), chh = Mathf.Max(32, Mathf.RoundToInt(vh * scale));
            int W = labelW + cols * (cw + gap) + gap, H = headerH + colHeadH + rows * (chh + gap) + gap;
            var cv = new ProbeCanvas(W, H, BgDark);
            cv.Text(8, 6, (title ?? (d.meta.avatar + "  " + Path.GetFileName(d.dir) + "  " + group.ToUpperInvariant())).ToUpperInvariant(), TextCol, 2);

            for (int ci = 0; ci < cols; ci++)
            {
                int f = d.NearestImageFrame(times[ci]);
                float t = f * d.meta.dt;
                int x = labelW + gap + ci * (cw + gap);
                string ctx = labels != null && ci < labels.Count ? labels[ci] : Ctx(d, t);
                bool eventCol = ctx != null && !ctx.StartsWith("start") && EventDelta(ctx) < 0.6f;
                cv.Text(x + 2, headerH + 2, "T=" + ProbeIO.F(t), TextCol, 2);
                cv.Text(x + 2, headerH + 20, Fit(ctx ?? "", cw - 4, 1), eventCol ? Accent : Dim, 1, false);
                for (int ri = 0; ri < rows; ri++)
                {
                    var v = views[ri];
                    int y = headerH + colHeadH + gap + ri * (chh + gap);
                    if (LoadFrame(d, v, f, out var px, out int w, out int h)) cv.Image(px, w, h, x, y, cw, chh);
                    else { cv.Rect(x, y, cw, chh, new Color32(60, 30, 30, 255)); cv.Text(x + 4, y + 4, "NO FRAME", TextCol, 1); }
                    if (v.group == "body")
                    {
                        Vector3 rp = d.rootPos[f];
                        var fp = Project(d, v, f, new Vector3(rp.x, d.meta.floorY, rp.z), cw / (float)v.w, chh / (float)v.h);
                        if (fp.y > 0 && fp.y < chh) cv.Dashed(x, y + fp.y, x + cw, y + fp.y, new Color32(20, 20, 20, 255), 1f, 4f, 0.6f);
                    }
                    if (ci == 0) cv.Text(6, y + chh / 2 - 7, v.name.ToUpperInvariant(), TextCol, 2);
                }
            }
            cv.SavePng(outPath);
            return outPath;
        }

        static float EventDelta(string ctx)
        {
            int i = ctx.LastIndexOf('+');
            if (i < 0) return 99f;
            return float.TryParse(ctx.Substring(i + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : 99f;
        }

        static string Ctx(ProbeData d, float t)
        {
            ProbeEventRec best = null;
            foreach (var e in d.meta.events) if (e.t <= t + 1e-4f && (best == null || e.t >= best.t)) best = e;
            return best == null ? "start+" + ProbeIO.F(t) : best.label + "+" + ProbeIO.F(t - best.t);
        }

        static string Fit(string s, int width, int scale)
        {
            int max = Mathf.Max(1, width / (6 * scale));
            return s.Length <= max ? s : s.Substring(0, max);
        }

        static Color32 TimeColor(float u)
        {
            u = Mathf.Clamp01(u);
            Color a, b; float t;
            if (u < 1f / 3f) { a = new Color(0.25f, 0.5f, 1f); b = new Color(0.2f, 0.9f, 0.5f); t = u * 3f; }
            else if (u < 2f / 3f) { a = new Color(0.2f, 0.9f, 0.5f); b = new Color(1f, 0.85f, 0.2f); t = u * 3f - 1f; }
            else { a = new Color(1f, 0.85f, 0.2f); b = new Color(1f, 0.25f, 0.25f); t = u * 3f - 2f; }
            return (Color32)Color.Lerp(a, b, t);
        }

        static bool Match(string name, string filter) => string.IsNullOrEmpty(filter) || name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// kind: physbone = zoomed panels per PhysBone group (default) | body = whole-body overview of limb paths |
        /// limbs = zoomed panels per limb for [t0,t1] | cycle = zoomed limb panels over one detected loop period | overview = all PhysBone tips in one picture
        /// </summary>
        public static string Trail(ProbeData d, ProbeAnalysis a, float t0, float t1, string kind, string filter, string outPath)
        {
            string k = (kind ?? "physbone").ToLowerInvariant();
            if (k.StartsWith("body")) return BodyOverview(d, a, t0, t1, outPath);
            if (k.StartsWith("limb")) return LimbTrails(d, a, t0, t1, false, outPath, "LIMB PATHS RELATIVE TO ROOT");
            if (k.StartsWith("cycle"))
            {
                var b = a.body.FirstOrDefault(x => x.period > 0f);
                if (b == null) throw new Exception("no loop (periodic body motion) was detected; use kind 'limbs' with a time window instead");
                return LimbTrails(d, a, b.steadyT0, b.steadyT0 + b.period, true, outPath, "ONE LOOP OF THE BODY MOTION, PERIOD " + ProbeIO.F(b.period, 3) + " S");
            }
            if (k.StartsWith("over")) return PhysOverview(d, t0, t1, filter, outPath);
            return GroupTrails(d, a, t0, t1, filter, outPath);
        }

        class ZoomSpec
        {
            public ProbeViewRec v;
            public int fRef;
            public string label;
            public readonly List<(Vector2[] pts, Color32 col, float width, float alpha)> lines = new List<(Vector2[], Color32, float, float)>();
            public readonly List<(Vector2[] pts, Color32 col, float width, float alpha)> context = new List<(Vector2[], Color32, float, float)>(); // drawn but not used for framing
            public readonly List<(Vector2[] pts, float u0, float u1)> paths = new List<(Vector2[], float, float)>();
            public readonly List<(Vector2 p, string text)> marks = new List<(Vector2, string)>();
        }

        static readonly Color32 White = new Color32(255, 255, 255, 255);
        static readonly Color32 PeakCol = new Color32(255, 140, 40, 255);
        static readonly Color32 PanelFill = new Color32(34, 36, 42, 255);

        /// <summary>Draws one zoomed panel: crops the cached frame around everything in the spec, then overlays lines, time-coloured paths and a scale bar.</summary>
        static void DrawZoom(ProbeCanvas cv, ProbeData d, ZoomSpec s, int px0, int py0, int pw, int ph)
        {
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            void Acc(Vector2 p) { minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y); }
            foreach (var l in s.lines) foreach (var p in l.pts) Acc(p);
            foreach (var l in s.paths) foreach (var p in l.pts) Acc(p);
            foreach (var m in s.marks) Acc(m.p);
            if (minX > maxX) { minX = 0; maxX = s.v.w; minY = 0; maxY = s.v.h; }
            float ppm = s.v.h / (2f * s.v.ortho);
            float minSize = 0.06f * ppm;
            float bw = Mathf.Max(maxX - minX, minSize) * 1.25f + 8f, bh = Mathf.Max(maxY - minY, minSize) * 1.25f + 8f;
            float aspect = pw / (float)ph;
            if (bw / bh < aspect) bw = bh * aspect; else bh = bw / aspect;
            float bx0 = (minX + maxX) * 0.5f - bw * 0.5f, by0 = (minY + maxY) * 0.5f - bh * 0.5f;
            float zoom = pw / bw;

            if (LoadFrame(d, s.v, s.fRef, out var img, out int iw, out int ih)) cv.ImageCrop(img, iw, ih, bx0, by0, bw, bh, px0, py0, pw, ph, 0.5f, 0.35f, PanelFill);
            else cv.Rect(px0, py0, pw, ph, PanelFill);
            cv.SetClip(px0, py0, pw, ph);
            Vector2 T(Vector2 p) => new Vector2(px0 + (p.x - bx0) * zoom, py0 + (p.y - by0) * zoom);
            foreach (var l in s.context.Concat(s.lines))
                for (int i = 0; i + 1 < l.pts.Length; i++)
                {
                    var a = T(l.pts[i]); var b = T(l.pts[i + 1]);
                    cv.Line(a.x, a.y, b.x, b.y, l.col, l.width, l.alpha);
                }
            foreach (var l in s.paths)
                for (int i = 0; i + 1 < l.pts.Length; i++)
                {
                    var a = T(l.pts[i]); var b = T(l.pts[i + 1]);
                    float u = Mathf.Lerp(l.u0, l.u1, l.pts.Length > 2 ? i / (float)(l.pts.Length - 2) : 0f);
                    cv.Line(a.x, a.y, b.x, b.y, TimeColor(u), 2f);
                }
            foreach (var m in s.marks)
            {
                var p = T(m.p);
                cv.Disc(p.x, p.y, 2.6f, White);
                if (!string.IsNullOrEmpty(m.text)) cv.Text(Mathf.RoundToInt(p.x) + 4, Mathf.RoundToInt(p.y) - 9, m.text, White, 1);
            }
            // scale bar
            float ppmZ = ppm * zoom;
            float want = 0.28f * pw / ppmZ;
            float[] nice = { 0.002f, 0.005f, 0.01f, 0.02f, 0.05f, 0.1f, 0.2f, 0.5f, 1f };
            float L = nice[0];
            foreach (var n in nice) if (Mathf.Abs(Mathf.Log(n / want)) < Mathf.Abs(Mathf.Log(L / want))) L = n;
            int barPx = Mathf.RoundToInt(L * ppmZ);
            int by = py0 + ph - 10;
            cv.Rect(px0 + 8, by, barPx, 2, White);
            cv.Rect(px0 + 8, by - 4, 1, 6, White); cv.Rect(px0 + 8 + barPx - 1, by - 4, 1, 6, White);
            string lab = L >= 0.01f ? ProbeIO.F(L * 100f, 0) + " CM" : ProbeIO.F(L * 1000f, 0) + " MM";
            cv.Text(px0 + 12 + barPx, by - 6, lab, White, 1);
            if (!string.IsNullOrEmpty(s.label)) cv.Text(px0 + 5, py0 + 5, s.label, White, 1);
            cv.ResetClip();
            cv.Outline(px0 - 1, py0 - 1, pw + 2, ph + 2, new Color32(70, 70, 80, 255));
        }

        static Vector2[] ProjectAll(ProbeData d, ProbeViewRec v, int fRef, IEnumerable<Vector3> world) =>
            world.Select(w => Project(d, v, fRef, w, 1f, 1f)).ToArray();

        static ProbeViewRec ViewFor(ProbeData d, bool preferHead, string name)
        {
            var v = preferHead ? d.View("head", name) : null;
            return v ?? d.View("body", name) ?? d.View("head", name);
        }

        /// <summary>Small multiples: for each of the most active PhysBone groups, a zoomed front and side panel with rest shape (white),
        /// shape at the group's peak (orange) and tip paths relative to the parent bone (colour = time).</summary>
        static string GroupTrails(ProbeData d, ProbeAnalysis a, float t0, float t1, string filter, string outPath)
        {
            int f0 = d.FrameAt(t0), f1 = Mathf.Max(f0 + 1, d.FrameAt(t1));
            int fRef = d.NearestImageFrame(t0);
            var units = new List<(string name, List<int> chains, float peak, int peakF, bool head)>();
            foreach (var g in a.groups)
            {
                bool groupMatch = Match(g.name, filter);
                var chs = g.chains.Where(c => groupMatch || Match(d.meta.chains[c].name, filter)).ToList();
                if (chs.Count == 0) continue;
                var peaks = chs.ToDictionary(c => c, c => { float m = 0f; for (int f = f0; f <= f1; f++) m = Mathf.Max(m, a.mag[c][f]); return m; });
                if (chs.Count > 10) chs = chs.OrderByDescending(c => peaks[c]).Take(10).ToList();
                int bestC = chs.OrderByDescending(c => peaks[c]).First();
                int pkF = f0;
                for (int f = f0; f <= f1; f++) if (a.mag[bestC][f] > a.mag[bestC][pkF]) pkF = f;
                bool head = g.comps.All(ci => d.meta.comps[ci].underHead);
                units.Add((g.name, chs, peaks[bestC], pkF, head));
            }
            units = units.OrderByDescending(u => u.peak).ToList();
            var moving = units.Where(u => u.peak >= 0.003f).Take(6).ToList();
            if (moving.Count == 0) moving = units.Take(Mathf.Min(2, units.Count)).ToList();
            if (moving.Count == 0) throw new Exception("no PhysBone chain matches '" + filter + "'");

            const int pw = 270, ph = 270, gap = 6, unitGap = 14, titleH = 20, headerH = 30, footerH = 46;
            int perRow = Mathf.Min(2, moving.Count);
            int rows = (moving.Count + perRow - 1) / perRow;
            int W = gap + perRow * (2 * (pw + gap)) + (perRow - 1) * unitGap, H = headerH + rows * (titleH + ph + gap) + footerH;
            var cv = new ProbeCanvas(W, H, BgDark);
            cv.Text(8, 8, ("PHYSBONE GROUPS: TIP PATHS RELATIVE TO PARENT BONE  T=" + ProbeIO.F(f0 * d.meta.dt) + "-" + ProbeIO.F(f1 * d.meta.dt) + (string.IsNullOrEmpty(filter) ? "" : "  FILTER " + filter)).ToUpperInvariant(), TextCol, 2);

            for (int ui = 0; ui < moving.Count; ui++)
            {
                var u = moving[ui];
                int x0 = gap + (ui % perRow) * (2 * (pw + gap) + unitGap), y0 = headerH + (ui / perRow) * (titleH + ph + gap);
                int degF = u.peakF;
                float maxDeg = u.chains.Max(c => { float m = 0f; for (int f = f0; f <= f1; f++) m = Mathf.Max(m, a.ang[c][f]); return m; });
                cv.Text(x0, y0 + 3, Fit((u.name + "  PEAK " + ProbeIO.Cm(u.peak) + " CM  " + ProbeIO.F(maxDeg, 0) + " DEG  AT T=" + ProbeIO.F(u.peakF * d.meta.dt)).ToUpperInvariant(), 2 * (pw + gap), 2), Accent, 2);
                var frames = u.chains.Select(c => d.meta.chains[c].frame).Distinct().ToList();
                var mRef = frames.ToDictionary(r => r, r => d.FrameM(fRef, r));
                var relPk = frames.ToDictionary(r => r, r => mRef[r] * d.FrameM(u.peakF, r).inverse);
                var relAt = new Dictionary<int, Dictionary<int, Matrix4x4>>();
                for (int f = f0; f <= f1; f++) relAt[f] = frames.ToDictionary(r => r, r => mRef[r] * d.FrameM(f, r).inverse);
                string[] names = { "front", "side" };
                for (int k = 0; k < 2; k++)
                {
                    var v = ViewFor(d, u.head, names[k]);
                    if (v == null) continue;
                    var spec = new ZoomSpec { v = v, fRef = fRef, label = (v.group + " " + v.name).ToUpperInvariant() };
                    foreach (int c in u.chains)
                    {
                        var ch = d.meta.chains[c];
                        spec.lines.Add((ProjectAll(d, v, fRef, ch.pts.Select(p => d.Pt(fRef, p))), White, 1.3f, 0.8f));
                        spec.lines.Add((ProjectAll(d, v, fRef, ch.pts.Select(p => relPk[ch.frame].MultiplyPoint3x4(d.Pt(u.peakF, p)))), PeakCol, 1.4f, 0.9f));
                        int tip = ch.pts[ch.pts.Length - 1];
                        var path = new List<Vector3>();
                        for (int f = f0; f <= f1; f++) path.Add(relAt[f][ch.frame].MultiplyPoint3x4(d.Pt(f, tip)));
                        spec.paths.Add((ProjectAll(d, v, fRef, path), 0f, 1f));
                    }
                    DrawZoom(cv, d, spec, x0 + k * (pw + gap), y0 + titleH, pw, ph);
                }
            }
            int fy = H - footerH + 8;
            int bx = 10, bwid = 360;
            for (int i = 0; i < bwid; i++) cv.Rect(bx + i, fy, 1, 12, TimeColor(i / (float)(bwid - 1)));
            cv.Text(bx, fy + 16, "T=" + ProbeIO.F(f0 * d.meta.dt), TextCol, 1);
            string e1 = "T=" + ProbeIO.F(f1 * d.meta.dt);
            cv.Text(bx + bwid - ProbeCanvas.TextW(e1, 1), fy + 16, e1, TextCol, 1);
            cv.Text(bx + bwid + 14, fy, "PATH COLOUR = TIME.  WHITE = REST SHAPE AT T=" + ProbeIO.F(fRef * d.meta.dt) + ".  ORANGE = SHAPE AT THE GROUP'S PEAK.", TextCol, 1);
            cv.Text(bx + bwid + 14, fy + 14, "MOTION OF THE BODY IS REMOVED: EACH CHAIN IS SHOWN RELATIVE TO ITS PARENT BONE. BAR = SCALE.", TextCol, 1);
            cv.SavePng(outPath);
            return outPath;
        }

        static readonly string[][] SkeletonBones =
        {
            new[] { "Hips", "Spine" }, new[] { "Spine", "Chest" }, new[] { "Chest", "UpperChest" }, new[] { "UpperChest", "Neck" }, new[] { "Chest", "Neck" }, new[] { "Neck", "Head" },
            new[] { "LeftUpperArm", "LeftLowerArm" }, new[] { "LeftLowerArm", "LeftHand" }, new[] { "RightUpperArm", "RightLowerArm" }, new[] { "RightLowerArm", "RightHand" },
            new[] { "LeftUpperLeg", "LeftLowerLeg" }, new[] { "LeftLowerLeg", "LeftFoot" }, new[] { "LeftFoot", "LeftToes" },
            new[] { "RightUpperLeg", "RightLowerLeg" }, new[] { "RightLowerLeg", "RightFoot" }, new[] { "RightFoot", "RightToes" },
            new[] { "Hips", "LeftUpperLeg" }, new[] { "Hips", "RightUpperLeg" }, new[] { "LeftShoulder", "LeftUpperArm" }, new[] { "RightShoulder", "RightUpperArm" },
        };

        /// <summary>Zoomed per-limb panels (front + side) of head/hips/hand/foot paths relative to the root. In cycle mode, numbered dots mark 8 phases.</summary>
        static string LimbTrails(ProbeData d, ProbeAnalysis a, float t0, float t1, bool cycle, string outPath, string title)
        {
            int f0 = d.FrameAt(t0), f1 = Mathf.Max(f0 + 1, d.FrameAt(t1));
            int fRef = d.NearestImageFrame(t0);
            var limbs = new[] { "Head", "Hips", "LeftHand", "RightHand", "LeftFoot", "RightFoot" }.Where(n => Array.IndexOf(a.effName, n) >= 0).ToList();
            if (limbs.Count == 0) throw new Exception("no humanoid limbs recorded");
            var mRef = d.RootM(fRef);
            var rel = new Matrix4x4[f1 - f0 + 1];
            for (int f = f0; f <= f1; f++) rel[f - f0] = mRef * d.RootM(f).inverse;

            const int pw = 250, ph = 250, gap = 6, unitGap = 14, titleH = 20, headerH = 30, footerH = 46;
            int perRow = 2, rows = (limbs.Count + 1) / 2;
            int W = gap + perRow * (2 * (pw + gap)) + unitGap, H = headerH + rows * (titleH + ph + gap) + footerH;
            var cv = new ProbeCanvas(W, H, BgDark);
            cv.Text(8, 8, (title + "  T=" + ProbeIO.F(f0 * d.meta.dt) + "-" + ProbeIO.F(f1 * d.meta.dt)).ToUpperInvariant(), TextCol, 2);
            for (int li = 0; li < limbs.Count; li++)
            {
                int e = Array.IndexOf(a.effName, limbs[li]);
                int x0 = gap + (li % perRow) * (2 * (pw + gap) + unitGap), y0 = headerH + (li / perRow) * (titleH + ph + gap);
                Vector3 mn = Vector3.one * 1e9f, mx = -Vector3.one * 1e9f;
                for (int f = f0; f <= f1; f++) { var r = a.effRel[e][f]; mn = Vector3.Min(mn, r); mx = Vector3.Max(mx, r); }
                Vector3 rg = mx - mn;
                cv.Text(x0, y0 + 3, (limbs[li] + "  RANGE X " + ProbeIO.Cm(rg.x) + " Y " + ProbeIO.Cm(rg.y) + " Z " + ProbeIO.Cm(rg.z) + " CM").ToUpperInvariant(), Accent, 2);
                var path = new List<Vector3>();
                for (int f = f0; f <= f1; f++) path.Add(rel[f - f0].MultiplyPoint3x4(d.Pt(f, a.effPt[e])));
                string[] names = { "front", "side" };
                for (int k = 0; k < 2; k++)
                {
                    var v = d.View("body", names[k]);
                    if (v == null) continue;
                    var spec = new ZoomSpec { v = v, fRef = fRef, label = ("BODY " + v.name).ToUpperInvariant() };
                    foreach (var b in SkeletonBones)
                    {
                        if (b[0] != limbs[li] && b[1] != limbs[li]) continue;
                        var ha = d.meta.humans.FirstOrDefault(h => h.name == b[0]);
                        var hb = d.meta.humans.FirstOrDefault(h => h.name == b[1]);
                        if (ha == null || hb == null) continue;
                        spec.context.Add((ProjectAll(d, v, fRef, new[] { d.Pt(fRef, ha.point), d.Pt(fRef, hb.point) }), White, 1.3f, 0.6f));
                    }
                    spec.paths.Add((ProjectAll(d, v, fRef, path), 0f, 1f));
                    if (cycle)
                        for (int i = 0; i < 8; i++)
                        {
                            int f = Mathf.Clamp(f0 + Mathf.RoundToInt((f1 - f0) * i / 8f), f0, f1);
                            spec.marks.Add((Project(d, v, fRef, path[f - f0], 1f, 1f), i.ToString()));
                        }
                    DrawZoom(cv, d, spec, x0 + k * (pw + gap), y0 + titleH, pw, ph);
                }
            }
            int fy = H - footerH + 8;
            int bx = 10, bwid = 300;
            for (int i = 0; i < bwid; i++) cv.Rect(bx + i, fy, 1, 12, TimeColor(i / (float)(bwid - 1)));
            cv.Text(bx, fy + 16, "T=" + ProbeIO.F(f0 * d.meta.dt), TextCol, 1);
            string e1 = "T=" + ProbeIO.F(f1 * d.meta.dt);
            cv.Text(bx + bwid - ProbeCanvas.TextW(e1, 1), fy + 16, e1, TextCol, 1);
            cv.Text(bx + bwid + 14, fy, "PATH COLOUR = TIME. POSITIONS RELATIVE TO THE AVATAR ROOT (TRAVEL AND TURNS REMOVED).", TextCol, 1);
            if (cycle) cv.Text(bx + bwid + 14, fy + 14, "NUMBERED DOTS = PHASES 0-7, SAME AS THE COLUMNS OF SHEET_CYCLE.PNG.", TextCol, 1);
            cv.SavePng(outPath);
            return outPath;
        }

        /// <summary>Whole-body front/side view with the paths of head, hips, hands and feet relative to the root.</summary>
        static string BodyOverview(ProbeData d, ProbeAnalysis a, float t0, float t1, string outPath)
        {
            var panels = new[] { "front", "side" }.Select(n => d.View("body", n)).Where(v => v != null).ToList();
            if (panels.Count == 0) throw new Exception("no body views recorded");
            int fRef = d.NearestImageFrame(t0);
            int f0 = d.FrameAt(t0), f1 = Mathf.Max(f0 + 1, d.FrameAt(t1));
            const int headerH = 28, footerH = 40, gap = 6;
            var sizes = panels.Select(v => { float s = Mathf.Min(1.6f, 560f / v.h); return new Vector2Int(Mathf.RoundToInt(v.w * s), Mathf.RoundToInt(v.h * s)); }).ToList();
            int cellW = sizes.Max(s => s.x), cellH = sizes.Max(s => s.y);
            int W = gap + panels.Count * (cellW + gap), H = headerH + cellH + gap + 18 + footerH;
            var cv = new ProbeCanvas(W, H, BgDark);
            cv.Text(8, 7, ("BODY PATHS RELATIVE TO ROOT  T=" + ProbeIO.F(f0 * d.meta.dt) + "-" + ProbeIO.F(f1 * d.meta.dt)).ToUpperInvariant(), TextCol, 2);
            var mRef = d.RootM(fRef);
            var show = new[] { "Head", "Hips", "LeftHand", "RightHand", "LeftFoot", "RightFoot" };
            int dotEvery = Mathf.Max(1, Mathf.RoundToInt(0.1f / d.meta.dt));
            for (int pi = 0; pi < panels.Count; pi++)
            {
                var v = panels[pi];
                int px0 = gap + pi * (cellW + gap), py0 = headerH + 18;
                int pw = sizes[pi].x, ph = sizes[pi].y;
                float sx = pw / (float)v.w, sy = ph / (float)v.h;
                cv.Text(px0, py0 - 16, ("BODY " + v.name).ToUpperInvariant(), TextCol, 2);
                if (LoadFrame(d, v, fRef, out var img, out int iw, out int ih)) cv.Image(img, iw, ih, px0, py0, pw, ph, 0.55f, 0.35f);
                cv.SetClip(px0, py0, pw, ph);
                foreach (var b in SkeletonBones)
                {
                    var ha = d.meta.humans.FirstOrDefault(h => h.name == b[0]);
                    var hb = d.meta.humans.FirstOrDefault(h => h.name == b[1]);
                    if (ha == null || hb == null) continue;
                    var p0 = Project(d, v, fRef, d.Pt(fRef, ha.point), sx, sy);
                    var p1 = Project(d, v, fRef, d.Pt(fRef, hb.point), sx, sy);
                    cv.Line(px0 + p0.x, py0 + p0.y, px0 + p1.x, py0 + p1.y, White, 1.2f, 0.55f);
                }
                for (int si = 0; si < show.Length; si++)
                {
                    int e = Array.IndexOf(a.effName, show[si]);
                    if (e < 0) continue;
                    Color baseCol = Palette[si % Palette.Length];
                    Vector2? q = null;
                    for (int f = f0; f <= f1; f++)
                    {
                        Vector3 w = mRef * d.RootM(f).inverse.MultiplyPoint3x4(d.Pt(f, a.effPt[e]));
                        var p = Project(d, v, fRef, w, sx, sy);
                        float u = (f - f0) / (float)(f1 - f0);
                        var col = (Color32)Color.Lerp(baseCol * 0.45f, baseCol, u);
                        if (q.HasValue) cv.Line(px0 + q.Value.x, py0 + q.Value.y, px0 + p.x, py0 + p.y, col, 1.6f);
                        if ((f - f0) % dotEvery == 0) cv.Disc(px0 + p.x, py0 + p.y, 1.8f, col);
                        if (f == f0) cv.Ring(px0 + p.x, py0 + p.y, 5f, (Color32)baseCol, 1.5f);
                        if (f == f1) cv.Rect(Mathf.RoundToInt(px0 + p.x) - 4, Mathf.RoundToInt(py0 + p.y) - 4, 8, 8, (Color32)baseCol);
                        q = p;
                    }
                }
                cv.ResetClip();
                cv.Outline(px0 - 1, py0 - 1, pw + 2, ph + 2, new Color32(70, 70, 80, 255));
            }
            int fy = H - footerH + 8;
            var legend = new[] { "HEAD", "HIPS", "L HAND", "R HAND", "L FOOT", "R FOOT" };
            int x = 10;
            for (int i = 0; i < legend.Length; i++)
            {
                cv.Rect(x, fy, 12, 12, Palette[i]);
                cv.Text(x + 16, fy + 2, legend[i], TextCol, 1);
                x += 24 + ProbeCanvas.TextW(legend[i], 1);
            }
            cv.Text(10, fy + 18, "DOTS EVERY 0.1 S, RING = START, SQUARE = END, DARK -> BRIGHT = TIME", TextCol, 1);
            cv.SavePng(outPath);
            return outPath;
        }

        /// <summary>All PhysBone tips in one picture (dense; use GroupTrails for reading).</summary>
        static string PhysOverview(ProbeData d, float t0, float t1, string filter, string outPath)
        {
            var panels = new[] { ("body", "front"), ("body", "side"), ("head", "front"), ("head", "side") }.Select(p => d.View(p.Item1, p.Item2)).Where(v => v != null).ToList();
            if (panels.Count == 0) throw new Exception("no views recorded");
            int fRef = d.NearestImageFrame(t0);
            int f0 = d.FrameAt(t0), f1 = Mathf.Max(f0 + 1, d.FrameAt(t1));
            var chains = Enumerable.Range(0, d.meta.chains.Count).Where(c => Match(d.meta.chains[c].name, filter) || Match(d.meta.comps[d.meta.chains[c].comp].group, filter)).ToList();
            if (chains.Count == 0) throw new Exception("no PhysBone chain matches '" + filter + "'");
            const int headerH = 28, footerH = 30, gap = 6;
            int colsN = Mathf.Min(2, panels.Count);
            var sizes = panels.Select(v => { float s = Mathf.Min(1.6f, 560f / v.h); return new Vector2Int(Mathf.RoundToInt(v.w * s), Mathf.RoundToInt(v.h * s)); }).ToList();
            int cellW = sizes.Max(s => s.x), cellH = sizes.Max(s => s.y), rowsN = (panels.Count + colsN - 1) / colsN;
            int W = gap + colsN * (cellW + gap), H = headerH + rowsN * (cellH + gap + 18) + footerH;
            var cv = new ProbeCanvas(W, H, BgDark);
            cv.Text(8, 7, ("ALL PHYSBONE TIP PATHS RELATIVE TO PARENT BONE  T=" + ProbeIO.F(f0 * d.meta.dt) + "-" + ProbeIO.F(f1 * d.meta.dt)).ToUpperInvariant(), TextCol, 2);
            var frames = chains.Select(c => d.meta.chains[c].frame).Distinct().ToList();
            var mRefs = frames.ToDictionary(r => r, r => d.FrameM(fRef, r));
            for (int pi = 0; pi < panels.Count; pi++)
            {
                var v = panels[pi];
                int px0 = gap + (pi % colsN) * (cellW + gap), py0 = headerH + (pi / colsN) * (cellH + gap + 18) + 18;
                int pw = sizes[pi].x, ph = sizes[pi].y;
                float sx = pw / (float)v.w, sy = ph / (float)v.h;
                cv.Text(px0, py0 - 16, (v.group + " " + v.name).ToUpperInvariant(), TextCol, 2);
                if (LoadFrame(d, v, fRef, out var img, out int iw, out int ih)) cv.Image(img, iw, ih, px0, py0, pw, ph, 0.55f, 0.35f);
                cv.SetClip(px0, py0, pw, ph);
                var prev = new Dictionary<int, Vector2>();
                for (int f = f0; f <= f1; f++)
                {
                    var rel = frames.ToDictionary(r => r, r => mRefs[r] * d.FrameM(f, r).inverse);
                    var col = TimeColor((f - f0) / (float)(f1 - f0));
                    foreach (int c in chains)
                    {
                        var ch = d.meta.chains[c];
                        var p = Project(d, v, fRef, rel[ch.frame].MultiplyPoint3x4(d.Pt(f, ch.pts[ch.pts.Length - 1])), sx, sy);
                        if (prev.TryGetValue(c, out Vector2 q)) cv.Line(px0 + q.x, py0 + q.y, px0 + p.x, py0 + p.y, col, 1.4f);
                        prev[c] = p;
                    }
                }
                cv.ResetClip();
                cv.Outline(px0 - 1, py0 - 1, pw + 2, ph + 2, new Color32(70, 70, 80, 255));
            }
            cv.SavePng(outPath);
            return outPath;
        }

        static float NiceStep(float range, int target)
        {
            if (range <= 0f) return 1f;
            float raw = range / Mathf.Max(1, target);
            float mag = Mathf.Pow(10f, Mathf.Floor(Mathf.Log10(raw)));
            float n = raw / mag;
            return (n < 1.5f ? 1f : n < 3.5f ? 2f : n < 7.5f ? 5f : 10f) * mag;
        }

        public static string Chart(ProbeData d, ProbeAnalysis a, float t0, float t1, string filter, string outPath)
        {
            const int W = 1400, H = 800, left = 80, right = 300, top = 44;
            int plotW = W - left - right;
            var cv = new ProbeCanvas(W, H, BgDark);
            int f0 = d.FrameAt(t0), f1 = Mathf.Max(f0 + 1, d.FrameAt(t1));
            cv.Text(10, 8, ("MOTIONPROBE CHART  " + d.meta.avatar + "  " + Path.GetFileName(d.dir) + (string.IsNullOrEmpty(filter) ? "" : "  FILTER " + filter)).ToUpperInvariant(), TextCol, 2);

            // panel A: PhysBone groups
            var groups = a.groups.Where(g => Match(g.name, filter) || g.chains.Any(c => Match(d.meta.chains[c].name, filter))).ToList();
            var series = new List<(string name, float[] v, float peak)>();
            foreach (var g in groups)
            {
                var chs = g.chains.Where(c => string.IsNullOrEmpty(filter) || Match(g.name, filter) || Match(d.meta.chains[c].name, filter)).ToList();
                var vals = new float[f1 - f0 + 1];
                float pk = 0f;
                for (int f = f0; f <= f1; f++)
                {
                    float m = 0f;
                    foreach (int c in chs) m = Mathf.Max(m, a.mag[c][f]);
                    vals[f - f0] = m * 100f;
                    pk = Mathf.Max(pk, vals[f - f0]);
                }
                series.Add((g.name, vals, pk));
            }
            series = series.OrderByDescending(s => s.peak).Take(8).ToList();
            int aTop = top + 20, aH = 420;
            DrawPanel(cv, d, series, f0, f1, left, aTop, plotW, aH, "PHYSBONE TIP DISPLACEMENT FROM REST (CM), MAX PER GROUP", "CM");

            // panel B: body speeds
            var bs = new List<(string name, float[] v, float peak)>();
            var rs = new float[f1 - f0 + 1];
            for (int f = f0; f <= f1; f++) rs[f - f0] = a.rootSpeed[f];
            bs.Add(("ROOT", rs, rs.Max()));
            foreach (var n in new[] { "Head", "LeftHand", "RightHand", "LeftFoot", "RightFoot" })
            {
                int e = Array.IndexOf(a.effName, n);
                if (e < 0) continue;
                var vals = new float[f1 - f0 + 1];
                for (int f = Mathf.Max(1, f0); f <= f1; f++)
                {
                    int fa = Mathf.Max(0, f - 1), fb = Mathf.Min(a.F - 1, f + 1);
                    vals[f - f0] = (a.effRel[e][fb] - a.effRel[e][fa]).magnitude / ((fb - fa) * d.meta.dt);
                }
                bs.Add((n.ToUpperInvariant(), vals, vals.Max()));
            }
            int bTop = aTop + aH + 70, bH = H - bTop - 50;
            DrawPanel(cv, d, bs, f0, f1, left, bTop, plotW, bH, "BODY SPEED (M/S): ROOT AND LIMBS RELATIVE TO ROOT", "M/S", true);

            // events across both panels
            int row = 0;
            foreach (var e in d.meta.events)
            {
                int f = Mathf.RoundToInt(e.t / d.meta.dt);
                if (f < f0 || f > f1) continue;
                float x = left + (f - f0) / (float)(f1 - f0) * plotW;
                cv.Dashed(x, aTop, x, aTop + aH, Accent, 1f, 5f, 0.7f);
                cv.Dashed(x, bTop, x, bTop + bH, Accent, 1f, 5f, 0.7f);
                cv.Text(Mathf.RoundToInt(x) + 3, aTop + 4 + (row % 3) * 10, Fit(e.label, 160, 1), Accent, 1);
                row++;
            }
            cv.SavePng(outPath);
            return outPath;
        }

        static void DrawPanel(ProbeCanvas cv, ProbeData d, List<(string name, float[] v, float peak)> series, int f0, int f1, int x0, int y0, int w, int h, string title, string unit, bool firstWhite = false)
        {
            cv.Text(x0, y0 - 18, title, TextCol, 2);
            cv.Rect(x0, y0, w, h, new Color32(30, 32, 38, 255));
            float ymax = Mathf.Max(0.01f, series.Count > 0 ? series.Max(s => s.peak) : 1f) * 1.1f;
            float ystep = NiceStep(ymax, 5);
            ymax = Mathf.Ceil(ymax / ystep) * ystep;
            for (float yv = 0f; yv <= ymax + 1e-4f; yv += ystep)
            {
                float y = y0 + h - yv / ymax * h;
                cv.Line(x0, y, x0 + w, y, new Color32(60, 63, 72, 255), 1f);
                string lab = yv.ToString(ystep < 0.1f ? "0.00" : ystep < 1f ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);
                cv.Text(x0 - 8 - ProbeCanvas.TextW(lab, 1), Mathf.RoundToInt(y) - 3, lab, Dim, 1, false);
            }
            float t0 = f0 * d.meta.dt, t1 = f1 * d.meta.dt;
            float tstep = NiceStep(t1 - t0, 10);
            for (float tv = Mathf.Ceil(t0 / tstep) * tstep; tv <= t1 + 1e-4f; tv += tstep)
            {
                float x = x0 + (tv - t0) / (t1 - t0) * w;
                cv.Line(x, y0, x, y0 + h, new Color32(50, 53, 60, 255), 1f);
                string lab = tv.ToString(tstep < 1f ? "0.0" : "0", System.Globalization.CultureInfo.InvariantCulture);
                cv.Text(Mathf.RoundToInt(x) - ProbeCanvas.TextW(lab, 1) / 2, y0 + h + 6, lab, Dim, 1, false);
            }
            cv.Text(x0 + w - 60, y0 + h + 18, "TIME (S)", Dim, 1, false);
            for (int si = 0; si < series.Count; si++)
            {
                var s = series[si];
                Color32 col = firstWhite ? (si == 0 ? new Color32(240, 240, 240, 255) : Palette[(si - 1) % Palette.Length]) : Palette[si % Palette.Length];
                float px = 0f, py = 0f;
                for (int i = 0; i < s.v.Length; i++)
                {
                    float x = x0 + i / (float)Mathf.Max(1, s.v.Length - 1) * w;
                    float y = y0 + h - Mathf.Clamp(s.v[i] / ymax, 0f, 1f) * h;
                    if (i > 0) cv.Line(px, py, x, y, col, 1.8f);
                    px = x; py = y;
                }
                int ly = y0 + 6 + si * 18;
                cv.Rect(x0 + w + 14, ly, 12, 12, col);
                string lab = Fit(s.name, 200, 1) + "  " + s.peak.ToString(unit == "CM" ? "0.0" : "0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + unit;
                cv.Text(x0 + w + 32, ly + 2, lab, TextCol, 1);
            }
            if (series.Count == 0) cv.Text(x0 + 10, y0 + 10, "NO DATA", Dim, 2);
        }
    }
}
