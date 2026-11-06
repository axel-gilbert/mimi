using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Mimi
{
    sealed class Particle
    {
        public int Kind;
        public float T, Life, Alpha = 1f;
        public bool Dead, Popped;
        public float X0, Y0, X, Y, Vx, Vy, Size, Rot, Spin, Phase, Drag, Gravity;
        public float Rise, Drift, SwayAmp, SwayFreq;
        public int Sprite;
        public string Text;
    }

    /// <summary>
    /// Fenêtre transparente sur l'écran principal, traversée par les clics, toujours au-dessus.
    /// Style "likes en live" : un flux de petits cœurs qui naissent dans un coin et montent en ondulant.
    /// Rendu logiciel (Blitter) puis UpdateLayeredWindowIndirect sur la seule zone modifiée.
    /// La fenêtre est cachée quand il n'y a rien à animer : 0 % CPU au repos.
    /// </summary>
    sealed class Overlay : Form
    {
        const int RISER = 0, BIG = 1, MINI = 2, SPARKLE = 3, RING = 4, TOAST = 5;
        const int MaxParticles = 260;
        int budget = MaxParticles;
        double frameCostEma = 2;

        readonly List<Particle> parts = new List<Particle>();
        readonly List<Particle> born = new List<Particle>();
        readonly Random rng = new Random();
        readonly Timer timer = new Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT;
        bool running;

        Rectangle vs;
        IntPtr memDC, hBmp, oldBmp;
        Bitmap canvas;
        Graphics g;
        Blitter blit;
        bool fullPresent;
        readonly Pen ringPen = new Pen(Color.White, 3f);

        /// <summary>0 = bas à droite, 1 = bas à gauche, 2 = bas au centre.</summary>
        public int Corner;

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "Mimi";
            Bounds = Screen.PrimaryScreen.Bounds;
            timer.Tick += (s, e) => Frame();
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                              Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
                return cp;
            }
        }

        protected override bool ShowWithoutActivation => true;

        void OnDisplayChanged(object s, EventArgs e)
        {
            try { BeginInvoke(new Action(FreeSurface)); } catch { }
        }

        // ───────────────────────────── API publique ─────────────────────────────

        public static bool IsMega(int combo) => combo == 10 || combo == 25 || combo == 50 || (combo > 0 && combo % 100 == 0);

        /// <summary>Un "like" reçu : quelques petits cœurs. Le spam densifie le flux (sans envahir l'écran).</summary>
        public void Wave(int combo)
        {
            int n = 2 + Math.Min(3, combo / 5);
            for (int i = 0; i < n; i++) AddRiser(-i * 0.08f - R(0, 0.06f), 1f, 1f);
            if (IsMega(combo)) AddBig();
            Kick();
        }

        /// <summary>Retour visuel discret quand on envoie soi-même un cœur.</summary>
        public void Echo()
        {
            AddRiser(0, 0.8f, 0.7f);
            Kick();
        }

        public void Toast(string text)
        {
            foreach (var p in parts) if (p.Kind == TOAST) p.Dead = true;
            born.Add(new Particle { Kind = TOAST, Text = text, Life = 3.2f });
            Kick();
        }

        // ───────────────────────────── Géométrie (relative à l'écran) ─────────────────────────────

        Rectangle Area => Screen.PrimaryScreen.Bounds;
        /// <summary>Unité = hauteur d'écran : même rendu en 16:9, 21:9, 32:9 ou 4K.</summary>
        float Unit => Area.Height;

        PointF Emitter()
        {
            var a = Area;
            float u = Unit;
            float y = a.Bottom - u * 0.08f;
            switch (Corner)
            {
                case 1: return new PointF(a.Left + u * 0.10f, y);
                case 2: return new PointF(a.Left + a.Width / 2f, y);
                default: return new PointF(a.Right - u * 0.10f, y);
            }
        }

        /// <summary>Sens de la dérive : vers l'intérieur de l'écran.</summary>
        float InwardSign => Corner == 0 ? -1f : Corner == 1 ? 1f : (rng.Next(2) == 0 ? -1f : 1f);

        // ───────────────────────────── Création des particules ─────────────────────────────

        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        bool Full => parts.Count + born.Count >= budget;

        void AddRiser(float delay, float sizeMul, float alpha)
        {
            if (Full) return;
            float u = Unit;
            var e = Emitter();
            var p = new Particle
            {
                Kind = RISER,
                T = delay,
                X0 = e.X + R(-0.012f, 0.012f) * u,
                Y0 = e.Y,
                Size = u * R(0.026f, 0.042f) * sizeMul,
                Life = R(2.1f, 2.9f),
                Rise = u * R(0.32f, 0.46f),
                Drift = u * R(0.0f, 0.07f) * InwardSign,
                SwayAmp = u * R(0.012f, 0.03f),
                SwayFreq = R(3.2f, 5.2f),
                Phase = R(0, 6.28f),
                Alpha = alpha,
                Sprite = Art.RandomHeart(rng)
            };
            p.X = p.X0; p.Y = p.Y0;
            born.Add(p);
            if (rng.NextDouble() < 0.18) AddSparkle(p.X0, p.Y0 - p.Size, p.Size, delay + 0.15f);
        }

        void AddBig()
        {
            float u = Unit;
            var e = Emitter();
            var p = new Particle
            {
                Kind = BIG,
                X0 = e.X, Y0 = e.Y,
                Size = u * 0.075f,
                Life = 2.2f,
                Rise = u * 0.32f,
                Drift = u * 0.03f * InwardSign,
                SwayAmp = u * 0.012f,
                SwayFreq = 3f,
                Phase = R(0, 6.28f),
                Sprite = 0
            };
            p.X = p.X0; p.Y = p.Y0;
            born.Add(p);
        }

        void AddBurst(float x, float y, float size, int count)
        {
            for (int i = 0; i < count && !Full; i++)
            {
                double ang = 2 * Math.PI * i / count + R(-0.3f, 0.3f);
                float speed = R(160, 320) * (Unit / 1080f);
                born.Add(new Particle
                {
                    Kind = MINI, X = x, Y = y,
                    Vx = (float)Math.Cos(ang) * speed,
                    Vy = (float)Math.Sin(ang) * speed - 40,
                    Drag = R(3f, 4.5f), Gravity = R(60, 140),
                    Size = size * R(0.25f, 0.4f),
                    Life = R(0.6f, 0.95f),
                    Rot = R(-25, 25), Spin = R(-200, 200),
                    Sprite = Art.RandomHeart(rng)
                });
            }
            born.Add(new Particle { Kind = RING, X = x, Y = y, Size = size * 1.2f, Life = 0.4f, Sprite = rng.Next(Art.RingColors.Length) });
            for (int i = 0; i < 3; i++) AddSparkle(x + R(-1, 1) * size * 0.6f, y + R(-1, 1) * size * 0.6f, size * 0.7f, 0);
        }

        void AddSparkle(float x, float y, float size, float delay)
        {
            if (Full) return;
            born.Add(new Particle
            {
                Kind = SPARKLE, X = x, Y = y, T = -delay,
                Size = size * R(0.3f, 0.5f),
                Life = R(0.35f, 0.6f),
                Rot = R(0, 90), Vy = -R(20, 50)
            });
        }

        // ───────────────────────────── Easings ─────────────────────────────

        static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
        static float OutCubic(float t) { t = 1 - t; return 1 - t * t * t; }
        static float InCubic(float t) => t * t * t;
        static float OutBack(float t)
        {
            const float c1 = 2.2f, c3 = c1 + 1;
            t -= 1;
            return 1 + c3 * t * t * t + c1 * t * t;
        }
        static float Gauss(float x, float m, float s) { float d = (x - m) / s; return (float)Math.Exp(-d * d); }
        static float Beat(float t)
        {
            float p = t - (float)Math.Floor(t);
            return Gauss(p, 0.08f, 0.05f) + 0.6f * Gauss(p, 0.28f, 0.05f);
        }

        // ───────────────────────────── Boucle ─────────────────────────────

        void Kick()
        {
            if (running) return;
            EnsureSurface();
            running = true;
            Native.timeBeginPeriod(1);
            lastT = clock.Elapsed.TotalSeconds;
            if (!Visible) Show();
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            timer.Start();
        }

        void StopLoop()
        {
            timer.Stop();
            running = false;
            Native.timeEndPeriod(1);
            Hide();
        }

        void Frame()
        {
            double now = clock.Elapsed.TotalSeconds;
            float dt = (float)Math.Min(0.05, now - lastT);
            lastT = now;
            if (canvas == null) EnsureSurface();
            Step(dt);
            Render();
            Present();
            // Auto-régulation : si une image coûte trop cher, moins de particules et cadence réduite.
            double cost = (clock.Elapsed.TotalSeconds - now) * 1000;
            frameCostEma = frameCostEma * 0.9 + cost * 0.1;
            if (frameCostEma > 6) budget = Math.Max(60, budget - 6);
            else if (frameCostEma < 3 && budget < MaxParticles) budget += 2;
            int interval = frameCostEma > 10 ? 33 : 16;
            if (timer.Interval != interval) timer.Interval = interval;
            if (parts.Count == 0 && born.Count == 0) StopLoop();
        }

        void Rise(Particle p)
        {
            float u = Clamp01(p.T / p.Life);
            float k = 1 - (float)Math.Pow(1 - u, 1.35); // montée qui ralentit doucement
            float sway = (float)Math.Sin(p.Phase + p.T * p.SwayFreq) * p.SwayAmp * Clamp01(u * 5);
            p.X = p.X0 + p.Drift * k + sway;
            p.Y = p.Y0 - p.Rise * k;
        }

        void Step(float dt)
        {
            if (born.Count > 0) { parts.AddRange(born); born.Clear(); }
            for (int i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                if (p.Dead) continue;
                p.T += dt;
                if (p.T >= p.Life) { p.Dead = true; continue; }
                if (p.T < 0) continue;
                switch (p.Kind)
                {
                    case RISER: Rise(p); break;
                    case BIG:
                        Rise(p);
                        if (!p.Popped && p.T >= p.Life - 0.25f)
                        {
                            p.Popped = true;
                            AddBurst(p.X, p.Y, p.Size, 10);
                        }
                        break;
                    case MINI:
                    {
                        float k = (float)Math.Exp(-p.Drag * dt);
                        p.Vx *= k; p.Vy *= k;
                        p.Vy += p.Gravity * dt;
                        p.X += p.Vx * dt; p.Y += p.Vy * dt;
                        p.Rot += p.Spin * dt * (1 - p.T / p.Life);
                        break;
                    }
                    case SPARKLE:
                        p.Y += p.Vy * dt;
                        p.Rot += 160 * dt;
                        break;
                }
            }
            parts.RemoveAll(p => p.Dead);
        }

        // ───────────────────────────── Rendu ─────────────────────────────

        void Render()
        {
            blit.BeginFrame();
            foreach (var p in parts)
            {
                if (p.T < 0) continue;
                switch (p.Kind)
                {
                    case RISER: DrawRiser(p); break;
                    case BIG: DrawBig(p); break;
                    case MINI:
                    {
                        float u = p.T / p.Life;
                        float s = OutBack(Clamp01(p.T / 0.18f)) * (1 - 0.4f * u);
                        DrawSprite(Art.Hearts[p.Sprite], p.X, p.Y, p.Size * s, p.Rot, 1, 1, 0, 1 - InCubic(u));
                        break;
                    }
                    case SPARKLE:
                    {
                        float s = (float)Math.Sin(Math.PI * p.T / p.Life);
                        DrawSprite(Art.Sparkle, p.X, p.Y, p.Size * s, p.Rot, 1, 1, 0, 1);
                        break;
                    }
                    case RING:
                    {
                        float u = p.T / p.Life;
                        float r = p.Size * OutCubic(u);
                        if (r < 2) break;
                        ringPen.Color = Color.FromArgb((int)(200 * (1 - u)), Art.RingColors[p.Sprite % Art.RingColors.Length]);
                        ringPen.Width = Math.Max(1f, p.Size * 0.06f * (1 - u));
                        g.ResetTransform();
                        g.DrawEllipse(ringPen, p.X - vs.X - r, p.Y - vs.Y - r, 2 * r, 2 * r);
                        int m = (int)(r + ringPen.Width + 2);
                        blit.Mark(new Rectangle((int)(p.X - vs.X) - m, (int)(p.Y - vs.Y) - m, 2 * m, 2 * m));
                        break;
                    }
                    case TOAST: DrawToast(p); break;
                }
            }
            g.ResetTransform();
            g.Flush();
        }

        void DrawRiser(Particle p)
        {
            float t = p.T, u = Clamp01(t / p.Life);
            // Apparition "pop" avec rebond, puis léger gonflement en montant, rétrécit en disparaissant
            float pop = OutBack(Clamp01(t / 0.32f));
            float s = pop * (1 + 0.18f * u) * (1 - 0.35f * Clamp01((u - 0.7f) / 0.3f));
            float alpha = p.Alpha * (1 - (float)Math.Pow(Clamp01((u - 0.5f) / 0.5f), 1.6));
            // inclinaison qui suit l'ondulation
            float rot = (float)Math.Cos(p.Phase + t * p.SwayFreq) * 14f * Clamp01(u * 5);
            // squash & stretch à la naissance (étiré verticalement puis se pose)
            float st = 0.22f * (1 - Clamp01(t / 0.35f));
            DrawSprite(Art.Hearts[p.Sprite], p.X, p.Y, p.Size * s, rot, 1 - st * 0.6f, 1 + st, 0, alpha);
        }

        void DrawBig(Particle p)
        {
            float t = p.T, u = Clamp01(t / p.Life);
            float s = OutBack(Clamp01(t / 0.4f)) * (1 + 0.12f * Beat(t * 1.4f));
            float pop = Clamp01((t - (p.Life - 0.25f)) / 0.25f);
            s *= 1 + 0.4f * OutCubic(pop);
            float rot = (float)Math.Cos(p.Phase + t * p.SwayFreq) * 8f;
            DrawSprite(Art.Hearts[p.Sprite], p.X, p.Y, p.Size * s, rot, 1, 1, 0, 1 - pop);
        }

        Font toastFont;
        float toastFontFor;

        void DrawToast(Particle p)
        {
            float unit = Unit;
            if (toastFont == null || toastFontFor != unit)
            {
                toastFont?.Dispose();
                toastFont = new Font("Segoe UI Semibold", Math.Max(12f, unit * 0.016f), GraphicsUnit.Pixel);
                toastFontFor = unit;
            }
            float t = p.T;
            float slide = OutBack(Clamp01(t / 0.5f));
            float outT = Clamp01((t - (p.Life - 0.4f)) / 0.4f);
            float alpha = Clamp01(t / 0.2f) * (1 - outT);
            if (alpha <= 0.01f) return;

            g.ResetTransform();
            var ts = g.MeasureString(p.Text, toastFont);
            float h = ts.Height * 1.6f;
            float icon = h * 0.6f;
            float w = ts.Width + icon + h * 0.8f;
            var e = Emitter();
            var a = Area;
            // Au-dessus du flux de cœurs, sans sortir de l'écran
            float cx = Math.Max(a.Left + w / 2 + unit * 0.02f, Math.Min(a.Right - w / 2 - unit * 0.02f, e.X)) - vs.X;
            float y = e.Y - vs.Y - unit * 0.5f - h + (1 - slide) * h * 0.8f - outT * h * 0.3f;
            var rect = new RectangleF(cx - w / 2, y, w, h);
            blit.Mark(Rectangle.Round(RectangleF.Inflate(rect, h * 0.2f, h * 0.3f)));

            using (var path = RoundRect(rect, h / 2))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var bg = new SolidBrush(Color.FromArgb((int)(235 * alpha), 255, 255, 255)))
                    g.FillPath(bg, path);
                using (var pen = new Pen(Color.FromArgb((int)(150 * alpha), 255, 128, 170), Math.Max(1.5f, h * 0.04f)))
                    g.DrawPath(pen, path);
            }
            float beat = 1 + 0.12f * Beat(t * 1.2f);
            DrawSprite(Art.Hearts[0], rect.X + h * 0.4f + icon / 2 + vs.X, rect.Y + h / 2 + vs.Y, icon * beat, 0, 1, 1, 0, alpha);
            g.ResetTransform();
            using (var tb = new SolidBrush(Color.FromArgb((int)(255 * alpha), 214, 51, 108)))
                g.DrawString(p.Text, toastFont, tb, rect.X + h * 0.4f + icon + h * 0.15f, rect.Y + (h - ts.Height) / 2);
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void DrawSprite(Art.SpriteSet set, float x, float y, float px, float rot, float sx, float sy, float stretchAng, float alpha)
        {
            if (alpha <= 0.01f || px < 1.5f) return;
            var sp = set.Pick(px * Math.Max(sx, sy));
            float scale = px / (sp.W * Art.ContentRatio);
            blit.Draw(sp, x - vs.X, y - vs.Y, scale, rot, sx, sy, stretchAng, alpha);
        }

        // ───────────────────────────── Surface & présentation ─────────────────────────────

        void EnsureSurface()
        {
            var r = Screen.PrimaryScreen.Bounds;
            if (canvas != null && r == vs) return;
            FreeSurface();
            vs = r;
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = 40, biWidth = vs.Width, biHeight = -vs.Height, biPlanes = 1, biBitCount = 32
            };
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memDC = Native.CreateCompatibleDC(screen);
            hBmp = Native.CreateDIBSection(screen, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            oldBmp = Native.SelectObject(memDC, hBmp);
            canvas = new Bitmap(vs.Width, vs.Height, vs.Width * 4, PixelFormat.Format32bppPArgb, bits);
            blit = new Blitter(bits, vs.Width, vs.Height);
            fullPresent = true;
            g = Graphics.FromImage(canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Bounds = vs;
        }

        void FreeSurface()
        {
            g?.Dispose(); g = null;
            blit = null;
            canvas?.Dispose(); canvas = null;
            if (memDC != IntPtr.Zero)
            {
                Native.SelectObject(memDC, oldBmp);
                Native.DeleteObject(hBmp);
                Native.DeleteDC(memDC);
                memDC = IntPtr.Zero; hBmp = IntPtr.Zero;
            }
        }

        unsafe void Present()
        {
            Rectangle dirty = fullPresent ? new Rectangle(0, 0, vs.Width, vs.Height) : blit.DirtyBounds();
            if (dirty.IsEmpty) return;
            fullPresent = false;
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            var dst = new Native.POINT(vs.X, vs.Y);
            var size = new Native.SIZE(vs.Width, vs.Height);
            var src = new Native.POINT(0, 0);
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            var rc = new Native.RECT { Left = dirty.Left, Top = dirty.Top, Right = dirty.Right, Bottom = dirty.Bottom };
            var info = new Native.UPDATELAYEREDWINDOWINFO
            {
                cbSize = (uint)sizeof(Native.UPDATELAYEREDWINDOWINFO),
                hdcDst = screen,
                pptDst = (IntPtr)(&dst),
                psize = (IntPtr)(&size),
                hdcSrc = memDC,
                pptSrc = (IntPtr)(&src),
                pblend = (IntPtr)(&blend),
                dwFlags = Native.ULW_ALPHA,
                prcDirty = (IntPtr)(&rc)
            };
            if (!Native.UpdateLayeredWindowIndirect(Handle, ref info))
                Native.UpdateLayeredWindow(Handle, screen, ref dst, ref size, memDC, ref src, 0, ref blend, Native.ULW_ALPHA);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
                timer.Dispose();
                if (running) Native.timeEndPeriod(1);
                running = false;
                FreeSurface();
                ringPen.Dispose();
                toastFont?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
