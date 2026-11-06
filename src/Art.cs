using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Mimi
{
    /// <summary>
    /// Tous les visuels sont dessinés en code au démarrage (pas d'images à embarquer) :
    /// cœurs brillants en dégradé, cœurs contour, étincelles, gros cœur "méga", icônes.
    /// Chaque sprite existe en plusieurs tailles (mipmaps) pour rester net quand on le réduit.
    /// </summary>
    static class Art
    {
        /// <summary>Largeur du cœur / largeur du bitmap (le reste = marge pour la lueur).</summary>
        public const float ContentRatio = 0.70f;

        /// <summary>Sprite en pixels bruts ARGB prémultipliés, pour le blitter maison (bien plus rapide que GDI+).</summary>
        public sealed class Sprite
        {
            public int W, H;
            public int[] Px;
        }

        public sealed class SpriteSet
        {
            public Sprite[] Levels;
            /// <summary>Plus petit niveau assez grand : on ne réduit jamais de plus de ~20 %, donc l'échantillonnage simple reste net.</summary>
            public Sprite Pick(float px)
            {
                foreach (var b in Levels)
                    if (b.W * ContentRatio >= px * 0.98f) return b;
                return Levels[Levels.Length - 1];
            }
        }

        // (haut du dégradé, bas du dégradé)
        static readonly Color[][] Palettes =
        {
            new[] { Color.FromArgb(255, 122, 162), Color.FromArgb(233, 30, 99) },   // rose
            new[] { Color.FromArgb(255, 110, 110), Color.FromArgb(205, 22, 62) },   // rouge
            new[] { Color.FromArgb(255, 196, 214), Color.FromArgb(255, 108, 152) }, // pastel
            new[] { Color.FromArgb(255, 130, 222), Color.FromArgb(186, 44, 204) },  // magenta
            new[] { Color.FromArgb(255, 176, 146), Color.FromArgb(255, 78, 104) },  // corail
            new[] { Color.FromArgb(224, 176, 255), Color.FromArgb(150, 82, 232) },  // lilas
        };
        static readonly Color[][] OutlinePalettes =
        {
            new[] { Color.FromArgb(255, 244, 248), Color.FromArgb(255, 120, 170) },
            new[] { Color.FromArgb(255, 226, 160), Color.FromArgb(255, 132, 124) },
        };

        public static SpriteSet[] Hearts;
        public static int OutlineStart;
        public static SpriteSet Sparkle;
        public static Color[] RingColors;
        static bool ready;

        public static void Init()
        {
            if (ready) return;
            ready = true;
            // tailles en progression géométrique (x1.22) : 16 px … ~290 px
            var sz = new System.Collections.Generic.List<int>();
            for (double v = 16; v < 300; v *= 1.22) sz.Add((int)Math.Round(v));
            int[] sizes = sz.ToArray();
            Hearts = new SpriteSet[Palettes.Length + OutlinePalettes.Length];
            for (int i = 0; i < Palettes.Length; i++)
                Hearts[i] = Make(sizes, s => HeartBitmap(s, Palettes[i][0], Palettes[i][1], false));
            OutlineStart = Palettes.Length;
            for (int i = 0; i < OutlinePalettes.Length; i++)
                Hearts[OutlineStart + i] = Make(sizes, s => HeartBitmap(s, OutlinePalettes[i][0], OutlinePalettes[i][1], true));

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

        public static int RandomHeart(Random r) =>
            r.NextDouble() < 0.12 ? OutlineStart + r.Next(Hearts.Length - OutlineStart) : r.Next(OutlineStart);

        /// <summary>Courbe de cœur paramétrique classique, normalisée dans le rectangle.</summary>
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

        public static Bitmap HeartBitmap(int S, Color top, Color bot, bool outline, float ratio = ContentRatio, bool glow = true)
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
                    // Lueur douce (halo) autour du cœur
                    if (glow)
                    {
                        for (int i = 12; i >= 1; i--)
                            using (var gp = Scaled(path, c, 1 + i * 0.024f))
                            using (var b = new SolidBrush(Color.FromArgb(outline ? 7 : 10, bot)))
                                g.FillPath(b, gp);
                    }

                    if (!outline)
                    {
                        // Corps en dégradé
                        using (var lg = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), top, bot, 70f))
                        {
                            lg.SetSigmaBellShape(0.85f, 1f);
                            g.FillPath(lg, path);
                        }

                        var state = g.Save();
                        g.SetClip(path);

                        // Ombre interne en bas à droite pour le volume
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

                        // Reflet principal sur le lobe gauche (ellipse inclinée)
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

                        // Petit point spéculaire + reflet secondaire
                        using (var sb = new SolidBrush(Color.FromArgb(240, 255, 255, 255)))
                            g.FillEllipse(sb, r.X + r.Width * 0.19f, r.Y + r.Height * 0.15f, r.Width * 0.08f, r.Width * 0.065f);
                        using (var sb2 = new SolidBrush(Color.FromArgb(110, 255, 255, 255)))
                            g.FillEllipse(sb2, r.X + r.Width * 0.70f, r.Y + r.Height * 0.15f, r.Width * 0.055f, r.Width * 0.045f);
                        g.Restore(state);

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
                // étoile à 4 branches aux bords incurvés
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

        /// <summary>Écrit un .ico multi-tailles (PNG intégrés) – sert à générer assets\mimi.ico.</summary>
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
