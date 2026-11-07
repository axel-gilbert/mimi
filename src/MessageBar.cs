using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>
    /// The bar that appears in the middle of the screen to write a sweet message, and optionally attach
    /// a picture (button, Ctrl+V from the clipboard, or drag &amp; drop). Enter sends, Esc closes.
    /// </summary>
    sealed class MessageBar : Form
    {
        static readonly Color Pink = Color.FromArgb(236, 64, 122);
        static readonly Color Border = Color.FromArgb(255, 150, 190);
        static readonly Color Ink = Color.FromArgb(72, 40, 56);
        static readonly Color Muted = Color.FromArgb(160, 125, 142);

        readonly TextBox box;
        readonly Button pick, clear, gifBtn;
        readonly Panel gifPanel;
        readonly TextBox gifSearch;
        readonly FlowLayoutPanel grid;
        readonly System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer { Interval = 450 };
        readonly string giphyKey;
        int searchGeneration;
        readonly PictureBox thumb;
        readonly Label hint;
        readonly System.Windows.Forms.Timer fade = new System.Windows.Forms.Timer { Interval = 15 };
        Image picture;
        bool dialogOpen, closing;

        /// <summary>Raised with the text and the picture (null if none) when the user presses Enter.</summary>
        public event Action<string, Image> Sent;
        /// <summary>Raised with the text (caption) and the GIPHY id when a GIF is picked.</summary>
        public event Action<string, string> SentGif;

        public MessageBar(Icon icon, string giphyKey)
        {
            this.giphyKey = giphyKey;
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            BackColor = Color.White;
            Icon = icon;
            Text = "Mimi";
            ClientSize = new Size(640, 92);
            Font = new Font("Segoe UI", 10f);
            AllowDrop = true;
            Opacity = 0;
            if (L.IsRightToLeft) RightToLeft = RightToLeft.Yes;

            thumb = new PictureBox
            {
                Bounds = new Rectangle(16, 14, 48, 48), SizeMode = PictureBoxSizeMode.Zoom, Visible = false,
                BackColor = Color.FromArgb(255, 240, 246)
            };
            box = new TextBox
            {
                Bounds = new Rectangle(64, 24, 428, 30), BorderStyle = BorderStyle.None, MaxLength = 140,
                Font = new Font("Segoe UI Semibold", 14f), ForeColor = Ink, BackColor = Color.White
            };
            pick = new Button
            {
                Bounds = new Rectangle(546, 18, 40, 40), FlatStyle = FlatStyle.Flat, Text = "🖼", Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Emoji", 14f), ForeColor = Pink, BackColor = Color.White, TabStop = false
            };
            pick.FlatAppearance.BorderSize = 0;
            pick.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 238, 244);
            clear = new Button
            {
                Bounds = new Rectangle(590, 18, 36, 40), FlatStyle = FlatStyle.Flat, Text = "✕", Cursor = Cursors.Hand,
                Font = new Font("Segoe UI", 11f), ForeColor = Muted, BackColor = Color.White, TabStop = false, Visible = false
            };
            clear.FlatAppearance.BorderSize = 0;
            hint = new Label
            {
                Bounds = new Rectangle(64, 62, 560, 20), ForeColor = Muted, Font = new Font("Segoe UI", 8.5f),
                Text = L.T("bar.hint"), UseMnemonic = false
            };
            gifBtn = new Button
            {
                Bounds = new Rectangle(500, 18, 44, 40), FlatStyle = FlatStyle.Flat, Text = L.T("bar.gif"), Cursor = Cursors.Hand,
                Font = new Font("Segoe UI Black", 9.5f), ForeColor = Pink, BackColor = Color.White, TabStop = false
            };
            gifBtn.FlatAppearance.BorderSize = 0;
            gifBtn.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 238, 244);

            gifPanel = new Panel { Bounds = new Rectangle(14, 92, 612, 352), Visible = false, BackColor = Color.White };
            gifSearch = new TextBox { Bounds = new Rectangle(0, 0, 612, 28), Font = new Font("Segoe UI", 10.5f), ForeColor = Ink };
            grid = new FlowLayoutPanel { Bounds = new Rectangle(0, 34, 612, 294), AutoScroll = true, BackColor = Color.White };
            var powered = new Label
            {
                Bounds = new Rectangle(0, 332, 612, 18), Text = L.T("gif.powered"), ForeColor = Muted,
                Font = new Font("Segoe UI", 8f), TextAlign = ContentAlignment.MiddleRight, UseMnemonic = false
            };
            gifPanel.Controls.AddRange(new Control[] { gifSearch, grid, powered });

            Controls.AddRange(new Control[] { thumb, box, gifBtn, pick, clear, hint, gifPanel });
            ResumeLayout(false);

            pick.Click += (s, e) => PickFile();
            gifBtn.Click += (s, e) => ToggleGifs();
            gifSearch.TextChanged += (s, e) => { debounce.Stop(); debounce.Start(); };
            debounce.Tick += (s, e) => { debounce.Stop(); LoadGifs(gifSearch.Text); };
            gifSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CloseBar(); }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; debounce.Stop(); LoadGifs(gifSearch.Text); }
            };
            clear.Click += (s, e) => SetPicture(null);
            box.KeyDown += OnKey;
            DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap) ? DragDropEffects.Copy : DragDropEffects.None;
            DragDrop += (s, e) => OnDrop(e.Data);
            Deactivate += (s, e) => { if (!dialogOpen) CloseBar(); };
            fade.Tick += (s, e) =>
            {
                double target = closing ? 0 : 1;
                Opacity = Math.Abs(Opacity - target) < 0.15 ? target : Opacity + (target - Opacity) * 0.35;
                if (Opacity == target) { fade.Stop(); if (closing) Close(); }
            };
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // centered horizontally, slightly above the middle of the primary screen
            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (int)(wa.Height * 0.42) - Height / 2);
            using (var path = RoundRect(new Rectangle(0, 0, Width, Height), Height / 3))
                Region = new Region(path);
            SetCue(box, L.T("bar.cue"));
            fade.Start();
        }

        // ───────────── GIFs ─────────────

        void ToggleGifs()
        {
            if (string.IsNullOrEmpty(giphyKey)) { hint.Text = L.T("gif.nokey"); return; }
            bool open = !gifPanel.Visible;
            gifPanel.Visible = open;
            int h = open ? LogicalToDeviceUnits(452) : LogicalToDeviceUnits(92);
            ClientSize = new Size(ClientSize.Width, h);
            using (var path = RoundRect(new Rectangle(0, 0, Width, Height), LogicalToDeviceUnits(30)))
                Region = new Region(path);
            Invalidate();
            if (open)
            {
                SetCue(gifSearch, L.T("gif.search"));
                gifSearch.Focus();
                if (grid.Controls.Count == 0) LoadGifs("");
            }
            else box.Focus();
        }

        /// <summary>Searches in the background and fills the grid with animated previews.</summary>
        void LoadGifs(string query)
        {
            int gen = ++searchGeneration;
            string key = giphyKey, lang = L.Code;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var gifs = Giphy.Search(key, query, lang);
                    UI(() => { if (gen == searchGeneration) ClearGrid(); });
                    foreach (var gif in gifs)
                    {
                        if (gen != searchGeneration) return;
                        byte[] preview;
                        try { preview = Giphy.Download(gif.PreviewUrl); } catch { continue; }
                        UI(() => { if (gen == searchGeneration) AddPreview(gif, preview); });
                    }
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                    UI(() => { if (gen == searchGeneration) hint.Text = L.T("gif.error"); });
                }
            });
        }

        void UI(Action a)
        {
            try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
        }

        void ClearGrid()
        {
            foreach (Control c in grid.Controls.Cast<Control>().ToList())
            {
                (c as PictureBox)?.Image?.Dispose();
                c.Dispose();
            }
            grid.Controls.Clear();
        }

        void AddPreview(Giphy.Gif gif, byte[] data)
        {
            Image img;
            // the stream must stay open for the PictureBox to animate the GIF
            try { img = Image.FromStream(new MemoryStream(data)); } catch { return; }
            var pb = new PictureBox
            {
                Size = new Size(LogicalToDeviceUnits(144), LogicalToDeviceUnits(96)), SizeMode = PictureBoxSizeMode.Zoom,
                Image = img, Cursor = Cursors.Hand, Margin = new Padding(LogicalToDeviceUnits(3)), BackColor = Color.FromArgb(255, 244, 248)
            };
            new ToolTip().SetToolTip(pb, gif.Title ?? "");
            pb.Click += (s, e) =>
            {
                SentGif?.Invoke(box.Text.Trim(), gif.Id);
                CloseBar();
            };
            grid.Controls.Add(pb);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Native.SetForegroundWindow(Handle);
            box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            using (var path = RoundRect(r, Height / 3))
            using (var pen = new Pen(Border, 2.5f))
                g.DrawPath(pen, path);
            if (!thumb.Visible)
            {
                // small heart where the thumbnail goes
                int s = (int)(Height * 0.34);
                using (var heart = Art.HeartBitmap(s * 2, Color.FromArgb(255, 122, 162), Color.FromArgb(233, 30, 99), false))
                    g.DrawImage(heart, thumb.Left + thumb.Width / 2 - s, box.Top + box.Height / 2 - s, s * 2, s * 2);
            }
        }

        void OnKey(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; CloseBar(); }
            else if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                string text = box.Text.Trim();
                if (text.Length == 0 && picture == null) return;
                var pic = picture;
                picture = null;
                Sent?.Invoke(text, pic);
                CloseBar();
            }
            else if (e.KeyCode == Keys.V && e.Control && Clipboard.ContainsImage())
            {
                e.SuppressKeyPress = true;
                try { SetPicture(Clipboard.GetImage()); } catch { }
            }
            else if (e.KeyCode == Keys.V && e.Control && Clipboard.ContainsFileDropList())
            {
                var f = Clipboard.GetFileDropList().Cast<string>().FirstOrDefault();
                if (f != null && IsPicture(f)) { e.SuppressKeyPress = true; LoadFile(f); }
            }
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
                box.Focus();
            }
        }

        void OnDrop(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var f = ((string[])data.GetData(DataFormats.FileDrop)).FirstOrDefault(IsPicture);
                if (f != null) LoadFile(f);
            }
            else if (data.GetDataPresent(DataFormats.Bitmap))
                SetPicture((Image)data.GetData(DataFormats.Bitmap));
        }

        static bool IsPicture(string f) =>
            new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant());

        void LoadFile(string path)
        {
            try
            {
                // copy into memory so the file isn't kept locked
                using (var fs = File.OpenRead(path))
                using (var img = Image.FromStream(fs))
                    SetPicture(new Bitmap(img));
            }
            catch { hint.Text = L.T("bar.badimage"); }
        }

        void SetPicture(Image img)
        {
            picture?.Dispose();
            picture = img;
            thumb.Image = img;
            thumb.Visible = clear.Visible = img != null;
            hint.Text = img != null ? L.T("bar.withimage") : L.T("bar.hint");
            Invalidate();
            box.Focus();
        }

        void CloseBar()
        {
            if (closing) return;
            closing = true;
            fade.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { fade.Dispose(); debounce.Dispose(); picture?.Dispose(); searchGeneration++; ClearGrid(); }
            base.Dispose(disposing);
        }

        static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        static void SetCue(TextBox t, string cue) => SendMessage(t.Handle, 0x1501, (IntPtr)1, cue);
    }
}
