using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>Fenêtre de réglages : prénom, appairage par code, raccourci, son, démarrage auto.</summary>
    sealed class SettingsForm : Form
    {
        static readonly Color Pink = Color.FromArgb(236, 64, 122);
        static readonly Color PinkDark = Color.FromArgb(200, 40, 100);
        static readonly Color Soft = Color.FromArgb(255, 246, 249);
        static readonly Color Ink = Color.FromArgb(72, 40, 56);
        static readonly Color Muted = Color.FromArgb(150, 118, 134);
        static readonly Color Green = Color.FromArgb(46, 160, 100);

        readonly MimiApp app;
        readonly TextBox name, peer, codeIn;
        readonly HotkeyBox hk;
        readonly TrackBar vol;
        readonly CheckBox snd, echo, auto;
        readonly NumericUpDown port;
        readonly ComboBox corner;
        readonly Label msg, ipHint, hkHint;

        // appairage
        readonly Panel pairBox;
        readonly Button btnCreate, btnJoin, btnCancel, btnUnpair;
        readonly Label lblOr, pairHint, bigCode, pairTitle, pairState;
        enum PairUi { Idle, Hosting, Joining, Paired }
        PairUi pairUi;

        public SettingsForm(MimiApp app, Icon icon)
        {
            this.app = app;
            var c = app.Cfg;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Mimi — réglages";
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Soft;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(460, 712);

            Controls.Add(new HeaderPanel { Bounds = new Rectangle(0, 0, 460, 96) });

            int x = 24, w = 412;
            AddLabel("Ton prénom (ta moitié le verra)", x, 110);
            name = new TextBox { Bounds = new Rectangle(x, 132, w, 26), Text = c.Name };
            Controls.Add(name);

            // ── Appairage ──
            AddSection("Appairage", x, 172);
            pairBox = new Panel { Bounds = new Rectangle(x, 196, w, 92), BackColor = Color.White };
            pairBox.Paint += (s, e) =>
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var p = new Pen(Color.FromArgb(255, 205, 222)))
                    e.Graphics.DrawRectangle(p, 0, 0, pairBox.Width - 1, pairBox.Height - 1);
            };
            Controls.Add(pairBox);

            btnCreate = MakeButton("Créer un code", true, new Rectangle(12, 12, 150, 36));
            lblOr = new Label { Text = "ou", AutoSize = true, Location = new Point(172, 21), ForeColor = Muted };
            codeIn = new TextBox
            {
                Bounds = new Rectangle(200, 17, 100, 28), MaxLength = 7, TextAlign = HorizontalAlignment.Center,
                Font = new Font("Segoe UI Semibold", 11f)
            };
            SetCue(codeIn, "code");
            btnJoin = MakeButton("Rejoindre", false, new Rectangle(306, 12, 94, 36));
            pairHint = new Label
            {
                Bounds = new Rectangle(12, 54, 390, 36), ForeColor = Muted,
                Text = "Un PC crée le code, l'autre le tape. Il faut être sur la même box / le même Wi-Fi."
            };
            bigCode = new Label
            {
                AutoSize = true, Location = new Point(8, 8), ForeColor = Pink,
                Font = new Font("Segoe UI Semibold", 26f)
            };
            pairTitle = new Label { AutoSize = true, Location = new Point(12, 12), Font = new Font("Segoe UI Semibold", 12f), ForeColor = Ink };
            pairState = new Label { AutoSize = true, Location = new Point(14, 44), ForeColor = Muted };
            btnCancel = MakeButton("Annuler", false, new Rectangle(306, 12, 94, 36));
            btnUnpair = MakeButton("Dissocier", false, new Rectangle(306, 12, 94, 36));
            pairBox.Controls.AddRange(new Control[] { btnCreate, lblOr, codeIn, btnJoin, pairHint, bigCode, pairTitle, pairState, btnCancel, btnUnpair });

            btnCreate.Click += (s, e) => StartHosting();
            btnJoin.Click += (s, e) => StartJoining();
            codeIn.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartJoining(); } };
            btnCancel.Click += (s, e) => { app.Link?.CancelPairing(); SetPairUi(PairUi.Idle); };
            btnUnpair.Click += (s, e) =>
            {
                if (MessageBox.Show("Dissocier Mimi de " + Who() + " ?", "Mimi", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    app.Unpair();
                    peer.Text = "";
                    SetPairUi(PairUi.Idle);
                }
            };

            // ── Raccourci ──
            AddSection("Raccourci", x, 304);
            AddLabel("Pour envoyer un cœur (garde appuyé ou spamme 😏)", x, 328);
            hk = new HotkeyBox { Bounds = new Rectangle(x, 350, 200, 28), Mods = c.HotMods, Key = (Keys)c.HotKey };
            hk.RefreshText();
            Controls.Add(hk);
            hkHint = new Label { Bounds = new Rectangle(x + 210, 350, 202, 32), ForeColor = Muted, Text = "Clique dans la case puis fais ta combinaison" };
            Controls.Add(hkHint);
            hk.Enter += (s, e) => { app.PauseHotkey(); hkHint.Text = "Appuie sur ta combinaison… (Échap = annuler)"; };
            hk.Leave += (s, e) => { app.ResumeHotkey(); hkHint.Text = "Clique dans la case puis fais ta combinaison"; };
            hk.Rejected += t => hkHint.Text = t;
            hk.Changed += () => hkHint.Text = "Parfait ! Pense à enregistrer.";

            // ── Son & options ──
            AddSection("Son et options", x, 394);
            vol = new TrackBar
            {
                Bounds = new Rectangle(x - 4, 418, 300, 36), Minimum = 0, Maximum = 100, TickFrequency = 10,
                Value = c.Volume, BackColor = Soft
            };
            vol.MouseUp += (s, e) => { app.SetPreviewVolume(snd.Checked ? vol.Value : 0); app.PreviewSound(); };
            Controls.Add(vol);
            snd = new CheckBox { Text = "Son activé", Checked = c.Sound, Location = new Point(x + 310, 422), AutoSize = true };
            Controls.Add(snd);
            echo = new CheckBox { Text = "Voir aussi mes cœurs quand j'en envoie", Checked = c.Echo, Location = new Point(x, 458), AutoSize = true };
            Controls.Add(echo);
            auto = new CheckBox { Text = "Lancer Mimi au démarrage de Windows", Checked = Installer.IsAutostart(), Location = new Point(x, 484), AutoSize = true };
            Controls.Add(auto);
            AddLabel("Les cœurs apparaissent", x, 514);
            corner = new ComboBox { Bounds = new Rectangle(x + 170, 510, 160, 26), DropDownStyle = ComboBoxStyle.DropDownList };
            corner.Items.AddRange(new object[] { "en bas à droite", "en bas à gauche", "en bas au centre" });
            corner.SelectedIndex = Math.Max(0, Math.Min(2, c.Corner));
            Controls.Add(corner);

            // ── Avancé ──
            AddSection("Avancé", x, 552);
            AddLabel("Adresse de ta moitié (remplie par l'appairage)", x, 576);
            peer = new TextBox { Bounds = new Rectangle(x, 598, 270, 26), Text = c.Peer };
            SetCue(peer, "ex : 192.168.1.42 ou 100.64.0.2");
            Controls.Add(peer);
            AddLabel("Port", x + 284, 601);
            port = new NumericUpDown { Bounds = new Rectangle(x + 320, 598, 92, 26), Minimum = 1024, Maximum = 65535, Value = c.Port };
            Controls.Add(port);
            ipHint = new Label { Bounds = new Rectangle(x, 628, w, 20), ForeColor = Muted, Text = "Mon adresse : " + PeerLink.LocalAddresses() };
            Controls.Add(ipHint);

            msg = new Label { Bounds = new Rectangle(x, 648, w, 18), ForeColor = Pink, Text = "" };
            Controls.Add(msg);

            var test = MakeButton("Tester l'animation", false, new Rectangle(x, 666, 180, 36));
            test.Click += (s, e) => app.Demo();
            var save = MakeButton("Enregistrer 💗", true, new Rectangle(x + 192, 666, w - 192, 36));
            save.Click += (s, e) => Save();
            Controls.Add(test);
            Controls.Add(save);
            AcceptButton = null;

            ResumeLayout(false);

            app.StateChanged += OnState;
            FormClosed += (s, e) => app.StateChanged -= OnState;
            SetPairUi(c.IsPaired ? PairUi.Paired : PairUi.Idle);
        }

        // ───────────── appairage ─────────────

        string Who() => app.PeerDisplayName.Length > 0 ? app.PeerDisplayName : "ta moitié";

        public void AutoPair(bool host, string code)
        {
            if (host)
            {
                app.Link?.HostPairing(code);
                bigCode.Text = code.Substring(0, 3) + " " + code.Substring(3);
                SetPairUi(PairUi.Hosting);
            }
            else { codeIn.Text = code; StartJoining(); }
        }

        void StartHosting()
        {
            if (app.Link == null || app.Link.Error != null) { msg.Text = app.Link?.Error ?? "Réseau indisponible"; return; }
            string code = PeerLink.NewCode();
            app.Link.HostPairing(code);
            bigCode.Text = code.Substring(0, 3) + " " + code.Substring(3);
            SetPairUi(PairUi.Hosting);
        }

        void StartJoining()
        {
            string code = PeerLink.NormalizeCode(codeIn.Text);
            if (code.Length != 6) { msg.Text = "Le code fait 6 chiffres 🙂"; codeIn.Focus(); return; }
            if (app.Link == null || app.Link.Error != null) { msg.Text = app.Link?.Error ?? "Réseau indisponible"; return; }
            msg.Text = "";
            app.Link.JoinPairing(code);
            SetPairUi(PairUi.Joining);
        }

        public void OnPaired(string who)
        {
            peer.Text = app.Cfg.Peer;
            msg.Text = "";
            SetPairUi(PairUi.Paired);
        }

        public void OnPairTimeout()
        {
            SetPairUi(PairUi.Idle);
            msg.Text = pairUi == PairUi.Idle ? "Pas trouvé 😕 Même réseau ? Le code est bien affiché sur l'autre PC ?" : "";
        }

        public void RefreshFromConfig()
        {
            peer.Text = app.Cfg.Peer;
        }

        void OnState()
        {
            if (pairUi == PairUi.Paired || app.Cfg.IsPaired) SetPairUi(app.Cfg.IsPaired ? PairUi.Paired : pairUi);
        }

        void SetPairUi(PairUi s)
        {
            pairUi = s;
            bool idle = s == PairUi.Idle;
            btnCreate.Visible = lblOr.Visible = codeIn.Visible = btnJoin.Visible = idle;
            pairHint.Visible = idle || s == PairUi.Hosting || s == PairUi.Joining;
            bigCode.Visible = s == PairUi.Hosting;
            btnCancel.Visible = s == PairUi.Hosting || s == PairUi.Joining;
            pairTitle.Visible = pairState.Visible = s == PairUi.Paired || s == PairUi.Joining;
            btnUnpair.Visible = s == PairUi.Paired;

            switch (s)
            {
                case PairUi.Idle:
                    pairHint.Location = new Point(12, 54);
                    pairHint.Text = "Un PC crée le code, l'autre le tape. Il faut être sur la même box / le même Wi-Fi.";
                    break;
                case PairUi.Hosting:
                    pairHint.Location = new Point(12, 58);
                    pairHint.Text = "Tape ce code dans Mimi sur l'autre PC → « Rejoindre ». Valable 5 minutes.";
                    break;
                case PairUi.Joining:
                    pairTitle.Text = "Recherche de ta moitié…";
                    pairState.Text = "";
                    pairHint.Visible = false;
                    break;
                case PairUi.Paired:
                    pairTitle.Text = "💞  Appairé avec " + Who();
                    bool on = app.Online;
                    pairState.Text = on ? "●  connecté, prêt à recevoir des cœurs" : "●  hors ligne pour l'instant (PC éteint ou autre réseau)";
                    pairState.ForeColor = on ? Green : Muted;
                    break;
            }
        }

        // ───────────── enregistrement ─────────────

        void Save()
        {
            if (hk.Key == Keys.None) { msg.Text = "Choisis un raccourci 🙂"; return; }
            var n = app.Cfg.Clone();
            n.Name = name.Text;
            n.Peer = peer.Text.Trim();
            n.Port = (int)port.Value;
            n.HotMods = hk.Mods;
            n.HotKey = (int)hk.Key;
            n.Volume = vol.Value;
            n.Sound = snd.Checked;
            n.Echo = echo.Checked;
            n.Corner = corner.SelectedIndex;
            if (n.Peer.Length > 0 && string.IsNullOrEmpty(app.Cfg.PairCode)) app.Cfg.PairCode = "manuel";
            if (n.Peer.Length == 0) app.Cfg.PairCode = "";
            string err = app.Apply(n, auto.Checked);
            msg.ForeColor = err == null ? Green : Pink;
            msg.Text = err ?? "Enregistré ✓";
            SetPairUi(app.Cfg.IsPaired ? PairUi.Paired : (pairUi == PairUi.Paired ? PairUi.Idle : pairUi));
        }

        // ───────────── helpers UI ─────────────

        void AddLabel(string text, int x, int y) =>
            Controls.Add(new Label { Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Ink });

        void AddSection(string text, int x, int y)
        {
            Controls.Add(new Label
            {
                Text = text.ToUpperInvariant(), Location = new Point(x, y), AutoSize = true,
                ForeColor = Pink, Font = new Font("Segoe UI Semibold", 8.5f)
            });
        }

        static Button MakeButton(string text, bool primary, Rectangle r)
        {
            var b = new Button
            {
                Text = text, Bounds = r, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand,
                BackColor = primary ? Pink : Color.White,
                ForeColor = primary ? Color.White : PinkDark,
                Font = new Font("Segoe UI Semibold", 9.5f)
            };
            b.FlatAppearance.BorderSize = primary ? 0 : 1;
            b.FlatAppearance.BorderColor = Color.FromArgb(255, 180, 205);
            b.FlatAppearance.MouseOverBackColor = primary ? PinkDark : Color.FromArgb(255, 238, 244);
            return b;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        static void SetCue(TextBox t, string cue) =>
            t.HandleCreated += (s, e) => SendMessage(t.Handle, 0x1501, (IntPtr)1, cue);

        // ───────────── en-tête dessiné ─────────────

        sealed class HeaderPanel : Panel
        {
            public HeaderPanel() { DoubleBuffered = true; }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var r = ClientRectangle;
                using (var b = new LinearGradientBrush(r, Color.FromArgb(255, 112, 160), Color.FromArgb(255, 160, 130), 0f))
                    g.FillRectangle(b, r);
                // petits cœurs décoratifs en fond
                var rnd = new Random(7);
                for (int i = 0; i < 9; i++)
                {
                    float s = r.Height * (0.12f + (float)rnd.NextDouble() * 0.18f);
                    float px = r.Width * (0.45f + (float)rnd.NextDouble() * 0.55f), py = (float)rnd.NextDouble() * r.Height;
                    using (var p = Art.HeartPath(new RectangleF(px, py - s / 2, s, s * 0.9f)))
                    using (var br = new SolidBrush(Color.FromArgb(40, 255, 255, 255)))
                        g.FillPath(br, p);
                }
                float hs = r.Height * 0.62f;
                using (var img = Art.HeartBitmap((int)(hs / Art.ContentRatio) + 2, Color.FromArgb(255, 122, 162), Color.FromArgb(233, 30, 99), false))
                    g.DrawImage(img, r.Height * 0.12f + hs / 2 - img.Width / 2f, r.Height / 2f - img.Height / 2f, img.Width, img.Height);

                float tx = r.Height * 0.12f + hs + r.Height * 0.14f;
                using (var f1 = new Font("Segoe UI Semibold", 22f))
                using (var f2 = new Font("Segoe UI", 10f))
                {
                    g.DrawString("Mimi", f1, Brushes.White, tx, r.Height * 0.12f);
                    g.DrawString("des cœurs pour ta moitié", f2, Brushes.White, tx + 3, r.Height * 0.56f);
                }
            }
        }
    }

    /// <summary>Case qui capture une combinaison de touches.</summary>
    sealed class HotkeyBox : TextBox
    {
        public int Mods;
        public Keys Key;
        int oldMods; Keys oldKey;
        public event Action Changed;
        public event Action<string> Rejected;

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            BackColor = Color.White;
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
            Font = new Font("Segoe UI Semibold", 10f);
        }

        public void RefreshText() => Text = Key == Keys.None ? "" : GlobalHotkey.Describe(Mods, Key);

        protected override void OnEnter(EventArgs e) { oldMods = Mods; oldKey = Key; base.OnEnter(e); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref msg, keyData);
            Keys code = keyData & Keys.KeyCode;
            int mods = 0;
            if ((keyData & Keys.Control) != 0) mods |= GlobalHotkey.MOD_CONTROL;
            if ((keyData & Keys.Alt) != 0) mods |= GlobalHotkey.MOD_ALT;
            if ((keyData & Keys.Shift) != 0) mods |= GlobalHotkey.MOD_SHIFT;
            if (Native.GetKeyState(0x5B) < 0 || Native.GetKeyState(0x5C) < 0) mods |= GlobalHotkey.MOD_WIN;

            if (code == Keys.Escape && mods == 0) { Mods = oldMods; Key = oldKey; RefreshText(); Parent?.SelectNextControl(this, true, true, true, true); return true; }
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu || code == Keys.LWin || code == Keys.RWin)
            {
                Text = GlobalHotkey.Describe(mods, Keys.None) + " + …";
                return true;
            }
            if (mods == 0 && !GlobalHotkey.IsStandaloneKey(code))
            {
                Rejected?.Invoke("Ajoute Ctrl, Alt ou Maj (sinon la touche serait bloquée partout)");
                return true;
            }
            Mods = mods;
            Key = code;
            RefreshText();
            Changed?.Invoke();
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (Key != Keys.None && Text.EndsWith("…")) RefreshText();
            base.OnKeyUp(e);
        }
    }
}
