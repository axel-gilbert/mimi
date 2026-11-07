using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Mimi
{
    /// <summary>
    /// All visuals are drawn in code at startup (no images to embed):
    /// glossy gradient hearts, outline hearts, sparkles, the big "mega" heart, icons.
    /// Each sprite exists in several sizes (mipmaps) so it stays sharp when scaled down.
    /// </summary>
    static class Art
    {
        /// <summary>Heart width / bitmap width (the rest is margin for the glow).</summary>
        public const float ContentRatio = 0.70f;

        /// <summary>Sprite as raw premultiplied ARGB pixels, for the custom blitter (much faster than GDI+).</summary>
        public sealed class Sprite
        {
            public int W, H;
            public int[] Px;
        }

        public sealed class SpriteSet
        {
            public Sprite[] Levels;
            /// <summary>Smallest level that is large enough: we never scale down by more than ~20%, so simple sampling stays sharp.</summary>
            public Sprite Pick(float px)
            {
                foreach (var b in Levels)
                    if (b.W * ContentRatio >= px * 0.98f) return b;
                return Levels[Levels.Length - 1];
            }
        }

        // ───────────── Themes: colour presets × styles ─────────────

        /// <summary>Preset ids, in the order shown in the settings ("custom" is handled separately).</summary>
        public static readonly string[] Presets = { "classic", "pastel", "passion", "galaxy", "sunset", "mint", "gold", "rainbow" };
        public static readonly string[] Styles = { "glossy", "flat", "outline" };

        static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

        /// <summary>(gradient top, gradient bottom) pairs of each preset.</summary>
        static Color[][] PresetPalettes(string preset)
        {
            switch (preset)
            {
                case "pastel": return new[] { new[] { C(255, 214, 228), C(255, 150, 185) }, new[] { C(226, 206, 255), C(178, 146, 240) }, new[] { C(204, 236, 255), C(130, 196, 245) }, new[] { C(255, 232, 204), C(255, 182, 140) }, new[] { C(212, 246, 226), C(140, 216, 180) } };
                case "passion": return new[] { new[] { C(255, 96, 96), C(190, 10, 40) }, new[] { C(255, 70, 120), C(200, 0, 70) }, new[] { C(235, 50, 70), C(140, 0, 30) }, new[] { C(255, 128, 150), C(220, 30, 80) } };
                case "galaxy": return new[] { new[] { C(176, 146, 255), C(92, 44, 205) }, new[] { C(126, 176, 255), C(42, 84, 214) }, new[] { C(232, 136, 255), C(150, 40, 200) }, new[] { C(146, 224, 255), C(60, 124, 232) } };
                case "sunset": return new[] { new[] { C(255, 204, 120), C(255, 112, 80) }, new[] { C(255, 156, 122), C(240, 70, 92) }, new[] { C(255, 224, 140), C(255, 150, 60) }, new[] { C(255, 136, 172), C(230, 60, 120) } };
                case "mint": return new[] { new[] { C(172, 250, 220), C(40, 190, 150) }, new[] { C(156, 232, 255), C(30, 160, 200) }, new[] { C(212, 255, 192), C(92, 200, 112) } };
                case "gold": return new[] { new[] { C(255, 238, 164), C(222, 162, 40) }, new[] { C(255, 222, 192), C(212, 142, 92) }, new[] { C(255, 250, 222), C(232, 192, 92) } };
                case "rainbow": return new[] { new[] { C(255, 120, 120), C(220, 30, 50) }, new[] { C(255, 190, 110), C(245, 120, 20) }, new[] { C(255, 236, 120), C(230, 180, 0) }, new[] { C(150, 236, 140), C(40, 170, 70) }, new[] { C(130, 196, 255), C(30, 110, 220) }, new[] { C(206, 150, 255), C(130, 50, 210) } };
                default: return new[] { new[] { C(255, 122, 162), C(233, 30, 99) }, new[] { C(255, 110, 110), C(205, 22, 62) }, new[] { C(255, 196, 214), C(255, 108, 152) }, new[] { C(255, 130, 222), C(186, 44, 204) }, new[] { C(255, 176, 146), C(255, 78, 104) }, new[] { C(224, 176, 255), C(150, 82, 232) } };
            }
        }

        /// <summary>Four harmonious palettes generated from one colour picked by the user.</summary>
        static Color[][] CustomPalettes(Color c)
        {
            var list = new System.Collections.Generic.List<Color[]>();
            foreach (var shift in new[] { 0f, -14f, 14f, 0f })
            {
                RgbToHsl(c, out float h, out float s, out float l);
                h = (h + shift + 360) % 360;
                bool soft = list.Count == 3;
                var bottom = HslToRgb(h, Math.Min(1, s * (soft ? 0.7f : 1)), Clamp(l * (soft ? 1.15f : 0.85f), 0.2f, 0.7f));
                var top = HslToRgb(h, Math.Min(1, s * 0.9f), Clamp(l + (1 - l) * 0.45f, 0.5f, 0.92f));
                list.Add(new[] { top, bottom });
            }
            return list.ToArray();
        }

        public sealed class Theme
        {
            public string Id;
            public SpriteSet[] Hearts;
            public int OutlineStart;        // index of the first outline set (== Hearts.Length if none)
            public Color Main;              // representative colour (rings, previews)

            public int RandomHeart(Random r) =>
                OutlineStart < Hearts.Length && OutlineStart > 0 && r.NextDouble() < 0.12
                    ? OutlineStart + r.Next(Hearts.Length - OutlineStart)
                    : r.Next(OutlineStart > 0 ? OutlineStart : Hearts.Length);
        }

        static readonly System.Collections.Generic.Dictionary<string, Theme> themes = new System.Collections.Generic.Dictionary<string, Theme>();
        static int[] heartSizes;

        /// <summary>Normalises a theme id "preset|style" or "custom-RRGGBB|style"; unknown parts fall back to defaults.</summary>
        public static string NormalizeTheme(string id)
        {
            string preset = "classic", style = "glossy";
            if (!string.IsNullOrEmpty(id))
            {
                var f = id.Split('|');
                if (Array.IndexOf(Presets, f[0]) >= 0) preset = f[0];
                else if (f[0].StartsWith("custom-") && f[0].Length == 13 && IsHex(f[0].Substring(7))) preset = f[0].ToUpperInvariant().Replace("CUSTOM-", "custom-");
                if (f.Length > 1 && Array.IndexOf(Styles, f[1]) >= 0) style = f[1];
            }
            return preset + "|" + style;
        }

        static bool IsHex(string s) { foreach (var ch in s) if (!Uri.IsHexDigit(ch)) return false; return true; }

        public static Color CustomColor(string presetPart) =>
            Color.FromArgb(Convert.ToInt32(presetPart.Substring(7, 2), 16), Convert.ToInt32(presetPart.Substring(9, 2), 16), Convert.ToInt32(presetPart.Substring(11, 2), 16));

        /// <summary>Gets (and builds on first use) the sprites of a theme. Thread-safe.</summary>
        public static Theme GetTheme(string id)
        {
            id = NormalizeTheme(id);
            lock (themes)
            {
                if (themes.TryGetValue(id, out var t)) return t;
                var f = id.Split('|');
                string preset = f[0], style = f[1];
                var pal = preset.StartsWith("custom-") ? CustomPalettes(CustomColor(preset)) : PresetPalettes(preset);
                var sets = new System.Collections.Generic.List<SpriteSet>();
                foreach (var p in pal)
                {
                    var top = p[0]; var bot = p[1];
                    sets.Add(Make(heartSizes, s => HeartBitmap(s, top, bot, style == "outline", ContentRatio, true, style == "flat")));
                }
                int outlineStart = sets.Count;
                if (preset == "classic" && style == "glossy")
                    foreach (var p in new[] { new[] { C(255, 244, 248), C(255, 120, 170) }, new[] { C(255, 226, 160), C(255, 132, 124) } })
                    {
                        var top = p[0]; var bot = p[1];
                        sets.Add(Make(heartSizes, s => HeartBitmap(s, top, bot, true)));
                    }
                t = new Theme { Id = id, Hearts = sets.ToArray(), OutlineStart = outlineStart, Main = pal[0][1] };
                themes[id] = t;
                return t;
            }
        }

        /// <summary>Default theme (used for icons, toasts and as a fallback).</summary>
        public static Theme Default => GetTheme("classic|glossy");

        public static SpriteSet[] Hearts => Default.Hearts;
        public static int OutlineStart => Default.OutlineStart;
        public static SpriteSet Sparkle;
        public static Color[] RingColors;
        static bool ready;

        public static void Init()
        {
            if (ready) return;
            ready = true;
            // sizes in geometric progression (x1.22): 16 px … ~290 px
            var sz = new System.Collections.Generic.List<int>();
            for (double v = 16; v < 300; v *= 1.22) sz.Add((int)Math.Round(v));
            heartSizes = sz.ToArray();
            GetTheme("classic|glossy");

            Sparkle = Make(new[] { 16, 22, 30, 40, 54, 72, 96, 128 }, SparkleBitmap);
            RingColors = new[]
            {
                Color.FromArgb(255, 120, 170), Color.FromArgb(255, 200, 220), Color.FromArgb(255, 160, 120), Color.FromArgb(210, 150, 255)
            };
        }

        static SpriteSet Make(int[] sizes, Func<int, Bitmap> f)
        {
            var set = new SpriteSet { Levels = new Sprite[sizes.Length] };
            for (int i = 0; i < sizes.Length; i++)
                using (var b = f(sizes[i]))
                    set.Levels[i] = ToSprite(b);
            return set;
        }

        static Sprite ToSprite(Bitmap b)
        {
            var sp = new Sprite { W = b.Width, H = b.Height, Px = new int[b.Width * b.Height] };
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < b.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, sp.Px, y * b.Width, b.Width);
            }
            finally { b.UnlockBits(data); }
            return sp;
        }

        public static int RandomHeart(Random r) => Default.RandomHeart(r);

        /// <summary>A small preview of a theme (three hearts), drawn directly (cheap: no sprite set is built).</summary>
        public static Bitmap ThemePreview(string themeId, int w, int h)
        {
            var f = NormalizeTheme(themeId).Split('|');
            var pal = f[0].StartsWith("custom-") ? CustomPalettes(CustomColor(f[0])) : PresetPalettes(f[0]);
            bool outline = f[1] == "outline", flat = f[1] == "flat";
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                float s = Math.Min(h * 1.05f, w / 2.2f);
                var spots = new[] { (-0.62f, 0.1f, 0.68f, -14f, 1), (0.62f, 0.1f, 0.68f, 14f, 2), (0f, 0f, 1f, 0f, 0) };
                foreach (var sp in spots)
                {
                    var c = pal[sp.Item5 % pal.Length];
                    int size = (int)(s * sp.Item3 / ContentRatio) + 2;
                    using (var heart = HeartBitmap(size, c[0], c[1], outline, ContentRatio, true, flat))
                    {
                        var state = g.Save();
                        g.TranslateTransform(w / 2f + sp.Item1 * s * 0.72f, h / 2f + sp.Item2 * s);
                        g.RotateTransform(sp.Item4);
                        g.DrawImage(heart, -size / 2f, -size / 2f, size, size);
                        g.Restore(state);
                    }
                }
            }
            return bmp;
        }

        static Bitmap SpriteBitmap(Sprite sp)
        {
            var b = new Bitmap(sp.W, sp.H, PixelFormat.Format32bppPArgb);
            var d = b.LockBits(new Rectangle(0, 0, sp.W, sp.H), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < sp.H; y++) System.Runtime.InteropServices.Marshal.Copy(sp.Px, y * sp.W, d.Scan0 + y * d.Stride, sp.W);
            b.UnlockBits(d);
            return b;
        }

        // ───────────── colour helpers ─────────────

        static float Clamp(float v, float a, float b) => v < a ? a : v > b ? b : v;

        static void RgbToHsl(Color c, out float h, out float s, out float l)
        {
            float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
            float max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            l = (max + min) / 2;
            if (max == min) { h = 0; s = 0; return; }
            float d = max - min;
            s = l > 0.5f ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }

        static Color HslToRgb(float h, float s, float l)
        {
            float c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
            float r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        /// <summary>Classic parametric heart curve, normalized to the rectangle.</summary>
        public static GraphicsPath HeartPath(RectangleF r)
        {
            const int N = 160;
            var pts = new PointF[N];
            float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
            for (int i = 0; i < N; i++)
            {
                double t = 2 * Math.PI * i / N;
                double s = Math.Sin(t);
                float x = (float)(16 * s * s * s);
                float y = (float)-(13 * Math.Cos(t) - 5 * Math.Cos(2 * t) - 2 * Math.Cos(3 * t) - Math.Cos(4 * t));
                pts[i] = new PointF(x, y);
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            for (int i = 0; i < N; i++)
                pts[i] = new PointF(r.X + (pts[i].X - minX) / (maxX - minX) * r.Width,
                                    r.Y + (pts[i].Y - minY) / (maxY - minY) * r.Height);
            var path = new GraphicsPath();
            path.AddPolygon(pts);
            return path;
        }

        static GraphicsPath Scaled(GraphicsPath src, PointF c, float k)
        {
            var p = (GraphicsPath)src.Clone();
            using (var m = new Matrix())
            {
                m.Translate(c.X, c.Y);
                m.Scale(k, k);
                m.Translate(-c.X, -c.Y);
                p.Transform(m);
            }
            return p;
        }

        static Color Darken(Color c, float k) =>
            Color.FromArgb(c.A, (int)(c.R * (1 - k)), (int)(c.G * (1 - k)), (int)(c.B * (1 - k)));

        public static Bitmap HeartBitmap(int S, Color top, Color bot, bool outline, float ratio = ContentRatio, bool glow = true, bool flat = false)
        {
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;

                float w = S * ratio, h = w * 0.9f;
                var r = new RectangleF((S - w) / 2f, (S - h) / 2f, w, h);
                var c = new PointF(S / 2f, S / 2f + h * 0.04f);
                using (var path = HeartPath(r))
                {
                    // Soft glow (halo) around the heart
                    if (glow)
                    {
                        for (int i = 12; i >= 1; i--)
                            using (var gp = Scaled(path, c, 1 + i * 0.024f))
                            using (var b = new SolidBrush(Color.FromArgb(outline ? 7 : 10, bot)))
                                g.FillPath(b, gp);
                    }

                    if (!outline)
                    {
                        // Gradient body
                        using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bot, 70f))
                        {
                            lg.SetSigmaBellShape(0.85f, 1f);
                            g.FillPath(lg, path);
                        }

                        if (!flat) // "flat" style: just the gradient, no shading or highlights
                        {
                            var state = g.Save();
                            g.SetClip(path);

                            // Inner shadow at the bottom right for volume
                            using (var sp = new GraphicsPath())
                            {
                                var sr = new RectangleF(r.X + r.Width * 0.25f, r.Y + r.Height * 0.35f, r.Width * 1.1f, r.Height * 1.1f);
                                sp.AddEllipse(sr);
                                using (var pb = new PathGradientBrush(sp)
                                {
                                    CenterColor = Color.FromArgb(70, Darken(bot, 0.35f)),
                                    SurroundColors = new[] { Color.FromArgb(0, bot) },
                                    CenterPoint = new PointF(sr.Right - sr.Width * 0.2f, sr.Bottom - sr.Height * 0.2f)
                                })
                                    g.FillPath(pb, sp);
                            }

                            // Main highlight on the left lobe (tilted ellipse)
                            var hr = new RectangleF(-r.Width * 0.16f, -r.Height * 0.11f, r.Width * 0.32f, r.Height * 0.22f);
                            g.TranslateTransform(r.X + r.Width * 0.29f, r.Y + r.Height * 0.22f);
                            g.RotateTransform(-32);
                            using (var hp = new GraphicsPath())
                            {
                                hp.AddEllipse(hr);
                                using (var pb = new PathGradientBrush(hp)
                                {
                                    CenterColor = Color.FromArgb(185, 255, 255, 255),
                                    SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) },
                                })
                                    g.FillPath(pb, hp);
                            }
                            g.ResetTransform();

                            // Small specular dot + secondary highlight
                            using (var sb = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                                g.FillEllipse(sb, r.X + r.Width * 0.19f, r.Y + r.Height * 0.15f, r.Width * 0.08f, r.Width * 0.065f);
                            using (var sb2 = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                                g.FillEllipse(sb2, r.X + r.Width * 0.70f, r.Y + r.Height * 0.15f, r.Width * 0.055f, r.Width * 0.045f);
                            g.Restore(state);
                        }

                        using (var pen = new Pen(Color.FromArgb(80, Darken(bot, 0.4f)), Math.Max(1f, S * 0.008f)))
                            g.DrawPath(pen, path);
                    }
                    else
                    {
                        using (var fill = new SolidBrush(Color.FromArgb(50, top)))
                            g.FillPath(fill, path);
                        using (var inner = Scaled(path, c, 0.92f))
                        using (var lg = new LinearGradientBrush(r, top, bot, 70f))
                        using (var pen = new Pen(lg, Math.Max(1.5f, w * 0.08f)) { LineJoin = LineJoin.Round })
                            g.DrawPath(pen, inner);
                        using (var sb = new SolidBrush(Color.FromArgb(230, 255, 255, 255)))
                            g.FillEllipse(sb, r.X + r.Width * 0.2f, r.Y + r.Height * 0.16f, r.Width * 0.07f, r.Width * 0.06f);
                    }
                }
            }
            return bmp;
        }

        static Bitmap SparkleBitmap(int S)
        {
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float c = S / 2f, R = S * ContentRatio / 2f, ri = R * 0.2f;
                // halo
                using (var gp = new GraphicsPath())
                {
                    gp.AddEllipse(c - R * 0.7f, c - R * 0.7f, R * 1.4f, R * 1.4f);
                    using (var pb = new PathGradientBrush(gp)
                    {
                        CenterColor = Color.FromArgb(120, 255, 240, 250),
                        SurroundColors = new[] { Color.FromArgb(0, 255, 200, 230) }
                    })
                        g.FillPath(pb, gp);
                }
                // 4-pointed star with curved edges
                var pts = new PointF[8];
                for (int k = 0; k < 8; k++)
                {
                    double a = (k * 45 - 90) * Math.PI / 180;
                    float rad = k % 2 == 0 ? R : ri;
                    pts[k] = new PointF(c + (float)Math.Cos(a) * rad, c + (float)Math.Sin(a) * rad);
                }
                using (var sp = new GraphicsPath())
                {
                    sp.AddClosedCurve(pts, 0.25f);
                    using (var pb = new PathGradientBrush(sp)
                    {
                        CenterPoint = new PointF(c, c),
                        CenterColor = Color.White,
                        SurroundColors = new[] { Color.FromArgb(255, 255, 214, 140) }
                    })
                        g.FillPath(pb, sp);
                }
            }
            return bmp;
        }

        public static Icon MakeIcon(bool online)
        {
            var top = online ? Color.FromArgb(255, 122, 162) : Color.FromArgb(205, 200, 210);
            var bot = online ? Color.FromArgb(233, 30, 99) : Color.FromArgb(140, 132, 150);
            using (var b = HeartBitmap(32, top, bot, false, 0.94f, false))
                return Icon.FromHandle(b.GetHicon());
        }

        /// <summary>Writes a multi-size .ico (embedded PNGs) – used to generate assets\mimi.ico.</summary>
        public static void ExportIco(string path)
        {
            int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
            var pngs = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var b = HeartBitmap(sizes[i], Color.FromArgb(255, 122, 162), Color.FromArgb(233, 30, 99), false, 0.92f, sizes[i] >= 48))
                using (var ms = new MemoryStream())
                {
                    b.Save(ms, ImageFormat.Png);
                    pngs[i] = ms.ToArray();
                }
            using (var fs = File.Create(path))
            using (var w = new BinaryWriter(fs))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(pngs[i].Length); w.Write(offset);
                    offset += pngs[i].Length;
                }
                foreach (var p in pngs) w.Write(p);
            }
        }
    }
}
