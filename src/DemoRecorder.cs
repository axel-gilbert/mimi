using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Mimi
{
    /// <summary>
    /// Renders the README demo off-screen: a scripted sequence (heart stream, big heart, message card,
    /// sync) drawn over a neutral fake desktop, saved as numbered PNG frames.
    /// Usage: Mimi.exe --record-demo &lt;folder&gt;   then turn the frames into a GIF (see build-gif.ps1).
    /// </summary>
    static class DemoRecorder
    {
        public static void Run(string outDir, int width = 960, int height = 540, int fps = 30)
        {
            Art.Init();
            L.Code = "en";
            Directory.CreateDirectory(outDir);
            var size = new Size(width, height);
            using (var ov = new Overlay { Corner = 0 })
            using (var bg = Background(size))
            using (var frame = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            using (var g = Graphics.FromImage(frame))
            {
                ov.StartRecording(size);
                var script = new List<(float at, Action act)>();
                script.Add((0.2f, () => ov.Toast("Léa is sending you hearts")));
                for (int i = 0; i < 12; i++) { int combo = i + 1; script.Add((0.25f + i * 0.17f, () => ov.Wave(combo))); }
                script.Add((3.6f, () => ov.HoldStart("remote")));
                script.Add((5.9f, () => ov.HoldEnd("remote")));
                script.Add((7.0f, () => ov.Note("Léa", "Dinner tonight? I miss you ♥")));
                script.Add((12.6f, () => ov.SyncBurst()));
                script.Add((12.7f, () => ov.Toast("In sync!")));
                const float duration = 15.2f;

                float dt = 1f / fps;
                int n = 0, next = 0;
                for (float t = 0; t < duration; t += dt, n++)
                {
                    while (next < script.Count && script[next].at <= t) script[next++].act();
                    var canvas = ov.RecordFrame(dt);
                    g.DrawImageUnscaled(bg, 0, 0);
                    g.DrawImageUnscaled(canvas, 0, 0);
                    frame.Save(Path.Combine(outDir, "frame_" + n.ToString("0000") + ".png"), ImageFormat.Png);
                }
            }
        }

        /// <summary>A soft, neutral fake desktop: wallpaper, a window and a taskbar.</summary>
        static Bitmap Background(Size s)
        {
            var b = new Bitmap(s.Width, s.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new LinearGradientBrush(new Rectangle(Point.Empty, s), Color.FromArgb(255, 222, 210, 245), Color.FromArgb(255, 255, 214, 200), 35f))
                    g.FillRectangle(br, 0, 0, s.Width, s.Height);
                // soft blobs
                var rnd = new Random(3);
                for (int i = 0; i < 6; i++)
                {
                    float r = s.Height * (0.25f + (float)rnd.NextDouble() * 0.3f);
                    float x = (float)rnd.NextDouble() * s.Width, y = (float)rnd.NextDouble() * s.Height;
                    using (var p = new GraphicsPath())
                    {
                        p.AddEllipse(x - r, y - r, 2 * r, 2 * r);
                        using (var pb = new PathGradientBrush(p) { CenterColor = Color.FromArgb(60, 255, 255, 255), SurroundColors = new[] { Color.FromArgb(0, 255, 255, 255) } })
                            g.FillPath(pb, p);
                    }
                }
                // a window
                var w = new RectangleF(s.Width * 0.08f, s.Height * 0.1f, s.Width * 0.52f, s.Height * 0.62f);
                using (var shadow = new SolidBrush(Color.FromArgb(40, 60, 30, 80))) g.FillRectangle(shadow, w.X + 4, w.Y + 6, w.Width, w.Height);
                g.FillRectangle(Brushes.White, w);
                using (var bar = new SolidBrush(Color.FromArgb(245, 240, 248))) g.FillRectangle(bar, w.X, w.Y, w.Width, s.Height * 0.06f);
                using (var line = new SolidBrush(Color.FromArgb(232, 226, 238)))
                    for (int i = 0; i < 7; i++)
                        g.FillRectangle(line, w.X + w.Width * 0.06f, w.Y + s.Height * (0.11f + i * 0.065f), w.Width * (i % 3 == 2 ? 0.5f : 0.85f), s.Height * 0.022f);
                // taskbar
                using (var tb = new SolidBrush(Color.FromArgb(235, 250, 248, 252))) g.FillRectangle(tb, 0, s.Height - s.Height * 0.075f, s.Width, s.Height * 0.075f);
            }
            return b;
        }
    }
}
