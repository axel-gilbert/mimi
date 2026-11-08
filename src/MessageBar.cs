using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>
    /// The composer that appears in the middle of the screen (Ctrl+Alt+M): write a sweet message,
    /// attach a picture (button, Ctrl+V, drag &amp; drop) or pick a GIF, then send with Enter.
    ///
    /// Everything is drawn by hand (no stock controls except the two text boxes) and laid out in code
    /// with an explicit DPI factor, so it looks the same at any scaling. Windows 11 gives it native
    /// rounded corners and a shadow; on Windows 10 a region and a drawn border are used instead.
    /// </summary>
    sealed class MessageBar : Form
    {
        // layout, in 96-DPI units
        const int W = 620, P = 20, HeaderY = 16, HeaderH = 22, FieldH = 52, SendD = 46, ToolbarH = 32;
        const int AttachH = 86, ThumbS = 74, PanelH = 452, GridH = 306;
        const int MaxLen = 140;

        static readonly (string id, string query)[] Categories =
        {
            ("trending", ""), ("love", "love"), ("hug", "hug"), ("kiss", "kiss"),
            ("missyou", "miss you"), ("cute", "cute"), ("funny", "funny"), ("goodnight", "good night"),
        };

        readonly string giphyKey, partner;
        readonly TextBox input, search;
        readonly GifGrid grid;
        readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer { Interval = 15 };
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 380 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Dictionary<string, RectangleF> hits = new Dictionary<string, RectangleF>();
        Font fName, fInput, fSmall, fSmallBold, fChip, fKey;
        Bitmap heart;

        Image picture;                 // attached picture (owned)
        GifGrid.Item gif;              // or picked GIF
        Image gifPreview;              // its animated preview (owned)

        bool gifOpen, closing, dialogOpen, dragOver, roundedByDwm;
        float gifT;                    // 0 = panel closed, 1 = open (animated)
        double gifFrom, gifStartAt, openAt, closeAt;
        string hover, category = "trending";
        int baseTop, searchGeneration;
        double shakeAt = -1;

        /// <summary>Text + picture (null if none) when the user sends.</summary>
        public event Action<string, Image> Sent;
        /// <summary>Text (caption) + GIPHY id when the user sends a GIF.</summary>
        public event Action<string, string> SentGif;

        public MessageBar(Icon icon, string giphyKey, string partnerName)
        {
            this.giphyKey = giphyKey;
            partner = string.IsNullOrWhiteSpace(partnerName) ? L.T("your.half") : partnerName;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.White;
            Icon = icon;
            Text = "Mimi";
            AllowDrop = true;
            Opacity = 0;
            if (L.IsRightToLeft) RightToLeft = RightToLeft.Yes;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            input = new TextBox { BorderStyle = BorderStyle.None, MaxLength = MaxLen, ForeColor = UiColors.Ink, BackColor = UiColors.PinkSoft, AllowDrop = true };
            search = new TextBox { BorderStyle = BorderStyle.None, ForeColor = UiColors.Ink, BackColor = UiColors.PinkSoft, Visible = false };
            grid = new GifGrid { Visible = false };
            Controls.AddRange(new Control[] { input, search, grid });

            input.KeyDown += OnInputKey;
            input.TextChanged += (s, e) => Invalidate();
            input.GotFocus += (s, e) => Invalidate();
            input.LostFocus += (s, e) => Invalidate();
            search.KeyDown += OnSearchKey;
            search.TextChanged += (s, e) =>
            {
                if (suppressSearch) return;
                category = null;
                debounce.Stop();
                if (search.TextLength == 0) { LoadCategory("trending"); return; }
                debounce.Start();
                Invalidate();
            };
            search.GotFocus += (s, e) => Invalidate();
            search.LostFocus += (s, e) => Invalidate();
            debounce.Tick += (s, e) => { debounce.Stop(); LoadGifs(search.Text); };
            grid.Picked += PickGif;

            foreach (var c in new Control[] { this, input })
            {
                c.DragEnter += OnDragEnter;
                c.DragLeave += (s, e) => { dragOver = false; Invalidate(); };
                c.DragDrop += (s, e) => { dragOver = false; OnDrop(e.Data); };
            }
            Deactivate += (s, e) => { if (!dialogOpen) CloseBar(); };
            anim.Tick += (s, e) => Animate();
        }

        float K => DeviceDpi / 96f;
        int S(float v) => (int)Math.Round(v * K);
        bool HasAttachment => picture != null || gif != null;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW (Windows 10; Windows 11 adds its own)
                return cp;
            }
        }

        // ───────────── opening / closing ─────────────

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int round = 2; // DWMWCP_ROUND
                roundedByDwm = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0;
                int border = UiColors.PinkLine.R | (UiColors.PinkLine.G << 8) | (UiColors.PinkLine.B << 16);
                DwmSetWindowAttribute(Handle, 34, ref border, 4);
            }
            catch { roundedByDwm = false; }
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            fName = new Font("Segoe UI Semibold", 9.5f);
            fInput = new Font("Segoe UI", 13f);
            fSmall = new Font("Segoe UI", 8.5f);
            fSmallBold = new Font("Segoe UI Semibold", 8.5f);
            fChip = new Font("Segoe UI Semibold", 8.5f);
            fKey = new Font("Segoe UI Semibold", 7.5f);
            input.Font = fInput;
            search.Font = new Font("Segoe UI", 10.5f);
            heart = Art.HeartBitmap(S(26), Color.FromArgb(255, 122, 162), Color.FromArgb(233, 30, 99), false, 0.8f, false);
            SetCue(input, L.T("bar.cue"));
            SetCue(search, L.T("gif.search"));

            var wa = Screen.PrimaryScreen.WorkingArea;
            Width = S(W);
            Height = CollapsedHeight();
            baseTop = wa.Top + (int)(wa.Height * 0.36) - Height / 2;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, baseTop + S(14));
            DoLayout();
            openAt = clock.Elapsed.TotalSeconds;
            anim.Start();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Native.SetForegroundWindow(Handle);
            input.Focus();
        }

        void CloseBar()
        {
            if (closing) return;
            closing = true;
            closeAt = clock.Elapsed.TotalSeconds;
            anim.Start();
        }

        // ───────────── layout ─────────────

        int CollapsedHeight() =>
            S(HeaderY + HeaderH + 10 + (HasAttachment ? AttachH : 0) + FieldH + 12 + ToolbarH + 16);

        float FieldY => HeaderY + HeaderH + 10 + (HasAttachment ? AttachH : 0);
        float ToolbarY => FieldY + FieldH + 12;
        float PanelTop => ToolbarY + ToolbarH + 16;

        void DoLayout()
        {
            if (fInput == null) return;
            int h = CollapsedHeight() + (int)(S(PanelH) * Ease(gifT));
            if (Height != h) Height = h;
            if (!roundedByDwm)
                using (var path = Ui.Round(new RectangleF(0, 0, Width, Height), S(14)))
                    Region = new Region(path);

            float fx = P, fw = W - P * 2 - SendD - 10;
            input.SetBounds(S(fx + 18), S(FieldY) + (S(FieldH) - input.Height) / 2, S(fw - 36), input.Height);

            float pt = PanelTop;
            search.SetBounds(S(P + 40), S(pt + 10) + (S(38) - search.Height) / 2, S(W - P * 2 - 56), search.Height);
            grid.SetBounds(S(P - 8), S(pt + 10 + 38 + 10 + 30 + 8), S(W - P * 2 + 16), S(GridH));
            bool panel = gifT > 0.02f;
            search.Visible = panel;
            grid.Visible = panel;
            Invalidate();
        }

        static float Ease(float t) { t = Math.Max(0, Math.Min(1, t)); float u = 1 - t; return 1 - u * u * u; }

        void Animate()
        {
            double now = clock.Elapsed.TotalSeconds;
            bool busy = false;

            // open: fade + slide up; close: fade out
            float open = (float)Math.Min(1, (now - openAt) / 0.22);
            if (closing)
            {
                float c = (float)Math.Min(1, (now - closeAt) / 0.14);
                Opacity = Math.Max(0, (1 - c) * open);
                if (c >= 1) { anim.Stop(); Close(); return; }
                busy = true;
            }
            else
            {
                Opacity = Ease(open);
                Top = baseTop + (int)(S(14) * (1 - Ease(open)));
                if (open < 1) busy = true;
            }

            // GIF panel
            float target = gifOpen ? 1 : 0;
            if (gifT != target)
            {
                float t = (float)Math.Min(1, (now - gifStartAt) / 0.24);
                gifT = (float)(gifFrom + (target - gifFrom) * t);
                if (t >= 1) gifT = target;
                DoLayout();
                busy = true;
            }

            // shake (nothing to send)
            if (shakeAt >= 0)
            {
                double s = now - shakeAt;
                if (s > 0.35) { shakeAt = -1; Left = (Screen.PrimaryScreen.WorkingArea.Width - Width) / 2 + Screen.PrimaryScreen.WorkingArea.Left; }
                else { Left = (Screen.PrimaryScreen.WorkingArea.Width - Width) / 2 + Screen.PrimaryScreen.WorkingArea.Left + (int)(Math.Sin(s * 55) * S(7) * (1 - s / 0.35)); busy = true; }
            }

            // animated GIF preview in the attachment
            if (gifPreview != null) { busy = true; Invalidate(); }

            if (!busy) anim.Stop();
        }

        void ToggleGifs()
        {
            if (string.IsNullOrEmpty(giphyKey)) { FlashHint(L.T("gif.nokey")); return; }
            gifOpen = !gifOpen;
            gifFrom = gifT;
            gifStartAt = clock.Elapsed.TotalSeconds;
            anim.Start();
            if (gifOpen)
            {
                search.Visible = grid.Visible = true;
                search.Focus();
                if (category != null) LoadCategory(category);
            }
            else input.Focus();
            Invalidate();
        }

        string flash;
        double flashAt;
        void FlashHint(string text) { flash = text; flashAt = clock.Elapsed.TotalSeconds; Invalidate(); }

        // ───────────── GIFs ─────────────

        void LoadCategory(string id)
        {
            category = id;
            var q = Categories.First(c => c.id == id).query;
            LoadGifs(q);
            Invalidate();
        }

        void LoadGifs(string query)
        {
            int gen = ++searchGeneration;
            string key = giphyKey, lang = L.Code;
            grid.ShowLoading();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var gifs = Giphy.Search(key, query, lang);
                    if (gifs.Count == 0) { UI(() => { if (gen == searchGeneration) grid.Done(); }); return; }
                    // download previews a few at a time, in order
                    foreach (var g in gifs)
                    {
                        if (gen != searchGeneration) return;
                        byte[] bytes;
                        try { bytes = Giphy.Download(g.PreviewUrl); } catch { continue; }
                        UI(() =>
                        {
                            if (gen != searchGeneration) return;
                            Image img = null;
                            try { img = Image.FromStream(new MemoryStream(bytes)); } catch { }
                            if (img != null) grid.Add(new GifGrid.Item { Gif = g, Image = img, Bytes = bytes });
                        });
                    }
                    UI(() => { if (gen == searchGeneration) grid.Done(); });
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                    UI(() => { if (gen == searchGeneration) grid.ShowMessage(L.T("gif.error")); });
                }
            });
        }

        void PickGif(GifGrid.Item item)
        {
            ClearAttachment();
            gif = item;
            try { gifPreview = Image.FromStream(new MemoryStream(item.Bytes)); if (ImageAnimator.CanAnimate(gifPreview)) ImageAnimator.Animate(gifPreview, GifGrid.FrameHandler); } catch { }
            gifOpen = false;
            gifFrom = gifT;
            gifStartAt = clock.Elapsed.TotalSeconds;
            anim.Start();
            DoLayout();
            input.Focus();
        }

        void UI(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        // ───────────── attachments ─────────────

        void ClearAttachment()
        {
            picture?.Dispose(); picture = null;
            if (gifPreview != null) { try { ImageAnimator.StopAnimate(gifPreview, GifGrid.FrameHandler); } catch { } gifPreview.Dispose(); gifPreview = null; }
            gif = null;
        }

        void SetPicture(Image img)
        {
            ClearAttachment();
            picture = img;
            DoLayout();
            input.Focus();
        }

        void PickFile()
        {
            dialogOpen = true;
            try
            {
                using (var dlg = new OpenFileDialog
                {
                    Filter = L.T("bar.filter") + "|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp",
                    Title = L.T("bar.pick")
                })
                    if (dlg.ShowDialog(this) == DialogResult.OK) LoadFile(dlg.FileName);
            }
            finally
            {
                dialogOpen = false;
                Activate();
                input.Focus();
            }
        }

        static bool IsPicture(string f) =>
            new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant());

        void LoadFile(string path)
        {
            try
            {
                using (var fs = File.OpenRead(path))
                using (var img = Image.FromStream(fs))
                    SetPicture(new Bitmap(img));
            }
            catch { FlashHint(L.T("bar.badimage")); }
        }

        void OnDragEnter(object s, DragEventArgs e)
        {
            bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap);
            e.Effect = ok ? DragDropEffects.Copy : DragDropEffects.None;
            if (ok && !dragOver) { dragOver = true; Invalidate(); }
        }

        void OnDrop(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var f = ((string[])data.GetData(DataFormats.FileDrop)).FirstOrDefault(IsPicture);
                if (f != null) LoadFile(f);
            }
            else if (data.GetDataPresent(DataFormats.Bitmap))
                SetPicture(new Bitmap((Image)data.GetData(DataFormats.Bitmap)));
            Invalidate();
        }

        // ───────────── sending ─────────────

        bool CanSend => input.Text.Trim().Length > 0 || HasAttachment;

        void Send()
        {
            if (!CanSend) { shakeAt = clock.Elapsed.TotalSeconds; anim.Start(); return; }
            string text = input.Text.Trim();
            if (gif != null) SentGif?.Invoke(text, gif.Gif.Id);
            else
            {
                var pic = picture;
                picture = null; // ownership goes to the receiver of the event
                Sent?.Invoke(text, pic);
            }
            CloseBar();
        }

        // ───────────── keyboard ─────────────

        void OnInputKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; if (gifOpen) ToggleGifs(); else CloseBar(); }
            else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Send(); }
            else if (e.KeyCode == Keys.G && e.Control) { e.SuppressKeyPress = true; ToggleGifs(); }
            else if (e.KeyCode == Keys.Back && input.TextLength == 0 && HasAttachment) { e.SuppressKeyPress = true; ClearAttachment(); DoLayout(); }
            else if (e.KeyCode == Keys.V && e.Control)
            {
                try
                {
                    if (Clipboard.ContainsImage()) { e.SuppressKeyPress = true; SetPicture(Clipboard.GetImage()); }
                    else if (Clipboard.ContainsFileDropList())
                    {
                        var f = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsPicture);
                        if (f != null) { e.SuppressKeyPress = true; LoadFile(f); }
                    }
                }
                catch { }
            }
        }

        void OnSearchKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; ToggleGifs(); }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Up || e.KeyCode == Keys.Down ||
                     ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Right) && search.TextLength == 0))
            {
                if (grid.HandleKey(e.KeyCode)) e.SuppressKeyPress = true;
            }
        }

        // ───────────── mouse ─────────────

        string HitAt(Point p)
        {
            foreach (var kv in hits) if (kv.Value.Contains(p)) return kv.Key;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = HitAt(e.Location);
            if (h != hover) { hover = h; Cursor = h != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e) { hover = null; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            var h = HitAt(e.Location);
            switch (h)
            {
                case "close": CloseBar(); break;
                case "photo": PickFile(); break;
                case "gif": ToggleGifs(); break;
                case "send": Send(); break;
                case "remove": ClearAttachment(); DoLayout(); input.Focus(); break;
                case "field": input.Focus(); break;
                case "search": search.Focus(); break;
                default:
                    if (h != null && h.StartsWith("cat:"))
                    {
                        suppressSearch = true;
                        search.Text = "";
                        suppressSearch = false;
                        LoadCategory(h.Substring(4));
                        search.Focus();
                    }
                    break;
            }
            base.OnMouseClick(e);
        }

        bool suppressSearch;

        // ───────────── test driver hooks (screenshots) ─────────────

        internal void TestSetText(string text) { input.Text = text; input.SelectionStart = text.Length; Invalidate(); }
        internal void TestOpenGifs() { if (!gifOpen) ToggleGifs(); }
        internal void TestCategory(string id) { suppressSearch = true; search.Text = ""; suppressSearch = false; LoadCategory(id); }
        internal void TestSearch(string q) { search.Text = q; debounce.Stop(); LoadGifs(q); }
        internal void TestAttach(string path) => LoadFile(path);
        internal void TestDragOver(bool on) { dragOver = on; Invalidate(); }

        // ───────────── painting ─────────────

        bool paintFailed;

        protected override void OnPaint(PaintEventArgs e)
        {
            // Never let an exception reach WinForms (it would draw a red cross instead of the bar).
            try { PaintBar(e); }
            catch (Exception ex)
            {
                if (!paintFailed) { paintFailed = true; Program.LogError(ex); }
            }
        }

        void PaintBar(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.White);
            hits.Clear();
            float k = K;

            // ── header: "To Léa" + close ──
            if (heart != null) g.DrawImage(heart, S(P) - S(4), S(HeaderY) + (S(HeaderH) - heart.Height) / 2);
            using (var b = new SolidBrush(UiColors.Muted))
                Txt.DrawString(g, L.F("bar.to", partner), fName, b, S(P + 24), S(HeaderY + 2));
            var close = new RectangleF(S(W - P - 26), S(HeaderY - 2), S(28), S(28));
            hits["close"] = close;
            if (hover == "close") using (var b = new SolidBrush(UiColors.PinkSoft)) g.FillEllipse(b, close);
            Ui.CloseIcon(g, new PointF(close.X + close.Width / 2, close.Y + close.Height / 2), S(5), hover == "close" ? UiColors.PinkDark : UiColors.Muted, k);

            // ── attachment preview ──
            if (HasAttachment)
            {
                var t = new RectangleF(S(P), S(HeaderY + HeaderH + 10), S(ThumbS), S(ThumbS));
                var img = gifPreview ?? picture;
                using (var path = Ui.Round(t, S(12)))
                {
                    using (var bg = new SolidBrush(UiColors.PinkSoft)) g.FillPath(bg, path);
                    if (img != null)
                    {
                        if (gifPreview != null) try { ImageAnimator.UpdateFrames(gifPreview); } catch { }
                        var st = g.Save();
                        g.SetClip(path);
                        GifGrid.DrawCover(g, img, t);
                        g.Restore(st);
                    }
                    using (var pen = new Pen(UiColors.PinkLine, 1f * k)) g.DrawPath(pen, path);
                }
                if (gif != null)
                {
                    var badge = new RectangleF(t.X + S(6), t.Bottom - S(20), S(30), S(15));
                    using (var path = Ui.Round(badge, S(4)))
                    using (var b = new SolidBrush(Color.FromArgb(200, 0, 0, 0))) g.FillPath(b, path);
                    using (var f = new Font("Segoe UI Black", 7f))
                        Txt.DrawString(g, "GIF", f, Brushes.White, badge, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                }
                var rm = new RectangleF(t.Right - S(11), t.Y - S(7), S(20), S(20));
                hits["remove"] = rm;
                using (var b = new SolidBrush(hover == "remove" ? UiColors.PinkDark : UiColors.Ink)) g.FillEllipse(b, rm);
                using (var pen = new Pen(Color.White, 1.8f * k)) g.DrawEllipse(pen, rm);
                Ui.CloseIcon(g, new PointF(rm.X + rm.Width / 2, rm.Y + rm.Height / 2), S(3.5f), Color.White, k);
                using (var b = new SolidBrush(UiColors.Muted))
                    Txt.DrawString(g, gif != null ? (gif.Gif.Title ?? "GIF") : L.T("bar.photo"), fSmall, b,
                        new RectangleF(t.Right + S(14), t.Y + t.Height / 2 - S(10), S(W - P * 2 - ThumbS - 20), S(20)),
                        new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            }

            // ── text field + send button ──
            var field = new RectangleF(S(P), S(FieldY), S(W - P * 2 - SendD - 10), S(FieldH));
            hits["field"] = field;
            using (var path = Ui.Round(field, S(FieldH / 2f)))
            {
                using (var b = new SolidBrush(UiColors.PinkSoft)) g.FillPath(b, path);
                bool focus = input.Focused;
                using (var pen = new Pen(focus ? UiColors.Pink : UiColors.PinkLine, (focus ? 1.8f : 1f) * k)) g.DrawPath(pen, path);
                if (focus)
                    using (var glow = new Pen(Color.FromArgb(40, UiColors.Pink), 5f * k))
                    using (var outer = Ui.Round(RectangleF.Inflate(field, 2.5f * k, 2.5f * k), S(FieldH / 2f) + 2.5f * k))
                        g.DrawPath(glow, outer);
            }
            var send = new RectangleF(S(W - P - SendD), S(FieldY) + (S(FieldH) - S(SendD)) / 2f, S(SendD), S(SendD));
            hits["send"] = send;
            bool can = CanSend;
            var sendRect = hover == "send" && can ? RectangleF.Inflate(send, 1.5f * k, 1.5f * k) : send;
            using (var b = can
                ? (Brush)new LinearGradientBrush(sendRect, Color.FromArgb(255, 110, 160), hover == "send" ? UiColors.PinkDark : UiColors.Pink, 60f)
                : new SolidBrush(UiColors.Faint))
                g.FillEllipse(b, sendRect);
            Ui.SendIcon(g, new PointF(sendRect.X + sendRect.Width / 2 + S(1.5f), sendRect.Y + sendRect.Height / 2), S(10), Color.White);

            // ── toolbar ──
            float ty = S(ToolbarY);
            float x = S(P);
            x += ToolButton(g, "photo", L.T("bar.photo"), x, ty, false) + S(8);
            x += ToolButton(g, "gif", L.T("bar.gif"), x, ty, gifOpen) + S(8);

            // right side: flash message, character count, or keyboard hints
            bool flashing = flash != null && clock.Elapsed.TotalSeconds - flashAt < 4;
            float right = S(W - P);
            if (flashing)
            {
                using (var b = new SolidBrush(UiColors.PinkDark))
                    Txt.DrawString(g, flash, fSmall, b, new RectangleF(x + S(8), ty, right - x - S(8), S(ToolbarH)),
                        new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter });
                if (!anim.Enabled) anim.Start();
            }
            else if (input.TextLength >= MaxLen - 40)
            {
                string c = input.TextLength + " / " + MaxLen;
                using (var b = new SolidBrush(input.TextLength >= MaxLen - 10 ? UiColors.Pink : UiColors.Muted))
                    Txt.DrawString(g, c, fSmallBold, b, new RectangleF(x, ty, right - x, S(ToolbarH)), new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center });
            }
            else
            {
                // "[↵] Send   [Esc] Close"  (drawn right to left)
                float kh = S(20), ky = ty + (S(ToolbarH) - kh) / 2;
                foreach (var pair in new[] { ("Esc", L.T("bar.close")), (L.T("key.enter") + " ↵", L.T("bar.send")) })
                {
                    var lw = Txt.MeasureString(g, pair.Item2, fSmall).Width;
                    right -= lw;
                    using (var b = new SolidBrush(UiColors.Muted)) Txt.DrawString(g, pair.Item2, fSmall, b, right, ty + (S(ToolbarH) - fSmall.Height) / 2f);
                    float kw = Ui.KeyCap(g, pair.Item1, fKey, 0, 0, kh, k, false);
                    right -= kw + S(4);
                    Ui.KeyCap(g, pair.Item1, fKey, right, ky, kh, k);
                    right -= S(14);
                }
            }

            // ── GIF panel ──
            if (gifT > 0.02f)
            {
                float pt = S(PanelTop);
                using (var pen = new Pen(UiColors.PinkLine, 1f * k)) g.DrawLine(pen, S(P), pt, S(W - P), pt);
                var sr = new RectangleF(S(P), pt + S(10), S(W - P * 2), S(38));
                hits["search"] = sr;
                using (var path = Ui.Round(sr, S(19)))
                {
                    using (var b = new SolidBrush(UiColors.PinkSoft)) g.FillPath(b, path);
                    using (var pen = new Pen(search.Focused ? UiColors.Pink : UiColors.PinkLine, (search.Focused ? 1.6f : 1f) * k)) g.DrawPath(pen, path);
                }
                Ui.SearchIcon(g, new PointF(sr.X + S(20), sr.Y + sr.Height / 2 - S(1)), S(6), UiColors.Muted, k);

                // category chips
                float cx = S(P), cy = pt + S(10 + 38 + 10);
                foreach (var c in Categories)
                {
                    string label = L.T(c.id == "trending" ? "gif.trending" : "gif.cat." + c.id);
                    var sz = Txt.MeasureString(g, label, fChip);
                    var chip = new RectangleF(cx, cy, sz.Width + S(14), S(28));
                    if (chip.Right > S(W - P)) break;
                    string id = "cat:" + c.id;
                    hits[id] = chip;
                    bool on = category == c.id;
                    using (var path = Ui.Round(chip, S(14)))
                    {
                        using (var b = new SolidBrush(on ? UiColors.Pink : hover == id ? UiColors.PinkSoft : Color.White)) g.FillPath(b, path);
                        if (!on) using (var pen = new Pen(UiColors.PinkLine, 1f * k)) g.DrawPath(pen, path);
                    }
                    using (var b = new SolidBrush(on ? Color.White : UiColors.PinkDark))
                        Txt.DrawString(g, label, fChip, b, chip, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
                    cx = chip.Right + S(5);
                }

                using (var b = new SolidBrush(UiColors.Faint))
                    Txt.DrawString(g, L.T("gif.powered"), fSmall, b, new RectangleF(S(P), grid.Bottom + S(4), S(W - P * 2), S(18)),
                        new StringFormat { Alignment = StringAlignment.Far });
            }

            // ── drop zone ──
            if (dragOver)
            {
                var r = new RectangleF(S(8), S(8), Width - S(16), Height - S(16));
                using (var b = new SolidBrush(Color.FromArgb(235, 255, 246, 250))) g.FillRectangle(b, 0, 0, Width, Height);
                using (var path = Ui.Round(r, S(12)))
                using (var pen = new Pen(UiColors.Pink, 2f * k) { DashStyle = DashStyle.Dash })
                    g.DrawPath(pen, path);
                Ui.PhotoIcon(g, new RectangleF(Width / 2f - S(16), Height / 2f - S(30), S(32), S(26)), UiColors.Pink, k);
                using (var b = new SolidBrush(UiColors.PinkDark))
                    Txt.DrawString(g, L.T("bar.drop"), fName, b, new RectangleF(0, Height / 2f + S(2), Width, S(24)), new StringFormat { Alignment = StringAlignment.Center });
            }

            // ── Windows 10: draw the rounded border ourselves ──
            if (!roundedByDwm)
                using (var path = Ui.Round(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), S(14)))
                using (var pen = new Pen(UiColors.PinkLine, 1.2f * k)) g.DrawPath(pen, path);
        }

        /// <summary>A pill button with an icon (photo or GIF badge). Returns its width.</summary>
        float ToolButton(Graphics g, string id, string label, float x, float y, bool active)
        {
            float k = K;
            var sz = Txt.MeasureString(g, label, fSmallBold);
            float iconW = S(id == "gif" ? 22 : 18);
            var r = new RectangleF(x, y, S(14) + iconW + S(6) + sz.Width + S(10), S(ToolbarH));
            hits[id] = r;
            bool hot = hover == id;
            using (var path = Ui.Round(r, S(ToolbarH / 2f)))
            {
                using (var b = new SolidBrush(active ? UiColors.Pink : hot ? UiColors.PinkSoft : Color.White)) g.FillPath(b, path);
                if (!active) using (var pen = new Pen(hot ? UiColors.Pink : UiColors.PinkLine, 1f * k)) g.DrawPath(pen, path);
            }
            Color fg = active ? Color.White : UiColors.PinkDark;
            var ir = new RectangleF(r.X + S(12), r.Y + (r.Height - S(14)) / 2, iconW, S(14));
            if (id == "photo") Ui.PhotoIcon(g, ir, fg, k);
            else
            {
                using (var path = Ui.Round(ir, S(3)))
                using (var pen = new Pen(fg, 1.6f * k)) g.DrawPath(pen, path);
                using (var f = new Font("Segoe UI Black", 5f))
                using (var b = new SolidBrush(fg))
                    Txt.DrawString(g, "GIF", f, b, ir, new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center });
            }
            using (var b = new SolidBrush(fg))
                Txt.DrawString(g, label, fSmallBold, b, ir.Right + S(6), r.Y + (r.Height - sz.Height) / 2);
            return r.Width;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                anim.Dispose(); debounce.Dispose();
                searchGeneration++;
                ClearAttachment();
                heart?.Dispose();
                foreach (var f in new[] { fName, fInput, fSmall, fSmallBold, fChip, fKey }) f?.Dispose();
            }
            base.Dispose(disposing);
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        static void SetCue(TextBox t, string cue) => SendMessage(t.Handle, 0x1501, (IntPtr)1, cue);
    }
}
