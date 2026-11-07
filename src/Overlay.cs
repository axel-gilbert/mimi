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
    /// <summary>One animated element on screen (heart, sparkle, ring, toast, message card…).</summary>
    sealed class Particle
    {
        public int Kind;
        public float T, Life, Alpha = 1f;
        public bool Dead, Popped, Released;
        public float X0, Y0, X, Y, Vx, Vy, Size, Rot, Spin, Phase, Drag, Gravity;
        public float Rise, Drift, SwayAmp, SwayFreq;
        public float HoldT, ReleaseT, ReleaseSize;
        public int Sprite;
        public string Text, Owner;
        public Art.Theme Th;            // colour theme of the hearts (null = default)
        public Bitmap Image;            // picture card: pre-scaled picture
        public Giphy.Animation Anim;    // or an animated GIF (frames drawn scaled)
        public float ImgW, ImgH;        // picture size on screen
    }

    /// <summary>
    /// Transparent, click-through, always-on-top window covering the primary screen.
    ///
    /// Animations:
    ///  • a stream of small hearts rising in a corner (like likes on a live stream);
    ///  • a big heart that grows while the partner holds the shortcut, then flies up and bursts;
    ///  • the "sync" animation: two hearts meet in the middle and merge;
    ///  • message cards in the middle of the screen;
    ///  • small toasts above the heart stream.
    ///
    /// Rendering: home-made software renderer (<see cref="Blitter"/>) into a DIB, then
    /// UpdateLayeredWindowIndirect on the changed area only. The window is hidden when nothing
    /// moves, so Mimi uses 0 % CPU at rest.
    ///
    /// The same class can also render off-screen into a fixed-size surface (<see cref="StartRecording"/>),
    /// which is how the demo GIF of the README is produced.
    /// </summary>
    sealed class Overlay : Form
    {
        const int RISER = 0, BIG = 1, MINI = 2, SPARKLE = 3, RING = 4, TOAST = 5, GROW = 6, SYNC = 7, MERGE = 8, NOTE = 9;
        const int MaxParticles = 300;
        const float MaxHold = 8f;            // a held heart auto-releases after 8 s
        int budget = MaxParticles;
        double frameCostEma = 2;

        readonly List<Particle> parts = new List<Particle>();
        readonly List<Particle> born = new List<Particle>();
        readonly Queue<Particle> notes = new Queue<Particle>();
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

        // off-screen recording (demo GIF)
        Rectangle? fixedArea;
        IntPtr recBits;

        /// <summary>0 = bottom right, 1 = bottom left, 2 = bottom center.</summary>
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

        // ───────────────────────────── Public API ─────────────────────────────

        public static bool IsMega(int combo) => combo == 10 || combo == 25 || combo == 50 || (combo > 0 && combo % 100 == 0);

        /// <summary>A heart received: a few small hearts. Spamming makes the stream denser, never fills the screen.</summary>
        public void Wave(int combo, Art.Theme theme = null)
        {
            theme = theme ?? Art.Default;
            int n = 2 + Math.Min(3, combo / 5);
            for (int i = 0; i < n; i++) AddRiser(-i * 0.08f - R(0, 0.06f), 1f, 1f, theme);
            if (IsMega(combo)) AddBig(theme);
            Kick();
        }

        /// <summary>Discreet feedback when I send a heart myself.</summary>
        public void Echo(Art.Theme theme = null)
        {
            AddRiser(0, 0.8f, 0.7f, theme ?? Art.Default);
            Kick();
        }

        public void Toast(string text)
        {
            foreach (var p in parts) if (p.Kind == TOAST) p.Dead = true;
            born.RemoveAll(p => p.Kind == TOAST);
            born.Add(new Particle { Kind = TOAST, Text = text, Life = 3.2f });
            Kick();
        }

        /// <summary>Starts a big heart that grows while the shortcut is held ("local" or "remote").</summary>
        public void HoldStart(string owner, float alpha = 1f, Art.Theme theme = null)
        {
            theme = theme ?? Art.Default;
            if (FindGrow(owner) != null) return;
            var e = Emitter();
            float u = Unit;
            float inward = Corner == 0 ? -1 : Corner == 1 ? 1 : 0;
            born.Add(new Particle
            {
                Kind = GROW, Owner = owner, Alpha = alpha,
                X0 = e.X + inward * u * 0.12f, Y0 = e.Y - u * 0.18f,
                Life = float.MaxValue, Phase = R(0, 6.28f), Th = theme, Sprite = 0
            });
            Kick();
        }

        /// <summary>Releases the growing heart: it flies up and bursts.</summary>
        public void HoldEnd(string owner)
        {
            var p = FindGrow(owner);
            if (p != null) Release(p);
        }

        /// <summary>Both partners pressed at the same time: two hearts meet in the middle and merge.</summary>
        public void SyncBurst(Art.Theme mine = null, Art.Theme theirs = null)
        {
            var a = Area;
            float u = Unit;
            foreach (var side in new[] { -1f, 1f })
                born.Add(new Particle
                {
                    Kind = SYNC, Phase = side, Life = 0.95f, Size = u * 0.09f,
                    X0 = side < 0 ? a.Left - u * 0.1f : a.Right + u * 0.1f, Y0 = a.Top + a.Height * 0.5f,
                    Th = side < 0 ? (mine ?? Art.Default) : (theirs ?? mine ?? Art.Default), Sprite = 0
                });
            Kick();
        }

        /// <summary>Shows a message card in the middle of the screen (queued if one is already shown).</summary>
        public void Note(string from, string text)
        {
            var p = new Particle
            {
                Kind = NOTE, Owner = from, Text = text,
                Life = Math.Min(10f, 3.2f + text.Length * 0.06f)
            };
            if (HasNote()) notes.Enqueue(p);
            else ShowNote(p);
            Kick();
        }

        /// <summary>
        /// Shows a received picture in the middle of the screen for about 3 seconds, with an optional caption.
        /// The picture takes at most 30 % of the screen surface. Takes ownership of <paramref name="picture"/>.
        /// </summary>
        public void Picture(string from, Bitmap picture, string caption)
        {
            var a = Area;
            // 30 % of the surface: fit inside a box of sqrt(0.3) ≈ 55 % of the width and of the height
            float box = (float)Math.Sqrt(PictureScreenFraction);
            float k = Math.Min(a.Width * box / picture.Width, a.Height * box / picture.Height);
            int w = Math.Max(1, (int)(picture.Width * k)), h = Math.Max(1, (int)(picture.Height * k));
            var scaled = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            using (var gg = Graphics.FromImage(scaled))
            {
                gg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                gg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gg.DrawImage(picture, 0, 0, w, h);
            }
            picture.Dispose();
            caption = caption ?? "";
            var p = new Particle
            {
                Kind = NOTE, Owner = from, Text = caption, Image = scaled, ImgW = w, ImgH = h,
                Life = PictureSeconds + Math.Min(3f, caption.Length * 0.04f)
            };
            if (HasNote()) notes.Enqueue(p);
            else ShowNote(p);
            Kick();
        }

        /// <summary>Shows a received animated GIF in the middle of the screen (same frame as pictures). Takes ownership.</summary>
        public void Animated(string from, Giphy.Animation anim, string caption)
        {
            var a = Area;
            var first = anim.Frames[0];
            float box = (float)Math.Sqrt(PictureScreenFraction);
            float k = Math.Min(a.Width * box / first.Width, a.Height * box / first.Height);
            caption = caption ?? "";
            var p = new Particle
            {
                Kind = NOTE, Owner = from, Text = caption, Anim = anim,
                ImgW = first.Width * k, ImgH = first.Height * k,
                // at least one full loop, between 3 and 6 seconds
                Life = Math.Max(PictureSeconds, Math.Min(6f, anim.TotalMs / 1000f)) + 1f + Math.Min(3f, caption.Length * 0.04f)
            };
            if (HasNote()) notes.Enqueue(p);
            else ShowNote(p);
            Kick();
        }

        /// <summary>Share of the screen surface a received picture may take.</summary>
        public const float PictureScreenFraction = 0.30f;
        /// <summary>How long a received picture stays on screen (a bit longer with a long caption).</summary>
        public const float PictureSeconds = 3f;

        // ───────────────────────────── Geometry (relative to the screen) ─────────────────────────────

        Rectangle Area => fixedArea ?? Screen.PrimaryScreen.Bounds;
        /// <summary>Unit = screen height: same look on 16:9, 21:9, 32:9 or 4K.</summary>
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

        /// <summary>Drift direction: towards the inside of the screen.</summary>
        float InwardSign => Corner == 0 ? -1f : Corner == 1 ? 1f : (rng.Next(2) == 0 ? -1f : 1f);

        // ───────────────────────────── Spawning ─────────────────────────────

        float R(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        bool Full => parts.Count + born.Count >= budget;

        Particle FindGrow(string owner)
        {
            foreach (var p in parts) if (p.Kind == GROW && p.Owner == owner && !p.Released && !p.Dead) return p;
            foreach (var p in born) if (p.Kind == GROW && p.Owner == owner && !p.Released) return p;
            return null;
        }

        bool HasNote()
        {
            foreach (var p in parts) if (p.Kind == NOTE && !p.Dead) return true;
            foreach (var p in born) if (p.Kind == NOTE) return true;
            return false;
        }

        void ShowNote(Particle p)
        {
            born.Add(p);
            // little hearts bursting from the card
            var a = Area;
            float u = Unit;
            float cx = a.Left + a.Width / 2f, cy = a.Top + a.Height * 0.45f;
            for (int i = 0; i < 8; i++)
            {
                double ang = Math.PI * (0.15 + 0.7 * i / 7.0) + Math.PI; // upper half
                float sp = R(200, 340) * (u / 1080f);
                born.Add(new Particle
                {
                    Kind = MINI, X = cx + (float)Math.Cos(ang) * u * 0.18f, Y = cy + (float)Math.Sin(ang) * u * 0.05f,
                    Vx = (float)Math.Cos(ang) * sp, Vy = (float)Math.Sin(ang) * sp,
                    Drag = 3.5f, Gravity = 60, Size = u * R(0.018f, 0.03f), Life = R(0.8f, 1.2f),
                    Rot = R(-25, 25), Spin = R(-150, 150), Sprite = Art.RandomHeart(rng)
                });
            }
        }

        void AddRiser(float delay, float sizeMul, float alpha, Art.Theme theme)
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
                Th = theme,
                Sprite = theme.RandomHeart(rng)
            };
            p.X = p.X0; p.Y = p.Y0;
            born.Add(p);
            if (rng.NextDouble() < 0.18) AddSparkle(p.X0, p.Y0 - p.Size, p.Size, delay + 0.15f);
        }

        void AddBig(Art.Theme theme)
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
                Th = theme,
                Sprite = 0
            };
            p.X = p.X0; p.Y = p.Y0;
            born.Add(p);
        }

        void AddBurst(float x, float y, float size, int count, Art.Theme theme)
        {
            for (int i = 0; i < count && !Full; i++)
            {
                double ang = 2 * Math.PI * i / count + R(-0.3f, 0.3f);
                float speed = R(160, 320) * (Unit / 1080f) * Math.Max(1f, size / (Unit * 0.08f));
                born.Add(new Particle
                {
                    Kind = MINI, X = x, Y = y,
                    Vx = (float)Math.Cos(ang) * speed,
                    Vy = (float)Math.Sin(ang) * speed - 40,
                    Drag = R(3f, 4.5f), Gravity = R(60, 140),
                    Size = Math.Min(size, Unit * 0.12f) * R(0.25f, 0.4f),
                    Life = R(0.6f, 0.95f),
                    Rot = R(-25, 25), Spin = R(-200, 200),
                    Th = theme ?? Art.Default,
                    Sprite = (theme ?? Art.Default).RandomHeart(rng)
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
                Size = Math.Min(size, Unit * 0.1f) * R(0.3f, 0.5f),
                Life = R(0.35f, 0.6f),
                Rot = R(0, 90), Vy = -R(20, 50)
            });
        }

        void Release(Particle p)
        {
            p.Released = true;
            p.ReleaseT = p.T;
            p.ReleaseSize = GrowSize(p);
            p.Life = p.T + 1.25f;
        }

        // ───────────────────────────── Easings ─────────────────────────────

        static float Clamp01(float x) => x < 0 ? 0 : x > 1 ? 1 : x;
        static float OutCubic(float t) { t = 1 - t; return 1 - t * t * t; }
        static float InCubic(float t) => t * t * t;
        static float InOutSine(float t) => (float)(-(Math.Cos(Math.PI * t) - 1) / 2);
        static float OutBack(float t)
        {
            const float c1 = 2.2f, c3 = c1 + 1;
            t -= 1;
            return 1 + c3 * t * t * t + c1 * t * t;
        }
        static float Gauss(float x, float m, float s) { float d = (x - m) / s; return (float)Math.Exp(-d * d); }
        /// <summary>"lub-dub" heartbeat, 0..1, period 1 s.</summary>
        static float Beat(float t)
        {
            float p = t - (float)Math.Floor(t);
            return Gauss(p, 0.08f, 0.05f) + 0.6f * Gauss(p, 0.28f, 0.05f);
        }

        float GrowSize(Particle p) => Unit * (0.06f + 0.22f * OutCubic(Clamp01(p.HoldT / 5f)));

        // ───────────────────────────── Loop ─────────────────────────────

        void Kick()
        {
            if (fixedArea != null) return; // recording: frames are driven by the recorder
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
            // Self-regulation: if a frame costs too much, fewer particles and a lower frame rate.
            double cost = (clock.Elapsed.TotalSeconds - now) * 1000;
            frameCostEma = frameCostEma * 0.9 + cost * 0.1;
            if (frameCostEma > 6) budget = Math.Max(60, budget - 6);
            else if (frameCostEma < 3 && budget < MaxParticles) budget += 2;
            int interval = frameCostEma > 10 ? 33 : 16;
            if (timer.Interval != interval) timer.Interval = interval;
            if (parts.Count == 0 && born.Count == 0 && notes.Count == 0) StopLoop();
        }

        void Rise(Particle p)
        {
            float u = Clamp01(p.T / p.Life);
            float k = 1 - (float)Math.Pow(1 - u, 1.35); // rise that slows down gently
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
                if (p.T >= p.Life)
                {
                    p.Dead = true;
                    p.Image?.Dispose();
                    p.Image = null;
                    p.Anim?.Dispose();
                    p.Anim = null;
                    if (p.Kind == NOTE && notes.Count > 0) ShowNote(notes.Dequeue());
                    continue;
                }
                if (p.T < 0) continue;
                switch (p.Kind)
                {
                    case RISER: Rise(p); break;
                    case BIG:
                        Rise(p);
                        if (!p.Popped && p.T >= p.Life - 0.25f)
                        {
                            p.Popped = true;
                            AddBurst(p.X, p.Y, p.Size, 10, p.Th);
                        }
                        break;
                    case GROW:
                        if (!p.Released)
                        {
                            p.HoldT += dt;
                            p.X = p.X0 + (float)Math.Sin(p.T * 2.3f + p.Phase) * Unit * 0.006f;
                            p.Y = p.Y0 + (float)Math.Sin(p.T * 1.7f) * Unit * 0.004f;
                            // sparkles orbiting the growing heart
                            if (rng.NextDouble() < dt * (3 + p.HoldT * 2))
                            {
                                double a = R(0, 6.28f);
                                float r = GrowSize(p) * 0.6f;
                                AddSparkle(p.X + (float)Math.Cos(a) * r, p.Y + (float)Math.Sin(a) * r, GrowSize(p) * 0.4f, 0);
                            }
                            if (p.HoldT >= MaxHold) Release(p);
                        }
                        else
                        {
                            float k = OutCubic(Clamp01((p.T - p.ReleaseT) / 1.0f));
                            p.Y = p.Y0 - Unit * 0.28f * k;
                            p.X = p.X0 + (float)Math.Sin((p.T - p.ReleaseT) * 5) * Unit * 0.01f;
                            if (!p.Popped && p.T >= p.Life - 0.25f)
                            {
                                p.Popped = true;
                                int n = 8 + (int)(14 * Clamp01(p.HoldT / 5f));
                                AddBurst(p.X, p.Y, p.ReleaseSize, n, p.Th);
                                born.Add(new Particle { Kind = RING, X = p.X, Y = p.Y, Size = p.ReleaseSize * 1.6f, Life = 0.55f, Sprite = 1 });
                            }
                        }
                        break;
                    case SYNC:
                    {
                        var a = Area;
                        float k = OutCubic(Clamp01(p.T / p.Life));
                        float cx = a.Left + a.Width / 2f + p.Phase * Unit * 0.04f;
                        p.X = p.X0 + (cx - p.X0) * k;
                        p.Y = p.Y0 - (float)Math.Sin(Math.PI * k) * Unit * 0.12f;
                        if (!p.Popped && p.T >= p.Life - 0.02f && p.Phase < 0)
                        {
                            p.Popped = true;
                            born.Add(new Particle
                            {
                                Kind = MERGE, X = a.Left + a.Width / 2f, Y = a.Top + a.Height * 0.5f,
                                Size = Unit * 0.2f, Life = 1.7f, Th = p.Th, Sprite = 0
                            });
                            born.Add(new Particle { Kind = RING, X = a.Left + a.Width / 2f, Y = a.Top + a.Height * 0.5f, Size = Unit * 0.18f, Life = 0.5f, Sprite = 1 });
                        }
                        break;
                    }
                    case MERGE:
                        if (!p.Popped && p.T >= p.Life - 0.25f)
                        {
                            p.Popped = true;
                            AddBurst(p.X, p.Y, p.Size * 0.6f, 26, p.Th);
                            born.Add(new Particle { Kind = RING, X = p.X, Y = p.Y, Size = p.Size * 1.4f, Life = 0.6f, Sprite = 3 });
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

        // ───────────────────────────── Rendering ─────────────────────────────

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
                    case GROW: DrawGrow(p); break;
                    case SYNC:
                    {
                        float s = OutBack(Clamp01(p.T / 0.35f));
                        float rot = p.Phase * -12 * (1 - p.T / p.Life);
                        DrawSprite(HeartsOf(p)[p.Sprite], p.X, p.Y, p.Size * s, rot, 1.12f, 0.9f, 0, 1);
                        break;
                    }
                    case MERGE:
                    {
                        float s = OutBack(Clamp01(p.T / 0.45f)) * (1 + 0.1f * Beat(p.T * 1.6f));
                        float pop = Clamp01((p.T - (p.Life - 0.25f)) / 0.25f);
                        s *= 1 + 0.45f * OutCubic(pop);
                        DrawSprite(HeartsOf(p)[p.Sprite], p.X, p.Y, p.Size * s, 0, 1, 1, 0, 1 - pop);
                        break;
                    }
                    case MINI:
                    {
                        float u = p.T / p.Life;
                        float s = OutBack(Clamp01(p.T / 0.18f)) * (1 - 0.4f * u);
                        DrawSprite(HeartsOf(p)[p.Sprite], p.X, p.Y, p.Size * s, p.Rot, 1, 1, 0, 1 - InCubic(u));
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
                    case NOTE: DrawNote(p); break;
                }
            }
            g.ResetTransform();
            g.Flush();
        }

        void DrawRiser(Particle p)
        {
            float t = p.T, u = Clamp01(t / p.Life);
            // "pop" entrance with overshoot, slight growth while rising, shrinks while fading
            float pop = OutBack(Clamp01(t / 0.32f));
            float s = pop * (1 + 0.18f * u) * (1 - 0.35f * Clamp01((u - 0.7f) / 0.3f));
            float alpha = p.Alpha * (1 - (float)Math.Pow(Clamp01((u - 0.5f) / 0.5f), 1.6));
            // tilt that follows the sway
            float rot = (float)Math.Cos(p.Phase + t * p.SwayFreq) * 14f * Clamp01(u * 5);
            // squash & stretch at birth (stretched vertically, then settles)
            float st = 0.22f * (1 - Clamp01(t / 0.35f));
            DrawSprite(HeartsOf(p)[p.Sprite], p.X, p.Y, p.Size * s, rot, 1 - st * 0.6f, 1 + st, 0, alpha);
        }

        void DrawBig(Particle p)
        {
            float t = p.T;
            float s = OutBack(Clamp01(t / 0.4f)) * (1 + 0.12f * Beat(t * 1.4f));
            float pop = Clamp01((t - (p.Life - 0.25f)) / 0.25f);
            s *= 1 + 0.4f * OutCubic(pop);
            float rot = (float)Math.Cos(p.Phase + t * p.SwayFreq) * 8f;
            DrawSprite(HeartsOf(p)[p.Sprite], p.X, p.Y, p.Size * s, rot, 1, 1, 0, 1 - pop);
        }

        void DrawGrow(Particle p)
        {
            float appear = OutBack(Clamp01(p.T / 0.35f));
            float size, alpha = p.Alpha;
            // the heartbeat speeds up as the heart grows
            float beat = Beat(p.T * (1.1f + p.HoldT * 0.35f));
            if (!p.Released) size = GrowSize(p) * appear * (1 + 0.08f * beat);
            else
            {
                size = p.ReleaseSize * (1 + 0.08f * beat);
                float pop = Clamp01((p.T - (p.Life - 0.25f)) / 0.25f);
                size *= 1 + 0.45f * OutCubic(pop);
                alpha *= 1 - pop;
            }
            // a held heart trembles a little, more and more
            float shake = p.Released ? 0 : Clamp01(p.HoldT / 6f) * 6f;
            float rot = (float)Math.Sin(p.T * 2.1f + p.Phase) * 5f + (float)Math.Sin(p.T * 37) * shake;
            DrawSprite(HeartsOf(p)[p.Sprite], p.X, p.Y, size, rot, 1, 1, 0, alpha);
        }

        Font toastFont, noteFont, noteFromFont;
        float fontsFor;

        void EnsureFonts()
        {
            float unit = Unit;
            if (toastFont != null && fontsFor == unit) return;
            toastFont?.Dispose(); noteFont?.Dispose(); noteFromFont?.Dispose();
            toastFont = new Font("Segoe UI Semibold", Math.Max(12f, unit * 0.016f), GraphicsUnit.Pixel);
            noteFont = new Font("Segoe UI Semibold", Math.Max(16f, unit * 0.032f), GraphicsUnit.Pixel);
            noteFromFont = new Font("Segoe UI", Math.Max(12f, unit * 0.018f), GraphicsUnit.Pixel);
            fontsFor = unit;
        }

        void DrawToast(Particle p)
        {
            EnsureFonts();
            float unit = Unit;
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
            // above the heart stream, without leaving the screen
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

        /// <summary>Picture card: the picture with a white frame, the sender's name and the caption below.</summary>
        void DrawPicture(Particle p)
        {
            EnsureFonts();
            var a = Area;
            float unit = Unit, t = p.T;
            float appear = OutBack(Clamp01(t / 0.45f));
            float outT = Clamp01((t - (p.Life - 0.45f)) / 0.45f);
            float alpha = Clamp01(t / 0.2f) * (1 - outT);
            Bitmap pic = p.Image;
            if (p.Anim != null)
            {
                int ms = (int)(t * 1000) % Math.Max(1, p.Anim.TotalMs), f = 0;
                while (f < p.Anim.Delays.Length - 1 && ms >= p.Anim.Delays[f]) { ms -= p.Anim.Delays[f]; f++; }
                pic = p.Anim.Frames[f];
            }
            if (alpha <= 0.01f || pic == null) return;
            float scale = (0.75f + 0.25f * appear) * (1 - 0.05f * outT);

            g.ResetTransform();
            float pad = unit * 0.014f;
            float iw = p.ImgW, ih = p.ImgH;
            string from = p.Owner ?? "";
            var fromSize = g.MeasureString(from, noteFromFont);
            SizeF capSize = p.Text.Length > 0 ? g.MeasureString(p.Text, noteFont, (int)Math.Max(iw, unit * 0.3f)) : SizeF.Empty;
            float w = Math.Max(iw, capSize.Width) + pad * 2;
            float h = ih + pad * 2 + fromSize.Height + pad * 0.5f + (capSize.Height > 0 ? capSize.Height + pad * 0.3f : 0);
            float cx = a.Left + a.Width / 2f - vs.X;
            float cy = a.Top + a.Height / 2f - vs.Y - outT * unit * 0.04f;

            float sw = w * scale, sh = h * scale;
            blit.Mark(Rectangle.Round(new RectangleF(cx - sw / 2 - pad * 2, cy - sh / 2 - pad * 2, sw + pad * 4, sh + pad * 5)));

            g.TranslateTransform(cx, cy);
            g.ScaleTransform(scale, scale);
            var rect = new RectangleF(-w / 2, -h / 2, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(rect, unit * 0.02f))
            {
                using (var shadow = new SolidBrush(Color.FromArgb((int)(50 * alpha), 150, 30, 80)))
                {
                    g.TranslateTransform(0, unit * 0.01f);
                    g.FillPath(shadow, path);
                    g.TranslateTransform(0, -unit * 0.01f);
                }
                using (var bg = new SolidBrush(Color.FromArgb((int)(248 * alpha), 255, 255, 255)))
                    g.FillPath(bg, path);
                using (var pen = new Pen(Color.FromArgb((int)(200 * alpha), 255, 128, 170), Math.Max(2f, unit * 0.003f)))
                    g.DrawPath(pen, path);
            }
            var imgRect = new RectangleF(-iw / 2, rect.Y + pad, iw, ih);
            var oldInterp = g.InterpolationMode;
            bool exact = p.Anim == null && scale > 0.99f;
            g.InterpolationMode = exact ? InterpolationMode.NearestNeighbor : InterpolationMode.Bilinear;
            if (alpha >= 0.99f) g.DrawImage(pic, imgRect);
            else
            {
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                    g.DrawImage(pic, Rectangle.Round(imgRect), 0, 0, pic.Width, pic.Height, GraphicsUnit.Pixel, ia);
                }
            }
            g.InterpolationMode = oldInterp;
            float ty = imgRect.Bottom + pad * 0.5f;
            using (var fb = new SolidBrush(Color.FromArgb((int)(255 * alpha), 214, 51, 108)))
                g.DrawString(from, noteFromFont, fb, rect.X + pad, ty);
            if (capSize.Height > 0)
                using (var tb = new SolidBrush(Color.FromArgb((int)(255 * alpha), 72, 40, 56)))
                    g.DrawString(p.Text, noteFont, tb, new RectangleF(rect.X + pad, ty + fromSize.Height + pad * 0.3f, w - pad * 2 + 2, capSize.Height + 2));
            g.ResetTransform();
        }

        /// <summary>Message card in the middle of the screen: pops in, floats gently, fades out upwards.</summary>
        void DrawNote(Particle p)
        {
            if (p.Image != null || p.Anim != null) { DrawPicture(p); return; }
            EnsureFonts();
            var a = Area;
            float unit = Unit, t = p.T;
            float appear = OutBack(Clamp01(t / 0.45f));
            float outT = Clamp01((t - (p.Life - 0.5f)) / 0.5f);
            float alpha = Clamp01(t / 0.2f) * (1 - outT);
            if (alpha <= 0.01f) return;
            float scale = (0.7f + 0.3f * appear) * (1 - 0.05f * outT);

            g.ResetTransform();
            float maxW = Math.Min(a.Width * 0.6f, unit * 1.1f);
            float pad = unit * 0.03f;
            var textSize = g.MeasureString(p.Text, noteFont, (int)maxW);
            string from = p.Owner ?? "";
            var fromSize = g.MeasureString(from, noteFromFont);
            float icon = fromSize.Height * 1.1f;
            float w = Math.Max(textSize.Width, fromSize.Width + icon * 1.3f) + pad * 2;
            float h = fromSize.Height + unit * 0.012f + textSize.Height + pad * 2;
            float cx = a.Left + a.Width / 2f - vs.X;
            float cy = a.Top + a.Height * 0.45f - vs.Y - outT * unit * 0.04f + (float)Math.Sin(t * 1.6f) * unit * 0.004f;

            float sw = w * scale, sh = h * scale;
            blit.Mark(Rectangle.Round(new RectangleF(cx - sw / 2 - pad, cy - sh / 2 - pad, sw + pad * 2, sh + pad * 2.5f)));

            g.TranslateTransform(cx, cy);
            g.ScaleTransform(scale, scale);
            var rect = new RectangleF(-w / 2, -h / 2, w, h);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = RoundRect(rect, unit * 0.025f))
            {
                using (var shadow = new SolidBrush(Color.FromArgb((int)(45 * alpha), 150, 30, 80)))
                {
                    g.TranslateTransform(0, unit * 0.008f);
                    g.FillPath(shadow, path);
                    g.TranslateTransform(0, -unit * 0.008f);
                }
                using (var bg = new SolidBrush(Color.FromArgb((int)(245 * alpha), 255, 255, 255)))
                    g.FillPath(bg, path);
                using (var pen = new Pen(Color.FromArgb((int)(200 * alpha), 255, 128, 170), Math.Max(2f, unit * 0.003f)))
                    g.DrawPath(pen, path);
            }
            using (var fb = new SolidBrush(Color.FromArgb((int)(255 * alpha), 214, 51, 108)))
                g.DrawString(from, noteFromFont, fb, rect.X + pad + icon * 1.2f, rect.Y + pad);
            using (var tb = new SolidBrush(Color.FromArgb((int)(255 * alpha), 72, 40, 56)))
                g.DrawString(p.Text, noteFont, tb, new RectangleF(rect.X + pad, rect.Y + pad + fromSize.Height + unit * 0.012f, maxW + 2, textSize.Height + 2));
            g.ResetTransform();

            // beating heart next to the sender's name (drawn by the blitter, so positioned by hand)
            float beat = 1 + 0.15f * Beat(t * 1.3f);
            float hx = cx + (rect.X + pad + icon * 0.5f) * scale + vs.X;
            float hy = cy + (rect.Y + pad + fromSize.Height / 2) * scale + vs.Y;
            DrawSprite(Art.Hearts[0], hx, hy, icon * scale * beat, 0, 1, 1, 0, alpha);
        }

        static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Art.SpriteSet[] HeartsOf(Particle p) => (p.Th ?? Art.Default).Hearts;

        void DrawSprite(Art.SpriteSet set, float x, float y, float px, float rot, float sx, float sy, float stretchAng, float alpha)
        {
            if (alpha <= 0.01f || px < 1.5f) return;
            var sp = set.Pick(px * Math.Max(sx, sy));
            float scale = px / (sp.W * Art.ContentRatio);
            blit.Draw(sp, x - vs.X, y - vs.Y, scale, rot, sx, sy, stretchAng, alpha);
        }

        // ───────────────────────────── Off-screen recording (demo GIF) ─────────────────────────────

        /// <summary>Renders into a fixed-size off-screen surface instead of the screen.</summary>
        public void StartRecording(Size size)
        {
            FreeSurface();
            fixedArea = new Rectangle(Point.Empty, size);
            vs = fixedArea.Value;
            recBits = System.Runtime.InteropServices.Marshal.AllocHGlobal(size.Width * size.Height * 4);
            Native.ZeroMemory(recBits, (IntPtr)(size.Width * size.Height * 4));
            canvas = new Bitmap(size.Width, size.Height, size.Width * 4, PixelFormat.Format32bppPArgb, recBits);
            blit = new Blitter(recBits, size.Width, size.Height);
            g = Graphics.FromImage(canvas);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        /// <summary>Advances the animation by dt seconds and returns the rendered frame (premultiplied ARGB).</summary>
        public Bitmap RecordFrame(float dt)
        {
            Step(dt);
            Render();
            return canvas;
        }

        // ───────────────────────────── Surface & presentation ─────────────────────────────

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
            if (recBits != IntPtr.Zero)
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(recBits);
                recBits = IntPtr.Zero;
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
            // Only the changed area is sent to the compositor.
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
                toastFont?.Dispose(); noteFont?.Dispose(); noteFromFont?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
