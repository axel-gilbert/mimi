using System;
using System.Drawing;
using System.Net;
using System.Threading;
using System.Windows.Forms;

namespace Mimi
{
    /// <summary>The heart of the app: tray icon, hotkey, network, overlay, sounds, updates.</summary>
    sealed class MimiApp : ApplicationContext
    {
        public readonly Config Cfg;
        readonly Overlay overlay;
        readonly GlobalHotkey hotkey = new GlobalHotkey();
        readonly SoundBank sound;
        readonly NotifyIcon tray;
        readonly Icon iconOn, iconOff;
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 20000 };
        ToolStripMenuItem statusItem;
        PeerLink link;
        SettingsForm settings;

        int recvCombo;
        DateTime lastRecv = DateTime.MinValue, lastToast = DateTime.MinValue, lastSend = DateTime.MinValue;
        bool hotkeyPaused, updating;
        Version failedUpdate;

        public event Action StateChanged;

        public MimiApp(EventWaitHandle showEvt, EventWaitHandle quitEvt, bool welcome, bool demo, bool updated)
        {
            Art.Init();
            GC.Collect();
            Cfg = Config.Load();
            L.Code = Cfg.Lang;

            overlay = new Overlay();
            var _ = overlay.Handle; // creates the window + the UI synchronization context
            overlay.Corner = Cfg.Corner;

            sound = new SoundBank { Volume = Cfg.Sound ? Cfg.Volume : 0 };
            iconOn = Art.MakeIcon(true);
            iconOff = Art.MakeIcon(false);

            tray = new NotifyIcon { Icon = iconOff, Visible = true, Text = "Mimi" };
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowSettings(); };
            BuildMenu();

            hotkey.Pressed += SendHeart;
            ApplyHotkey(true);
            StartLink();

            ThreadPool.RegisterWaitForSingleObject(showEvt, (s, t) => UI(ShowSettings), null, -1, false);
            ThreadPool.RegisterWaitForSingleObject(quitEvt, (s, t) => UI(Quit), null, -1, false);

            // Automatic updates: first check 20 s after start, then every 6 hours.
            updateTimer.Tick += (s, e) =>
            {
                updateTimer.Interval = 6 * 3600 * 1000;
                if (Cfg.AutoUpdate && Program.Profile == "default") CheckForUpdate(false);
            };
            updateTimer.Start();

            if (updated)
            {
                Updater.CleanupOld();
                if (Installer.IsRunningInstalled()) Installer.RefreshVersion();
                UI(() =>
                {
                    overlay.Toast(L.F("upd.done", Updater.CurrentText));
                    if (Cfg.Sound) sound.Chime();
                });
            }

