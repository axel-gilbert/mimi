using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>
    /// Settings window, organised in tabs: Couple (name, pairing, do not disturb), Shortcuts,
    /// Look &amp; sound (with previews), Stats, General (language, startup, updates, advanced).
    /// All coordinates are given at 96 DPI and scaled automatically; nothing is moved after scaling.
    /// </summary>
    sealed class SettingsForm : Form
    {
        internal static readonly Color Pink = Color.FromArgb(236, 64, 122);
        internal static readonly Color PinkDark = Color.FromArgb(200, 40, 100);
        internal static readonly Color PinkLight = Color.FromArgb(255, 205, 222);
        internal static readonly Color Lilac = Color.FromArgb(170, 120, 230);
        internal static readonly Color Soft = Color.FromArgb(255, 246, 249);
        internal static readonly Color Ink = Color.FromArgb(72, 40, 56);
        internal static readonly Color Muted = Color.FromArgb(150, 118, 134);
        internal static readonly Color Green = Color.FromArgb(46, 160, 100);

        const int W = 560, M = 28, CW = W - 2 * M;

        readonly MimiApp app;
        readonly HeaderPanel header;
        readonly List<Button> tabs = new List<Button>();
        readonly List<Panel> pages = new List<Panel>();
        readonly Label msg;

        // Couple
        readonly TextBox name;
        readonly Panel pairBox;
        readonly Button btnCreate, btnJoin, btnCancel, btnUnpair;
        readonly TextBox codeIn;
        readonly Label lblOr, pairHint, bigCode, pairTitle, pairState, legacyHint, sinceLabel, dndHelp;
        readonly ComboBox dnd;
        bool settingDnd;
        enum PairUi { Idle, Hosting, Joining, Paired }
        PairUi pairUi;

        // Shortcuts
        readonly HotkeyBox hkHeart, hkMsg;
        readonly Label hkHeartHint, hkMsgHint;

        // Look & sound
        readonly ComboBox corner;
        readonly TrackBar vol;
        readonly CheckBox snd, echo, partnerColors;
        readonly List<ThemeTile> tiles = new List<ThemeTile>();
        readonly List<Button> styleButtons = new List<Button>();
        string theme;

        // Stats
        readonly StatsPanel statsPanel;
        readonly Timer statsRefresh = new Timer { Interval = 2000 };

        // General
        readonly ComboBox lang;
        readonly CheckBox auto, autoUpdate;
        readonly TextBox peer, giphy;
        readonly NumericUpDown port;

        public SettingsForm(MimiApp app, Icon icon)
        {
            this.app = app;
            var c = app.Cfg;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = L.T("set.title");
            Icon = icon;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Soft;
            ForeColor = Ink;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(W, 624);
            if (L.IsRightToLeft) RightToLeft = RightToLeft.Yes; // text direction only; the layout is not mirrored

            header = new HeaderPanel(app) { Bounds = new Rectangle(0, 0, W, 84) };
            Controls.Add(header);

            // ── tab strip ──
            string[] tabNames = { L.T("tab.couple"), L.T("tab.shortcuts"), L.T("tab.look"), L.T("tab.stats"), L.T("tab.general") };
            int tx = 16;
            for (int i = 0; i < tabNames.Length; i++)
            {
                int index = i;
                int tw = 100;
                var b = new Button
                {
                    Text = tabNames[i], Bounds = new Rectangle(tx, 94, tw, 32), FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI Semibold", tabNames.Max(t => t.Length) > 11 ? 8.25f : 9f), Cursor = Cursors.Hand, UseMnemonic = false, TabStop = false
                };
                b.FlatAppearance.BorderSize = 0;
                b.Click += (s, e) => SelectTab(index);
                tabs.Add(b);
                Controls.Add(b);
                tx += tw + 6;
            }
            for (int i = 0; i < tabNames.Length; i++)
            {
                var p = new Panel { Bounds = new Rectangle(0, 134, W, 424), BackColor = Soft, Visible = false };
                pages.Add(p);
                Controls.Add(p);
            }

            // ═════════ Couple ═════════
            var pc = pages[0];
            Add(pc, Label(L.T("set.name"), M, 8));
            name = new TextBox { Bounds = new Rectangle(M, 30, CW, 26), Text = c.Name };
            pc.Controls.Add(name);

            Add(pc, Section(L.T("set.pairing"), M, 72));
            pairBox = new Panel { Bounds = new Rectangle(M, 94, CW, 96), BackColor = Color.White };
            pairBox.Paint += (s, e) =>
            {
                using (var p = new Pen(PinkLight))
                    e.Graphics.DrawRectangle(p, 0, 0, pairBox.Width - 1, pairBox.Height - 1);
            };
            pc.Controls.Add(pairBox);
            btnCreate = MakeButton(L.T("set.createcode"), true, new Rectangle(12, 12, 160, 36));
            lblOr = new Label { Font = BaseFont, Text = L.T("set.or"), AutoSize = true, Location = new Point(182, 21), ForeColor = Muted };
            codeIn = new TextBox
            {
                Bounds = new Rectangle(212, 17, 110, 28), MaxLength = 7, TextAlign = HorizontalAlignment.Center,
                Font = new Font("Segoe UI Semibold", 11f)
            };
            SetCue(codeIn, L.T("set.codecue"));
            btnJoin = MakeButton(L.T("set.join"), false, new Rectangle(330, 12, 100, 36));
            pairHint = new Label { Font = BaseFont, Bounds = new Rectangle(12, 54, 480, 38), ForeColor = Muted, UseMnemonic = false };
            bigCode = new Label { AutoSize = true, Location = new Point(8, 6), ForeColor = Pink, Font = new Font("Segoe UI Semibold", 22f) };
            pairTitle = new Label { AutoSize = true, Location = new Point(12, 14), Font = new Font("Segoe UI Semibold", 12f), ForeColor = Ink, UseMnemonic = false };
            pairState = new Label { Font = BaseFont, AutoSize = true, Location = new Point(14, 48), ForeColor = Muted, UseMnemonic = false };
            btnCancel = MakeButton(L.T("set.cancel"), false, new Rectangle(392, 12, 100, 36));
            btnUnpair = MakeButton(L.T("set.unpair"), false, new Rectangle(392, 12, 100, 36));
            pairBox.Controls.AddRange(new Control[] { btnCreate, lblOr, codeIn, btnJoin, pairHint, bigCode, pairTitle, pairState, btnCancel, btnUnpair });
            btnCreate.Click += (s, e) => StartHosting();
            btnJoin.Click += (s, e) => StartJoining();
            codeIn.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartJoining(); } };
            btnCancel.Click += (s, e) => { app.Link?.CancelPairing(); SetPairUi(PairUi.Idle); };
            btnUnpair.Click += (s, e) =>
            {
                if (MessageBox.Show(L.F("set.unpair.ask", Who()), "Mimi", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    app.Unpair();
                    peer.Text = "";
                    SetPairUi(PairUi.Idle);
                }
            };
            legacyHint = new Label { Font = BaseFont, Bounds = new Rectangle(M, 196, CW, 36), ForeColor = Lilac, Text = L.T("set.legacy"), UseMnemonic = false };
            pc.Controls.Add(legacyHint);

            Add(pc, Section(L.T("set.dnd"), M, 240));
            dnd = new ComboBox { Bounds = new Rectangle(M, 264, 300, 26), DropDownStyle = ComboBoxStyle.DropDownList };
            pc.Controls.Add(dnd);
            dndHelp = new Label { Font = BaseFont, Bounds = new Rectangle(M, 296, CW, 40), ForeColor = Muted, UseMnemonic = false };
            pc.Controls.Add(dndHelp);
            dnd.SelectedIndexChanged += (s, e) => OnDndChosen();
            sinceLabel = new Label { Font = BaseFont, Bounds = new Rectangle(M, 380, CW, 22), ForeColor = Pink, UseMnemonic = false, TextAlign = ContentAlignment.MiddleCenter };
            pc.Controls.Add(sinceLabel);

            // ═════════ Shortcuts ═════════
            var ps = pages[1];
            Add(ps, Section(L.T("set.hk.heart"), M, 12));
            hkHeart = new HotkeyBox { Bounds = new Rectangle(M, 36, 230, 30), Mods = c.HotMods, Key = (Keys)c.HotKey };
            hkHeart.RefreshText();
            ps.Controls.Add(hkHeart);
            hkHeartHint = new Label { Font = BaseFont, Bounds = new Rectangle(M + 244, 34, CW - 244, 40), ForeColor = Muted, Text = L.T("set.hk.hint"), UseMnemonic = false };
            ps.Controls.Add(hkHeartHint);
            ps.Controls.Add(new Label { Font = BaseFont, Bounds = new Rectangle(M, 76, CW, 40), Text = L.T("set.hk.heart.help"), ForeColor = Ink, UseMnemonic = false });

            Add(ps, Section(L.T("set.hk.msg"), M, 136));
            hkMsg = new HotkeyBox { Bounds = new Rectangle(M, 160, 230, 30), Mods = c.MsgMods, Key = (Keys)c.MsgKey };
            hkMsg.RefreshText();
            ps.Controls.Add(hkMsg);
            hkMsgHint = new Label { Font = BaseFont, Bounds = new Rectangle(M + 244, 158, CW - 244, 40), ForeColor = Muted, Text = L.T("set.hk.hint"), UseMnemonic = false };
            ps.Controls.Add(hkMsgHint);
            ps.Controls.Add(new Label { Font = BaseFont, Bounds = new Rectangle(M, 200, CW, 40), Text = L.T("set.hk.msg.help"), ForeColor = Ink, UseMnemonic = false });

            ps.Controls.Add(new Label
            {
                Bounds = new Rectangle(M, 270, CW, 44), Text = L.T("set.sync.tip"), ForeColor = Pink,
                Font = new Font("Segoe UI Semibold", 10f), UseMnemonic = false
            });
            foreach (var pair in new[] { (hkHeart, hkHeartHint), (hkMsg, hkMsgHint) })
            {
                var box = pair.Item1; var hint = pair.Item2;
                box.Enter += (s, e) => { app.PauseHotkeys(); hint.Text = L.T("set.hk.press"); };
                box.Leave += (s, e) => { app.ResumeHotkeys(); hint.Text = L.T("set.hk.hint"); };
                box.Rejected += t => hint.Text = t;
                box.Changed += () => hint.Text = L.T("set.hk.ok");
            }

            // ═════════ Look & sound ═════════
            var pl = pages[2];
            theme = Art.NormalizeTheme(c.Theme);
            Add(pl, Section(L.T("set.theme"), M, 8));
            var tileIds = new List<string>(Art.Presets) { "custom" };
            for (int i = 0; i < tileIds.Count; i++)
            {
                string id = tileIds[i];
                var tile = new ThemeTile
                {
                    Bounds = new Rectangle(M + (i % 5) * 102, 30 + (i / 5) * 62, 96, 56),
                    Preset = id,
                    Caption = id == "custom" ? L.T("theme.custom") : L.T("theme." + id)
                };
                tile.Click += (s2, e2) => ChooseTile(tile);
                tiles.Add(tile);
                pl.Controls.Add(tile);
            }
            string[] styleIds = Art.Styles;
            for (int i = 0; i < styleIds.Length; i++)
            {
                string id = styleIds[i];
                var b = MakeButton(L.T("style." + id), false, new Rectangle(M + i * 124, 156, 118, 30));
                b.Tag = id;
                b.Click += (s2, e2) => { SetTheme(PresetPart() + "|" + id); app.PreviewTheme(theme); };
                styleButtons.Add(b);
                pl.Controls.Add(b);
            }
            partnerColors = new CheckBox { Text = L.T("set.partnercolors"), Checked = c.PartnerColors, Location = new Point(M, 194), AutoSize = true, UseMnemonic = false };
            pl.Controls.Add(partnerColors);

            Add(pl, Label(L.T("set.corner"), M, 230));
            corner = new ComboBox { Bounds = new Rectangle(M + 230, 226, 200, 26), DropDownStyle = ComboBoxStyle.DropDownList };
            corner.Items.AddRange(new object[] { L.T("set.corner.br"), L.T("set.corner.bl"), L.T("set.corner.bc") });
            corner.SelectedIndex = Math.Max(0, Math.Min(2, c.Corner));
            pl.Controls.Add(corner);

            Add(pl, Section(L.T("set.sound"), M, 266));
            vol = new TrackBar
            {
                Bounds = new Rectangle(M - 4, 286, 340, 36), Minimum = 0, Maximum = 100, TickFrequency = 10,
                Value = c.Volume, BackColor = Soft
            };
            vol.MouseUp += (s2, e2) => { app.SetPreviewVolume(snd.Checked ? vol.Value : 0); app.PreviewSound(); };
            pl.Controls.Add(vol);
            snd = new CheckBox { Text = L.T("set.sound.on"), Checked = c.Sound, Location = new Point(M + 360, 290), AutoSize = true };
            pl.Controls.Add(snd);
            echo = new CheckBox { Text = L.T("set.echo"), Checked = c.Echo, Location = new Point(M, 328), AutoSize = true, UseMnemonic = false };
            pl.Controls.Add(echo);

            Add(pl, Section(L.T("set.preview"), M, 360));
            var previews = new (string, Action)[]
            {
                (L.T("set.test.hearts"), () => app.PreviewTheme(theme)), (L.T("set.test.big"), app.DemoBig),
                (L.T("set.test.sync"), app.DemoSync), (L.T("set.test.note"), app.DemoNote)
            };
            for (int i = 0; i < previews.Length; i++)
            {
                var act = previews[i].Item2;
                var b = MakeButton(previews[i].Item1, false, new Rectangle(M + i * 127, 382, 119, 34));
                b.Click += (s2, e2) => act();
                pl.Controls.Add(b);
            }
            RefreshThemeUi();

            // ═════════ Stats ═════════
            statsPanel = new StatsPanel(app) { Bounds = new Rectangle(M - 8, 4, CW + 16, 414) };
            pages[3].Controls.Add(statsPanel);
            statsRefresh.Tick += (s, e) => { if (pages[3].Visible) statsPanel.Invalidate(); };
            statsRefresh.Start();

            // ═════════ General ═════════
            var pg = pages[4];
            Add(pg, Label(L.T("set.language"), M, 18));
            lang = new ComboBox { Bounds = new Rectangle(M + 230, 14, 200, 26), DropDownStyle = ComboBoxStyle.DropDownList };
            foreach (var l in L.Available) lang.Items.Add(l[1]);
            lang.SelectedIndex = Math.Max(0, Array.FindIndex(L.Available, l => l[0] == c.Lang));
            pg.Controls.Add(lang);
            auto = new CheckBox { Text = L.T("set.autostart"), Checked = Installer.IsAutostart(), Location = new Point(M, 58), AutoSize = true };
            pg.Controls.Add(auto);
            autoUpdate = new CheckBox { Text = L.T("set.autoupdate"), Checked = c.AutoUpdate, Location = new Point(M, 86), AutoSize = true };
            pg.Controls.Add(autoUpdate);
            var check = MakeButton(L.T("set.checknow"), false, new Rectangle(M, 118, 230, 34));
            check.Click += (s, e) => app.CheckForUpdate(true);
            pg.Controls.Add(check);
            pg.Controls.Add(new Label { Font = BaseFont, Location = new Point(M + 244, 127), AutoSize = true, ForeColor = Muted, Text = L.F("set.version", Updater.CurrentText) });

            Add(pg, Section(L.T("set.advanced"), M, 178));
            Add(pg, Label(L.T("set.peer"), M, 202));
            peer = new TextBox { Bounds = new Rectangle(M, 224, 330, 26), Text = c.Peer };
            SetCue(peer, L.T("set.peer.cue"));
            pg.Controls.Add(peer);
            Add(pg, Label(L.T("set.port"), M + 344, 227));
            port = new NumericUpDown { Bounds = new Rectangle(M + 384, 224, 120, 26), Minimum = 1024, Maximum = 65535, Value = c.Port };
            pg.Controls.Add(port);
            pg.Controls.Add(new Label { Font = BaseFont, Bounds = new Rectangle(M, 256, CW, 20), ForeColor = Muted, Text = L.F("set.myaddr", PeerLink.LocalAddresses()) });

            Add(pg, Label(L.T("set.giphy"), M, 292));
            giphy = new TextBox { Bounds = new Rectangle(M, 314, 330, 26), Text = c.GiphyKey };
            pg.Controls.Add(giphy);
            pg.Controls.Add(new Label { Font = BaseFont, Bounds = new Rectangle(M, 344, CW, 36), ForeColor = Muted, Text = L.T("set.giphy.help"), UseMnemonic = false });

            // ── footer ──
            msg = new Label { Bounds = new Rectangle(M, 572, 320, 40), ForeColor = Pink, Text = "", UseMnemonic = false, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Segoe UI", 9.5f) };
            Controls.Add(msg);
            var save = MakeButton(L.T("set.save"), true, new Rectangle(W - M - 170, 572, 170, 38));
            save.Click += (s, e) => Save();
            Controls.Add(save);

            // Fixed-size labels that inherit their font get scaled twice at high DPI by WinForms:
            // giving each one its own font object avoids that.
            ResumeLayout(false);

            app.StateChanged += OnState;
            FormClosed += (s, e) => { app.StateChanged -= OnState; statsRefresh.Dispose(); };
            SetPairUi(c.IsPaired ? PairUi.Paired : PairUi.Idle);
            FillDnd();
            RefreshCouple();
            SelectTab(0);
        }

        /// <summary>
        /// Every label gets its own font when created: fixed-size labels that inherit their font are
        /// scaled twice by WinForms at high DPI.
        /// </summary>
        static Font BaseFont => new Font("Segoe UI", 9.5f);

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Small screens / high scaling: keep the window inside the screen and let it scroll.
            var wa = Screen.FromControl(this).WorkingArea;
            if (Height > wa.Height)
            {
                AutoScroll = true;
                Width += SystemInformation.VerticalScrollBarWidth;
                Height = wa.Height;
                Top = wa.Top;
            }
        }

        /// <summary>Opens a tab by index (0 Couple, 1 Shortcuts, 2 Look, 3 Stats, 4 General).</summary>
        public void SelectTab(int index)
        {
            for (int i = 0; i < tabs.Count; i++)
            {
                bool on = i == index;
                tabs[i].BackColor = on ? Pink : Color.White;
                tabs[i].ForeColor = on ? Color.White : PinkDark;
                tabs[i].FlatAppearance.MouseOverBackColor = on ? PinkDark : Color.FromArgb(255, 238, 244);
                pages[i].Visible = on;
            }
            if (index == 3) statsPanel.Invalidate();
        }

        // ───────────── couple: pairing ─────────────

        string Who() => app.PeerDisplayName.Length > 0 ? app.PeerDisplayName : L.T("your.half");

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
            if (app.Link == null || app.Link.Error != null) { Say(app.Link?.Error ?? L.T("set.netunavail"), false); return; }
            string code = PeerLink.NewCode();
            app.Link.HostPairing(code);
            bigCode.Text = code.Substring(0, 3) + " " + code.Substring(3);
            SetPairUi(PairUi.Hosting);
        }

        void StartJoining()
        {
            string code = PeerLink.NormalizeCode(codeIn.Text);
            if (code.Length != 6) { Say(L.T("set.code6"), false); codeIn.Focus(); return; }
            if (app.Link == null || app.Link.Error != null) { Say(app.Link?.Error ?? L.T("set.netunavail"), false); return; }
            Say("", true);
            app.Link.JoinPairing(code);
            SetPairUi(PairUi.Joining);
        }

        public void OnPaired(string who)
        {
            peer.Text = app.Cfg.Peer;
            Say("", true);
            SetPairUi(PairUi.Paired);
            RefreshCouple();
        }

        public void OnPairTimeout()
        {
            SetPairUi(PairUi.Idle);
            Say(L.T("set.pair.notfound"), false);
        }

        public void RefreshFromConfig() => peer.Text = app.Cfg.Peer;

        void OnState()
        {
            if (app.Cfg.IsPaired && pairUi != PairUi.Hosting && pairUi != PairUi.Joining) SetPairUi(PairUi.Paired);
            RefreshCouple();
            header.Invalidate();
        }

        void SetPairUi(PairUi s)
        {
            pairUi = s;
            bool idle = s == PairUi.Idle;
            btnCreate.Visible = lblOr.Visible = codeIn.Visible = btnJoin.Visible = idle;
            pairHint.Visible = idle || s == PairUi.Hosting;
            bigCode.Visible = s == PairUi.Hosting;
            btnCancel.Visible = s == PairUi.Hosting || s == PairUi.Joining;
            pairTitle.Visible = pairState.Visible = s == PairUi.Paired || s == PairUi.Joining;
            btnUnpair.Visible = s == PairUi.Paired;

            switch (s)
            {
                case PairUi.Idle: pairHint.Text = L.T("set.pair.hint.idle"); break;
                case PairUi.Hosting: pairHint.Text = L.T("set.pair.hint.host"); break;
                case PairUi.Joining:
                    pairTitle.Text = L.T("set.pair.searching");
                    pairState.Text = "";
                    break;
                case PairUi.Paired:
                    pairTitle.Text = L.F("set.pair.paired", Who());
                    bool on = app.Online;
                    pairState.Text = on ? L.T("set.pair.online") : L.T("set.pair.offline");
                    pairState.ForeColor = on ? Green : Muted;
                    break;
            }
        }

        void RefreshCouple()
        {
            var c = app.Cfg;
            legacyHint.Visible = c.IsPaired && c.WeakKey;
            sinceLabel.Text = c.IsPaired && DateTime.TryParseExact(c.PairedSince, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? "💞  " + L.F("set.since", d.ToString("D", CultureInfo.GetCultureInfo(L.Code == "fr" ? "fr-FR" : "en-US"))) : "";
            string state = app.Dnd
                ? (c.DndUntil == DateTime.MaxValue ? L.T("status.dnd.forever") : L.F("status.dnd", c.DndUntil.ToLocalTime().ToString("HH:mm")))
                : "";
            dndHelp.Text = (state.Length > 0 ? "🌙 " + state + "\n" : "") + L.T("set.dnd.help");
            if (!app.Dnd && dnd.SelectedIndex > 0 && !settingDnd) FillDnd();
        }

        // ───────────── couple: do not disturb ─────────────

        void FillDnd()
        {
            settingDnd = true;
            dnd.Items.Clear();
            dnd.Items.Add(L.T("dnd.off"));
            dnd.Items.Add(L.T("dnd.on"));
            foreach (int m in MimiApp.DndMinutes) dnd.Items.Add(L.F("dnd.for", MimiApp.DurationText(m)));
            var c = app.Cfg;
            if (!app.Dnd) dnd.SelectedIndex = 0;
            else if (c.DndUntil == DateTime.MaxValue) dnd.SelectedIndex = 1;
            else
            {
                dnd.Items.Add(L.F("status.dnd", c.DndUntil.ToLocalTime().ToString("HH:mm")));
                dnd.SelectedIndex = dnd.Items.Count - 1;
            }
            settingDnd = false;
        }

        void OnDndChosen()
        {
            if (settingDnd) return;
            int i = dnd.SelectedIndex;
            if (i == 0) app.SetDnd(null);
            else if (i == 1) app.SetDnd(TimeSpan.MaxValue);
            else if (i - 2 < MimiApp.DndMinutes.Length) app.SetDnd(TimeSpan.FromMinutes(MimiApp.DndMinutes[i - 2]));
            RefreshCouple();
        }

        // ───────────── save ─────────────

        void Save()
        {
            if (hkHeart.Key == Keys.None || hkMsg.Key == Keys.None) { Say(L.T("set.choosehk"), false); return; }
            var n = app.Cfg.Clone();
            n.Name = name.Text;
            n.Peer = peer.Text.Trim();
            n.Port = (int)port.Value;
            n.HotMods = hkHeart.Mods;
            n.HotKey = (int)hkHeart.Key;
            n.MsgMods = hkMsg.Mods;
            n.MsgKey = (int)hkMsg.Key;
            n.Volume = vol.Value;
            n.Sound = snd.Checked;
            n.Echo = echo.Checked;
            n.Corner = corner.SelectedIndex;
            n.AutoUpdate = autoUpdate.Checked;
            n.GiphyKey = giphy.Text.Trim();
            n.Theme = theme;
            n.PartnerColors = partnerColors.Checked;
            n.Lang = L.Available[Math.Max(0, lang.SelectedIndex)][0];
            string err = app.Apply(n, auto.Checked);
            if (IsDisposed) return;
            Say(err ?? L.T("set.saved"), err == null);
        }

        // ───────────── look: heart themes ─────────────

        string PresetPart() => theme.Split('|')[0];
        string StylePart() => theme.Split('|')[1];

        void SetTheme(string id)
        {
            theme = Art.NormalizeTheme(id);
            RefreshThemeUi();
        }

        void ChooseTile(ThemeTile tile)
        {
            if (tile.Preset == "custom")
            {
                var start = PresetPart().StartsWith("custom-") ? Art.CustomColor(PresetPart()) : Pink;
                using (var dlg = new ColorDialog { Color = start, FullOpen = true, AnyColor = true })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    var col = dlg.Color;
                    SetTheme("custom-" + col.R.ToString("X2") + col.G.ToString("X2") + col.B.ToString("X2") + "|" + StylePart());
                }
            }
            else SetTheme(tile.Preset + "|" + StylePart());
            app.PreviewTheme(theme);
        }

        void RefreshThemeUi()
        {
            string preset = PresetPart(), style = StylePart();
            foreach (var t in tiles)
            {
                bool custom = t.Preset == "custom";
                t.Selected = custom ? preset.StartsWith("custom-") : t.Preset == preset;
                t.SetTheme(custom ? (preset.StartsWith("custom-") ? preset + "|" + style : null) : t.Preset + "|" + style);
            }
            foreach (var b in styleButtons)
            {
                bool on = (string)b.Tag == style;
                b.BackColor = on ? Pink : Color.White;
                b.ForeColor = on ? Color.White : PinkDark;
                b.FlatAppearance.MouseOverBackColor = on ? PinkDark : Color.FromArgb(255, 238, 244);
            }
        }

        void Say(string text, bool good)
        {
            msg.ForeColor = good ? Green : Pink;
            msg.Text = text;
        }

        // ───────────── UI helpers ─────────────

        static void Add(Control parent, Control c) => parent.Controls.Add(c);

        static Label Label(string text, int x, int y) =>
            new Label { Font = BaseFont, Text = text, Location = new Point(x, y), AutoSize = true, ForeColor = Ink, UseMnemonic = false };

        static Label Section(string text, int x, int y) =>
            new Label
            {
                Text = text.ToUpperInvariant(), Location = new Point(x, y), AutoSize = true, UseMnemonic = false,
                ForeColor = Pink, Font = new Font("Segoe UI Semibold", 8.5f)
            };

        internal static Button MakeButton(string text, bool primary, Rectangle r)
        {
            var b = new Button
            {
                Text = text, Bounds = r, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseMnemonic = false,
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

        // ───────────── painted header with the status pill ─────────────

        sealed class HeaderPanel : Panel
        {
            readonly MimiApp app;
            public HeaderPanel(MimiApp app) { this.app = app; DoubleBuffered = true; }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                var r = ClientRectangle;
                using (var b = new LinearGradientBrush(r, Color.FromArgb(255, 112, 160), Color.FromArgb(255, 160, 130), 0f))
                    g.FillRectangle(b, r);
                var rnd = new Random(7);
                for (int i = 0; i < 9; i++)
                {
                    float s = r.Height * (0.12f + (float)rnd.NextDouble() * 0.18f);
                    float px = r.Width * (0.45f + (float)rnd.NextDouble() * 0.55f), py = (float)rnd.NextDouble() * r.Height;
                    using (var p = Art.HeartPath(new RectangleF(px, py - s / 2, s, s * 0.9f)))
                    using (var br = new SolidBrush(Color.FromArgb(36, 255, 255, 255)))
                        g.FillPath(br, p);
                }
                float hs = r.Height * 0.6f;
                using (var img = Art.HeartBitmap((int)(hs / Art.ContentRatio) + 2, Color.FromArgb(255, 122, 162), Color.FromArgb(233, 30, 99), false))
                    g.DrawImage(img, r.Height * 0.14f + hs / 2 - img.Width / 2f, r.Height / 2f - img.Height / 2f, img.Width, img.Height);

                float tx = r.Height * 0.14f + hs + r.Height * 0.14f;
                using (var f1 = new Font("Segoe UI Semibold", 20f))
                using (var f2 = new Font("Segoe UI", 9.5f))
                using (var f3 = new Font("Segoe UI Semibold", 8.5f))
                {
                    Txt.DrawString(g, "Mimi", f1, Brushes.White, tx, r.Height * 0.1f);
                    Txt.DrawString(g, L.T("set.subtitle"), f2, Brushes.White, tx + 3, r.Height * 0.56f);

                    // status pill on the right
                    string status = app.StatusText ?? "";
                    var sz = Txt.MeasureString(g, status, f3);
                    float ph = sz.Height + 10, pw = sz.Width + ph;
                    var pill = new RectangleF(r.Width - pw - 14, (r.Height - ph) / 2, pw, ph);
                    using (var path = new GraphicsPath())
                    {
                        path.AddArc(pill.X, pill.Y, ph, ph, 90, 180);
                        path.AddArc(pill.Right - ph, pill.Y, ph, ph, 270, 180);
                        path.CloseFigure();
                        using (var bg = new SolidBrush(Color.FromArgb(235, 255, 255, 255))) g.FillPath(bg, path);
                    }
                    Color dot = app.Dnd ? Lilac : app.Online ? Green : Color.FromArgb(190, 170, 180);
                    using (var db = new SolidBrush(dot))
                        g.FillEllipse(db, pill.X + ph * 0.32f, pill.Y + ph * 0.36f, ph * 0.28f, ph * 0.28f);
                    using (var tb = new SolidBrush(Ink))
                        Txt.DrawString(g, status, f3, tb, pill.X + ph * 0.75f, pill.Y + 5);
                }
            }
        }
    }

    /// <summary>A clickable tile showing a preview of a heart theme.</summary>
    sealed class ThemeTile : Control
    {
        public string Preset, Caption;
        bool selected, hover;
        Bitmap preview;
        string previewFor;

        public ThemeTile()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = SettingsForm.Soft;
        }

        public bool Selected { get => selected; set { if (selected != value) { selected = value; Invalidate(); } } }

        /// <summary>Theme to preview, or null for the "pick my colour" tile without a colour yet.</summary>
        public void SetTheme(string themeId)
        {
            if (themeId == previewFor) return;
            previewFor = themeId;
            preview?.Dispose();
            preview = null;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var r = new RectangleF(1, 1, Width - 3, Height - 3);
            using (var path = Round(r, 8))
            {
                using (var b = new SolidBrush(selected ? Color.FromArgb(255, 241, 246) : Color.White)) g.FillPath(b, path);
                using (var p = new Pen(selected ? SettingsForm.Pink : hover ? Color.FromArgb(255, 170, 200) : SettingsForm.PinkLight, selected ? 2.4f : 1.2f))
                    g.DrawPath(p, path);
            }
            float captionH = Font.Height + 2;
            var area = new Rectangle(4, 3, Width - 8, (int)(Height - captionH - 4));
            if (previewFor != null)
            {
                if (preview == null || preview.Width != area.Width || preview.Height != area.Height)
                {
                    preview?.Dispose();
                    preview = Art.ThemePreview(previewFor, Math.Max(1, area.Width), Math.Max(1, area.Height));
                }
                g.DrawImage(preview, area.X, area.Y);
            }
            else
            {
                // "my colour" tile without a colour yet: a little rainbow wheel with a plus
                float d = Math.Min(area.Width, area.Height) * 0.8f;
                var wheel = new RectangleF(area.X + (area.Width - d) / 2, area.Y + (area.Height - d) / 2, d, d);
                var cols = new[] { Color.HotPink, Color.Orange, Color.Gold, Color.MediumSeaGreen, Color.DeepSkyBlue, Color.MediumPurple };
                for (int i = 0; i < cols.Length; i++)
                    using (var b = new SolidBrush(cols[i])) g.FillPie(b, wheel.X, wheel.Y, wheel.Width, wheel.Height, i * 60 - 90, 60);
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, RectangleF.Inflate(wheel, -d * 0.28f, -d * 0.28f));
                using (var p = new Pen(SettingsForm.Pink, Math.Max(1.5f, d * 0.07f)))
                {
                    float cx = wheel.X + d / 2, cy = wheel.Y + d / 2, a = d * 0.13f;
                    g.DrawLine(p, cx - a, cy, cx + a, cy);
                    g.DrawLine(p, cx, cy - a, cx, cy + a);
                }
            }
            using (var f = new Font("Segoe UI", 8f, selected ? FontStyle.Bold : FontStyle.Regular))
            using (var b = new SolidBrush(selected ? SettingsForm.PinkDark : SettingsForm.Ink))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
                Txt.DrawString(g, Caption ?? "", f, b, new RectangleF(2, Height - captionH - 2, Width - 4, captionH), sf);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) preview?.Dispose();
            base.Dispose(disposing);
        }

        static GraphicsPath Round(RectangleF r, float rad)
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
    }

    /// <summary>The "Stats" tab: today, streak, all-time totals and a 7-day chart.</summary>
    sealed class StatsPanel : Panel
    {
        readonly MimiApp app;
        public StatsPanel(MimiApp app) { this.app = app; DoubleBuffered = true; BackColor = SettingsForm.Soft; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            var st = app.Stats;
            float k = ClientSize.Width / 520f; // design width
            float pad = 8 * k, gap = 12 * k;
            float cardW = (ClientSize.Width - 2 * pad - 2 * gap) / 3f, cardH = 108 * k;

            using (var big = new Font("Segoe UI Semibold", 22f))
            using (var mid = new Font("Segoe UI Semibold", 11f))
            using (var small = new Font("Segoe UI", 8.5f))
            using (var title = new Font("Segoe UI Semibold", 8.5f))
            using (var ink = new SolidBrush(SettingsForm.Ink))
            using (var muted = new SolidBrush(SettingsForm.Muted))
            using (var pink = new SolidBrush(SettingsForm.Pink))
            using (var lilac = new SolidBrush(SettingsForm.Lilac))
            {
                // ── three cards ──
                var today = st.TodayCounts;
                int streak = st.Streak();
                long total = st.HeartsSent + st.HeartsReceived;
                for (int i = 0; i < 3; i++)
                {
                    var rc = new RectangleF(pad + i * (cardW + gap), pad, cardW, cardH);
                    Card(g, rc);
                    float x = rc.X + 12 * k, y = rc.Y + 10 * k;
                    if (i == 0)
                    {
                        Txt.DrawString(g, L.T("stats.today").ToUpperInvariant(), title, pink, x, y);
                        Txt.DrawString(g, today.Sent.ToString(), big, pink, x, y + 18 * k);
                        var w1 = Txt.MeasureString(g, today.Sent.ToString(), big).Width;
                        Txt.DrawString(g, today.Received.ToString(), big, lilac, x + Math.Max(w1, 40 * k) + 10 * k, y + 18 * k);
                        Txt.DrawString(g, L.T("stats.sent") + "  ·  " + L.T("stats.received"), small, muted, x, y + 66 * k);
                    }
                    else if (i == 1)
                    {
                        Txt.DrawString(g, L.T("stats.streak.title"), title, pink, x, y);
                        Txt.DrawString(g, streak.ToString(), big, ink, x, y + 18 * k);
                        var w = Txt.MeasureString(g, streak.ToString(), big).Width;
                        DrawFlame(g, x + w + 4 * k, y + 28 * k, 22 * k);
                        Txt.DrawString(g, L.T("stats.streak"), small, muted, x, y + 66 * k);
                    }
                    else
                    {
                        Txt.DrawString(g, L.T("stats.total").ToUpperInvariant(), title, pink, x, y);
                        Txt.DrawString(g, total.ToString("N0"), big, ink, x, y + 18 * k);
                        Txt.DrawString(g, L.T("stats.hearts"), small, muted, x, y + 66 * k);
                    }
                }

                // ── 7-day chart ──
                var chart = new RectangleF(pad, pad + cardH + gap, ClientSize.Width - 2 * pad, 190 * k);
                Card(g, chart);
                Txt.DrawString(g, L.T("stats.week").ToUpperInvariant(), title, pink, chart.X + 12 * k, chart.Y + 10 * k);
                string you = L.T("stats.you"), them = app.PeerDisplayName.Length > 0 ? app.PeerDisplayName : L.T("your.half");
                float lx = chart.Right - 12 * k;
                var tw = Txt.MeasureString(g, them, small).Width; lx -= tw; Txt.DrawString(g, them, small, muted, lx, chart.Y + 9 * k);
                lx -= 14 * k; g.FillEllipse(lilac, lx, chart.Y + 14 * k, 9 * k, 9 * k);
                tw = Txt.MeasureString(g, you, small).Width; lx -= tw + 12 * k; Txt.DrawString(g, you, small, muted, lx, chart.Y + 9 * k);
                lx -= 14 * k; g.FillEllipse(pink, lx, chart.Y + 14 * k, 9 * k, 9 * k);

                var days = st.LastDays(7);
                int max = 1;
                foreach (var d in days) max = Math.Max(max, Math.Max(d.sent, d.received));
                float plotTop = chart.Y + 40 * k, plotBottom = chart.Bottom - 28 * k, plotH = plotBottom - plotTop;
                float slot = (chart.Width - 24 * k) / 7f, bw = Math.Min(16 * k, slot * 0.3f);
                var culture = CultureInfo.GetCultureInfo(L.Code == "fr" ? "fr-FR" : "en-US");
                using (var line = new Pen(Color.FromArgb(255, 232, 240), 1))
                    g.DrawLine(line, chart.X + 12 * k, plotBottom, chart.Right - 12 * k, plotBottom);
                for (int i = 0; i < days.Count; i++)
                {
                    float cx = chart.X + 12 * k + slot * (i + 0.5f);
                    float hs = plotH * days[i].sent / max, hr = plotH * days[i].received / max;
                    Bar(g, pink, cx - bw - 1.5f * k, plotBottom, bw, hs);
                    Bar(g, lilac, cx + 1.5f * k, plotBottom, bw, hr);
                    string lbl = days[i].day.ToString("ddd", culture);
                    var ls = Txt.MeasureString(g, lbl, small);
                    Txt.DrawString(g, lbl, small, i == days.Count - 1 ? pink : muted, cx - ls.Width / 2, plotBottom + 4 * k);
                }

                // ── small totals ──
                var foot = new RectangleF(pad, chart.Bottom + gap, ClientSize.Width - 2 * pad, 64 * k);
                Card(g, foot);
                var items = new[]
                {
                    (st.NotesSent + st.NotesReceived, L.T("stats.notes")),
                    (st.PicturesSent + st.PicturesReceived, L.T("stats.pictures")),
                    (st.Syncs, L.T("stats.syncs")),
                    (st.BigHearts, L.T("stats.big")),
                };
                float iw = foot.Width / items.Length;
                for (int i = 0; i < items.Length; i++)
                {
                    string n = items[i].Item1.ToString("N0");
                    var ns = Txt.MeasureString(g, n, mid);
                    var ts = Txt.MeasureString(g, items[i].Item2, small);
                    float cx = foot.X + iw * (i + 0.5f);
                    Txt.DrawString(g, n, mid, ink, cx - ns.Width / 2, foot.Y + 8 * k);
                    Txt.DrawString(g, items[i].Item2, small, muted, cx - ts.Width / 2, foot.Y + 36 * k);
                }
            }
        }

        static void Card(Graphics g, RectangleF r)
        {
            using (var path = Round(r, 10))
            {
                using (var b = new SolidBrush(Color.White)) g.FillPath(b, path);
                using (var p = new Pen(SettingsForm.PinkLight)) g.DrawPath(p, path);
            }
        }

        static void Bar(Graphics g, Brush b, float x, float bottom, float w, float h)
        {
            if (h < 1) { g.FillRectangle(b, x, bottom - 1.5f, w, 1.5f); return; }
            using (var path = Round(new RectangleF(x, bottom - h, w, h), Math.Min(w / 2, 5)))
                g.FillPath(b, path);
        }

        static void DrawFlame(Graphics g, float x, float y, float s)
        {
            using (var path = new GraphicsPath())
            {
                path.AddBezier(x + s * 0.5f, y, x + s * 0.95f, y + s * 0.35f, x + s, y + s * 0.75f, x + s * 0.5f, y + s);
                path.AddBezier(x + s * 0.5f, y + s, x, y + s * 0.75f, x + s * 0.1f, y + s * 0.4f, x + s * 0.5f, y);
                using (var b = new LinearGradientBrush(new RectangleF(x, y, s, s), Color.FromArgb(255, 200, 80), Color.FromArgb(255, 90, 60), 90f))
                    g.FillPath(b, path);
            }
        }

        static GraphicsPath Round(RectangleF r, float rad)
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
    }

    /// <summary>Text box that captures a key combination.</summary>
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
            Font = new Font("Segoe UI Semibold", 10.5f);
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
                Rejected?.Invoke(L.T("set.hk.needmod"));
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
