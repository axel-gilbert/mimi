using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>
    /// The GIF picker grid, drawn entirely by hand: rounded tiles with cover-cropped animated previews,
    /// shimmering placeholders while loading, hover / keyboard selection, smooth scrolling and a thin scrollbar.
    /// </summary>
    sealed class GifGrid : Control
    {
        public sealed class Item
        {
            public Giphy.Gif Gif;
            public Image Image;      // animated preview (null while downloading)
            public byte[] Bytes;     // preview bytes (kept to show the picked GIF elsewhere)
        }

        readonly List<Item> items = new List<Item>();
        readonly Timer tick = new Timer { Interval = 33 };
        int placeholders;
        string message;
        float scroll, scrollTarget;
        int hover = -1, selected = -1;
        float shimmer;
        const int Cols = 4;

        public event Action<Item> Picked;

        public GifGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Color.White;
            tick.Tick += (s, e) => Animate();
        }

        float K => DeviceDpi / 96f;
        float Gap => 8 * K;

        /// <summary>Shows shimmering placeholder tiles (search in progress).</summary>
        public void ShowLoading(int count = 12)
        {
            Clear();
            placeholders = count;
            message = null;
            tick.Start();
            Invalidate();
        }

        public void ShowMessage(string text)
        {
            Clear();
            message = text;
            Invalidate();
        }

        public void Add(Item item)
        {
            if (placeholders > 0) placeholders--;
            if (item.Image != null && ImageAnimator.CanAnimate(item.Image)) ImageAnimator.Animate(item.Image, (s, e) => { });
            items.Add(item);
            if (selected < 0) selected = 0;
            tick.Start();
            Invalidate();
        }

        /// <summary>Called when all results arrived (remaining placeholders disappear).</summary>
        public void Done()
        {
            placeholders = 0;
            if (items.Count == 0 && message == null) message = L.T("gif.empty");
            Invalidate();
        }

        public void Clear()
        {
            foreach (var it in items)
            {
                if (it.Image != null) { try { ImageAnimator.StopAnimate(it.Image, (s, e) => { }); } catch { } it.Image.Dispose(); }
            }
            items.Clear();
            placeholders = 0;
            scroll = scrollTarget = 0;
            hover = selected = -1;
            message = null;
        }

        /// <summary>Keyboard navigation from the search box: arrows move, Enter picks.</summary>
        public bool HandleKey(Keys key)
        {
            if (items.Count == 0) return false;
            int s = Math.Max(0, selected);
            switch (key)
            {
                case Keys.Right: s = Math.Min(items.Count - 1, s + 1); break;
                case Keys.Left: s = Math.Max(0, s - 1); break;
                case Keys.Down: s = Math.Min(items.Count - 1, s + Cols); break;
                case Keys.Up: s = Math.Max(0, s - Cols); break;
                case Keys.Enter: Picked?.Invoke(items[s]); return true;
                default: return false;
            }
            selected = s;
            EnsureVisible(s);
            Invalidate();
            return true;
        }

        // ───────────── layout ─────────────

        float TileW => (Width - Gap * (Cols + 1) - 6 * K) / Cols;
        float TileH => TileW * 0.72f;

        RectangleF TileRect(int i)
        {
            int r = i / Cols, c = i % Cols;
            return new RectangleF(Gap + c * (TileW + Gap), Gap + r * (TileH + Gap) - scroll, TileW, TileH);
        }

        float ContentHeight
        {
            get
            {
                int n = items.Count + placeholders;
                int rows = (n + Cols - 1) / Cols;
                return Gap + rows * (TileH + Gap);
            }
        }

        float MaxScroll => Math.Max(0, ContentHeight - Height);

        void EnsureVisible(int i)
        {
            var r = TileRect(i);
            r.Y += scroll; // unscrolled
            if (r.Y - Gap < scrollTarget) scrollTarget = Math.Max(0, r.Y - Gap);
            else if (r.Bottom + Gap > scrollTarget + Height) scrollTarget = Math.Min(MaxScroll, r.Bottom + Gap - Height);
            tick.Start();
        }

        int HitTest(Point p)
        {
            for (int i = 0; i < items.Count; i++)
                if (TileRect(i).Contains(p)) return i;
            return -1;
        }

        // ───────────── animation & input ─────────────

        void Animate()
        {
            if (!Visible) return;
            shimmer = (shimmer + 0.035f) % 1f;
            scroll += (scrollTarget - scroll) * 0.28f;
            if (Math.Abs(scrollTarget - scroll) < 0.5f) scroll = scrollTarget;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scrollTarget = Math.Max(0, Math.Min(MaxScroll, scrollTarget - e.Delta * 0.6f * K));
            tick.Start();
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != hover) { hover = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h >= 0) { selected = h; Picked?.Invoke(items[h]); }
            base.OnMouseClick(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (Visible) tick.Start(); else tick.Stop();
            base.OnVisibleChanged(e);
        }

        // ───────────── painting ─────────────

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.Bilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(BackColor);
            float rad = 10 * K;

            if (message != null)
            {
                using (var f = new Font("Segoe UI", 10f))
                using (var b = new SolidBrush(UiColors.Muted))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    Txt.DrawString(g, message, f, b, ClientRectangle, sf);
                }
                return;
            }

            ImageAnimator.UpdateFrames();
            for (int i = 0; i < items.Count + placeholders; i++)
            {
                var r = TileRect(i);
                if (r.Bottom < 0 || r.Top > Height) continue;
                bool hot = i == hover || (i == selected && Focused);
                var draw = hot ? RectangleF.Inflate(r, 2 * K, 2 * K) : r;
                using (var path = Ui.Round(draw, rad))
                {
                    if (i >= items.Count || items[i].Image == null) DrawShimmer(g, path, draw, i);
                    else
                    {
                        var state = g.Save();
                        g.SetClip(path);
                        DrawCover(g, items[i].Image, draw);
                        g.Restore(state);
                    }
                    if (hot)
                        using (var pen = new Pen(UiColors.Pink, 2.5f * K)) g.DrawPath(pen, path);
                    else if (i == selected)
                        using (var pen = new Pen(Color.FromArgb(150, UiColors.Pink), 1.5f * K)) g.DrawPath(pen, path);
                }
            }

            // thin scrollbar
            if (MaxScroll > 1)
            {
                float trackH = Height - 2 * Gap;
                float thumbH = Math.Max(30 * K, trackH * Height / ContentHeight);
                float y = Gap + (trackH - thumbH) * (scroll / MaxScroll);
                using (var path = Ui.Round(new RectangleF(Width - 5 * K, y, 3.5f * K, thumbH), 1.75f * K))
                using (var b = new SolidBrush(Color.FromArgb(120, UiColors.Pink)))
                    g.FillPath(b, path);
            }
        }

        void DrawShimmer(Graphics g, GraphicsPath path, RectangleF r, int i)
        {
            using (var bg = new SolidBrush(UiColors.PinkSoft)) g.FillPath(bg, path);
            float phase = (shimmer + (i % Cols) * 0.08f) % 1f;
            float w = r.Width * 0.6f;
            float x = r.X - w + (r.Width + 2 * w) * phase;
            var band = new RectangleF(x, r.Y, w, r.Height);
            if (band.Width < 1) return;
            using (var lg = new LinearGradientBrush(band, Color.FromArgb(0, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 0f))
            {
                var blend = new ColorBlend
                {
                    Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb(170, 255, 255, 255), Color.FromArgb(0, 255, 255, 255) },
                    Positions = new[] { 0f, 0.5f, 1f }
                };
                lg.InterpolationColors = blend;
                var state = g.Save();
                g.SetClip(path);
                g.FillRectangle(lg, band);
                g.Restore(state);
            }
        }

        /// <summary>Draws the image filling the rectangle, cropping what doesn't fit (like CSS object-fit: cover).</summary>
        internal static void DrawCover(Graphics g, Image img, RectangleF r)
        {
            float ia = (float)img.Width / img.Height, ra = r.Width / r.Height;
            RectangleF src;
            if (ia > ra) { float w = img.Height * ra; src = new RectangleF((img.Width - w) / 2, 0, w, img.Height); }
            else { float h = img.Width / ra; src = new RectangleF(0, (img.Height - h) / 2, img.Width, h); }
            g.DrawImage(img, r, src, GraphicsUnit.Pixel);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { tick.Dispose(); Clear(); }
            base.Dispose(disposing);
        }
    }

    /// <summary>Shared colours of the hand-drawn UI.</summary>
    static class UiColors
    {
        public static readonly Color Pink = Color.FromArgb(236, 64, 122);
        public static readonly Color PinkDark = Color.FromArgb(205, 38, 100);
        public static readonly Color PinkSoft = Color.FromArgb(255, 241, 246);
        public static readonly Color PinkLine = Color.FromArgb(255, 212, 226);
        public static readonly Color Ink = Color.FromArgb(58, 32, 46);
        public static readonly Color Muted = Color.FromArgb(150, 120, 136);
        public static readonly Color Faint = Color.FromArgb(214, 196, 205);
    }

    /// <summary>Drawing helpers: rounded rectangles and small vector icons.</summary>
    static class Ui
    {
        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Max(0.5f, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Photo icon: rounded frame, a mountain and a sun.</summary>
        public static void PhotoIcon(Graphics g, RectangleF r, Color c, float k)
        {
            using (var pen = new Pen(c, 1.6f * k) { LineJoin = LineJoin.Round })
            using (var b = new SolidBrush(c))
            using (var frame = Round(r, 3 * k))
            {
                g.DrawPath(pen, frame);
                var m = new[]
                {
                    new PointF(r.X + r.Width * 0.12f, r.Bottom - r.Height * 0.18f),
                    new PointF(r.X + r.Width * 0.42f, r.Y + r.Height * 0.48f),
                    new PointF(r.X + r.Width * 0.62f, r.Y + r.Height * 0.68f),
                    new PointF(r.X + r.Width * 0.74f, r.Y + r.Height * 0.56f),
                    new PointF(r.Right - r.Width * 0.12f, r.Bottom - r.Height * 0.18f),
                };
                g.FillPolygon(b, m);
                g.FillEllipse(b, r.X + r.Width * 0.62f, r.Y + r.Height * 0.2f, r.Width * 0.18f, r.Width * 0.18f);
            }
        }

        public static void CloseIcon(Graphics g, PointF c, float s, Color col, float k)
        {
            using (var pen = new Pen(col, 1.7f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawLine(pen, c.X - s, c.Y - s, c.X + s, c.Y + s);
                g.DrawLine(pen, c.X - s, c.Y + s, c.X + s, c.Y - s);
            }
        }

        public static void SearchIcon(Graphics g, PointF c, float s, Color col, float k)
        {
            using (var pen = new Pen(col, 1.8f * k) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawEllipse(pen, c.X - s, c.Y - s, s * 1.6f, s * 1.6f);
                g.DrawLine(pen, c.X + s * 0.45f, c.Y + s * 0.45f, c.X + s, c.Y + s);
            }
        }

        /// <summary>Paper plane, pointing right.</summary>
        public static void SendIcon(Graphics g, PointF c, float s, Color col)
        {
            using (var path = new GraphicsPath())
            using (var b = new SolidBrush(col))
            {
                path.AddPolygon(new[]
                {
                    new PointF(c.X - s * 0.9f, c.Y - s * 0.85f),
                    new PointF(c.X + s, c.Y),
                    new PointF(c.X - s * 0.9f, c.Y + s * 0.85f),
                    new PointF(c.X - s * 0.55f, c.Y + s * 0.08f),
                    new PointF(c.X + s * 0.15f, c.Y),
                    new PointF(c.X - s * 0.55f, c.Y - s * 0.08f),
                });
                g.FillPath(b, path);
            }
        }

        /// <summary>A keyboard key cap with a label, e.g. [Esc]. Returns its width.</summary>
        public static float KeyCap(Graphics g, string key, Font f, float x, float y, float h, float k, bool draw = true)
        {
            var sz = Txt.MeasureString(g, key, f);
            float w = Math.Max(h, sz.Width + 8 * k);
            if (draw)
            {
                var r = new RectangleF(x, y, w, h);
                using (var path = Round(r, 4 * k))
                {
                    using (var b = new SolidBrush(Color.FromArgb(250, 246, 248))) g.FillPath(b, path);
                    using (var pen = new Pen(UiColors.Faint, 1f * k)) g.DrawPath(pen, path);
                    using (var tb = new SolidBrush(UiColors.Muted))
                        Txt.DrawString(g, key, f, tb, r, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
            }
            return w;
        }
    }
}