            UpdateStatus();
            if (welcome || !Cfg.Exists || !Cfg.IsPaired) UI(ShowSettings);
            if (demo) UI(Demo);
            if (Program.TestPairHost != null) UI(() => { ShowSettings(); settings.AutoPair(true, Program.TestPairHost); });
            if (Program.TestPairJoin != null) UI(() => { ShowSettings(); settings.AutoPair(false, Program.TestPairJoin); });
        }

        void UI(Action a)
        {
            try { overlay.BeginInvoke(a); } catch { }
        }

        void BuildMenu()
        {
            var old = tray.ContextMenuStrip;
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            var title = new ToolStripMenuItem("Mimi " + Updater.CurrentText + " 💗") { Enabled = false, Font = new Font(SystemFonts.MenuFont, FontStyle.Bold) };
            statusItem = new ToolStripMenuItem("") { Enabled = false };
            menu.Items.Add(title);
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(L.T("menu.send"), null, (s, e) => SendHeart());
            menu.Items.Add(L.T("menu.test"), null, (s, e) => Demo());
            menu.Items.Add(L.T("menu.settings"), null, (s, e) => ShowSettings());
            menu.Items.Add(L.T("menu.update"), null, (s, e) => CheckForUpdate(true));
            menu.Items.Add(new ToolStripSeparator());
            if (Installer.IsRunningInstalled())
                menu.Items.Add(L.T("menu.uninstall"), null, (s, e) => { if (Installer.Uninstall(true)) Quit(); });
            menu.Items.Add(L.T("menu.quit"), null, (s, e) => Quit());
            tray.ContextMenuStrip = menu;
            old?.Dispose();
        }

        // ───────────── network ─────────────

        void StartLink()
        {
            link?.Dispose();
            link = new PeerLink(Cfg, overlay);
            link.Heart += OnHeart;
            link.StatusChanged += UpdateStatus;
            link.Paired += OnPaired;
            link.PairTimeout += () => settings?.OnPairTimeout();
            link.PeerMoved += ep =>
            {
                Cfg.Peer = PeerLink.FormatPeer(ep);
                Cfg.Save();
                settings?.RefreshFromConfig();
            };
            link.Start();
            if (link.Error != null) Balloon(L.T("net.problem"), link.Error, ToolTipIcon.Warning);
        }

        public PeerLink Link => link;
        public bool Online => link != null && link.Online;
        public string PeerDisplayName => Cfg.PeerName.Length > 0 ? Cfg.PeerName : (link?.LastPeerName ?? "");

        void OnPaired(string code, IPEndPoint ep, string name)
        {
            Cfg.PairCode = code;
            Cfg.Peer = PeerLink.FormatPeer(ep);
            Cfg.PeerName = name;
            Cfg.Save();
            UpdateStatus();
            settings?.OnPaired(name);
            overlay.Wave(10);
            overlay.Toast(L.F("toast.paired", name.Length > 0 ? name : L.T("your.half")));
            if (Cfg.Sound) { sound.Chime(); sound.Mega(); }
        }

        void UpdateStatus()
        {
            bool on = Online;
            tray.Icon = on ? iconOn : iconOff;
            string s;
            if (link?.Error != null) s = L.T("status.neterr");
            else if (!Cfg.IsPaired) s = L.T("status.unpaired");
            else if (on) s = L.F("status.online", PeerDisplayName);
            else s = L.F("status.waiting", PeerDisplayName.Length > 0 ? PeerDisplayName : L.T("your.half"));
            statusItem.Text = s;
            string tip = "Mimi — " + s;
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
            if (on && link.LastPeerName.Length > 0 && link.LastPeerName != Cfg.PeerName)
            {
                Cfg.PeerName = link.LastPeerName;
                Cfg.Save();
            }
            StateChanged?.Invoke();
        }

        // ───────────── hearts ─────────────

        public void SendHeart()
        {
            var now = DateTime.UtcNow;
            if ((now - lastSend).TotalMilliseconds < 60) return; // burst limit (~16/s max)
            lastSend = now;
            link?.SendHeart();
            if (Cfg.Echo) overlay.Echo();
            if (Cfg.Sound) sound.Send();
            if (!Cfg.IsPaired && (now - lastToast).TotalSeconds > 6)
            {
                lastToast = now;
                overlay.Toast(L.T("toast.notpaired"));
            }
        }

        void OnHeart(string name)
        {
            var now = DateTime.UtcNow;
            bool fresh = (now - lastRecv).TotalSeconds > 1.6;
            recvCombo = fresh ? 1 : recvCombo + 1;
            lastRecv = now;

            overlay.Wave(recvCombo);
            if (fresh && (now - lastToast).TotalSeconds > 8)
            {
                lastToast = now;
                overlay.Toast(L.F("toast.sends", name));
            }
            if (Cfg.Sound)
            {
                if (fresh) sound.Chime(); else sound.Pop();
                if (Overlay.IsMega(recvCombo)) sound.Mega();
            }
        }

        /// <summary>Simulates a burst of received hearts (to test without the other PC).</summary>
        public void Demo()
        {
            int n = 0;
            var t = new System.Windows.Forms.Timer { Interval = 130 };
            t.Tick += (s, e) =>
            {
                OnHeart(Cfg.Name.Length > 0 ? Cfg.Name : "Mimi");
                if (++n >= 14) { t.Stop(); t.Dispose(); }
            };
            t.Start();
        }

        // ───────────── updates ─────────────

        public void CheckForUpdate(bool manual)
        {
            if (updating) return;
            updating = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Updater.UpdateInfo info = null;
                string file = null;
                Exception error = null;
                try
                {
                    info = Updater.Check();
                    if (info != null && (manual || !info.Version.Equals(failedUpdate)))
                        file = Updater.Download(info);
                }
                catch (Exception e) { error = e; }
                UI(() => OnUpdateChecked(manual, info, file, error));
            });
        }

        void OnUpdateChecked(bool manual, Updater.UpdateInfo info, string file, Exception error)
        {
            updating = false;
            if (error != null)
            {
                Program.LogError(error);
                if (manual) Balloon(L.T("upd.title"), L.F("upd.failed", error.Message), ToolTipIcon.Warning);
                return;
            }
            if (info == null)
            {
                if (manual) Balloon(L.T("upd.title"), L.F("upd.uptodate", Updater.CurrentText), ToolTipIcon.Info);
                return;
            }
            if (file == null) return;
            try
            {
                Updater.ApplyAndRestart(file);
                Quit();
            }
            catch (Exception e)
            {
                failedUpdate = info.Version;
                Program.LogError(e);
                if (manual) Balloon(L.T("upd.title"), L.F("upd.failed", e.Message), ToolTipIcon.Warning);
            }
        }

        // ───────────── hotkey ─────────────

        public bool ApplyHotkey(bool notify)
        {
            if (hotkeyPaused) return true;
            bool ok = hotkey.Set(Cfg.HotMods, (Keys)Cfg.HotKey);
            if (!ok && notify)
                Balloon(L.T("hk.unavailable.title"),
                    L.F("hk.unavailable", GlobalHotkey.Describe(Cfg.HotMods, (Keys)Cfg.HotKey)),
                    ToolTipIcon.Warning);
            return ok;
        }

        public void PauseHotkey() { hotkeyPaused = true; hotkey.Clear(); }
        public void ResumeHotkey() { hotkeyPaused = false; ApplyHotkey(false); }

        /// <summary>Applies the settings window values. Returns an error message or null.</summary>
        public string Apply(Config n, bool autostart)
        {
            bool restartNet = n.Port != Cfg.Port || n.Peer != Cfg.Peer;
            bool langChanged = n.Lang != Cfg.Lang;
            Cfg.Name = n.Name.Trim().Length > 0 ? n.Name.Trim() : Environment.UserName;
            Cfg.Peer = n.Peer.Trim();
            Cfg.Port = n.Port;
            Cfg.HotMods = n.HotMods;
            Cfg.HotKey = n.HotKey;
            Cfg.Volume = n.Volume;
            Cfg.Sound = n.Sound;
            Cfg.Echo = n.Echo;
            Cfg.Corner = n.Corner;
            Cfg.AutoUpdate = n.AutoUpdate;
            Cfg.Lang = n.Lang;
            overlay.Corner = n.Corner;
            Cfg.Save();
            sound.Volume = Cfg.Sound ? Cfg.Volume : 0;
            try { Installer.SetAutostart(autostart); } catch { }
            if (langChanged)
            {
                L.Code = Cfg.Lang;
                BuildMenu();
            }
            if (restartNet) StartLink();
            UpdateStatus();
            if (link?.Error != null) return link.Error;
            hotkeyPaused = false;
            if (!ApplyHotkey(false)) return L.T("hk.taken");
            if (langChanged) UI(ReopenSettings);
            return null;
        }

        void ReopenSettings()
        {
            settings?.Close();
            ShowSettings();
        }

        public void Unpair()
        {
            Cfg.PairCode = "";
            Cfg.Peer = "";
            Cfg.PeerName = "";
            Cfg.Save();
            StartLink();
            UpdateStatus();
        }

        public void PreviewSound() { if (sound.Volume > 0) sound.Chime(); }
        public void SetPreviewVolume(int v) { sound.Volume = v; }
        public void RestoreVolume() { sound.Volume = Cfg.Sound ? Cfg.Volume : 0; }

        // ───────────── windows ─────────────

        public void ShowSettings()
        {
            if (settings == null || settings.IsDisposed)
            {
                var f = new SettingsForm(this, iconOn);
                f.FormClosed += (s, e) =>
                {
                    link?.CancelPairing();
                    ResumeHotkey();
                    RestoreVolume();
                    if (settings == f) settings = null;
                };
                settings = f;
            }
            settings.Show();
            if (settings.WindowState == FormWindowState.Minimized) settings.WindowState = FormWindowState.Normal;
            settings.Activate();
        }

        void Balloon(string title, string text, ToolTipIcon icon)
        {
            tray.BalloonTipTitle = title;
            tray.BalloonTipText = text;
            tray.BalloonTipIcon = icon;
            tray.ShowBalloonTip(5000);
        }

        void Quit()
        {
            updateTimer.Stop();
            tray.Visible = false;
            settings?.Close();
            link?.Dispose();
            hotkey.Dispose();
            overlay.Dispose();
            tray.Dispose();
            ExitThread();
        }
    }
}
